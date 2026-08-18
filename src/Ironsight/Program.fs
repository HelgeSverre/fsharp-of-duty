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
        let mutable selectedOnlineMode = if args |> Array.contains "--ffa" then FreeForAll else TeamDeathmatch
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
        let mutable predictionError = System.Numerics.Vector3.Zero
        let mutable serverStatusTask: Task<ServerStatus option> option = None
        let mutable serverStatusAt = DateTimeOffset.MinValue
        let mutable predictedFireCooldown = 0.0f
        let mutable predictedFireHeld = false
        let mutable subtitle: struct (string * float32<s>) option = None
        let mutable damageDirection: struct (System.Numerics.Vector3 * float32<s>) option = None
        let mutable hitMarkerRemaining = Units.seconds 0.0f
        let hitMarkerDuration lethal = Units.seconds (if lethal then 0.34f else 0.22f)
        let mutable hitMarkerLethal = false
        let mutable inventoryShow = Units.seconds 0.0f
        let mutable lastActiveWeaponName = ""
        let mutable lastActiveInMag = -1
        let mutable grenadeButtonHeld = false
        let mutable loadoutScreen: int option = None
        let mutable lastHeartbeatTick = -1L
        let mutable lastDistantTick = -1L
        let mutable settings =
            if args |> Array.contains "--reset-settings" then Settings.defaults else Settings.load ()
        let mutable settingsScreen: SettingsUi.State option = None
        let applySettings () =
            sampler |> Option.iter (fun input -> input.SetSensitivity settings.MouseSensitivity; input.SetAdsToggle settings.AdsToggle)
            renderer |> Option.iter (fun value -> value.SetSettings settings)
        let createOfflineWorld map =
            match map with
            | "training" -> Sim.createTrainingWorld 0x1A0B3CUL
            | "depot" -> Sim.createScrapDepotWorld 0x1A0B3CUL
            | "canal" -> Sim.createCanalYardWorld 0x1A0B3CUL
            | "omaha" -> Sim.createOmahaWorld 0x1A0B3CUL
            | _ -> Sim.createPaintballWorld 0x1A0B3CUL
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
        let mutable menu =
            if onlineRequested || requestedMap.IsSome || customMapWorld.IsSome then None
            else Some(StartMenu.create playerName)
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

        /// Ping the server's leaderboard endpoint: one HTTP round trip gives
        /// both the latency figure and the per-room player counts.
        let fetchServerStatus () =
            task {
                try
                    let ws = OnlineDefaults.serverUri ()
                    let scheme = if ws.Scheme = "wss" then "https" else "http"
                    let target = UriBuilder(ws, Scheme = scheme, Path = "/api/leaderboard").Uri
                    use http = new System.Net.Http.HttpClient(Timeout = TimeSpan.FromSeconds 5.0)
                    let clock = System.Diagnostics.Stopwatch.StartNew()
                    let! json = http.GetStringAsync target
                    let ping = int clock.ElapsedMilliseconds
                    use document = System.Text.Json.JsonDocument.Parse json
                    let root = document.RootElement
                    let capacity =
                        match root.TryGetProperty "capacityPerRoom" with
                        | true, value -> value.GetInt32()
                        | _ -> 16
                    let rooms =
                        root.GetProperty("rooms").EnumerateArray()
                        |> Seq.map (fun room ->
                            { Mode = (if room.GetProperty("mode").GetString() = "FreeForAll" then FreeForAll else TeamDeathmatch)
                              Phase = room.GetProperty("phase").GetString()
                              Players = room.GetProperty("connectedPlayers").GetInt32()
                              Capacity = capacity })
                        |> Seq.toArray
                    return Some { PingMs = ping; Rooms = rooms }
                with _ -> return None
            }

        let beginReconnect generation token =
            task {
                let client = new OnlineClient(OnlineDefaults.serverUri (), playerName, selectedOnlineMode, selectedOnlineWeapon, ?resumeToken = token)
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

        let returnToMenu (inputSampler: InputSampler) =
            disconnectOnline ()
            settingsScreen <- None
            loadoutScreen <- None
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
                    let client = new OnlineClient(OnlineDefaults.serverUri (), playerName, selectedOnlineMode, selectedOnlineWeapon)
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
                inventoryShow <- max (Units.seconds 0.0f) (inventoryShow - Tuning.TickDuration)
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
                    match menu with
                    | Some state ->
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
                                    | Some status -> { state with ServerStatus = Some status }
                                    | None -> state
                                | Some _ -> state
                                | None when DateTimeOffset.UtcNow - serverStatusAt > TimeSpan.FromSeconds 3.0 ->
                                    serverStatusTask <- Some(fetchServerStatus ())
                                    state
                                | None -> state
                        match settingsScreen with
                        | Some screen ->
                            let menuInput = inputSampler.ConsumeMenuInput()
                            if menuInput.Back then
                                settingsScreen <- None
                                Settings.save settings |> ignore
                            else
                                let updated = SettingsUi.update menuInput screen
                                settingsScreen <- Some updated
                                if updated.Settings <> settings then
                                    settings <- updated.Settings
                                    applySettings ()
                                    Settings.save settings |> ignore
                        | None ->
                            let menuInput = inputSampler.ConsumeMenuInput()
                            let sessionLive = onlineClient |> Option.exists (fun c -> c.Connected)
                            if sessionLive && menuInput.Back && state.Page = Main then
                                // Esc on the root page of the pause menu: back to
                                // the match. Deeper pages still step back a page.
                                menu <- None
                                inputSampler.SetMenuActive false
                            else
                            let struct (nextMenu, action) = StartMenu.update window.Size.X window.Size.Y menuInput state
                            menu <- Some nextMenu
                            playerName <- nextMenu.PlayerName
                            action
                            |> Option.iter (function
                                | StartOffline map ->
                                    disconnectOnline ()
                                    initialWorld <- createOfflineWorld map
                                    previous <- initialWorld
                                    current <- initialWorld
                                    menu <- None
                                    inputSampler.SetMenuActive false
                                    window.Title <- $"IRONSIGHT — {current.Level.Name}"
                                | StartOnline(weaponName, mode) ->
                                    disconnectOnline ()
                                    onlineRequested <- true
                                    selectedOnlineWeapon <- weaponName
                                    selectedOnlineMode <- mode
                                    reconnectAfter <- DateTimeOffset.MinValue
                                    menu <- None
                                    inputSampler.SetMenuActive false
                                    window.Title <- "IRONSIGHT — CONNECTING TO FLY.IO"
                                | OpenSettings ->
                                    settingsScreen <- Some(SettingsUi.create settings)
                                | ExitGame -> window.Close())
                    | None ->
                        if inputSampler.ConsumeLoadoutToggle() && loadoutScreen.IsNone then
                            loadoutScreen <- Some 0
                            inputSampler.SetMenuActive true
                        match loadoutScreen with
                        | Some selected ->
                            let struct (nextSelected, choice) = LoadoutMenu.update (inputSampler.ConsumeMenuInput()) selected
                            match choice with
                            | LoadoutMenu.Browsing -> loadoutScreen <- Some nextSelected
                            | LoadoutMenu.Closed ->
                                loadoutScreen <- None
                                inputSampler.SetMenuActive false
                            | LoadoutMenu.Chosen weaponName ->
                                loadoutScreen <- None
                                inputSampler.SetMenuActive false
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
                        | None -> ()
                        if inputSampler.ConsumeEscape() then
                            if onlineClient |> Option.exists (fun c -> c.Connected) then
                                // Pause menu over a live match: the session stays
                                // up and the server coasts us like a stalled
                                // stream. Esc again closes it.
                                menu <- Some(StartMenu.create playerName)
                                inputSampler.SetMenuActive true
                            else returnToMenu inputSampler
                        let sampledFrame = inputSampler.Sample()
                        // While the loadout picker is open the world keeps
                        // simulating, but the player stands idle (CS buy-menu
                        // feel); online the server coasts us the same way.
                        let inputFrame =
                            if loadoutScreen.IsSome then
                                { sampledFrame with Move = System.Numerics.Vector2.Zero; Look = System.Numerics.Vector2.Zero; Buttons = InputButtons.None }
                            else sampledFrame
                        let weaponKeys =
                            InputButtons.Weapon1 ||| InputButtons.Weapon2 ||| InputButtons.Weapon3
                            ||| InputButtons.Weapon4 ||| InputButtons.Weapon5
                        if inputFrame.Buttons &&& weaponKeys <> InputButtons.None then inventoryShow <- Units.seconds 2.5f
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
                            if inLiveMatch && current.Player.Health > Units.health 0.0f && localWeapon.InMag > 0
                               && not current.Player.Sprinting
                               && predictedFireCooldown <= 0.0f && (triggerEdge || mayRepeat) then
                                let origin = Ballistics.playerMuzzleOrigin current.Player localWeapon.Class
                                let direction = Ballistics.directionFromAngles current.Player.Yaw current.Player.Pitch System.Numerics.Vector2.Zero
                                let cosmetic = [ ShotFired(Some current.Player.Id, origin, direction, localWeapon.Class.Name) ]
                                renderer |> Option.iter (fun value -> value.HandleEvents cosmetic; value.KickWeapon())
                                audio |> Option.iter (fun value -> value.Handle cosmetic)
                                predictedFireCooldown <- 60.0f / localWeapon.Class.RoundsPerMin
                            predictedFireHeld <- firePressed
                            if current.Player.Health > Units.health 0.0f then
                                pendingInputs.Enqueue inputFrame
                                while pendingInputs.Count > 240 do pendingInputs.Dequeue() |> ignore
                                current <- OnlineWorld.applyPrediction current.Level inputFrame current
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
                                networkEvents
                                |> List.tryPick (function Subtitle(_, line) -> Some line | _ -> None)
                                |> Option.iter (fun text -> subtitle <- Some(struct (text, Units.seconds 4.0f)))
                                match hitMarkerKind networkEvents with
                                | Some lethal ->
                                    hitMarkerLethal <- lethal
                                    hitMarkerRemaining <- hitMarkerDuration lethal
                                | None -> ()
                                let beforeReconcile = current.Player.Position
                                let reconciled, remaining = OnlineWorld.reconcile current.Level (pendingInputs |> Seq.toList) client.PlayerId current snapshot
                                let error = predictionError + (beforeReconcile - reconciled.Player.Position)
                                // Large errors are teleports (respawn, round
                                // reset): snapping is correct there.
                                predictionError <- if error.Length() > 1.0f then System.Numerics.Vector3.Zero else error
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
                        | _ ->
                            if current.Round.IsNone && current.Player.Health <= Units.health 0.0f && inputFrame.Buttons.HasFlag InputButtons.Reload then
                                current <- initialWorld
                                previous <- initialWorld
                                subtitle <- None
                            elif current.Player.Health > Units.health 0.0f || current.Round.IsSome then
                                let previousWeaponState = current.Player.Slots[current.Player.Active].State
                                // A fallen player no longer steers the body or the
                                // camera, but the world keeps stepping so the round
                                // timer, friendly AI, and grenades settle.
                                let aliveInput =
                                    if current.Player.Health > Units.health 0.0f then inputFrame
                                    else
                                        { inputFrame with
                                            Move = System.Numerics.Vector2.Zero
                                            Look = System.Numerics.Vector2.Zero
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
                                    hitMarkerRemaining <- hitMarkerDuration lethal
                                | None -> ()
                | None -> ()
                let activeSlot = current.Player.Slots[current.Player.Active]
                if activeSlot.Class.Name <> lastActiveWeaponName then
                    if lastActiveWeaponName <> "" then inventoryShow <- Units.seconds 2.5f
                elif activeSlot.Class.Name = "M1 Garand" && activeSlot.InMag = 0 && lastActiveInMag > 0 then
                    // The Garand's en-bloc clip ejects with its famous ping.
                    audio |> Option.iter (fun value -> value.PlayPing current.Player.Position)
                lastActiveWeaponName <- activeSlot.Class.Name
                lastActiveInMag <- activeSlot.InMag
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
            let renderedWorld =
                let interpolated = RenderInterpolation.world alpha previous current
                if predictionError = System.Numerics.Vector3.Zero then interpolated
                else { interpolated with Player = { interpolated.Player with Position = interpolated.Player.Position + predictionError } }
            let subtitleText = subtitle |> Option.map (fun struct (text, _) -> text)
            let hudInfo =
                { Online = onlineSnapshot
                  LocalPlayerId = onlineClient |> Option.map (fun client -> client.PlayerId)
                  ShowScoreboard = sampler |> Option.exists (fun input -> input.ScoreboardHeld)
                  DamageDirection = damageDirection |> Option.map (fun struct (direction, _) -> direction)
                  HitMarker = MathEx.clamp01 (hitMarkerRemaining / hitMarkerDuration hitMarkerLethal)
                  HitMarkerLethal = hitMarkerLethal
                  Subtitle = subtitleText
                  ShowInventory = inventoryShow > Units.seconds 0.0f
                  GrenadeCooking =
                    (match current.Player.Grenade with Cooking _ -> true | _ -> false)
                    // Online the hand state is never advanced locally, so fall back
                    // to the button. Sprinting is excluded to match the rule the
                    // simulation uses, otherwise the arc promises a throw that
                    // never happens.
                    || (onlineClient.IsSome && grenadeButtonHeld && not current.Player.Sprinting && current.Player.Health > Units.health 0.0f)
                  Menu = if settingsScreen.IsSome then None else menu
                  Settings = settings
                  LoadoutScreen = loadoutScreen
                  SettingsScreen = settingsScreen }
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
            onlineClient
            |> Option.iter (fun client ->
                closeClient client)
            renderer |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            audio |> Option.iter (fun value -> (value :> IDisposable).Dispose())
            input |> Option.iter (fun value -> value.Dispose())
            gl |> Option.iter (fun value -> value.Dispose()))
        window.Run()
        0
