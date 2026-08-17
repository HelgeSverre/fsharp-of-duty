namespace Ironsight.Shell

open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module OnlineWorld =
    let eventToGameEvent (event: OnlineEvent) =
        let material =
            match event.Text with
            | "Brick" -> Brick | "Plaster" -> Plaster | "Wood" -> Wood | "Snow" -> Snow
            | "Sandbag" -> Sandbag | "Metal" -> Metal | _ -> Mud
        match event.Kind with
        | "shot" -> Some(ShotFired((if event.EntityId = 0 then None else Some(EntityId event.EntityId)), event.Position, event.Direction, event.Text))
        | "impact" -> Some(Impact(event.Position, event.Direction, material))
        | "hit" -> Some(HitConfirmed(EntityId event.EntityId, event.Value > 0.5f))
        | "blood" -> Some(BloodImpact(event.Position, event.Direction, event.Value > 0.5f))
        | "head-gib" -> Some(HeadGib(event.Position, event.Direction))
        | "explosion" -> Some(Explosion(event.Position, event.Value))
        | "footstep" -> Some(FootStep(event.Position, material))
        | "subtitle" -> Some(Subtitle("RADIO", event.Text))
        | _ -> None

    let private weaponFor (player: OnlinePlayer) =
        let weapon =
            Tuning.weaponByName player.WeaponName
            |> Option.defaultValue (if player.Team = Allies then Tuning.thompson else Tuning.kar98k)
        { Tuning.weaponSlot weapon 0 with InMag = player.Ammo; Reserve = player.Reserve }

    let private localPlayer previous (snapshot: OnlinePlayer) =
        { previous with
            Id = EntityId snapshot.Id
            Position = snapshot.Position
            Velocity = Vector3.Zero
            Yaw = snapshot.Yaw
            Pitch = snapshot.Pitch
            Sprinting = false
            Ads = snapshot.Ads
            Health = Units.health snapshot.Health
            RegenIn = Units.seconds 0.0f
            Slots = [| weaponFor snapshot |]
            Active = 0 }

    let private remoteSoldier (snapshot: OnlinePlayer) =
        { Id = EntityId snapshot.Id
          Team = snapshot.Team
          Position = snapshot.Position
          Facing = snapshot.Yaw
          Stance = Standing
          Health = Units.health snapshot.Health
          Behavior = if snapshot.Alive then Idle else Dying(Units.seconds 0.7f)
          Weapon = weaponFor snapshot
          Squad = if snapshot.Team = Allies then 1 else 2
          Contacts = Map.empty
          Suppression = 0.0f
          AnimPhase = float32 snapshot.Id }

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
            let grenades =
                snapshot.Grenades
                |> Array.map (fun grenade ->
                    { Owner = EntityId grenade.OwnerId
                      Position = grenade.Position
                      Velocity = Vector3.Zero
                      Fuse = Units.seconds grenade.Fuse })
            { world with Tick = snapshot.Tick; Player = predicted; Soldiers = soldiers; Grenades = grenades; Squads = Map.empty }, pending

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
                        Behavior = if remote.Alive then soldier.Behavior else Dying(Units.seconds 0.7f) }
                | _ -> soldier)
        let grenades =
            snapshot.Grenades
            |> Array.map (fun grenade ->
                { Owner = EntityId grenade.OwnerId
                  Position = grenade.Position
                  Velocity = Vector3.Zero
                  Fuse = Units.seconds grenade.Fuse })
        { world with Soldiers = soldiers; Grenades = grenades }
