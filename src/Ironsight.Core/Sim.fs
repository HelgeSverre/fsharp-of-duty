namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Sim =
    let private playerSlots () =
        [| Tuning.weaponSlot Tuning.kar98k 6
           Tuning.weaponSlot Tuning.m1Garand 5
           Tuning.weaponSlot Tuning.leeEnfield 5
           Tuning.weaponSlot Tuning.thompson 4
           Tuning.weaponSlot Tuning.stg44 4
           Tuning.weaponSlot Tuning.mp40 4
           Tuning.weaponSlot Tuning.m1911 5
           Tuning.weaponSlot Tuning.kar98kSniper 5
           Tuning.weaponSlot Tuning.fg42 4
           Tuning.weaponSlot Tuning.m1897 5
           Tuning.weaponSlot Tuning.bar 5
           Tuning.weaponSlot Tuning.luger 5 |]

    /// CS-style category slots: each weapon key owns a category and pressing
    /// the same key again cycles within it. Indices refer to playerSlots order.
    let weaponCategories =
        [| [| 0; 1; 2 |]     // 1: bolt/semi rifles — Kar98k, Garand, Lee-Enfield
           [| 3; 4; 5 |]     // 2: automatics — Thompson, STG-44, MP40
           [| 6; 11 |]       // 3: pistols — M1911, Luger P08
           [| 7; 8 |]        // 4: scoped — Kar98k Sniper, FG42
           [| 9; 10 |] |]    // 5: heavy — Trench Gun, BAR

    /// The paintball round loadout opens on the Thompson.
    let private thompsonSlot = 3

    /// Staggered post-respawn cooldown for round-mode bots, so a whole squad
    /// doesn't open fire on the exact same tick. Distinct from the MG-crew and
    /// battlefield-infantry stagger constants below, which model a different
    /// pace and must not be merged with this one.
    let private staggeredReady index = Cooling(Units.seconds (0.18f + float32 index * 0.12f))

    let private resetRoundCombatants (world: World) round =
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
                    Active = thompsonSlot
                    Grenade = GrenadeIdle 3 }
            Soldiers = soldiers
            Grenades = [||]
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
        let fire = stepWeapon && canFire && Input.hasButton InputButtons.Fire input.Buttons && not moved.Sprinting
        let reload = Input.hasButton InputButtons.Reload input.Buttons
        let moveSpeed = MathEx.horizontalSpeed moved.Velocity
        let weapon, shots =
            if stepWeapon then
                let struct (weapon, shots) = Weapons.step dt moveSpeed moved.Stance fire reload moved.Ads &rng active
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

    let step (input: InputFrame) (world: World) =
        let regenerated = Damage.stepRegen Tuning.TickDuration world.Player
        let requestedCategory =
            [| InputButtons.Weapon1; InputButtons.Weapon2; InputButtons.Weapon3; InputButtons.Weapon4; InputButtons.Weapon5 |]
            |> Array.tryFindIndex (fun button -> Input.hasButton button input.Buttons)
        // A key selects its category; pressing it again cycles within the
        // category. Holding the key is rate-limited naturally by the 0.35 s
        // Switching state, which ignores requests while in flight.
        let requestedWeapon =
            requestedCategory
            |> Option.map (fun category ->
                let members = weaponCategories[category]
                match Array.tryFindIndex ((=) regenerated.Active) members with
                | Some position -> members[(position + 1) % members.Length]
                | None -> members[0])
        let prepared, weaponLocked =
            match regenerated.Slots[regenerated.Active].State with
            | Switching(incoming, remaining) when remaining <= Tuning.TickDuration ->
                { withActiveState Ready regenerated with Active = incoming }, false
            | Switching(incoming, remaining) ->
                withActiveState (Switching(incoming, remaining - Tuning.TickDuration)) regenerated, true
            | _ ->
                match requestedWeapon with
                | Some incoming when incoming >= 0 && incoming < regenerated.Slots.Length && incoming <> regenerated.Active ->
                    withActiveState (Switching(incoming, Units.seconds 0.35f)) regenerated, true
                | _ -> regenerated, false
        let mutable rng = world.Rng
        let canFire = prepared.IsAlive
        let result = stepLocomotion Tuning.TickDuration world.Level world.Tick input (not weaponLocked) canFire true prepared &rng
        let slots = Array.copy result.Player.Slots
        slots[result.Player.Active] <- result.Weapon
        let armedPlayer = { result.Player with Slots = slots }
        let shotEvents = ResizeArray<GameEvent>()
        let mutable soldiers = world.Soldiers
        if not result.Shots.IsEmpty then
            // Tracer starts at the muzzle; the hit trace below leaves the eye.
            let origin = Ballistics.playerMuzzleOrigin armedPlayer result.Weapon.Class
            let struct (_, direction, _) = List.head result.Shots
            shotEvents.Add(ShotFired(Some armedPlayer.Id, origin, direction, result.Weapon.Class.Name))
        for struct (origin, direction, shot) in result.Shots do
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
                        // Direct hits flinch and duck living soldiers. A lethal
                        // hit already carries its Dying/DyingHeadshot behaviour;
                        // overwriting it with Suppressed would let a corpse
                        // stand back up and keep walking.
                        { soldier with
                            Suppression = 3.0f
                            Behavior = Suppressed(Units.seconds 1.5f)
                            Contacts = Map.add armedPlayer.Id (struct (armedPlayer.Position, Units.seconds 0.0f)) soldier.Contacts }
                    else soldier)
            shotEvents.AddRange hitEvents
        let grenades = match result.Thrown with Some grenade -> Array.append world.Grenades [| grenade |] | None -> world.Grenades
        let activeGrenades, explosions = Grenades.stepProjectiles Tuning.TickDuration world.Level grenades
        let explodedSoldiers, explosionEvents = Grenades.applyExplosions world.Level explosions soldiers
        let player, playerExplosionEvents = Grenades.applyExplosionsToPlayer world.Level explosions armedPlayer
        let aiPlayer, aiSoldiers, aiEvents = AiBrain.step Tuning.TickDuration &rng world.Level world.Squads player explodedSoldiers
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
        let events = List.concat [ List.ofSeq shotEvents; explosionEvents; playerExplosionEvents; aiEvents; footstepEvents; objectiveEvents ]
        let updated =
            { world with
                Tick = world.Tick + 1L
                Rng = rng
                Player = aiPlayer
                Soldiers = aiSoldiers
                Grenades = activeGrenades
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
          Rng = Rng.create seed
          Player = player
          Soldiers = soldiers
          Grenades = [||]
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

