namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Sim =
    let private hasButton button (buttons: InputButtons) = buttons.HasFlag button

    let private playerSlots () =
        [| Tuning.weaponSlot Tuning.kar98k 6
           Tuning.weaponSlot Tuning.thompson 4
           Tuning.weaponSlot Tuning.m1911 5
           Tuning.weaponSlot Tuning.kar98kSniper 5
           Tuning.weaponSlot Tuning.m1897 5 |]

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
            |> Array.truncate 4
            |> Array.mapi (fun index soldier ->
                let position = if index < enemySpawns.Length then enemySpawns[index] else soldier.Position
                { soldier with
                    Position = position
                    Facing = MathF.PI
                    Health = Units.health 100.0f
                    Behavior = Idle
                    Weapon =
                        { Tuning.weaponSlot soldier.Weapon.Class 4 with
                            State = Cooling(Units.seconds (0.18f + float32 index * 0.12f)) }
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
                    Active = 1
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
                let playerDead = world.Player.Health <= Units.health 0.0f
                let enemiesDead =
                    world.Soldiers
                    |> Array.filter (fun soldier -> soldier.Team = Axis)
                    |> Array.forall (fun soldier -> soldier.Health <= Units.health 0.0f)
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

    let private surfaceBelow (level: Level) (position: Vector3) =
        LevelCompile.brushesNear position 0.5f level
        |> Array.filter (fun brush ->
            position.X >= brush.Bounds.Min.X && position.X <= brush.Bounds.Max.X
            && position.Z >= brush.Bounds.Min.Z && position.Z <= brush.Bounds.Max.Z
            && brush.Bounds.Max.Y <= position.Y + 0.12f)
        |> Array.sortByDescending (fun brush -> brush.Bounds.Max.Y)
        |> Array.tryHead
        |> Option.map (fun brush -> brush.Material)
        |> Option.defaultValue Mud

    let step (input: InputFrame) (world: World) =
        let moved = Movement.step Tuning.TickDuration input world.Level world.Player |> Damage.stepRegen Tuning.TickDuration
        let requestedWeapon =
            if hasButton InputButtons.Weapon1 input.Buttons then Some 0
            elif hasButton InputButtons.Weapon2 input.Buttons then Some 1
            elif hasButton InputButtons.Weapon3 input.Buttons then Some 2
            elif hasButton InputButtons.Weapon4 input.Buttons then Some 3
            elif hasButton InputButtons.Weapon5 input.Buttons then Some 4
            else None
        let prepared, weaponLocked =
            match moved.Slots[moved.Active].State with
            | Switching(incoming, remaining) when remaining <= Tuning.TickDuration ->
                let slots = Array.copy moved.Slots
                slots[moved.Active] <- { slots[moved.Active] with State = Ready }
                { moved with Active = incoming; Slots = slots; Ads = 0.0f }, false
            | Switching(incoming, remaining) ->
                let slots = Array.copy moved.Slots
                slots[moved.Active] <- { slots[moved.Active] with State = Switching(incoming, remaining - Tuning.TickDuration) }
                { moved with Slots = slots; Ads = 0.0f }, true
            | _ ->
                match requestedWeapon with
                | Some incoming when incoming >= 0 && incoming < moved.Slots.Length && incoming <> moved.Active ->
                    let slots = Array.copy moved.Slots
                    slots[moved.Active] <- { slots[moved.Active] with State = Switching(incoming, Units.seconds 0.35f) }
                    { moved with Slots = slots; Ads = 0.0f }, true
                | _ -> moved, false
        let active = prepared.Slots[prepared.Active]
        let fire =
            prepared.Health > Units.health 0.0f
            && hasButton InputButtons.Fire input.Buttons
            && not prepared.Sprinting
            && not weaponLocked
        let reload = hasButton InputButtons.Reload input.Buttons
        let mutable rng = world.Rng
        let moveSpeed = MathEx.horizontal prepared.Velocity |> fun velocity -> velocity.Length()
        let struct (weapon, shots) =
            if weaponLocked then struct (active, [])
            else Weapons.step Tuning.TickDuration moveSpeed fire reload prepared.Ads &rng active
        let slots = Array.copy prepared.Slots
        slots[prepared.Active] <- weapon
        let armedPlayer = { prepared with Slots = slots }
        let grenadeHeld = hasButton InputButtons.Grenade input.Buttons && not armedPlayer.Sprinting
        let playerWithHand, thrown = Grenades.stepHand Tuning.TickDuration grenadeHeld armedPlayer
        let shotEvents = ResizeArray<GameEvent>()
        let mutable soldiers = world.Soldiers
        if not shots.IsEmpty then
            // Tracer starts at the muzzle; the hit trace below leaves the eye.
            let origin = Ballistics.playerMuzzleOrigin playerWithHand weapon.Class
            let direction = shotDirection playerWithHand shots.Head
            shotEvents.Add(ShotFired(Some playerWithHand.Id, origin, direction, weapon.Class.Name))
        for shot in shots do
            let origin = Ballistics.playerEyeOrigin playerWithHand
            let direction = shotDirection playerWithHand shot
            let hitSoldiers, hitEvents =
                Ballistics.applyShotFiltered (fun soldier -> soldier.Team = Axis) origin direction shot.Damage shot.Penetration shot.HeadshotMultiplier world.Level soldiers
            let hitIds =
                hitEvents
                |> List.choose (function HitConfirmed(victim, _) -> Some victim | _ -> None)
                |> Set.ofList
            soldiers <-
                hitSoldiers
                |> Array.map (fun soldier ->
                    if soldier.Team = Axis && soldier.Health > Units.health 0.0f && not (Set.contains soldier.Id hitIds) then
                        let offset = soldier.Position + Vector3(0.0f, 1.0f, 0.0f) - origin
                        let alongRay = max 0.0f (Vector3.Dot(offset, direction))
                        let nearMissDistance = Vector3.Distance(origin + direction * alongRay, soldier.Position + Vector3(0.0f, 1.0f, 0.0f))
                        let heard = Vector3.Distance(origin, soldier.Position) < 75.0f
                        let suppression = if nearMissDistance < 1.0f then min 3.0f (soldier.Suppression + 1.0f) else soldier.Suppression
                        { soldier with
                            Suppression = suppression
                            Behavior = if suppression >= 2.0f then Suppressed(Units.seconds 1.5f) else soldier.Behavior
                            Contacts = if heard then Map.add playerWithHand.Id (struct (playerWithHand.Position, Units.seconds 0.0f)) soldier.Contacts else soldier.Contacts }
                    elif soldier.Team = Axis && soldier.Health > Units.health 0.0f then
                        // Direct hits flinch and duck living soldiers. A lethal
                        // hit already carries its Dying/DyingHeadshot behaviour;
                        // overwriting it with Suppressed would let a corpse
                        // stand back up and keep walking.
                        { soldier with
                            Suppression = 3.0f
                            Behavior = Suppressed(Units.seconds 1.5f)
                            Contacts = Map.add playerWithHand.Id (struct (playerWithHand.Position, Units.seconds 0.0f)) soldier.Contacts }
                    else soldier)
            shotEvents.AddRange hitEvents
        let grenades = match thrown with Some grenade -> Array.append world.Grenades [| grenade |] | None -> world.Grenades
        let activeGrenades, explosions = Grenades.stepProjectiles Tuning.TickDuration world.Level grenades
        let explodedSoldiers, explosionEvents = Grenades.applyExplosions world.Level explosions soldiers
        let player, playerExplosionEvents = Grenades.applyExplosionsToPlayer world.Level explosions playerWithHand
        let aiPlayer, aiSoldiers, aiEvents = AiBrain.step Tuning.TickDuration &rng world.Level world.Squads player explodedSoldiers
        let footstepEvents =
            let speed = MathEx.horizontal aiPlayer.Velocity |> fun velocity -> velocity.Length()
            let interval = if aiPlayer.Sprinting then 18L else 26L
            if speed > 1.0f && aiPlayer.Position.Y <= 0.06f && world.Tick % interval = 0L then
                [ FootStep(aiPlayer.Position, surfaceBelow world.Level aiPlayer.Position) ]
            else []
        let objectives, objectiveEvents =
            if world.Round.IsNone && world.Objectives.Length > 0 && not world.Objectives[0].Done
               && (aiSoldiers |> Array.filter (fun soldier -> soldier.Team = Axis) |> Array.forall (fun soldier -> soldier.Health <= Units.health 0.0f)) then
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
                        soldier.Health > Units.health 0.0f
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

    let private createWorld level objective seed =
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
              CrouchLatched = false
              CrouchPrevHeld = false
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

    let createPaintballWorld seed =
        let world = createWorld Levels.paintballArena "Win the round" seed
        let soldiers =
            world.Soldiers
            |> Array.filter (fun soldier -> soldier.Team = Axis)
            |> Array.truncate 4
            |> Array.mapi (fun index soldier ->
                let weaponClass = if index = 1 then Tuning.thompson else Tuning.kar98k
                { soldier with
                    Weapon =
                        { Tuning.weaponSlot weaponClass 4 with
                            State = Cooling(Units.seconds (0.18f + float32 index * 0.12f)) } })
        { world with
            Player = { world.Player with Active = 1 }
            Soldiers = soldiers
            Round =
                Some
                    { Number = 1
                      PlayerScore = 0
                      EnemyScore = 0
                      ResetIn = None
                      LastResult = None } }

    let createStalingradWorld seed =
        createWorld Levels.stalingradStreet "Clear the MG nest at the end of the street" seed

    let createBattlefieldWorld seed =
        createWorld Levels.battlefield "Break through the German defensive line" seed
