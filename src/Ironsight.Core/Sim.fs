namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Sim =
    /// Offline is a sandbox: the whole arsenal, carried at once. Derived from
    /// `onlineWeapons` rather than hand-listed, so a weapon added to the game
    /// is carryable — and pickable in the loadout menu — the day it lands.
    let private playerSlots () =
        // Offline is a sandbox: the whole online arsenal, plus the special
        // weapons that stay offline-only until they are balanced for
        // multiplayer. The first half is derived, so a gun added to the online
        // arsenal is carryable here the day it lands.
        let online = Tuning.onlineWeapons |> Array.map _.Name |> Set.ofArray
        Array.append
            Tuning.onlineWeapons
            // Only the specials that have not been promoted to the online
            // arsenal yet; the two lists overlap and neither is hand-kept here.
            (Tuning.specialWeapons |> Array.filter (fun weapon -> not (online.Contains weapon.Name)))
        // A belt-fed gun carries fewer spare belts than a rifle carries clips.
        |> Array.map (fun weapon -> Tuning.weaponSlot weapon (if weapon.MagSize >= 100 then 2 else 5))

    /// The paintball round loadout opens on the Thompson.
    let private thompsonSlot =
        Tuning.onlineWeapons |> Array.findIndex (fun weapon -> weapon.Name = Tuning.thompson.Name)

    /// Staggered post-respawn cooldown for round-mode bots, so a whole squad
    /// doesn't open fire on the exact same tick. Distinct from the MG-crew and
    /// battlefield-infantry stagger constants below, which model a different
    /// pace and must not be merged with this one.
    let private staggeredReady index = Cooling(Units.seconds (0.18f + float32 index * 0.12f))

    let private resetRoundCombatants (world: World) round =
        let mutable colorRng = world.Rng
        let paintColor = SpecialProjectiles.chooseNextPaintColor world.PaintColor &colorRng
        let playerSpawn =
            world.Level.Spawns
            |> Array.pick (fun struct (team, position) -> if team = Some Allies then Some position else None)
        let enemySpawns =
            world.Level.Spawns
            |> Array.choose (fun struct (team, position) -> if team = Some Axis then Some position else None)
        let soldiers =
            world.Soldiers
            |> Array.filter (fun soldier -> soldier.Team = Axis)
            |> Array.mapi (fun index soldier ->
                let position = if index < enemySpawns.Length then enemySpawns[index] else soldier.Position
                { soldier with
                    Position = position
                    Facing = MathF.PI
                    Health = Units.health 100.0f
                    Behavior = Idle
                    Weapon =
                        { Tuning.weaponSlot soldier.Weapon.Class 4 with
                            State = staggeredReady index }
                    Contacts = Map.empty
                    Suppression = 0.0f
                    AnimPhase = 0.0f })
        { world with
            Rng = colorRng
            Player =
                { world.Player with
                    Position = playerSpawn
                    Velocity = Vector3.Zero
                    Yaw = 0.0f
                    Pitch = 0.0f
                    Stance = Standing
                    Sprinting = false
                    Ads = 0.0f
                    Health = Units.health 100.0f
                    RegenIn = Units.seconds 0.0f
                    Slots = playerSlots ()
                    Active = world.Player.Active
                    Grenade = GrenadeIdle 3 }
            Soldiers = soldiers
            Grenades = [||]
            SpecialProjectiles = [||]
            PersistentMarks = [||]
            ElementalStatus = Map.empty
            Dismemberments = Map.empty
            PaintColor = paintColor
            Squads = Map.empty
            Script = { MissionTime = Units.seconds 0.0f; Ended = false; Rules = world.Level.MissionRules }
            Objectives = world.Objectives |> Array.map (fun objective -> { objective with Done = false })
            Round = Some { round with Number = round.Number + 1; ResetIn = None; LastResult = None } }

    let private advanceRound (world: World) events =
        match world.Round with
        | None -> world, events
        | Some round ->
            match round.ResetIn with
            | Some remaining when remaining <= Tuning.TickDuration ->
                let reset = resetRoundCombatants world round
                reset, events @ [ Subtitle("MARSHAL", $"ROUND {round.Number + 1}") ]
            | Some remaining ->
                { world with Round = Some { round with ResetIn = Some(remaining - Tuning.TickDuration) } }, events
            | None ->
                let playerDead = world.Player.IsDead
                let enemiesDead =
                    world.Soldiers
                    |> Array.filter (fun soldier -> soldier.Team = Axis)
                    |> Array.forall (fun soldier -> soldier.IsDead)
                if not playerDead && not enemiesDead then world, events
                else
                    let result, playerScore, enemyScore =
                        match playerDead, enemiesDead with
                        | false, true -> "ROUND WON", round.PlayerScore + 1, round.EnemyScore
                        | true, false -> "ROUND LOST", round.PlayerScore, round.EnemyScore + 1
                        | _ -> "DRAW", round.PlayerScore, round.EnemyScore
                    let nextRound =
                        { round with
                            PlayerScore = playerScore
                            EnemyScore = enemyScore
                            ResetIn = Some(Units.seconds 1.5f)
                            LastResult = Some result }
                    { world with Round = Some nextRound }, events @ [ Subtitle("MARSHAL", result) ]

    let private shotDirection (player: Player) shot =
        Ballistics.directionFromAngles player.Yaw player.Pitch shot.DirectionOffset

    /// Material of the surface underfoot, for footstep sound. Takes it from the
    /// triangle actually stood on, so a slope or a raised terrace reports its own
    /// material rather than whatever brush top happened to be nearest.
    let private surfaceBelow (level: Level) (position: Vector3) =
        LevelCompile.trianglesNear position 0.6f level
        |> Array.choose (fun triangle ->
            if triangle.Normal.Y >= Tuning.MaxSlopeCosine then
                match MathEx.rayTriangle (position + Vector3(0.0f, 0.3f, 0.0f)) -Vector3.UnitY triangle.A triangle.B triangle.C with
                | ValueSome distance when distance <= 0.5f -> Some(struct (0.3f - distance, triangle.Material))
                | _ -> None
            else None)
        |> function
            | [||] -> Mud
            | hits -> hits |> Array.maxBy (fun struct (height, _) -> height) |> fun struct (_, material) -> material

    /// Result of `stepLocomotion`: the mechanically-identical core shared by
    /// the campaign sim tick and the multiplayer match host tick.
    type LocomotionResult =
        { Player: Player
          Weapon: WeaponSlot
          /// Eye origin, aim direction, and the shot itself, one per pellet.
          Shots: struct (Vector3 * Vector3 * ShotRequest) list
          Thrown: Grenade option
          FootStep: GameEvent option }

    /// Movement -> weapon cycling -> grenade hand -> footstep -> shot rays,
    /// shared by Sim.step (campaign/round bots) and MatchHost.stepFrame
    /// (multiplayer). `stepWeapon = false` freezes the active slot entirely —
    /// used by Sim while a weapon switch is in flight, when even the
    /// cooldown/reload clock must not advance. `canFire`/`canThrowGrenade` are
    /// separate because the two callers disagree on what blocks each: the
    /// campaign player can still cook a grenade at 0 HP (existing behaviour,
    /// unrelated to these findings) while multiplayer gates both on match
    /// phase alone.
    let stepLocomotion
        (dt: float32<s>)
        (level: Level)
        (tick: int64)
        (input: InputFrame)
        (stepWeapon: bool)
        (canFire: bool)
        (canThrowGrenade: bool)
        (player: Player)
        (rng: byref<Rng.State>)
        : LocomotionResult =
        let moved = Movement.step dt input level player
        let active = moved.Slots[moved.Active]
        let fireHeld = Input.hasButton InputButtons.Fire input.Buttons
        let katanaSecondary = active.Class.Mechanism = Katana && Input.hasButton InputButtons.Ads input.Buttons
        // Once a bow is drawn, sprint suppression must not masquerade as a
        // trigger release and loose an accidental arrow. It may finish/release
        // a draw while sprinting, but cannot begin one there.
        let alreadyDrawing = match active.State with Drawing _ -> true | _ -> false
        let fire = stepWeapon && canFire && (fireHeld || katanaSecondary) && (not moved.Sprinting || alreadyDrawing)
        let reload = Input.hasButton InputButtons.Reload input.Buttons
        let moveSpeed = MathEx.horizontalSpeed moved.Velocity
        let weapon, shots =
            if stepWeapon then
                let weaponAim = if katanaSecondary then 1.0f else moved.Ads
                let struct (weapon, shots) = Weapons.step dt moveSpeed moved.Stance fire reload weaponAim &rng active
                weapon, shots
            else active, []
        let grenadeHeld = canThrowGrenade && Input.hasButton InputButtons.Grenade input.Buttons && not moved.Sprinting
        let handPlayer, thrown = Grenades.stepHand dt grenadeHeld moved
        let footstep =
            let speed = MathEx.horizontalSpeed handPlayer.Velocity
            let interval = if handPlayer.Sprinting then 18L else 26L
            if speed > 1.0f && Movement.grounded level handPlayer.Position && tick % interval = 0L then
                Some(FootStep(handPlayer.Position, surfaceBelow level handPlayer.Position))
            else None
        let rays = shots |> List.map (fun shot -> struct (Ballistics.playerEyeOrigin handPlayer, shotDirection handPlayer shot, shot))
        { Player = handPlayer; Weapon = weapon; Shots = rays; Thrown = thrown; FootStep = footstep }

    /// Copy-and-patch the active slot's weapon state; a switch always drops ADS.
    let private withActiveState state (player: Player) =
        let slots = Array.copy player.Slots
        slots[player.Active] <- { slots[player.Active] with State = state }
        { player with Slots = slots; Ads = 0.0f }

    /// One tick of weapon selection: starts a switch the keys asked for, or
    /// advances one already in flight. Returns the player and whether the
    /// weapon clock is frozen this tick (a switch in progress locks firing,
    /// reloading and bloom alike).
    ///
    /// Shared by the campaign and the server so online play switches by the
    /// same rules — it reads nothing but the player, which is what lets the
    /// server call it without a World.
    let stepWeaponSwitch (input: InputFrame) (player: Player) =
        let requestedCategory =
            [| InputButtons.Weapon1; InputButtons.Weapon2; InputButtons.Weapon3; InputButtons.Weapon4; InputButtons.Weapon5 |]
            |> Array.tryFindIndex (fun button -> Input.hasButton button input.Buttons)
        // A key selects its category; pressing it again cycles within the
        // category. Holding the key is rate-limited naturally by the 0.35 s
        // Switching state, which ignores requests while in flight.
        let requestedWeapon =
            match requestedCategory with
            | Some category ->
                match Tuning.categorySlots player.Slots category with
                | [||] -> None
                | members ->
                    match Array.tryFindIndex ((=) player.Active) members with
                    | Some position -> Some members[(position + 1) % members.Length]
                    | None -> Some members[0]
            | None ->
                // Scroll wheel steps through the carried weapons in slot order,
                // the way Half-Life and Counter-Strike bind invnext/invprev —
                // it ignores categories, so it always reaches every gun.
                let step =
                    (if Input.hasButton InputButtons.WeaponNext input.Buttons then 1 else 0)
                    - (if Input.hasButton InputButtons.WeaponPrev input.Buttons then 1 else 0)
                if step = 0 || player.Slots.Length < 2 then None
                else Some((player.Active + step + player.Slots.Length) % player.Slots.Length)
        match player.Slots[player.Active].State with
        | Switching(incoming, remaining) when remaining <= Tuning.TickDuration ->
            { withActiveState Ready player with Active = incoming }, false
        | Switching(incoming, remaining) ->
            withActiveState (Switching(incoming, remaining - Tuning.TickDuration)) player, true
        | _ ->
            match requestedWeapon with
            | Some incoming when incoming >= 0 && incoming < player.Slots.Length && incoming <> player.Active ->
                withActiveState (Switching(incoming, Tuning.WeaponSwitchTime)) player, true
            | _ -> player, false

    let step (input: InputFrame) (world: World) =
        let regenerated = Damage.stepRegen Tuning.TickDuration world.Player
        let prepared, weaponLocked = stepWeaponSwitch input regenerated
        let mutable rng = world.Rng
        let canFire = prepared.IsAlive
        let result = stepLocomotion Tuning.TickDuration world.Level world.Tick input (not weaponLocked) canFire true prepared &rng
        let slots = Array.copy result.Player.Slots
        slots[result.Player.Active] <- result.Weapon
        let armedPlayer = { result.Player with Slots = slots }
        let shotEvents = ResizeArray<GameEvent>()
        let mutable soldiers = world.Soldiers
        let mutable projectilePlayer = armedPlayer
        let mutable elementalStatus = world.ElementalStatus
        let mutable dismemberments = world.Dismemberments
        let spawnedProjectiles = ResizeArray<SpecialProjectile>()
        if not result.Shots.IsEmpty then
            // Tracer starts at the muzzle; the hit trace below leaves the eye.
            let origin = Ballistics.playerMuzzleOrigin armedPlayer result.Weapon.Class
            let struct (_, direction, _) = List.head result.Shots
            shotEvents.Add(ShotFired(Some armedPlayer.Id, origin, direction, result.Weapon.Class.Name))
        for struct (origin, direction, shot) in result.Shots do
            let muzzle = Ballistics.playerMuzzleOrigin armedPlayer result.Weapon.Class
            // What the trigger launches is decided in one place for both the
            // campaign and the match host; only what resolves instantly differs.
            spawnedProjectiles.AddRange(
                SpecialProjectiles.launch armedPlayer.Id world.PaintColor result.Weapon.Class shot.Damage muzzle direction &rng)
            match result.Weapon.Class.Mechanism with
            | Rocket ->
                let nextPlayer, nextSoldiers, backblastEvents =
                    SpecialProjectiles.applyBackblast muzzle direction world.Level projectilePlayer soldiers
                projectilePlayer <- nextPlayer
                soldiers <- nextSoldiers
                shotEvents.AddRange backblastEvents
            | FlameJet ->
                let nextPlayer, nextSoldiers, nextStatus, flameEvents =
                    SpecialProjectiles.applyFlameJet armedPlayer.Id muzzle direction world.Level projectilePlayer soldiers elementalStatus
                projectilePlayer <- nextPlayer
                soldiers <- nextSoldiers
                elementalStatus <- nextStatus
                shotEvents.AddRange flameEvents
            | Laser ->
                let hitSoldiers, endpoint, hitEvents =
                    Ballistics.applyLaserFiltered (fun soldier -> soldier.Team = Axis) muzzle direction shot.Damage world.Level soldiers
                soldiers <- hitSoldiers
                shotEvents.Add(LaserBeam(muzzle, endpoint))
                shotEvents.AddRange hitEvents
                for event in hitEvents do
                    match event with
                    | HitConfirmed(victim, true) ->
                        shotEvents.Add(Kill(Some armedPlayer.Id, victim, result.Weapon.Class.Name, false))
                    | _ -> ()
            | Katana ->
                match shot.Melee with
                | None -> ()
                | Some attack ->
                    let targets =
                        soldiers
                        |> Array.map (fun soldier ->
                            { Id = soldier.Id
                              Position = soldier.Position
                              Yaw = soldier.Facing
                              Stance = Anatomy.effectiveStance soldier
                              AnimPhase = soldier.AnimPhase })
                    let hits =
                        Melee.resolve attack armedPlayer.Position armedPlayer.Yaw armedPlayer.Pitch
                            (fun target ->
                                soldiers
                                |> Array.exists (fun soldier -> soldier.Id = target.Id && soldier.Team = Axis && soldier.IsAlive))
                            world.Level targets
                    let endpoint = Melee.traceEndpoint attack armedPlayer.Position armedPlayer.Yaw armedPlayer.Pitch
                    shotEvents.Add(MeleeTrace(armedPlayer.Id, armedPlayer.Position + Vector3.UnitY * 1.15f, endpoint, attack))
                    for hit in hits do
                        match soldiers |> Array.tryFindIndex (fun soldier -> soldier.Id = hit.Victim) with
                        | None -> ()
                        | Some index ->
                            let before = soldiers[index]
                            if before.IsAlive then
                                let health = max (Units.health 0.0f) (before.Health - shot.Damage)
                                let lethal = health <= Units.health 0.0f
                                let updated = Array.copy soldiers
                                updated[index] <-
                                    { before with
                                        Health = health
                                        Behavior = if lethal then Dying(Units.seconds 0.0f) else before.Behavior
                                        // InCover carries the AI crouch outside
                                        // Soldier.Stance. Preserve that pose as
                                        // the behavior changes to Dying so the
                                        // corpse cut does not pop upward.
                                        Stance = if lethal then Anatomy.effectiveStance before else before.Stance
                                        Suppression = if lethal then before.Suppression else min 3.0f (before.Suppression + 0.5f) }
                                soldiers <- updated
                                let travel = Ballistics.directionFromAngles armedPlayer.Yaw armedPlayer.Pitch Vector2.Zero
                                shotEvents.Add(BloodImpact(hit.Point, travel, hit.Part = BodyHead))
                                shotEvents.Add(HitConfirmed(hit.Victim, lethal))
                                if lethal then
                                    match Melee.tryMakeCut world.Tick before.Position before.Facing attack (int (world.Tick ^^^ int64 index)) hit with
                                    | Some cut ->
                                        dismemberments <- Map.add hit.Victim cut dismemberments
                                        shotEvents.Add(Dismembered(hit.Victim, hit.Point, cut))
                                    | None -> ()
                                    shotEvents.Add(Kill(Some armedPlayer.Id, hit.Victim, result.Weapon.Class.Name, hit.Part = BodyHead))
            // Already launched above, and nothing resolves this tick.
            | Paintball | FoamDart | Nail | Harpoon | Bow | WaterJet -> ()
            | Hitscan ->
              let hitSoldiers, hitEvents =
                  Ballistics.applyShotFiltered (fun soldier -> soldier.Team = Axis) origin direction shot.Damage shot.Penetration shot.HeadshotMultiplier shot.Kind world.Level soldiers
              let hitIds =
                  hitEvents
                  |> List.choose (function HitConfirmed(victim, _) -> Some victim | _ -> None)
                  |> Set.ofList
              soldiers <-
                  hitSoldiers
                  |> Array.map (fun soldier ->
                      if soldier.Team = Axis && soldier.IsAlive && not (Set.contains soldier.Id hitIds) then
                          let offset = soldier.Position + Vector3(0.0f, 1.0f, 0.0f) - origin
                          let alongRay = max 0.0f (Vector3.Dot(offset, direction))
                          let nearMissDistance = Vector3.Distance(origin + direction * alongRay, soldier.Position + Vector3(0.0f, 1.0f, 0.0f))
                          let heard = Vector3.Distance(origin, soldier.Position) < 75.0f
                          let suppression = if nearMissDistance < 1.0f then min 3.0f (soldier.Suppression + 1.0f) else soldier.Suppression
                          { soldier with
                              Suppression = suppression
                              Behavior = if suppression >= 2.0f then Suppressed(Units.seconds 1.5f) else soldier.Behavior
                              Contacts = if heard then Map.add armedPlayer.Id (struct (armedPlayer.Position, Units.seconds 0.0f)) soldier.Contacts else soldier.Contacts }
                      elif soldier.Team = Axis && soldier.IsAlive then
                          { soldier with
                              Suppression = 3.0f
                              Behavior = Suppressed(Units.seconds 1.5f)
                              Contacts = Map.add armedPlayer.Id (struct (armedPlayer.Position, Units.seconds 0.0f)) soldier.Contacts }
                      else soldier)
              shotEvents.AddRange hitEvents
              for event in hitEvents do
                  match event with
                  | HitConfirmed(victim, true) ->
                      let headshot =
                          hitSoldiers
                          |> Array.tryFind (fun soldier -> soldier.Id = victim)
                          |> Option.exists (fun soldier -> match soldier.Behavior with DyingHeadshot _ -> true | _ -> false)
                      shotEvents.Add(Kill(Some armedPlayer.Id, victim, result.Weapon.Class.Name, headshot))
                  | _ -> ()
        let projectiles = Array.append world.SpecialProjectiles (spawnedProjectiles.ToArray())
        let activeSpecial, persistentMarks, specialPlayer, specialSoldiers, projectileStatus, specialEvents =
            SpecialProjectiles.stepWithStatus Tuning.TickDuration world.Level projectilePlayer soldiers projectiles world.PersistentMarks elementalStatus
        let grenades = match result.Thrown with Some grenade -> Array.append world.Grenades [| grenade |] | None -> world.Grenades
        let activeGrenades, explosions = Grenades.stepProjectiles Tuning.TickDuration world.Level grenades
        let explodedSoldiers, explosionEvents = Grenades.applyExplosions world.Level explosions specialSoldiers
        let player, playerExplosionEvents = Grenades.applyExplosionsToPlayer world.Level explosions specialPlayer
        let elementalPlayer, elementalSoldiers, nextElementalStatus, elementalEvents =
            SpecialProjectiles.stepElemental Tuning.TickDuration player explodedSoldiers projectileStatus
        let aiPlayer, aiSoldiers, aiEvents = AiBrain.step Tuning.TickDuration &rng world.Level world.Squads elementalPlayer elementalSoldiers
        let footstepEvents = result.FootStep |> Option.toList
        let objectives, objectiveEvents =
            if world.Round.IsNone && world.Objectives.Length > 0 && not world.Objectives[0].Done
               && (aiSoldiers |> Array.filter (fun soldier -> soldier.Team = Axis) |> Array.forall (fun soldier -> soldier.IsDead)) then
                let updated = Array.copy world.Objectives
                updated[0] <- { updated[0] with Done = true }
                updated, [ ObjectiveUpdated 0 ]
            else world.Objectives, []
        let squads =
            aiSoldiers
            |> Array.groupBy (fun soldier -> soldier.Squad)
            |> Array.map (fun (squad, members) ->
                let contacts = members |> Array.fold (fun known soldier -> Map.fold (fun map id value -> Map.add id value map) known soldier.Contacts) Map.empty
                let objective = if members |> Array.exists (fun soldier -> soldier.Team = Axis) then aiPlayer.Position else Vector3.Zero
                let suppressor =
                    members
                    |> Array.filter (fun soldier ->
                        soldier.IsAlive
                        && (soldier.Weapon.Class.Mode = FullAuto || match soldier.Behavior with InCover _ -> true | _ -> false))
                    |> Array.sortBy (fun soldier -> Vector3.DistanceSquared(soldier.Position, objective))
                    |> Array.tryHead
                    |> Option.map (fun soldier -> soldier.Id)
                squad, { Contacts = contacts; Objective = objective; Suppressor = suppressor })
            |> Map.ofArray
        let events = List.concat [ List.ofSeq shotEvents; specialEvents; explosionEvents; playerExplosionEvents; elementalEvents; aiEvents; footstepEvents; objectiveEvents ]
        let updated =
            { world with
                Tick = world.Tick + 1L
                Rng = rng
                Player = aiPlayer
                Soldiers = aiSoldiers
                Grenades = activeGrenades
                SpecialProjectiles = activeSpecial
                PersistentMarks = persistentMarks
                ElementalStatus = nextElementalStatus
                Dismemberments = dismemberments
                Objectives = objectives
                Squads = squads
                Script = { world.Script with MissionTime = world.Script.MissionTime + Tuning.TickDuration } }
        let scripted, scriptEvents = Script.step updated
        let rounded, roundEvents = advanceRound scripted (events @ scriptEvents)
        struct (rounded, roundEvents)

    /// Public so tests and tools can build a world for any compiled level.
    let createWorld level objective seed =
        let playerSpawn =
            level.Spawns
            |> Array.pick (fun struct (team, position) -> if team = Some Allies then Some position else None)
        let mutable colorRng = Rng.create seed
        let paintColor = SpecialProjectiles.choosePaintColor &colorRng
        let player =
            { Id = EntityId 1
              Position = playerSpawn
              Velocity = Vector3.Zero
              Yaw = 0.0f
              Pitch = 0.0f
              Stance = Standing
              Sprinting = false
              Ads = 0.0f
              Health = Units.health 100.0f
              RegenIn = Units.seconds 0.0f
              Slots = playerSlots ()
              Active = 0
              Grenade = GrenadeIdle 3 }
        let infantry =
            level.Spawns
            |> Array.mapi (fun index struct (team, position) -> index, team, position)
            |> Array.choose (fun (index, team, position) ->
                match team with
                | Some Allies when index = 0 -> None
                | Some soldierTeam ->
                    let baseWeapon = Tuning.weaponSlot (if soldierTeam = Allies then Tuning.thompson else Tuning.kar98k) 4
                    let staggeredWeapon =
                        if soldierTeam = Axis then
                            { baseWeapon with State = Cooling(Units.seconds (0.35f + float32 (index % 90) / 60.0f)) }
                        else baseWeapon
                    Some
                        { Id = EntityId(100 + index)
                          Team = soldierTeam
                          Position = position
                          Facing = if soldierTeam = Allies then 0.0f else MathF.PI
                          Stance = Standing
                          Health = Units.health 100.0f
                          Behavior = Idle
                          Weapon = staggeredWeapon
                          Squad = if soldierTeam = Allies then 1 else 2
                          Contacts = Map.empty
                          Suppression = 0.0f
                          AnimPhase = 0.0f }
                | None -> None)
        let nextMountedId =
            infantry
            |> Array.map (fun soldier -> let (EntityId id) = soldier.Id in id)
            |> fun ids -> if ids.Length = 0 then 1000 else Array.max ids + 1
        let mountedCrews =
            level.MountedGuns
            |> Array.mapi (fun index emplacement ->
                { Id = EntityId(nextMountedId + index)
                  Team = emplacement.Team
                  Position = emplacement.Position
                  Facing = emplacement.Facing
                  Stance = Crouched
                  Health = Units.health 100.0f
                  Behavior =
                    InCover(
                        { Pos = emplacement.Position
                          PeekDir = MathEx.yawForward emplacement.Facing
                          Crouch = true
                          Owner = Some emplacement.Team },
                        Units.seconds 1.1f)
                  Weapon = { Tuning.weaponSlot Tuning.mg42 5 with State = Cooling(Units.seconds (0.8f + float32 index * 0.15f)) }
                  Squad = if emplacement.Team = Allies then 3 else 4
                  Contacts = Map.empty
                  Suppression = 0.0f
                  AnimPhase = 0.0f })
        let soldiers = Array.append infantry mountedCrews
        { Tick = 0L
          Rng = colorRng
          Player = player
          Soldiers = soldiers
          Grenades = [||]
          SpecialProjectiles = [||]
          PersistentMarks = [||]
          ElementalStatus = Map.empty
          Dismemberments = Map.empty
          PaintColor = paintColor
          Level = level
          Squads = Map.empty
          Script = { MissionTime = Units.seconds 0.0f; Ended = false; Rules = level.MissionRules }
          Objectives = [| { Text = objective; Done = false } |]
          Round = None }

    let createTrainingWorld seed = createWorld Levels.trainingYard "Clear the training yard" seed

    /// Round-based paintball world: only the first `bots` Axis soldiers stay,
    /// re-armed with a rifle/SMG mix, and the round loop drives resets.
    let private createRoundWorld level bots seed =
        let world = createWorld level "Win the round" seed
        let soldiers =
            world.Soldiers
            |> Array.filter (fun soldier -> soldier.Team = Axis)
            |> Array.truncate bots
            |> Array.mapi (fun index soldier ->
                let weaponClass = if index % 3 = 1 then Tuning.thompson else Tuning.kar98k
                { soldier with
                    Weapon =
                        { Tuning.weaponSlot weaponClass 4 with
                            State = staggeredReady index } })
        { world with
            Player = { world.Player with Active = thompsonSlot }
            Soldiers = soldiers
            Round =
                Some
                    { Number = 1
                      PlayerScore = 0
                      EnemyScore = 0
                      ResetIn = None
                      LastResult = None } }

    /// Round-mode world for an arbitrary level (downloaded or custom map).
    let createRoundWorldFor (level: Level) seed =
        let axisSpawns =
            level.Spawns |> Array.filter (fun struct (team, _) -> team = Some Axis) |> Array.length
        createRoundWorld level (max 1 (min 5 axisSpawns)) seed

    let createPaintballWorld seed = createRoundWorld Levels.paintballArena 4 seed

    let createScrapDepotWorld seed = createRoundWorld Levels.scrapDepot 5 seed

    let createCanalYardWorld seed = createRoundWorld Levels.canalYard 5 seed

    let createOmahaWorld seed = createRoundWorld Levels.omahaDraw 5 seed

    /// Offline bot round for a map alias (Levels.offlineAliases / argv);
    /// unknown aliases fall back to the paintball arena.
    let createOfflineWorld (alias: string) seed =
        match alias with
        // Only the two maps that set their own bot count are named here. Every
        // other alias resolves through the level registry and takes one bot per
        // Axis spawn, so a map added to the menu needs no entry in this file —
        // a second alias table here is how Rust silently loaded the paintball
        // arena instead.
        | "training" -> createTrainingWorld seed
        | "paintball" -> createPaintballWorld seed
        | alias ->
            Levels.byAlias alias
            |> Option.map (fun level -> createRoundWorldFor level seed)
            |> Option.defaultWith (fun () -> createPaintballWorld seed)

