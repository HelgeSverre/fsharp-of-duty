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

    /// Replace one player's entry in the authoritative state.
    let setPlayer id player =
        state <- { state with Players = Map.add id player state.Players }

    /// Update a player if connected-known; a missing id is a no-op.
    let updatePlayer id update =
        Map.tryFind id state.Players |> Option.iter (update >> setPlayer id)
    // Per-player FIFO of unapplied inputs. Buffering (instead of keeping only
    // the newest frame) means a TCP burst no longer silently discards the
    // overwritten frames — dropped fire clicks and jumps were the main source
    // of client rubber-banding. Bounded so a flooder only overwrites himself.
    let mutable pendingInputs: Map<EntityId, struct (InputFrame * int64) list> = Map.empty
    // One credit accrues per tick (capped). Applying an input spends one, so a
    // client can never average more than one input per server tick; credits
    // banked during a network stall let a late burst catch up briefly.
    let mutable inputCredits: Map<EntityId, int> = Map.empty
    let mutable positionHistory: Map<int64, Map<EntityId, Vector3>> = Map.empty
    // Session tokens are a convenience for reconnecting after a disconnect, not
    // a security boundary: the server does no authentication and a token simply
    // resumes whichever player slot it was minted for.
    let mutable sessionOwners: Map<string, EntityId> = Map.empty
    let mutable disconnectedSince: Map<EntityId, DateTimeOffset> = Map.empty

    let clamp (minimum: float32) (maximum: float32) (value: float32) = Math.Clamp(value, minimum, maximum)
    let selectedWeapon team weaponName =
        weaponName
        |> Option.bind Tuning.weaponByName
        |> Option.defaultValue (Tuning.defaultWeapon team)

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

    let freshSpawn tick id (player: NetworkPlayer) =
        { player with
            Position = spawnFor player.Team id tick
            Velocity = Vector3.Zero
            Health = Units.health 100.0f
            RegenIn = Units.seconds 0.0f
            Weapon = Tuning.weaponSlot (defaultArg player.RequestedWeapon player.Weapon.Class) 4
            RequestedWeapon = None
            Grenade = GrenadeIdle 3
            Alive = true
            RespawnIn = Units.seconds 0.0f
            SpawnProtection = Units.seconds 2.0f }

    let asSoldier (player: NetworkPlayer) =
        { Id = player.Id
          Team = player.Team
          Position = player.Position
          Facing = player.Yaw
          Stance = player.Stance
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
                        setPlayer id restored
                        disconnectedSince <- Map.remove id disconnectedSince
                        Some(id, token)
                    | _ -> None)
            match resumed with
            | Some value -> Some value
            | None when state.Players.Count >= 16 -> None
            | None ->
                let id = EntityId nextPlayerId
                nextPlayerId <- nextPlayerId + 1
                // Balance by live headcount, not join parity: parity drifts as
                // soon as players leave, stacking one team.
                let team =
                    let count wanted = state.Players |> Map.filter (fun _ player -> player.Team = wanted) |> Map.count
                    if count Allies <= count Axis then Allies else Axis
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
                      RequestedWeapon = None
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
                setPlayer id player
                sessionOwners <- Map.add token id sessionOwners
                Some(id, token))

    member _.RemovePlayer id =
        lock gate (fun () ->
            match Map.tryFind id state.Players with
            | Some player ->
                setPlayer id { player with Connected = false; Ready = false }
                disconnectedSince <- Map.add id DateTimeOffset.UtcNow disconnectedSince
                pendingInputs <- Map.remove id pendingInputs
                inputCredits <- Map.remove id inputCredits
            | None -> ())

    member _.SetReady id =
        lock gate (fun () ->
            updatePlayer id (fun player -> { player with Ready = true }))

    /// Unrestricted mid-match loadout change. Outside live play it swaps the
    /// weapon on the spot; during a live round it arms on the next spawn so a
    /// firefight cannot be reset by re-rolling ammunition.
    member _.SetLoadout(id, weaponName: string) =
        lock gate (fun () ->
            match Tuning.weaponByName weaponName, Map.tryFind id state.Players with
            | Some weaponClass, Some player ->
                let updated =
                    if state.Phase <> Playing && player.Alive then
                        { player with Weapon = Tuning.weaponSlot weaponClass 4; RequestedWeapon = None }
                    else { player with RequestedWeapon = Some weaponClass }
                setPlayer id updated
            | _ -> ())

    member _.ApplyInput(id, message: JsonElement) =
        lock gate (fun () ->
            let queuedAfter player =
                Map.tryFind player pendingInputs
                |> Option.defaultValue []
                |> List.tryLast
                |> Option.map (fun struct (input, _) -> input.Sequence)
            match Map.tryFind id state.Players, Protocol.tryInt64 "sequence" message with
            // Any newer sequence is accepted. The old "+120" ceiling was meant as
            // anti-flood armour, but a stalled server (GC pause, host migration)
            // leaves the client numbering far ahead and the window can never
            // close again: the player's inputs stay rejected for the rest of the
            // match, even after a session resume. Replays are still blocked by
            // the lower bound, and flooding is capped by the input credits spent
            // in AdvanceTick plus the message rate limit.
            | Some player, Some sequence when sequence > defaultArg (queuedAfter id) player.LastInputSequence ->
                if player.Alive then
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
                    let queue = Map.tryFind id pendingInputs |> Option.defaultValue []
                    let bounded =
                        let appended = queue @ [ struct (input, estimatedTick) ]
                        // Oldest frames spill when a client outruns the buffer;
                        // they are never acknowledged, so reconciliation replays
                        // them client-side.
                        if appended.Length > 4 then List.skip (appended.Length - 4) appended else appended
                    pendingInputs <- Map.add id bounded pendingInputs
                else
                    // The client keeps numbering input while dead. Acknowledge
                    // those no-op frames so respawn does not see a false jump.
                    setPlayer id { player with LastInputSequence = sequence }
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
                pendingInputs <- Map.remove id pendingInputs
                inputCredits <- Map.remove id inputCredits
                sessionOwners <- sessionOwners |> Map.filter (fun _ owner -> owner <> id)
            let readyPlayers = state.Players |> Map.toSeq |> Seq.filter (fun (_, player) -> player.Connected && player.Ready) |> Seq.length
            let lifecycleState =
                match state.Phase with
                | Waiting when readyPlayers >= 2 -> { state with Phase = Warmup; PhaseRemaining = Units.seconds 10.0f }
                | Waiting -> state
                | Warmup when state.PhaseRemaining <= Tuning.TickDuration ->
                    let resetPlayers = state.Players |> Map.map (freshSpawn state.Tick)
                    { state with Phase = Playing; PhaseRemaining = state.TimeLimit; Players = resetPlayers; Grenades = [||] }
                | Warmup -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
                | Playing when state.PhaseRemaining <= Tuning.TickDuration || Multiplayer.hasWinner state ->
                    { state with Phase = Results; PhaseRemaining = Units.seconds 10.0f }
                | Playing -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
                | Results when state.PhaseRemaining <= Tuning.TickDuration ->
                    let resetPlayers =
                        state.Players
                        |> Map.map (fun id player -> { freshSpawn state.Tick id player with Kills = 0; Deaths = 0 })
                    { state with Phase = Warmup; PhaseRemaining = Units.seconds 10.0f; Players = resetPlayers; Grenades = [||]; AlliesScore = 0; AxisScore = 0 }
                | Results -> { state with PhaseRemaining = state.PhaseRemaining - Tuning.TickDuration }
            let mutable rng = state.Rng
            let shots = ResizeArray<EntityId * Vector3 * Vector3 * float32<hp> * float32 * float32 * WeaponKind * int64>()
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
                        if remaining <= Units.seconds 0.0f then freshSpawn lifecycleState.Tick id player
                        else { player with RespawnIn = remaining })
            let stepFrame id (player: NetworkPlayer) (input: InputFrame) (estimatedTick: int64) acknowledge =
                let canEngage = lifecycleState.Phase = Playing
                // Shared with Sim.step's campaign/round-bot tick: movement,
                // weapon cycling, grenade hand, footstep material, shot rays.
                let result = Sim.stepLocomotion Tuning.TickDuration level lifecycleState.Tick input true canEngage canEngage (toPlayer player) &rng
                result.Thrown |> Option.iter thrownGrenades.Add
                result.FootStep |> Option.iter emit
                if not result.Shots.IsEmpty then
                    // Eye trace, muzzle visuals: the authoritative ray leaves the
                    // camera so anything the player can see over, the bullet
                    // clears; the tracer event still starts at the barrel.
                    let muzzle = Ballistics.playerMuzzleOrigin result.Player result.Weapon.Class
                    emit (ShotFired(Some id, muzzle, Ballistics.directionFromAngles result.Player.Yaw result.Player.Pitch Vector2.Zero, result.Weapon.Class.Name))
                    for struct (origin, direction, request) in result.Shots do
                        shots.Add(id, origin, direction, request.Damage, request.Penetration, request.HeadshotMultiplier, request.Kind, estimatedTick)
                let handPlayer = result.Player
                { player with
                    Position = handPlayer.Position
                    Velocity = handPlayer.Velocity
                    Yaw = handPlayer.Yaw
                    Pitch = handPlayer.Pitch
                    Stance = handPlayer.Stance
                    Sprinting = handPlayer.Sprinting
                    Ads = handPlayer.Ads
                    Weapon = result.Weapon
                    Grenade = handPlayer.Grenade
                    SpawnProtection = if result.Shots.IsEmpty && result.Thrown.IsNone then player.SpawnProtection else Units.seconds 0.0f
                    LastInputSequence = if acknowledge then input.Sequence else player.LastInputSequence }
            let mutable leftoverInputs: Map<EntityId, struct (InputFrame * int64) list> = Map.empty
            let mutable nextCredits: Map<EntityId, int> = Map.empty
            let movedPlayers =
                respawnedPlayers
                |> Map.map (fun id player ->
                    if not player.Alive then player
                    else
                        let mutable current = player
                        let mutable remaining = Map.tryFind id pendingInputs |> Option.defaultValue []
                        let mutable credits = min 4 (1 + (Map.tryFind id inputCredits |> Option.defaultValue 0))
                        let mutable applied = 0
                        // At most two frames per tick: the live frame plus one
                        // banked catch-up frame after network jitter. Credits cap
                        // the long-run rate at one input per server tick.
                        while not (List.isEmpty remaining) && credits > 0 && applied < 2 do
                            let struct (input, estimatedTick) = List.head remaining
                            remaining <- List.tail remaining
                            if input.Sequence > current.LastInputSequence then
                                current <- stepFrame id current input estimatedTick true
                                credits <- credits - 1
                                applied <- applied + 1
                        if applied = 0 then
                            // No input this tick: the player still coasts under
                            // gravity and friction, weapons keep cycling, and a
                            // pinned grenade keeps burning instead of freezing.
                            let idleButtons = match current.Grenade with Cooking _ -> InputButtons.Grenade | _ -> InputButtons.None
                            let idle = { Sequence = current.LastInputSequence; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = idleButtons }
                            current <- stepFrame id current idle lifecycleState.Tick false
                        if not (List.isEmpty remaining) then leftoverInputs <- Map.add id remaining leftoverInputs
                        nextCredits <- Map.add id credits nextCredits
                        current)
            pendingInputs <- leftoverInputs
            inputCredits <- nextCredits
            let mutable combatState = { lifecycleState with Players = movedPlayers; Rng = rng }
            let authoritativeShots = if lifecycleState.Phase = Playing then shots :> seq<_> else Seq.empty
            for shooterId, origin, direction, damage, penetration, headshotMultiplier, kind, estimatedTick in authoritativeShots do
                match Map.tryFind shooterId combatState.Players with
                | Some shooter when shooter.Alive ->
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
                            // Disconnected players are reserved identities, not
                            // bodies: they are invisible to clients and must not
                            // block shots or hand out free kills.
                            target.Connected && target.Alive && target.SpawnProtection <= Units.seconds 0.0f
                            && Multiplayer.areHostile combatState.Mode shooter target
                        | None -> false
                    let hitSoldiers, hitEvents = Ballistics.applyShotFiltered canHit origin direction damage penetration headshotMultiplier kind level soldiers
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
                            if after.IsDead then
                                combatState <- Multiplayer.recordKill shooterId targetId combatState
                | _ -> ()
            let grenadeSet = Array.append lifecycleState.Grenades (thrownGrenades.ToArray())
            let activeGrenades, explosions =
                if lifecycleState.Phase = Playing then Grenades.stepProjectilesOwned Tuning.TickDuration level grenadeSet
                else [||], [||]
            combatState <- { combatState with Grenades = activeGrenades }
            for struct (ownerId, position) in explosions do
                emit (Explosion(position, Grenades.BlastRadius))
                let targets = combatState.Players |> Map.toArray
                for targetId, target in targets do
                    let canDamage =
                        target.Connected && target.Alive && target.SpawnProtection <= Units.seconds 0.0f
                        && match Map.tryFind ownerId combatState.Players with
                           | Some owner -> targetId = ownerId || Multiplayer.areHostile combatState.Mode owner target
                           | None -> false
                    match (if canDamage then Grenades.explosionDamageAt level position target.Position else None) with
                    | Some damage ->
                        let health = max (Units.health 0.0f) (target.Health - damage)
                        combatState <-
                            { combatState with
                                Players = Map.add targetId { target with Health = health; RegenIn = Tuning.RegenDelay } combatState.Players }
                        if health <= Units.health 0.0f then
                            if ownerId <> targetId then combatState <- Multiplayer.recordKill ownerId targetId combatState
                            else combatState <- Multiplayer.markDead targetId combatState
                    | None -> ()
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
