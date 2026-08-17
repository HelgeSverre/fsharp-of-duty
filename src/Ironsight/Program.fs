namespace Ironsight.Shell

open System
open System.Threading.Tasks
open Ironsight
open Silk.NET.Input
open Silk.NET.Maths
open Silk.NET.OpenGL
open Silk.NET.Windowing

module Program =
    let private argumentValue name (args: string array) =
        args
        |> Array.tryFindIndex ((=) name)
        |> Option.bind (fun index -> if index + 1 < args.Length then Some args[index + 1] else None)

    let private hitMarkerKind events =
        let hits = events |> List.choose (function HitConfirmed(_, lethal) -> Some lethal | _ -> None)
        if List.isEmpty hits then None else Some(List.contains true hits)

    [<EntryPoint>]
    let main args =
        let mutable onlineRequested = args |> Array.contains "--online"
        let requestedMode = if args |> Array.contains "--ffa" then FreeForAll else TeamDeathmatch
        let mutable playerName =
            argumentValue "--name" args
            |> Option.defaultValue (Environment.GetEnvironmentVariable "USER" |> Option.ofObj |> Option.defaultValue "Soldier")
            |> Multiplayer.sanitizeName
        let mutable selectedOnlineWeapon =
            argumentValue "--weapon" args
            |> Option.bind Tuning.weaponByName
            |> Option.defaultValue Tuning.thompson
            |> fun weapon -> weapon.Name
        let mutable options = WindowOptions.Default
        options.Title <- "IRONSIGHT — F# of Duty"
        options.Size <- Vector2D<int>(1280, 720)
        options.VSync <- true
        options.API <- GraphicsAPI(ContextAPI.OpenGL, ContextProfile.Core, ContextFlags.ForwardCompatible, APIVersion(4, 1))
        use window = Window.Create options
        let mutable gl: GL option = None
        let mutable input: Silk.NET.Input.IInputContext option = None
        let mutable sampler: InputSampler option = None
        let mutable renderer: Renderer option = None
        let mutable audio: AudioSystem option = None
        let mutable onlineClient: OnlineClient option = None
        let mutable reconnectTask: Task<struct (int * OnlineClient option)> option = None
        let mutable connectionGeneration = 0
        let mutable reconnectAfter = DateTimeOffset.MinValue
        let mutable pendingInputs: InputFrame list = []
        let mutable reconciledTick = -1L
        let mutable lastOnlineEventId = 0L
        let mutable onlineSnapshot: OnlineSnapshot option = None
        let mutable predictedFireCooldown = 0.0f
        let mutable predictedFireHeld = false
        let mutable subtitle: struct (string * float32<s>) option = None
        let mutable damageDirection: struct (System.Numerics.Vector3 * float32<s>) option = None
        let mutable hitMarkerRemaining = Units.seconds 0.0f
        let mutable hitMarkerLethal = false
        let mutable lastHeartbeatTick = -1L
        let mutable lastDistantTick = -1L
        let createOfflineWorld map =
            match map with
            | "stalingrad" -> Sim.createStalingradWorld 0x1A0B3CUL
            | "training" -> Sim.createTrainingWorld 0x1A0B3CUL
            | "battlefield" -> Sim.createBattlefieldWorld 0x1A0B3CUL
            | _ -> Sim.createPaintballWorld 0x1A0B3CUL
        let requestedMap =
            if args |> Array.contains "--stalingrad" then Some "stalingrad"
            elif args |> Array.contains "--training" then Some "training"
            elif args |> Array.contains "--battlefield" then Some "battlefield"
            else None
        let mutable initialWorld = createOfflineWorld (requestedMap |> Option.defaultValue "paintball")
        let mutable menu = if onlineRequested || requestedMap.IsSome then None else Some(StartMenu.create playerName)
        let mutable previous = initialWorld
        let mutable current = previous
        let mutable accumulator = 0.0
        let fixedStep = 1.0 / float Tuning.TickRate

        let closeClient (client: OnlineClient) =
            try client.CloseAsync().GetAwaiter().GetResult()
            with _ -> ()
            (client :> IDisposable).Dispose()

        let beginReconnect generation token =
            task {
                let client = new OnlineClient(OnlineDefaults.serverUri (), playerName, requestedMode, selectedOnlineWeapon, ?resumeToken = token)
                try
                    do! client.ConnectAsync()
                    return struct (generation, Some client)
                with error ->
                    Console.Error.WriteLine($"Online reconnect failed: {error.Message}")
                    (client :> IDisposable).Dispose()
                    return struct (generation, None)
            }

        let returnToMenu (inputSampler: InputSampler) =
            connectionGeneration <- connectionGeneration + 1
            onlineRequested <- false
            onlineClient |> Option.iter closeClient
            onlineClient <- None
            onlineSnapshot <- None
            pendingInputs <- []
            reconciledTick <- -1L
            predictedFireHeld <- false
            menu <- Some(StartMenu.create playerName)
            inputSampler.SetMenuActive true
            window.Title <- "IRONSIGHT — F# of Duty"

        window.add_Load(fun () ->
            let api = window.CreateOpenGL()
            let inputContext = window.CreateInput()
            gl <- Some api
            input <- Some inputContext
            let inputSampler = InputSampler inputContext
            menu |> Option.iter (fun _ -> inputSampler.SetMenuActive true)
            sampler <- Some inputSampler
            renderer <- Some(new Renderer(api))
            try audio <- Some(new AudioSystem())
            with error -> Console.Error.WriteLine($"Audio unavailable: {error.Message}")
            if onlineRequested then
                try
                    let client = new OnlineClient(OnlineDefaults.serverUri (), playerName, requestedMode, selectedOnlineWeapon)
                    client.ConnectAsync().GetAwaiter().GetResult()
                    onlineClient <- Some client
                    window.Title <- $"IRONSIGHT — ONLINE — {client.ServerUri.Host}"
                with error ->
                    Console.Error.WriteLine($"Online connection failed: {error.Message}")
                    window.Title <- "IRONSIGHT — ONLINE UNAVAILABLE — CAMPAIGN FALLBACK")

        window.add_Update(fun elapsed ->
            accumulator <- min 0.25 (accumulator + elapsed)
            while accumulator >= fixedStep do
                subtitle <-
                    subtitle
                    |> Option.bind (fun struct (text, remaining) ->
                        let next = remaining - Tuning.TickDuration
                        if next > Units.seconds 0.0f then Some(struct (text, next)) else None)
                damageDirection <-
                    damageDirection
                    |> Option.bind (fun struct (direction, remaining) ->
                        let next = remaining - Tuning.TickDuration
                        if next > Units.seconds 0.0f then Some(struct (direction, next)) else None)
                hitMarkerRemaining <- max (Units.seconds 0.0f) (hitMarkerRemaining - Tuning.TickDuration)
                if hitMarkerRemaining <= Units.seconds 0.0f then hitMarkerLethal <- false
                predictedFireCooldown <- max 0.0f (predictedFireCooldown - float32 fixedStep)
                match reconnectTask with
                | Some attempt when attempt.IsCompleted ->
                    reconnectTask <- None
                    let struct (generation, result) = attempt.GetAwaiter().GetResult()
                    match result with
                    | Some client when onlineRequested && generation = connectionGeneration ->
                        onlineClient |> Option.iter closeClient
                        onlineClient <- Some client
                        pendingInputs <- []
                        reconciledTick <- -1L
                        lastOnlineEventId <- 0L
                        window.Title <- $"IRONSIGHT — ONLINE — {client.ServerUri.Host}"
                    | Some client -> closeClient client
                    | None when onlineRequested && generation = connectionGeneration ->
                        reconnectAfter <- DateTimeOffset.UtcNow.AddSeconds 2.0
                    | None -> ()
                | _ -> ()
                match sampler with
                | Some inputSampler ->
                    match menu with
                    | Some state ->
                        let struct (nextMenu, action) = StartMenu.update window.Size.X window.Size.Y (inputSampler.ConsumeMenuInput()) state
                        menu <- Some nextMenu
                        playerName <- nextMenu.PlayerName
                        action
                        |> Option.iter (function
                            | StartOffline map ->
                                onlineRequested <- false
                                initialWorld <- createOfflineWorld map
                                previous <- initialWorld
                                current <- initialWorld
                                menu <- None
                                inputSampler.SetMenuActive false
                                window.Title <- $"IRONSIGHT — {current.Level.Name}"
                            | StartOnline weaponName ->
                                connectionGeneration <- connectionGeneration + 1
                                onlineRequested <- true
                                selectedOnlineWeapon <- weaponName
                                reconnectAfter <- DateTimeOffset.MinValue
                                menu <- None
                                inputSampler.SetMenuActive false
                                window.Title <- "IRONSIGHT — CONNECTING TO FLY.IO"
                            | ExitGame -> window.Close())
                    | None ->
                        if inputSampler.ConsumeEscape() then returnToMenu inputSampler
                        let inputFrame = inputSampler.Sample()
                        previous <- current
                        match onlineClient with
                        | Some client when client.Connected ->
                            client.QueueInput inputFrame
                            let firePressed = inputFrame.Buttons.HasFlag InputButtons.Fire
                            let localWeapon = current.Player.Slots[current.Player.Active]
                            let inLiveMatch = onlineSnapshot |> Option.exists (fun snapshot -> snapshot.Phase = Playing)
                            let triggerEdge = firePressed && not predictedFireHeld
                            let mayRepeat = localWeapon.Class.Mode = FullAuto && firePressed
                            if inLiveMatch && current.Player.Health > Units.health 0.0f && localWeapon.InMag > 0
                               && predictedFireCooldown <= 0.0f && (triggerEdge || mayRepeat) then
                                let origin = Ballistics.playerMuzzleOrigin current.Player localWeapon.Class.Name
                                let direction = Ballistics.directionFromAngles current.Player.Yaw current.Player.Pitch System.Numerics.Vector2.Zero
                                let cosmetic = [ ShotFired(Some current.Player.Id, origin, direction, localWeapon.Class.Name) ]
                                renderer |> Option.iter (fun value -> value.HandleEvents cosmetic; value.KickWeapon())
                                audio |> Option.iter (fun value -> value.Handle cosmetic)
                                predictedFireCooldown <- 60.0f / localWeapon.Class.RoundsPerMin
                            predictedFireHeld <- firePressed
                            pendingInputs <- (pendingInputs @ [ inputFrame ]) |> List.truncate 240
                            current <- OnlineWorld.applyPrediction current.Level inputFrame current
                            match client.TryLatestSnapshot() with
                            | Some snapshot when snapshot.Tick > reconciledTick ->
                                if snapshot.LevelName <> current.Level.Name then
                                    let level =
                                        if snapshot.LevelName = Ironsight.ProcGen.Levels.paintballArena.Name then Ironsight.ProcGen.Levels.paintballArena
                                        elif snapshot.LevelName = Ironsight.ProcGen.Levels.stalingradStreet.Name then Ironsight.ProcGen.Levels.stalingradStreet
                                        elif snapshot.LevelName = Ironsight.ProcGen.Levels.trainingYard.Name then Ironsight.ProcGen.Levels.trainingYard
                                        else Ironsight.ProcGen.Levels.battlefield
                                    current <- { current with Level = level }
                                let networkEvents =
                                    snapshot.Events
                                    |> Array.filter (fun event -> event.Id > lastOnlineEventId && (event.RecipientId = 0 || event.RecipientId = client.PlayerId))
                                    |> Array.sortBy (fun event -> event.Id)
                                    |> Array.choose OnlineWorld.eventToGameEvent
                                    |> Array.toList
                                let presentationEvents =
                                    networkEvents
                                    |> List.filter (function
                                        | ShotFired(Some(EntityId shooter), _, _, _) when shooter = client.PlayerId -> false
                                        | _ -> true)
                                if snapshot.Events.Length > 0 then
                                    lastOnlineEventId <- max lastOnlineEventId (snapshot.Events |> Array.maxBy (fun event -> event.Id)).Id
                                renderer |> Option.iter (fun value -> value.HandleEvents presentationEvents)
                                audio |> Option.iter (fun value -> value.Handle presentationEvents)
                                networkEvents
                                |> List.tryPick (function Subtitle(_, line) -> Some line | _ -> None)
                                |> Option.iter (fun text -> subtitle <- Some(struct (text, Units.seconds 4.0f)))
                                match hitMarkerKind networkEvents with
                                | Some lethal ->
                                    hitMarkerLethal <- lethal
                                    hitMarkerRemaining <- Units.seconds (if lethal then 0.26f else 0.16f)
                                | None -> ()
                                let reconciled, remaining = OnlineWorld.reconcile current.Level pendingInputs client.PlayerId current snapshot
                                current <- reconciled
                                pendingInputs <- remaining
                                reconciledTick <- snapshot.Tick
                                onlineSnapshot <- Some snapshot
                            | _ -> ()
                            match client.TryInterpolatedSnapshot() with
                            | Some interpolated -> current <- OnlineWorld.interpolateRemotes client.PlayerId current interpolated
                            | None -> ()
                        | Some client when onlineRequested ->
                            if reconnectTask.IsNone && DateTimeOffset.UtcNow >= reconnectAfter then
                                let token = if String.IsNullOrWhiteSpace client.SessionToken then None else Some client.SessionToken
                                reconnectTask <- Some(beginReconnect connectionGeneration token)
                                window.Title <- "IRONSIGHT — RECONNECTING"
                        | None when onlineRequested ->
                            if reconnectTask.IsNone && DateTimeOffset.UtcNow >= reconnectAfter then
                                reconnectTask <- Some(beginReconnect connectionGeneration None)
                                window.Title <- "IRONSIGHT — CONNECTING"
                        | _ ->
                            if current.Round.IsNone && current.Player.Health <= Units.health 0.0f && inputFrame.Buttons.HasFlag InputButtons.Reload then
                                current <- initialWorld
                                previous <- initialWorld
                                subtitle <- None
                            elif current.Player.Health > Units.health 0.0f || current.Round.IsSome then
                                let previousWeaponState = current.Player.Slots[current.Player.Active].State
                                let struct (next, events) = Sim.step inputFrame current
                                current <- next
                                let previousRound = previous.Round |> Option.map (fun round -> round.Number)
                                let currentRound = current.Round |> Option.map (fun round -> round.Number)
                                if previousRound <> currentRound then previous <- current
                                match previousWeaponState, current.Player.Slots[current.Player.Active].State with
                                | Ready, Reloading _ -> audio |> Option.iter (fun value -> value.PlayReload current.Player.Position)
                                | _ -> ()
                                renderer |> Option.iter (fun value -> value.HandleEvents events)
                                if events |> List.exists (function ShotFired(Some shooter, _, _, _) when shooter = current.Player.Id -> true | _ -> false) then
                                    renderer |> Option.iter (fun value -> value.KickWeapon())
                                audio |> Option.iter (fun value -> value.Handle events)
                                events
                                |> List.rev
                                |> List.tryPick (function Subtitle(speaker, line) -> Some $"{speaker}: {line}" | _ -> None)
                                |> Option.iter (fun text -> subtitle <- Some(struct (text, Units.seconds 4.0f)))
                                events
                                |> List.tryPick (function PlayerHurt(direction, _) -> Some direction | _ -> None)
                                |> Option.iter (fun direction -> damageDirection <- Some(struct (direction, Units.seconds 0.75f)))
                                match hitMarkerKind events with
                                | Some lethal ->
                                    hitMarkerLethal <- lethal
                                    hitMarkerRemaining <- Units.seconds (if lethal then 0.26f else 0.16f)
                                | None -> ()
                | None -> ()
                renderer |> Option.iter (fun value -> value.StepEffects(float32 fixedStep))
                renderer |> Option.iter (fun value -> value.StepViewmodel(float32 fixedStep))
                audio |> Option.iter (fun value -> value.UpdateListener current.Player)
                if current.Tick <> lastHeartbeatTick && current.Player.Health > Units.health 0.0f && current.Player.Health < Units.health 30.0f && current.Tick % 60L = 0L then
                    audio |> Option.iter (fun value -> value.PlayHeartbeat current.Player.Position)
                    lastHeartbeatTick <- current.Tick
                if onlineClient.IsNone && current.Tick <> lastDistantTick && current.Tick % 480L = 240L then
                    let offset = if (current.Tick / 480L) % 2L = 0L then System.Numerics.Vector3(38.0f, 3.0f, -30.0f) else System.Numerics.Vector3(-34.0f, 4.0f, -36.0f)
                    audio |> Option.iter (fun value -> value.PlayDistantShot(current.Player.Position + offset))
                    lastDistantTick <- current.Tick
                accumulator <- accumulator - fixedStep)

        window.add_Render(fun _ ->
            let alpha = float32 (accumulator / fixedStep)
            let renderedWorld = RenderInterpolation.world alpha previous current
            let subtitleText = subtitle |> Option.map (fun struct (text, _) -> text)
            let hudInfo =
                { Online = onlineSnapshot
                  LocalPlayerId = onlineClient |> Option.map (fun client -> client.PlayerId)
                  ShowScoreboard = sampler |> Option.exists (fun input -> input.ScoreboardHeld)
                  DamageDirection = damageDirection |> Option.map (fun struct (direction, _) -> direction)
                  HitMarker = hitMarkerRemaining > Units.seconds 0.0f
                  HitMarkerLethal = hitMarkerLethal
                  Subtitle = subtitleText
                  Menu = menu }
            renderer |> Option.iter (fun value -> value.Render(renderedWorld, hudInfo)))
        window.add_FramebufferResize(fun size ->
            // The framebuffer can be larger than the window on high-DPI displays;
            // the renderer derives the UI scale from both so the HUD keeps its
            // logical size while the world renders at native resolution.
            renderer |> Option.iter (fun value -> value.Resize(size.X, size.Y, window.Size)))
        window.add_Closing(fun () ->
            onlineClient
            |> Option.iter (fun client ->
                closeClient client)
            renderer |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            audio |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            input |> Option.iter (fun value -> value.Dispose())
            gl |> Option.iter (fun value -> value.Dispose()))
        window.Run()
        0
