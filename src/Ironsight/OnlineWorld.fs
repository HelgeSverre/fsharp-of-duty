namespace Ironsight.Shell

open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module OnlineWorld =
    let eventToGameEvent (event: OnlineEvent) =
        // Every Material case is matched explicitly (not just the common
        // surfaces) so an unrecognised string is the only path to the Mud
        // fallback, not a silently missing case.
        let material =
            match event.Text with
            | "Brick" -> Brick | "Plaster" -> Plaster | "Wood" -> Wood | "Mud" -> Mud
            | "Snow" -> Snow | "Sandbag" -> Sandbag | "Metal" -> Metal
            | "UniformOlive" -> UniformOlive | "UniformFeldgrau" -> UniformFeldgrau
            | "Skin" -> Skin | "Water" -> Water
            | "PaintRed" -> PaintRed | "PaintBlue" -> PaintBlue | "PaintGreen" -> PaintGreen
            | "PaintYellow" -> PaintYellow | "PaintPurple" -> PaintPurple | "PaintOrange" -> PaintOrange
            | "FoamBlue" -> FoamBlue | "FoamOrange" -> FoamOrange
            | "ToolBlack" -> ToolBlack | "WaterBlue" -> WaterBlue | "WetDark" -> WetDark
            | _ -> Mud
        match event.Kind with
        | "shot" -> Some(ShotFired((if event.EntityId = 0 then None else Some(EntityId event.EntityId)), event.Position, event.Direction, event.Text))
        | "impact" -> Some(Impact(event.Position, event.Direction, material))
        | "hit" -> Some(HitConfirmed(EntityId event.EntityId, event.Value > 0.5f))
        | "blood" -> Some(BloodImpact(event.Position, event.Direction, event.Value > 0.5f))
        | "head-gib" -> Some(HeadGib(event.Position, event.Direction))
        | "explosion" -> Some(Explosion(event.Position, event.Value))
        | "paint" -> Some(PaintImpact(event.Position, event.Direction, material))
        | "dart" -> Some(DartImpact(event.Position, event.Direction, event.Value > 0.5f))
        | "rocket-dud" -> Some(RocketDud(event.Position, event.Direction))
        | "backblast" -> Some(Backblast(event.Position, event.Direction))
        | "flame-stream" -> Some(FlameStream(event.Position, event.Position + event.Direction * event.Value))
        | "flame-impact" -> Some(FlameImpact(event.Position, event.Direction))
        | "water-impact" -> Some(WaterImpact(event.Position, event.Direction))
        | "nail-impact" -> Some(NailImpact(event.Position, event.Direction, event.Value > 0.5f))
        | "harpoon-skewer" -> Some(HarpoonSkewer(event.Position, event.Direction, EntityId event.EntityId))
        | "harpoon-embedded" -> Some(HarpoonEmbedded(event.Position, event.Direction))
        | "arrow-impact" -> Some(ArrowImpact(event.Position, event.Direction, event.Value > 0.5f))
        | "ignited" -> Some(Ignited(EntityId event.EntityId, event.Position))
        | "extinguished" -> Some(Extinguished(EntityId event.EntityId, event.Position))
        | "burning" -> Some(Burning(EntityId event.EntityId, event.Position))
        | "footstep" -> Some(FootStep(event.Position, material))
        | "hurt" -> Some(PlayerHurt(event.Direction, Units.health event.Value))
        // The wire text already carries "{speaker}: {line}" pre-formatted, so
        // the speaker slot stays empty — the HUD must not prefix it again.
        | "subtitle" -> Some(Subtitle("", event.Text))
        | "objective" -> Some(ObjectiveUpdated event.EntityId)
        // kill: entityId = victim, x = killer id (0 = world/suicide),
        // value = headshot, text = weapon. Mirrors Protocol.eventSnapshot.
        | "kill" ->
            let killerId = int event.Position.X
            let killer = if killerId = 0 then None else Some(EntityId killerId)
            Some(Kill(killer, EntityId event.EntityId, event.Text, event.Value > 0.5f))
        | "joined" -> Some(PlayerJoined(EntityId event.EntityId, event.Text))
        | "left" -> Some(PlayerLeft(EntityId event.EntityId, event.Text))
        | "phase-change" -> Some(PhaseChanged event.Text)
        // chat: entityId = sender (0 = server/system), text = "{name}\t{line}".
        // Tab separates safely because Multiplayer.sanitizeText strips control
        // scalars from both halves. Mirrors Protocol.eventSnapshot.
        | "chat" ->
            let parts = event.Text.Split('\t', 2)
            let sender = if event.EntityId = 0 then None else Some(EntityId event.EntityId)
            if parts.Length = 2 then Some(Chat(sender, parts[0], parts[1])) else Some(Chat(sender, "", event.Text))
        | _ -> None

    /// The single place an online weapon slot is built (local player, HUD,
    /// viewmodel and remote soldiers all come through here), so the server's
    /// reload timer has to be reapplied here or it is lost.
    let private slotFor (team: Team) (wire: OnlineWeapon) =
        let weapon = Tuning.weaponByName wire.WeaponName |> Option.defaultValue (Tuning.defaultWeapon team)
        { Tuning.weaponSlot weapon 0 with
            InMag = wire.Ammo
            Reserve = wire.Reserve
            State = if wire.ReloadRemaining > 0.0f then Reloading(Units.seconds wire.ReloadRemaining) else Ready }

    /// The carried kit, and which slot is in hand. A server built before kits
    /// sends no `slots`, so fall back to the flat fields it does send — that is
    /// a one-weapon inventory, exactly the old behaviour.
    let private kitFor (player: OnlinePlayer) =
        let slots =
            if player.Slots.Length > 0 then player.Slots |> Array.map (slotFor player.Team)
            else
                [| slotFor
                       player.Team
                       { WeaponName = player.WeaponName
                         Ammo = player.Ammo
                         Reserve = player.Reserve
                         ReloadRemaining = player.ReloadRemaining } |]
        let active = if player.Active >= 0 && player.Active < slots.Length then player.Active else 0
        // A switch in flight lives on the outgoing slot and carries its
        // destination, so the viewmodel plays the raise instead of popping.
        if player.SwitchRemaining > 0.0f && player.SwitchTo >= 0 && player.SwitchTo < slots.Length then
            let switching = Array.copy slots
            switching[active] <- { switching[active] with State = Switching(player.SwitchTo, Units.seconds player.SwitchRemaining) }
            switching, active
        elif player.DrawCharge > 0.0f && slots[active].Class.Mechanism = Bow then
            let drawing = Array.copy slots
            drawing[active] <- { drawing[active] with State = Drawing(Units.seconds player.DrawCharge) }
            drawing, active
        else slots, active

    let private weaponFor (player: OnlinePlayer) =
        let slots, active = kitFor player
        slots[active]

    let private localPlayer (previous: Player) (snapshot: OnlinePlayer) =
        let slots, active = kitFor snapshot
        { previous with
            Id = EntityId snapshot.Id
            Position = snapshot.Position
            // The full movement state, not just position: replaying pending
            // inputs from a zeroed velocity can never match the continuous
            // local prediction (the acceleration model needs several ticks to
            // spin back up, and a jump arc collapses entirely), which showed
            // up as 20 Hz stutter on flat ground and mangled jumps.
            Velocity = snapshot.Velocity
            Stance = snapshot.Stance
            Yaw = snapshot.Yaw
            Pitch = snapshot.Pitch
            Sprinting = false
            Ads = snapshot.Ads
            Health = Units.health snapshot.Health
            RegenIn = Units.seconds 0.0f
            Slots = slots
            Active = active }

    let private remoteSoldier (snapshot: OnlinePlayer) =
        { Id = EntityId snapshot.Id
          Team = snapshot.Team
          Position = snapshot.Position
          Facing = snapshot.Yaw
          Stance = snapshot.Stance
          Health = Units.health snapshot.Health
          Behavior = if snapshot.Alive then Idle else Dying(Units.seconds 0.7f)
          Weapon = weaponFor snapshot
          Squad = if snapshot.Team = Allies then 1 else 2
          Contacts = Map.empty
          Suppression = 0.0f
          AnimPhase = float32 snapshot.Id }

    let private toGrenade (grenade: OnlineGrenade) =
        { Owner = EntityId grenade.OwnerId
          Position = grenade.Position
          Velocity = Vector3.Zero
          Fuse = Units.seconds grenade.Fuse }

    let reconcile level pendingInputs localId (world: World) (snapshot: OnlineSnapshot) =
        match snapshot.Players |> Array.tryFind (fun player -> player.Id = localId) with
        | None -> world, pendingInputs
        | Some authoritative ->
            let pending = pendingInputs |> List.filter (fun input -> input.Sequence > authoritative.AcknowledgedInput)
            let basePlayer = localPlayer world.Player authoritative
            // A fallen player is not moved by unacknowledged frames — the server
            // treats inputs while dead as no-ops anyway.
            let predicted =
                if authoritative.Health <= 0.0f then basePlayer
                else
                    pending
                    |> List.fold (fun player input -> Movement.step Tuning.TickDuration input level player) basePlayer
            let soldiers =
                snapshot.Players
                |> Array.filter (fun player -> player.Id <> localId)
                |> Array.map remoteSoldier
            let grenades = snapshot.Grenades |> Array.map toGrenade
            { world with
                Tick = snapshot.Tick
                Player = predicted
                Soldiers = soldiers
                Grenades = grenades
                SpecialProjectiles = [||]
                PersistentMarks = [||]
                ElementalStatus = Map.empty
                Squads = Map.empty }, pending

    let applyPrediction level input (world: World) =
        { world with
            Tick = world.Tick + 1L
            Player = Movement.step Tuning.TickDuration input level world.Player }

    let interpolateRemotes localId (world: World) (snapshot: OnlineSnapshot) =
        let byId = snapshot.Players |> Array.map (fun player -> EntityId player.Id, player) |> Map.ofArray
        let soldiers =
            world.Soldiers
            |> Array.map (fun soldier ->
                match Map.tryFind soldier.Id byId with
                | Some remote when remote.Id <> localId ->
                    { soldier with
                        Position = remote.Position
                        Facing = remote.Yaw
                        Health = Units.health remote.Health
                        Weapon = weaponFor remote
                        Behavior = if remote.Alive then soldier.Behavior else Dying(Units.seconds 0.7f) }
                | _ -> soldier)
        let grenades = snapshot.Grenades |> Array.map toGrenade
        { world with Soldiers = soldiers; Grenades = grenades }
