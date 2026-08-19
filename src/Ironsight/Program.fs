namespace Ironsight.Shell

open System
open System.Numerics
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

    let private weaponKeys =
        InputButtons.Weapon1 ||| InputButtons.Weapon2 ||| InputButtons.Weapon3
        ||| InputButtons.Weapon4 ||| InputButtons.Weapon5

    let private hitMarkerKind events =
        let hits = events |> List.choose (function HitConfirmed(_, lethal) -> Some lethal | _ -> None)
        if List.isEmpty hits then None else Some(List.contains true hits)

    /// One fixed-step tick off a `struct (payload * remaining)` countdown,
    /// clearing the option once time runs out.
    let private tickTimer (timer: struct ('a * float32<s>) option) =
        timer
        |> Option.bind (fun struct (payload, remaining) ->
            let next = remaining - Tuning.TickDuration
            if next > Units.seconds 0.0f then Some(struct (payload, next)) else None)

    /// Transient HUD feedback the fixed-step loop accumulates and the render
    /// pass reads: subtitle, damage direction, hit marker, inventory flash.
    /// Pure so it stays testable without a window.
    type FeedbackState =
        { Subtitle: struct (string * float32<s>) option
          DamageDirection: struct (Vector3 * float32<s>) option
          HitMarkerRemaining: float32<s>
          HitMarkerLethal: bool
          InventoryShow: float32<s> }

    [<RequireQualifiedAccess>]
    module Feedback =
        let empty =
            { Subtitle = None
              DamageDirection = None
              HitMarkerRemaining = Units.seconds 0.0f
              HitMarkerLethal = false
              InventoryShow = Units.seconds 0.0f }

        let hitMarkerDuration lethal = Units.seconds (if lethal then 0.34f else 0.22f)

        /// One fixed tick of decay for every timed element.
        let tick (state: FeedbackState) =
            let hitMarker = max (Units.seconds 0.0f) (state.HitMarkerRemaining - Tuning.TickDuration)
            { state with
                Subtitle = tickTimer state.Subtitle
                DamageDirection = tickTimer state.DamageDirection
                HitMarkerRemaining = hitMarker
                HitMarkerLethal = state.HitMarkerLethal && hitMarker > Units.seconds 0.0f
                InventoryShow = max (Units.seconds 0.0f) (state.InventoryShow - Tuning.TickDuration) }

        /// Subtitle / damage-direction / hit-marker pickup in one event scan,
        /// shared by the offline and online drains. Offline events carry a
        /// speaker; online subtitles arrive pre-formatted with an empty one.
        let applyEvents (events: GameEvent list) (state: FeedbackState) =
            let state =
                events
                |> List.rev
                |> List.tryPick (function
                    | Subtitle("", line) -> Some line
                    | Subtitle(speaker, line) -> Some $"{speaker}: {line}"
                    | _ -> None)
                |> Option.map (fun text -> { state with Subtitle = Some(struct (text, Units.seconds 4.0f)) })
                |> Option.defaultValue state
            let state =
                events
                |> List.tryPick (function PlayerHurt(direction, _) -> Some direction | _ -> None)
                |> Option.map (fun direction -> { state with DamageDirection = Some(struct (direction, Units.seconds 0.75f)) })
                |> Option.defaultValue state
            match hitMarkerKind events with
            | Some lethal -> { state with HitMarkerLethal = lethal; HitMarkerRemaining = hitMarkerDuration lethal }
            | None -> state

    type MenuHome =
        /// The boot menu: no session behind it, Esc on the root page does nothing.
        | Boot
        /// The pause menu: a session is behind it, Esc on the root page resumes.
        | Pause

    /// What is on screen — exactly one at a time. The input sampler's menu mode
    /// and the HUD's overlay fields all derive from this, so a screen change
    /// cannot forget to flip the input mode or leave two panels fighting.
    [<RequireQualifiedAccess>]
    type Screen =
        | Playing
        /// Weapon picker over live play; the world keeps simulating.
        | Loadout of selected: int
        /// The start or pause menu, optionally with settings pushed over it.
        | Menu of home: MenuHome * state: StartMenuState * settings: SettingsUi.State option

    [<EntryPoint>]
    let main args =
        let mutable onlineRequested = args |> Array.contains "--online"
        let mutable selectedOnlineMode = if args |> Array.contains "--ffa" then FreeForAll else TeamDeathmatch
        let mutable selectedServerUri = ServerDirectory.defaultUri ()
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
        let pendingInputs = System.Collections.Generic.Queue<InputFrame>()
        let mutable reconciledTick = -1L
        let mutable lastOnlineEventId = 0L
        let mutable onlineSnapshot: OnlineSnapshot option = None
        let mutable onlineLevel: Level option = None
        // Residual misprediction after a reconcile, decayed over ~100 ms and
        // applied to the *rendered* position only (QuakeWorld-style error
        // smoothing): tiny corrections glide instead of snapping the camera.
        let mutable predictionError = Vector3.Zero
        let mutable debugView = false
        let mutable serverStatusTask: Task<ServerRow array option> option = None
        let mutable serverStatusAt = DateTimeOffset.MinValue
        let mutable predictedFireCooldown = 0.0f
        let mutable predictedFireHeld = false
        let mutable feedback = Feedback.empty
        let mutable lastActiveWeaponName = ""
        let mutable lastActiveInMag = -1
        let mutable grenadeButtonHeld = false
        let mutable lastHeartbeatTick = -1L
        let mutable lastDistantTick = -1L
        let mutable settings =
            if args |> Array.contains "--reset-settings" then Settings.defaults else Settings.load ()
        let applySettings () =
            sampler |> Option.iter (fun input -> input.ApplySettings settings)
            renderer |> Option.iter (fun value -> value.SetSettings settings)
        let createOfflineWorld map = Sim.createOfflineWorld map 0x1A0B3CUL
        let requestedMap =
            if args |> Array.contains "--training" then Some "training"
            else None
        // --map <file.ironmap>: play a custom map offline as a bot round.
        let customMapWorld =
            argumentValue "--map" args
            |> Option.map (fun path ->
                match Ironsight.ProcGen.MapFile.decode (IO.File.ReadAllBytes path) with
                | Ok spec -> Sim.createRoundWorldFor (Ironsight.ProcGen.LevelCompile.compile spec) 0x1A0B3CUL
                | Error message ->
                    eprintfn $"--map {path}: {message}"
                    exit 1)
        let mutable initialWorld =
            customMapWorld
            |> Option.defaultWith (fun () -> createOfflineWorld (requestedMap |> Option.defaultValue "paintball"))
        let mutable screen =
            if onlineRequested || requestedMap.IsSome || customMapWorld.IsSome then Screen.Playing
            else Screen.Menu(Boot, StartMenu.create playerName, None)
        let mutable previous = initialWorld
        let mutable current = previous
        let mutable accumulator = 0.0
        let fixedStep = 1.0 / float Tuning.TickRate

        let closeClient (client: OnlineClient) =
            try client.CloseAsync().GetAwaiter().GetResult()
            with _ -> ()
            (client :> IDisposable).Dispose()

        /// Sync the server's announced map: builtin, hash cache, or download.
        /// False means the map could not be obtained and play must not start.
        let applyServerMap (client: OnlineClient) =
            if String.IsNullOrEmpty client.MapHash then
                onlineLevel <- None // pre-map-sync server; snapshot name fallback applies
                true
            else
                match MapStore.resolve client.ServerUri client.MapHash with
                | Ok level ->
                    onlineLevel <- Some level
                    true
                | Error message ->
                    Console.Error.WriteLine($"Map sync failed: {message}")
                    window.Title <- "IRONSIGHT — MAP DOWNLOAD FAILED"
                    false

        /// Load the server directory (packaged + user + community lists) and
        /// probe every server in parallel for its rooms, counts and ping.
        let fetchServerRows () =
            System.Threading.Tasks.Task.Run(fun () ->
                try (ServerDirectory.probeAll (ServerDirectory.load ())).GetAwaiter().GetResult() |> List.toArray |> Some
                with _ -> None)

        let beginReconnect generation token =
            task {
                let client = new OnlineClient(selectedServerUri, playerName, selectedOnlineMode, selectedOnlineWeapon, ?resumeToken = token)
                try
                    do! client.ConnectAsync()
                    return struct (generation, Some client)
                with error ->
                    Console.Error.WriteLine($"Online reconnect failed: {error.Message}")
                    (client :> IDisposable).Dispose()
                    return struct (generation, None)
            }

        let disconnectOnline () =
            connectionGeneration <- connectionGeneration + 1
            onlineRequested <- false
            onlineClient |> Option.iter closeClient
            onlineClient <- None
            onlineSnapshot <- None
            onlineLevel <- None
            pendingInputs.Clear()
            reconciledTick <- -1L
            predictedFireHeld <- false

        /// The only place a screen changes: the input sampler's menu mode (Esc
        /// routing, cursor capture, look-delta suppression) always follows.
        let setScreen next =
            screen <- next
            sampler |> Option.iter (fun s -> s.SetMenuActive(match next with Screen.Playing -> false | _ -> true))

        window.add_Load(fun () ->
            let api = window.CreateOpenGL()
            let inputContext = window.CreateInput()
            gl <- Some api
            input <- Some inputContext
            let inputSampler = InputSampler inputContext
            match screen with
            | Screen.Playing -> ()
            | _ -> inputSampler.SetMenuActive true
            sampler <- Some inputSampler
            renderer <- Some(new Renderer(api))
            // The OS can deliver the initial framebuffer size before the
            // renderer exists, which would leave the viewport at its 1280x720
            // default while the surface is larger (retina). Seed it explicitly.
            let value = renderer.Value
            value.Resize(window.FramebufferSize.X, window.FramebufferSize.Y, window.Size)
            Console.WriteLine($"Window {window.Size.X}x{window.Size.Y} framebuffer {window.FramebufferSize.X}x{window.FramebufferSize.Y} uiScale {HudLayout.uiScale window.FramebufferSize.X window.Size.X}")
            applySettings ()
            try audio <- Some(new AudioSystem())
            with error -> Console.Error.WriteLine($"Audio unavailable: {error.Message}")
            if onlineRequested then
                try
                    let client = new OnlineClient(selectedServerUri, playerName, selectedOnlineMode, selectedOnlineWeapon)
                    client.ConnectAsync().GetAwaiter().GetResult()
                    if applyServerMap client then
                        onlineClient <- Some client
                        window.Title <- $"IRONSIGHT — ONLINE — {client.ServerUri.Host}"
                    else closeClient client
                with error ->
                    Console.Error.WriteLine($"Online connection failed: {error.Message}")
                    window.Title <- "IRONSIGHT — CONNECTING")

        window.add_Update(fun elapsed ->
            accumulator <- min 0.25 (accumulator + elapsed)
            while accumulator >= fixedStep do
                feedback <- Feedback.tick feedback
                predictionError <- predictionError * 0.75f
                predictedFireCooldown <- max 0.0f (predictedFireCooldown - float32 fixedStep)
                match reconnectTask with
                | Some attempt when attempt.IsCompleted ->
                    reconnectTask <- None
                    let struct (generation, result) = attempt.GetAwaiter().GetResult()
                    match result with
                    | Some client when onlineRequested && generation = connectionGeneration ->
                        if applyServerMap client then
                            onlineClient |> Option.iter closeClient
                            onlineClient <- Some client
                            pendingInputs.Clear()
                            reconciledTick <- -1L
                            lastOnlineEventId <- 0L
                            window.Title <- $"IRONSIGHT — ONLINE — {client.ServerUri.Host}"
                        else
                            closeClient client
                            reconnectAfter <- DateTimeOffset.UtcNow.AddSeconds 2.0
                    | Some client -> closeClient client
                    | None when onlineRequested && generation = connectionGeneration ->
                        reconnectAfter <- DateTimeOffset.UtcNow.AddSeconds 2.0
                    | None -> ()
                | _ -> ()
                match sampler with
                | Some inputSampler ->
                    match screen with
                    | Screen.Menu(home, state, settingsOverlay) ->
                        // Server list rows show live player counts and ping,
                        // refreshed every few seconds while the page is open.
                        // The status is folded into the state *before* the menu
                        // update below runs — assigning `menu` here instead was
                        // clobbered by the update's own reassignment, leaving
                        // the page stuck on "pinging".
                        let state =
                            if state.Page <> ServerList then state
                            else
                                match serverStatusTask with
                                | Some fetch when fetch.IsCompleted ->
                                    serverStatusTask <- None
                                    serverStatusAt <- DateTimeOffset.UtcNow
                                    match fetch.GetAwaiter().GetResult() with
                                    | Some rows -> { state with ServerRows = Some rows }
                                    | None -> state
                                | Some _ -> state
                                | None when DateTimeOffset.UtcNow - serverStatusAt > TimeSpan.FromSeconds 5.0 ->
                                    serverStatusTask <- Some(fetchServerRows ())
                                    state
                                | None -> state
                        match settingsOverlay with
                        | Some settingsState ->
                            let menuInput = inputSampler.ConsumeMenuInput()
                            if menuInput.Back then
                                screen <- Screen.Menu(home, state, None)
                                Settings.save settings |> ignore
                            else
                                let updated = SettingsUi.update window.Size.X window.Size.Y menuInput settingsState
                                screen <- Screen.Menu(home, state, Some updated)
                                if updated.Settings <> settings then
                                    settings <- updated.Settings
                                    applySettings ()
                                    Settings.save settings |> ignore
                        | None ->
                            let menuInput = inputSampler.ConsumeMenuInput()
                            if home = Pause && menuInput.Back && state.Page = Main then
                                // Esc on the pause menu's root page resumes the
                                // session: offline the frozen sim, online the
                                // match that never stopped. Deeper pages still
                                // step back one page inside StartMenu.update.
                                setScreen Screen.Playing
                            else
                                let struct (nextMenu, action) = StartMenu.update window.Size.X window.Size.Y menuInput state
                                screen <- Screen.Menu(home, nextMenu, None)
                                playerName <- nextMenu.PlayerName
                                action
                                |> Option.iter (function
                                    | StartOffline map ->
                                        disconnectOnline ()
                                        initialWorld <- createOfflineWorld map
                                        previous <- initialWorld
                                        current <- initialWorld
                                        setScreen Screen.Playing
                                        window.Title <- $"IRONSIGHT — {current.Level.Name}"
                                    | StartOnline(weaponName, mode, server) ->
                                        disconnectOnline ()
                                        onlineRequested <- true
                                        selectedOnlineWeapon <- weaponName
                                        selectedOnlineMode <- mode
                                        selectedServerUri <- server
                                        reconnectAfter <- DateTimeOffset.MinValue
                                        setScreen Screen.Playing
                                        window.Title <- "IRONSIGHT — CONNECTING"
                                    | OpenSettings ->
                                        screen <- Screen.Menu(home, nextMenu, Some(SettingsUi.create settings))
                                    | ExitGame -> window.Close())
                    | Screen.Playing | Screen.Loadout _ ->
                        // Captured before the loadout branch can flip screen to
                        // Playing, so the closing frame is still masked below —
                        // otherwise a held Enter/A leaks into the sim as-is.
                        let wasLoadout = match screen with Screen.Loadout _ -> true | _ -> false
                        (match screen with
                         | Screen.Playing ->
                             if inputSampler.ConsumeDebugToggle() then debugView <- not debugView
                             if inputSampler.ConsumeLoadoutToggle() then setScreen (Screen.Loadout 0)
                             elif inputSampler.ConsumeEscape() then
                                 // Pause, uniformly: offline the sim freezes
                                 // because the play block below is gated on
                                 // Screen.Playing; online the session stays up
                                 // and the server coasts the idle player.
                                 setScreen (Screen.Menu(Pause, StartMenu.create playerName, None))
                         | Screen.Loadout selected ->
                             // The key/button that opened the picker (B / Y)
                             // also closes it.
                             if inputSampler.ConsumeLoadoutToggle() then setScreen Screen.Playing
                             else
                                 let struct (nextSelected, choice) = LoadoutMenu.update window.Size.X window.Size.Y (inputSampler.ConsumeMenuInput()) selected
                                 match choice with
                                 | LoadoutMenu.Browsing -> screen <- Screen.Loadout nextSelected
                                 | LoadoutMenu.Closed -> setScreen Screen.Playing
                                 | LoadoutMenu.Chosen weaponName ->
                                     setScreen Screen.Playing
                                     match onlineClient with
                                     | Some client when client.Connected ->
                                         selectedOnlineWeapon <- weaponName
                                         client.RequestLoadout weaponName
                                     | _ ->
                                         current.Player.Slots
                                         |> Array.tryFindIndex (fun slot -> slot.Class.Name = weaponName)
                                         |> Option.iter (fun index ->
                                             if index <> current.Player.Active then
                                                 current <- { current with Player = { current.Player with Active = index; Ads = 0.0f } }
                                                 previous <- current)
                         | Screen.Menu _ -> ())
                        let sampledFrame = inputSampler.Sample()
                        // While the loadout picker is open the world keeps
                        // simulating, but the player stands idle (CS buy-menu
                        // feel); online the server coasts us the same way.
                        // Masked when the tick started OR ended on the picker,
                        // so neither the opening nor the closing frame leaks
                        // held movement/buttons into the sim.
                        let isLoadout = match screen with Screen.Loadout _ -> true | _ -> false
                        let inputFrame =
                            if wasLoadout || isLoadout then
                                { sampledFrame with Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.None }
                            else sampledFrame
                        if inputFrame.Buttons &&& weaponKeys <> InputButtons.None then
                            feedback <- { feedback with InventoryShow = Units.seconds 2.5f }
                        grenadeButtonHeld <- inputFrame.Buttons.HasFlag InputButtons.Grenade
                        previous <- current
                        match onlineClient with
                        | Some client when client.Connected ->
                            client.QueueInput inputFrame
                            let firePressed = inputFrame.Buttons.HasFlag InputButtons.Fire
                            let localWeapon = current.Player.Slots[current.Player.Active]
                            let inLiveMatch = onlineSnapshot |> Option.exists (fun snapshot -> snapshot.Phase = Playing)
                            let triggerEdge = firePressed && not predictedFireHeld
                            let mayRepeat = localWeapon.Class.Mode = FullAuto && firePressed
                            // ponytail: cosmetic predictor mirrors Weapons.step's gate by hand;
                            // route through Sim.stepLocomotion if it drifts again.
                            if inLiveMatch && current.Player.IsAlive && localWeapon.InMag > 0
                               && localWeapon.State = Ready
                               && not current.Player.Sprinting
                               && predictedFireCooldown <= 0.0f && (triggerEdge || mayRepeat) then
                                let origin = Ballistics.playerMuzzleOrigin current.Player localWeapon.Class
                                let direction = Ballistics.directionFromAngles current.Player.Yaw current.Player.Pitch Vector2.Zero
                                let cosmetic = [ ShotFired(Some current.Player.Id, origin, direction, localWeapon.Class.Name) ]
                                renderer |> Option.iter (fun value -> value.HandleEvents cosmetic; value.KickWeapon())
                                audio |> Option.iter (fun value -> value.Handle cosmetic)
                                predictedFireCooldown <- 60.0f / localWeapon.Class.RoundsPerMin
                            predictedFireHeld <- firePressed
                            if current.Player.IsAlive then
                                pendingInputs.Enqueue inputFrame
                                while pendingInputs.Count > 240 do pendingInputs.Dequeue() |> ignore
                                current <- OnlineWorld.applyPrediction current.Level inputFrame current
                        | _ when onlineRequested ->
                            // Connecting or reconnecting: the world stays
                            // frozen; the shared network step below drives the
                            // reconnect attempts.
                            ()
                        | _ ->
                            if current.Round.IsNone && current.Player.IsDead && inputFrame.Buttons.HasFlag InputButtons.Reload then
                                current <- initialWorld
                                previous <- initialWorld
                                feedback <- { feedback with Subtitle = None }
                            elif current.Player.IsAlive || current.Round.IsSome then
                                let previousWeaponState = current.Player.Slots[current.Player.Active].State
                                // A fallen player no longer steers the body or the
                                // camera, but the world keeps stepping so the round
                                // timer, friendly AI, and grenades settle.
                                let aliveInput =
                                    if current.Player.IsAlive then inputFrame
                                    else
                                        { inputFrame with
                                            Move = Vector2.Zero
                                            Look = Vector2.Zero
                                            Buttons = InputButtons.None }
                                let struct (next, events) = Sim.step aliveInput current
                                current <- next
                                let previousRound = previous.Round |> Option.map (fun round -> round.Number)
                                let currentRound = current.Round |> Option.map (fun round -> round.Number)
                                if previousRound <> currentRound then previous <- current
                                match previousWeaponState, current.Player.Slots[current.Player.Active].State with
                                | Ready, Reloading _ -> audio |> Option.iter (fun value -> value.PlayReload current.Player.Position)
                                | _ -> ()
                                renderer |> Option.iter (fun value -> value.HandleEvents events)
                                if events |> List.exists (function ShotFired(Some shooter, _, _, _) -> shooter = current.Player.Id | _ -> false) then
                                    renderer |> Option.iter (fun value -> value.KickWeapon())
                                audio |> Option.iter (fun value -> value.Handle events)
                                feedback <- Feedback.applyEvents events feedback
                | None -> ()
                // The receive half of the online session runs on every screen,
                // so the match keeps flowing behind the pause menu and a
                // dropped connection recovers without unpausing. Nothing is
                // sent from here: paused players coast on the server.
                match onlineClient with
                | Some client when client.Connected ->
                    (match screen with
                     | Screen.Playing | Screen.Loadout _ -> ()
                     | _ -> previous <- current)
                    match client.TryLatestSnapshot() with
                    | Some snapshot when snapshot.Tick > reconciledTick ->
                        if snapshot.LevelName <> current.Level.Name then
                            let level =
                                match onlineLevel with
                                | Some synced when synced.Name = snapshot.LevelName -> synced
                                | _ ->
                                    Ironsight.ProcGen.Levels.byName snapshot.LevelName
                                    |> Option.defaultValue Ironsight.ProcGen.Levels.paintballArena
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
                        feedback <- Feedback.applyEvents networkEvents feedback
                        let beforeReconcile = current.Player.Position
                        let reconciled, remaining = OnlineWorld.reconcile current.Level (pendingInputs |> Seq.toList) client.PlayerId current snapshot
                        let error = predictionError + (beforeReconcile - reconciled.Player.Position)
                        // Large errors are teleports (respawn, round
                        // reset): snapping is correct there.
                        predictionError <- if error.Length() > 1.0f then Vector3.Zero else error
                        current <- reconciled
                        pendingInputs.Clear()
                        remaining |> List.iter pendingInputs.Enqueue
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
                | _ -> ()
                let activeSlot = current.Player.Slots[current.Player.Active]
                if activeSlot.Class.Name <> lastActiveWeaponName then
                    if lastActiveWeaponName <> "" then feedback <- { feedback with InventoryShow = Units.seconds 2.5f }
                elif activeSlot.Class.Name = Tuning.m1Garand.Name && activeSlot.InMag = 0 && lastActiveInMag > 0 then
                    // The Garand's en-bloc clip ejects with its famous ping.
                    audio |> Option.iter (fun value -> value.PlayPing current.Player.Position)
                lastActiveWeaponName <- activeSlot.Class.Name
                lastActiveInMag <- activeSlot.InMag
                renderer
                |> Option.iter (fun value ->
                    value.StepEffects(float32 fixedStep)
                    value.StepViewmodel(float32 fixedStep))
                audio |> Option.iter (fun value -> value.UpdateListener current.Player)
                if current.Tick <> lastHeartbeatTick && current.Player.IsAlive && current.Player.Health < Units.health 30.0f && current.Tick % 60L = 0L then
                    audio |> Option.iter (fun value -> value.PlayHeartbeat current.Player.Position)
                    lastHeartbeatTick <- current.Tick
                if onlineClient.IsNone && current.Tick <> lastDistantTick && current.Tick % 480L = 240L then
                    let offset = if (current.Tick / 480L) % 2L = 0L then Vector3(38.0f, 3.0f, -30.0f) else Vector3(-34.0f, 4.0f, -36.0f)
                    audio |> Option.iter (fun value -> value.PlayDistantShot(current.Player.Position + offset))
                    lastDistantTick <- current.Tick
                accumulator <- accumulator - fixedStep)

        window.add_Render(fun _ ->
            let alpha =
                match screen, onlineClient with
                // A menu over a frozen offline world: previous = current, and a
                // varying alpha would make the backdrop jiggle a tick behind it.
                | Screen.Menu _, None -> 1.0f
                | _ -> float32 (accumulator / fixedStep)
            let renderedWorld =
                let interpolated = RenderInterpolation.world alpha previous current
                if predictionError = Vector3.Zero then interpolated
                else { interpolated with Player = { interpolated.Player with Position = interpolated.Player.Position + predictionError } }
            let subtitleText = feedback.Subtitle |> Option.map (fun struct (text, _) -> text)
            let hudInfo =
                { Online = onlineSnapshot
                  LocalPlayerId = onlineClient |> Option.map (fun client -> client.PlayerId)
                  ShowScoreboard = sampler |> Option.exists (fun input -> input.ScoreboardHeld)
                  DamageDirection = feedback.DamageDirection |> Option.map (fun struct (direction, _) -> direction)
                  HitMarker = MathEx.clamp01 (feedback.HitMarkerRemaining / Feedback.hitMarkerDuration feedback.HitMarkerLethal)
                  HitMarkerLethal = feedback.HitMarkerLethal
                  Subtitle = subtitleText
                  ShowInventory = feedback.InventoryShow > Units.seconds 0.0f
                  DebugView = debugView
                  GrenadeCooking =
                    (match current.Player.Grenade with Cooking _ -> true | _ -> false)
                    // Online the hand state is never advanced locally, so fall back
                    // to the button. Sprinting is excluded to match the rule the
                    // simulation uses, otherwise the arc promises a throw that
                    // never happens.
                    || (onlineClient.IsSome && grenadeButtonHeld && not current.Player.Sprinting && current.Player.IsAlive)
                  Menu = (match screen with Screen.Menu(_, state, None) -> Some state | _ -> None)
                  Settings = settings
                  LoadoutScreen = (match screen with Screen.Loadout selected -> Some selected | _ -> None)
                  SettingsScreen = (match screen with Screen.Menu(_, _, Some settingsState) -> Some settingsState | _ -> None) }
            renderer |> Option.iter (fun value -> value.Render(renderedWorld, hudInfo)))
        window.add_FramebufferResize(fun _ ->
            // Query the properties rather than trusting the event payload so
            // high-DPI platforms that report logical sizes through the event
            // still get the true framebuffer dimensions.
            renderer
            |> Option.iter (fun value ->
                value.Resize(window.FramebufferSize.X, window.FramebufferSize.Y, window.Size)))
        window.add_Closing(fun () ->
            Settings.save settings |> ignore
            let dispose (value: #IDisposable) = value.Dispose()
            onlineClient |> Option.iter closeClient
            renderer |> Option.iter dispose
            audio |> Option.iter dispose
            input |> Option.iter dispose
            gl |> Option.iter dispose)
        window.Run()
        0
