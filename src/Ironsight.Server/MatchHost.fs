namespace Ironsight.Server

open System
open System.Numerics
open System.Text.Json
open Ironsight

type MatchHost(mode: GameMode, ?matchLevel: Level) =
    let gate = obj ()
    let level = defaultArg matchLevel (Sim.createTrainingWorld 0xF5A4D3UL).Level
    let mutable nextPlayerId = 1
    let mutable state = { Multiplayer.create mode with LevelName = level.Name }
    let mutable pendingInputs: Map<EntityId, struct (InputFrame * int64)> = Map.empty
    let mutable positionHistory: Map<int64, Map<EntityId, Vector3>> = Map.empty
    let mutable sessionOwners: Map<string, EntityId> = Map.empty
    let mutable disconnectedSince: Map<EntityId, DateTimeOffset> = Map.empty

    let clamp (minimum: float32) (maximum: float32) (value: float32) = Math.Clamp(value, minimum, maximum)
    let hasButton button (buttons: InputButtons) = buttons.HasFlag button
    let selectedWeapon team weaponName =
        weaponName
        |> Option.bind Tuning.weaponByName
        |> Option.defaultValue (if team = Allies then Tuning.thompson else Tuning.kar98k)

    let toPlayer (networkPlayer: NetworkPlayer) =
        { Id = networkPlayer.Id
          Position = networkPlayer.Position
          Velocity = networkPlayer.Velocity
          Yaw = networkPlayer.Yaw
          Pitch = networkPlayer.Pitch
          Stance = networkPlayer.Stance
          Sprinting = networkPlayer.Sprinting
          Ads = networkPlayer.Ads
          Health = networkPlayer.Health
          RegenIn = networkPlayer.RegenIn
          Slots = [| networkPlayer.Weapon |]
          Active = 0
          Grenade = networkPlayer.Grenade }

    let spawnFor team (EntityId id) tick =
        let candidates =
            level.Spawns
            |> Array.choose (fun struct (owner, position) -> if owner = Some team then Some position else None)
        if candidates.Length = 0 then Vector3.Zero
        else
            let enemies =
                state.Players
                |> Map.toArray
                |> Array.map snd
                |> Array.filter (fun player -> player.Connected && player.Alive && (mode = FreeForAll || player.Team <> team))
            let safe =
                candidates
                |> Array.filter (fun candidate ->
                    enemies
                    |> Array.forall (fun enemy ->
                        not (Ballistics.lineOfSight (candidate + Vector3(0.0f, 1.0f, 0.0f)) (enemy.Position + Vector3(0.0f, 1.0f, 0.0f)) level)))
            let choices = if safe.Length > 0 then safe else candidates
            choices[(int tick + id) % choices.Length]

    let asSoldier (player: NetworkPlayer) =
        { Id = player.Id
          Team = player.Team
          Position = player.Position
          Facing = player.Yaw
          Health = player.Health
          Behavior = if player.Alive then Idle else Dying(Units.seconds 0.0f)
          Weapon = player.Weapon
          Squad = 0
          Contacts = Map.empty
          Suppression = 0.0f
          AnimPhase = 0.0f }

    member _.TryAddPlayer(name: string, ?weaponName: string, ?sessionToken: string) =
        lock gate (fun () ->
            let resumed =
                sessionToken
                |> Option.bind (fun token -> Map.tryFind token sessionOwners |> Option.map (fun id -> token, id))
                |> Option.bind (fun (token, id) ->
                    match Map.tryFind id disconnectedSince, Map.tryFind id state.Players with
                    | Some since, Some player when DateTimeOffset.UtcNow - since <= TimeSpan.FromSeconds 30.0 ->
                        let selected = selectedWeapon player.Team weaponName
                        let weapon =
                            if player.Weapon.Class.Name = selected.Name then player.Weapon
                            else Tuning.weaponSlot selected 4
                        let restored =
                            { player with
                                Connected = true
                                Name = Multiplayer.sanitizeName name
                                Weapon = weapon }
                        state <- { state with Players = Map.add id restored state.Players }
                        disconnectedSince <- Map.remove id disconnectedSince
                        Some(id, token)
                    | _ -> None)
            match resumed with
            | Some value -> Some value
            | None when state.Players.Count >= 16 -> None
            | None ->
                let id = EntityId nextPlayerId
                nextPlayerId <- nextPlayerId + 1
                let team = if state.Players.Count % 2 = 0 then Allies else Axis
                let spawn = spawnFor team id state.Tick
                let player =
                    { Id = id
                      Name = Multiplayer.sanitizeName name
                      Team = team
                      Position = spawn
                      Velocity = Vector3.Zero
                      Yaw = if team = Allies then 0.0f else MathF.PI
                      Pitch = 0.0f
                      Stance = Standing
                      Sprinting = false
                      Ads = 0.0f
                      Health = Units.health 100.0f
                      RegenIn = Units.seconds 0.0f
                      Weapon = Tuning.weaponSlot (selectedWeapon team weaponName) 4
                      Grenade = GrenadeIdle 3
                      Connected = true
                      Ready = false
                      Alive = true
                      RespawnIn = Units.seconds 0.0f
                      SpawnProtection = Units.seconds 2.0f
                      Kills = 0
                      Deaths = 0
                      LastInputSequence = -1L }
                let token = Convert.ToHexString(Guid.NewGuid().ToByteArray())
                state <- { state with Players = Map.add id player state.Players }
                sessionOwners <- Map.add token id sessionOwners
                Some(id, token))

    member _.RemovePlayer id =
        lock gate (fun () ->
            match Map.tryFind id state.Players with
            | Some player ->
                state <- { state with Players = Map.add id { player with Connected = false; Ready = false } state.Players }
                disconnectedSince <- Map.add id DateTimeOffset.UtcNow disconnectedSince
                pendingInputs <- Map.remove id pendingInputs
            | None -> ())

    member _.SetReady id =
        lock gate (fun () ->
            match Map.tryFind id state.Players with
            | Some player -> state <- { state with Players = Map.add id { player with Ready = true } state.Players }
            | None -> ())

    member _.ApplyInput(id, message: JsonElement) =
        lock gate (fun () ->
            match Map.tryFind id state.Players, Protocol.tryInt64 "sequence" message with
            | Some player, Some sequence when player.Alive && sequence > player.LastInputSequence && sequence <= player.LastInputSequence + 120L ->
                let moveX = Protocol.tryFloat32 "moveX" message |> Option.defaultValue 0.0f |> clamp -1.0f 1.0f
                let moveY = Protocol.tryFloat32 "moveY" message |> Option.defaultValue 0.0f |> clamp -1.0f 1.0f
                let lookX = Protocol.tryFloat32 "lookX" message |> Option.defaultValue 0.0f |> clamp -0.25f 0.25f
                let lookY = Protocol.tryFloat32 "lookY" message |> Option.defaultValue 0.0f |> clamp -0.25f 0.25f
                let rawButtons = Protocol.tryInt64 "buttons" message |> Option.defaultValue 0L
                let allowedButtons = int64 (InputButtons.Fire ||| InputButtons.Ads ||| InputButtons.Sprint ||| InputButtons.Reload ||| InputButtons.Crouch ||| InputButtons.Prone ||| InputButtons.Jump ||| InputButtons.Grenade)
                let buttons = enum<InputButtons> (int (rawButtons &&& allowedButtons))
                let input = { Sequence = sequence; Move = Vector2(moveX, moveY); Look = Vector2(lookX, lookY); Buttons = buttons }
                let requestedTick = Protocol.tryInt64 "estimatedServerTick" message |> Option.defaultValue state.Tick
                let estimatedTick = Math.Clamp(requestedTick, state.Tick - 12L, state.Tick)
                pendingInputs <- Map.add id (struct (input, estimatedTick)) pendingInputs
            | _ -> ())

    member _.AdvanceTick() =
        lock gate (fun () ->
            let expired =
                disconnectedSince
                |> Map.toArray
                |> Array.choose (fun (id, since) -> if DateTimeOffset.UtcNow - since > TimeSpan.FromSeconds 30.0 then Some id else None)
            for id in expired do
                state <- { state with Players = Map.remove id state.Players }
                disconnectedSince <- Map.remove id disconnectedSince
                sessionOwners <- sessionOwners |> Map.filter (fun _ owner -> owner <> id)
            let readyPlayers = state.Players |> Map.toSeq |> Seq.filter (fun (_, player) -> player.Connected && player.Ready) |> Seq.length
            let lifecycleState =
                match state.Phase with
                | Waiting when readyPlayers >= 2 -> { state with Phase = Warmup; PhaseRemaining = Units.seconds 10.0f }
                | Waiting -> state
                | Warmup when state.PhaseRemaining <= Tuning.TickDuration ->
                    let resetPlayers =
                        state.Players
                        |> Map.map (fun id player ->
                            { player with
                                Position = spawnFor player.Team id state.Tick
                                Velocity = Vector3.Zero
                                Health = Units.health 100.0f
                                RegenIn = Units.seconds 0.0f
                                Weapon = Tuning.weaponSlot player.Weapon.Class 4
                                Grenade = GrenadeIdle 3
                                Alive = true
                                RespawnIn = Units.seconds 0.0f
                                SpawnProtection = Units.seconds 2.0f })
                    { state with Phase = Playing; PhaseRemaining = state.TimeLimit; Players = resetPlayers; Grenades = [||] }
                | Warmup -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
                | Playing when state.PhaseRemaining <= Tuning.TickDuration || Multiplayer.hasWinner state ->
                    { state with Phase = Results; PhaseRemaining = Units.seconds 10.0f }
                | Playing -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
                | Results when state.PhaseRemaining <= Tuning.TickDuration ->
                    let resetPlayers =
                        state.Players
                        |> Map.map (fun id player ->
                            { player with
                                Position = spawnFor player.Team id state.Tick
                                Velocity = Vector3.Zero
                                Health = Units.health 100.0f
                                Weapon = Tuning.weaponSlot player.Weapon.Class 4
                                Alive = true
                                Kills = 0
                                Deaths = 0
                                SpawnProtection = Units.seconds 2.0f })
                    { state with Phase = Warmup; PhaseRemaining = Units.seconds 10.0f; Players = resetPlayers; Grenades = [||]; AlliesScore = 0; AxisScore = 0 }
                | Results -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
            let mutable rng = state.Rng
            let shots = ResizeArray<EntityId * Vector3 * Vector3 * float32<hp> * float32 * int64 * bool>()
            let thrownGrenades = ResizeArray<Grenade>()
            let emitted = ResizeArray<struct (EntityId option * GameEvent)>()
            let emit event = emitted.Add(struct (None, event))
            let emitOnly recipient event = emitted.Add(struct (Some recipient, event))
            let respawnedPlayers =
                lifecycleState.Players
                |> Map.map (fun id player ->
                    if player.Alive then
                        let regenerated = Damage.stepRegen Tuning.TickDuration (toPlayer player)
                        { player with
                            Health = regenerated.Health
                            RegenIn = regenerated.RegenIn
                            SpawnProtection = max (Units.seconds 0.0f) (player.SpawnProtection - Tuning.TickDuration) }
                    else
                        let remaining = player.RespawnIn - Tuning.TickDuration
                        if remaining <= Units.seconds 0.0f then
                            { player with
                                Position = spawnFor player.Team id lifecycleState.Tick
                                Velocity = Vector3.Zero
                                Health = Units.health 100.0f
                                RegenIn = Units.seconds 0.0f
                                Weapon = Tuning.weaponSlot player.Weapon.Class 4
                                Grenade = GrenadeIdle 3
                                Alive = true
                                RespawnIn = Units.seconds 0.0f
                                SpawnProtection = Units.seconds 2.0f }
                        else { player with RespawnIn = remaining })
            let movedPlayers =
                pendingInputs
                |> Map.fold (fun players id struct (input, estimatedTick) ->
                    match Map.tryFind id players with
                    | Some player when player.Alive && input.Sequence > player.LastInputSequence ->
                        let moved = Movement.step Tuning.TickDuration input level (toPlayer player)
                        let fire = hasButton InputButtons.Fire input.Buttons && not moved.Sprinting
                        let reload = hasButton InputButtons.Reload input.Buttons
                        let struct (weapon, requests) = Weapons.step Tuning.TickDuration fire reload moved.Ads &rng moved.Slots[0]
                        let grenadeHeld = hasButton InputButtons.Grenade input.Buttons && not moved.Sprinting
                        let handPlayer, thrown = Grenades.stepHand Tuning.TickDuration grenadeHeld moved
                        thrown |> Option.iter thrownGrenades.Add
                        let horizontalSpeed = MathEx.horizontal handPlayer.Velocity |> fun velocity -> velocity.Length()
                        let footstepInterval = if handPlayer.Sprinting then 18L else 26L
                        if horizontalSpeed > 1.0f && handPlayer.Position.Y <= 0.06f && lifecycleState.Tick % footstepInterval = 0L then
                            emit (FootStep(handPlayer.Position, Mud))
                        requests
                        |> List.iteri (fun index request ->
                            let origin = Ballistics.playerMuzzleOrigin handPlayer weapon.Class.Name
                            let direction = Ballistics.directionFromAngles moved.Yaw moved.Pitch request.DirectionOffset
                            shots.Add(id, origin, direction, request.Damage, request.Penetration, estimatedTick, (index = 0)))
                        let updated =
                            { player with
                                Position = handPlayer.Position
                                Velocity = handPlayer.Velocity
                                Yaw = handPlayer.Yaw
                                Pitch = handPlayer.Pitch
                                Stance = handPlayer.Stance
                                Sprinting = handPlayer.Sprinting
                                Ads = handPlayer.Ads
                                Weapon = weapon
                                Grenade = handPlayer.Grenade
                                SpawnProtection = if requests.IsEmpty && thrown.IsNone then player.SpawnProtection else Units.seconds 0.0f
                                LastInputSequence = input.Sequence }
                        Map.add id updated players
                    | _ -> players) respawnedPlayers
            pendingInputs <- Map.empty
            let mutable combatState = { lifecycleState with Players = movedPlayers; Rng = rng }
            let authoritativeShots = if lifecycleState.Phase = Playing then shots :> seq<_> else Seq.empty
            for shooterId, origin, direction, damage, penetration, estimatedTick, isFirstPellet in authoritativeShots do
                match Map.tryFind shooterId combatState.Players with
                | Some shooter when shooter.Alive ->
                    if isFirstPellet then emit (ShotFired(Some shooterId, origin, direction, shooter.Weapon.Class.Name))
                    let targets = combatState.Players |> Map.toArray
                    let historical = positionHistory |> Map.tryFind estimatedTick |> Option.defaultValue Map.empty
                    let soldiers =
                        targets
                        |> Array.map (fun (id, player) ->
                            let soldier = asSoldier player
                            match Map.tryFind id historical with
                            | Some position -> { soldier with Position = position }
                            | None -> soldier)
                    let canHit (candidate: Soldier) =
                        match Map.tryFind candidate.Id combatState.Players with
                        | Some target ->
                            target.Alive && target.SpawnProtection <= Units.seconds 0.0f
                            && Multiplayer.areHostile combatState.Mode shooter target
                        | None -> false
                    let hitSoldiers, hitEvents = Ballistics.applyShotFiltered canHit origin direction damage penetration level soldiers
                    for event in hitEvents do
                        match event with
                        | HitConfirmed _ -> emitOnly shooterId event
                        | _ -> emit event
                    for index in 0..targets.Length - 1 do
                        let targetId, before = targets[index]
                        let after = hitSoldiers[index]
                        if after.Health < before.Health then
                            let damaged = { before with Health = after.Health; RegenIn = Tuning.RegenDelay }
                            combatState <- { combatState with Players = Map.add targetId damaged combatState.Players }
                            if after.Health <= Units.health 0.0f then
                                combatState <- Multiplayer.recordKill shooterId targetId combatState
                | _ -> ()
            let grenadeSet = Array.append lifecycleState.Grenades (thrownGrenades.ToArray())
            let activeGrenades, explosions =
                if lifecycleState.Phase = Playing then Grenades.stepProjectilesOwned Tuning.TickDuration level grenadeSet
                else [||], [||]
            combatState <- { combatState with Grenades = activeGrenades }
            for struct (ownerId, position) in explosions do
                emit (Explosion(position, 6.0f))
                let targets = combatState.Players |> Map.toArray
                for targetId, target in targets do
                    let canDamage =
                        target.Alive && target.SpawnProtection <= Units.seconds 0.0f
                        && match Map.tryFind ownerId combatState.Players with
                           | Some owner -> targetId = ownerId || Multiplayer.areHostile combatState.Mode owner target
                           | None -> false
                    let torso = target.Position + Vector3(0.0f, 1.0f, 0.0f)
                    let distance = Vector3.Distance(position, torso)
                    if canDamage && distance < 6.0f && Ballistics.lineOfSight position torso level then
                        let damage = Units.health (110.0f * (1.0f - distance / 6.0f) ** 1.5f)
                        let health = max (Units.health 0.0f) (target.Health - damage)
                        combatState <-
                            { combatState with
                                Players = Map.add targetId { target with Health = health; RegenIn = Tuning.RegenDelay } combatState.Players }
                        if health <= Units.health 0.0f then
                            if ownerId <> targetId then combatState <- Multiplayer.recordKill ownerId targetId combatState
                            else
                                let victim = combatState.Players[targetId]
                                combatState <-
                                    { combatState with
                                        Players =
                                            Map.add targetId
                                                { victim with
                                                    Alive = false
                                                    Deaths = victim.Deaths + 1
                                                    RespawnIn = Units.seconds 5.0f
                                                    Velocity = Vector3.Zero }
                                                combatState.Players }
            let finalState =
                if combatState.Phase = Playing && Multiplayer.hasWinner combatState then
                    { combatState with Phase = Results; PhaseRemaining = Units.seconds 10.0f }
                else combatState
            let nextTick = state.Tick + 1L
            let newEvents =
                emitted
                |> Seq.mapi (fun index struct (recipient, event) ->
                    { Id = finalState.NextEventId + int64 index; Tick = nextTick; Recipient = recipient; Event = event })
                |> Seq.toList
            let retained = finalState.Events |> List.filter (fun event -> event.Tick >= nextTick - 12L)
            state <-
                { finalState with
                    Tick = nextTick
                    Rng = rng
                    Events = retained @ newEvents
                    NextEventId = finalState.NextEventId + int64 newEvents.Length }
            let positions = state.Players |> Map.map (fun _ player -> player.Position)
            positionHistory <-
                positionHistory
                |> Map.add nextTick positions
                |> Map.filter (fun tick _ -> tick >= nextTick - 12L))

    member _.Snapshot() = lock gate (fun () -> state)
