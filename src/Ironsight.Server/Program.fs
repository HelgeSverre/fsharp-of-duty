namespace Ironsight.Server

open System
open System.Diagnostics
open System.Net.WebSockets
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Ironsight
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting

module Program =
    type private Room = { Id: string; Name: string; Host: MatchHost }

    /// Every room this process hosts, built from server.json (or the two
    /// defaults when there is no config). `level`/`mapBytes` stay the *boot*
    /// map: /maps/{hash} serves only those bytes, and a room on any other
    /// builtin is resolved by the client by name instead.
    type private MatchDirectory(identity: ServerIdentity, rooms: RoomConfig array, level: Level, mapBytes: byte array) =
        let rooms =
            rooms
            |> Array.map (fun room ->
                { Id = room.Id
                  Name = room.Name
                  Host =
                    MatchHost(
                        room.Mode,
                        room.Level,
                        scoreLimit = room.ScoreLimit,
                        timeLimit = room.TimeLimit,
                        maxPlayers = room.MaxPlayers) })

        member _.Rooms = rooms
        member _.Identity = identity
        member _.LevelName = level.Name
        member _.MapBytes = mapBytes
        member val MapHash = if Array.isEmpty mapBytes then "" else Ironsight.ProcGen.MapFile.hash mapBytes

        member _.TryFind(id: string) =
            rooms |> Array.tryFind (fun room -> String.Equals(room.Id, id, StringComparison.OrdinalIgnoreCase))

        /// Where a client that named no room ends up: the first room of the
        /// mode it asked for that still has a slot. Keeps pre-room clients
        /// working, and spills a full room into the next one of its mode.
        member _.TryFindByMode(mode: GameMode) =
            let ofMode = rooms |> Array.filter (fun room -> room.Host.Snapshot().Mode = mode)
            ofMode
            |> Array.tryFind (fun room -> room.Host.HasRoom)
            |> Option.orElse (Array.tryHead ofMode)

        member _.Leaderboard() =
            rooms
            |> Array.map (fun room -> room.Id, room.Name, room.Host.Capacity, room.Host.Snapshot())
            |> Protocol.leaderboard identity.Name

    let private receiveMessage (socket: WebSocket) (cancellationToken: CancellationToken) = task {
        let buffer = Array.zeroCreate<byte> Protocol.MaxMessageBytes
        let! result = socket.ReceiveAsync(Memory buffer, cancellationToken)
        if result.MessageType = WebSocketMessageType.Close then return None
        elif not result.EndOfMessage then
            do! socket.CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message exceeds 16 KiB.", cancellationToken)
            return None
        elif result.MessageType <> WebSocketMessageType.Text then
            do! socket.CloseAsync(WebSocketCloseStatus.InvalidMessageType, "Text JSON messages are required.", cancellationToken)
            return None
        else
            try
                return Some(JsonDocument.Parse(ReadOnlyMemory(buffer, 0, result.Count)))
            with :? JsonException ->
                do! socket.CloseAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid JSON.", cancellationToken)
                return None
    }

    /// Handles one client message; returns false when the client has asked to
    /// leave.
    let private dispatch extensions (host: MatchHost) playerId (message: JsonElement) =
        match Protocol.tryString "type" message with
        | Some "input" ->
            host.ApplyInput(playerId, message)
            true
        | Some "ready" ->
            host.SetReady playerId
            true
        | Some "loadout" ->
            Protocol.tryString "weapon" message
            |> Option.iter (fun weapon -> host.SetLoadout(playerId, weapon))
            true
        | Some "chat" ->
            Protocol.tryString "text" message
            |> Option.iter (Commands.handleChat extensions host playerId)
            true
        | Some "leave" -> false
        | _ -> true

    let private send value (socket: WebSocket) cancellationToken = task {
        let bytes = Protocol.serialize value
        do! socket.SendAsync(ReadOnlyMemory bytes, WebSocketMessageType.Text, true, cancellationToken)
    }

    let private handleSocket (extensions: ServerExtension list) (matches: MatchDirectory) (context: HttpContext) = task {
        if not context.WebSockets.IsWebSocketRequest then
            context.Response.StatusCode <- StatusCodes.Status400BadRequest
        else
            // Ping/pong keepalive (mirrors NetworkClient): a dead peer aborts
            // the socket in ~30s, so its pending receive throws and the finally
            // below frees the player slot instead of waiting out TCP timeouts.
            let acceptContext = WebSocketAcceptContext(KeepAliveInterval = TimeSpan.FromSeconds 10.0, KeepAliveTimeout = TimeSpan.FromSeconds 20.0)
            use! socket = context.WebSockets.AcceptWebSocketAsync acceptContext
            let cancellationToken = context.RequestAborted
            // A connection that never says hello must not hold a socket open.
            use helloTimeout = CancellationTokenSource.CreateLinkedTokenSource cancellationToken
            helloTimeout.CancelAfter(TimeSpan.FromSeconds 10.0)
            let! hello = task {
                try
                    return! receiveMessage socket helloTimeout.Token
                with
                | :? OperationCanceledException -> return None
                | :? WebSocketException -> return None
            }
            match hello with
            | None -> ()
            | Some document ->
                use document = document
                let root = document.RootElement
                match Protocol.tryString "type" root, Protocol.tryString "name" root, Protocol.tryInt64 "version" root with
                | Some "hello", Some name, Some version when version = int64 Protocol.Version ->
                    // An explicit room id wins. Without one — every client
                    // built before rooms existed — fall back to the requested
                    // mode, which is how the two-room server always routed.
                    let requestedMode =
                        match Protocol.tryString "mode" root with
                        | Some value when String.Equals(value, "FreeForAll", StringComparison.OrdinalIgnoreCase) -> FreeForAll
                        | _ -> TeamDeathmatch
                    let room =
                        Protocol.tryString "room" root
                        |> Option.bind matches.TryFind
                        |> Option.orElseWith (fun () -> matches.TryFindByMode requestedMode)
                    let resumeToken = Protocol.tryString "sessionToken" root
                    let weaponName = Protocol.tryString "weapon" root
                    let header name =
                        match context.Request.Headers.TryGetValue(name: string) with
                        | true, values -> string values
                        | _ -> ""
                    let address =
                        Bans.clientAddress
                            (header "Fly-Client-IP")
                            (header "X-Forwarded-For")
                            (if isNull context.Connection.RemoteIpAddress then "" else string context.Connection.RemoteIpAddress)
                    if Bans.isBanned address then
                        do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "You are banned from this server.", cancellationToken)
                    else
                    match room with
                    | None ->
                        do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "No such room on this server.", cancellationToken)
                    | Some room ->
                    let host = room.Host
                    match host.TryAddPlayer(name, ?weaponName = weaponName, ?sessionToken = resumeToken) with
                    | None ->
                        do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The match is full.", cancellationToken)
                    | Some(playerId, token) ->
                    // Known for as long as the connection lives, so /ban can
                    // turn a name into an address to refuse.
                    Bans.remember playerId address
                    try
                        try
                            do! send (Protocol.welcomeFor playerId token matches.LevelName matches.MapHash room.Id (host.Snapshot())) socket cancellationToken
                            // Greeting rides the chat channel rather than a new
                            // message type: it is already a whispered, throttled,
                            // sanitized line that the client logs and renders.
                            if matches.Identity.Motd <> "" then
                                host.Enqueue(Chat(None, "", matches.Identity.Motd), recipient = playerId)
                            let mutable connected = true
                            let mutable pendingReceive = receiveMessage socket cancellationToken
                            // The snapshot timer must keep its own cadence. Starting a
                            // fresh delay after every message meant a client streaming
                            // inputs at tick rate starved itself of snapshots forever.
                            let mutable snapshotDelay = Task.Delay(50, cancellationToken)
                            let mutable rateWindow = Stopwatch.GetTimestamp()
                            let mutable messagesInWindow = 0
                            // /kick has no socket to close, so the victim's own
                            // loop polls the flag; the snapshot delay bounds
                            // the wait at 50ms. RemovePlayer runs in the finally.
                            while connected && not (host.IsKicked playerId) && socket.State = WebSocketState.Open && not cancellationToken.IsCancellationRequested do
                                let! completed = Task.WhenAny(pendingReceive, snapshotDelay)
                                if Object.ReferenceEquals(completed, snapshotDelay) then
                                    do! send (Protocol.snapshotFor playerId (host.Snapshot())) socket cancellationToken
                                    snapshotDelay <- Task.Delay(50, cancellationToken)
                                else
                                    let! incoming = pendingReceive
                                    match incoming with
                                    | Some inputDocument ->
                                        use inputDocument = inputDocument
                                        let now = Stopwatch.GetTimestamp()
                                        if Stopwatch.GetElapsedTime(rateWindow, now) >= TimeSpan.FromSeconds 1.0 then
                                            rateWindow <- now
                                            messagesInWindow <- 0
                                        messagesInWindow <- messagesInWindow + 1
                                        if messagesInWindow > 120 then
                                            connected <- false
                                            do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Message rate exceeded.", cancellationToken)
                                        else
                                            connected <- dispatch extensions host playerId inputDocument.RootElement
                                        if connected then pendingReceive <- receiveMessage socket cancellationToken
                                    | None -> connected <- false
                        with
                        | :? WebSocketException -> ()
                        | :? OperationCanceledException -> ()
                    finally
                        host.RemovePlayer playerId
                        Bans.forget playerId
                | _ ->
                    do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "First message must be a compatible hello.", cancellationToken)
    }

    /// Builds the configured application without starting it. Tests bind port 0
    /// to get an ephemeral port; the real process passes the PORT env value.
    let build (args: string array) (port: int) (extensions: ServerExtension list) =
        let sourceWebRoot = IO.Path.GetFullPath("../../website", __SOURCE_DIRECTORY__)
        let options =
            WebApplicationOptions(
                Args = args,
                WebRootPath = if IO.Directory.Exists sourceWebRoot then sourceWebRoot else null)
        let builder = WebApplication.CreateBuilder options
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore
        // IRONSIGHT_LEVEL is a builtin alias or a path to an .ironmap file. The
        // resolved *spec* is kept so its encoded bytes can be served to clients
        // that do not have the map (see /maps/{hash} below).
        let matchSpec =
            let value = Environment.GetEnvironmentVariable "IRONSIGHT_LEVEL"
            match Ironsight.ProcGen.Levels.specByAlias value with
            | Some spec -> spec
            | None when not (String.IsNullOrWhiteSpace value) && value.EndsWith(Ironsight.ProcGen.MapFile.Extension, StringComparison.OrdinalIgnoreCase) ->
                match Ironsight.ProcGen.MapFile.decode (IO.File.ReadAllBytes value) with
                | Ok spec -> spec
                | Error message -> failwith $"IRONSIGHT_LEVEL '{value}' is not a valid map file: {message}"
            | None -> Ironsight.ProcGen.PaintballMap.spec
        // A map built from props cannot be encoded, so it is not offered for
        // download and announces no hash; the client resolves it by name like
        // any other builtin.
        let mapBytes =
            if Ironsight.ProcGen.MapFile.encodable matchSpec then Ironsight.ProcGen.MapFile.encode matchSpec
            else [||]
        let matchLevel = Ironsight.ProcGen.LevelCompile.compile matchSpec
        // server.json when present, otherwise the two rooms this server has
        // always hosted on the IRONSIGHT_LEVEL map. A bad config throws here,
        // at boot, rather than leaving an operator wondering why it was ignored.
        let identity, rooms = ServerConfig.load matchLevel
        let matches = MatchDirectory(identity, rooms, matchLevel, mapBytes)
        builder.Services.AddSingleton matches |> ignore
        builder.Services.AddHostedService(fun _ ->
            { new BackgroundService() with
                override _.ExecuteAsync cancellationToken = task {
                    // A sim fault must not stop the other match, or the host:
                    // an unhandled BackgroundService exception shuts the
                    // process down (StopHost is the default).
                    // Extension hooks share the isolation: a faulty hook is a
                    // logged line, never a dead room. OnEvent runs outside the
                    // gate (Snapshot has already returned) so a hook is free to
                    // call back into the host.
                    let tickSafely name (host: MatchHost) =
                        try
                            host.AdvanceTick()
                            let state = host.Snapshot()
                            for extension in extensions do
                                extension.OnEvent
                                |> Option.iter (fun hook ->
                                    for event in state.Events do
                                        if event.Tick = state.Tick then hook host event)
                                extension.OnTick |> Option.iter (fun hook -> hook host state)
                        with ex -> eprintfn $"[{name}] AdvanceTick failed: {ex}"
                    use timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / float Tuning.TickRate))
                    while! timer.WaitForNextTickAsync cancellationToken do
                        for room in matches.Rooms do
                            tickSafely room.Id room.Host
                } }) |> ignore
        let app = builder.Build()
        app.UseDefaultFiles() |> ignore
        app.UseStaticFiles() |> ignore
        app.UseWebSockets() |> ignore
        app.MapGet("/health/live", Func<string>(fun () -> "ok")) |> ignore
        app.MapGet("/health/ready", Func<string>(fun () -> "ready")) |> ignore
        app.MapGet(
            "/api/leaderboard",
            Func<HttpContext, IResult>(fun context ->
                context.Response.Headers.CacheControl <- "no-store"
                context.Response.Headers.AccessControlAllowOrigin <- "*"
                Results.Json(matches.Leaderboard())))
        |> ignore
        app.MapGet(
            "/api/arsenal",
            Func<HttpContext, IResult>(fun context ->
                context.Response.Headers.CacheControl <- "public, max-age=300"
                context.Response.Headers.AccessControlAllowOrigin <- "*"
                Results.Json(Protocol.arsenal ())))
        |> ignore
        app.MapGet(
            "/maps/{hash}",
            Func<HttpContext, string, IResult>(fun context hash ->
                // Content-addressed: the URL names the exact bytes, so an
                // aggressive cache policy can never serve a stale map (the
                // classic FastDL failure mode).
                if String.Equals(hash, matches.MapHash, StringComparison.OrdinalIgnoreCase) then
                    context.Response.Headers.CacheControl <- "public, max-age=31536000, immutable"
                    context.Response.Headers.AccessControlAllowOrigin <- "*"
                    Results.Bytes(matches.MapBytes, "application/octet-stream")
                else Results.NotFound()))
        |> ignore
        app.Map("/play", Action<IApplicationBuilder>(fun branch ->
            branch.Run(fun context -> handleSocket extensions matches context) |> ignore)) |> ignore
        app

    /// Rewrite the bundled fallback JSON inside arsenal.html from the live
    /// Tuning-driven arsenal, so the offline page can never drift from the game.
    let private syncArsenal (path: string) =
        let weapons =
            (Protocol.arsenal ()).weapons
            |> Array.map JsonSerializer.Serialize
            |> String.concat ",\n      "
        let json =
            "\n    {\"generatedFrom\":\"Bundled snapshot of Ironsight.Core.Tuning (offline mode)\",\"weapons\":[\n      "
            + weapons
            + "\n    ]}\n  "
        let html = IO.File.ReadAllText path
        let openTag = "<script id=\"arsenal-fallback\" type=\"application/json\">"
        let start = html.IndexOf openTag
        if start < 0 then failwith $"{path} has no arsenal-fallback script block"
        let contentStart = start + openTag.Length
        let contentEnd = html.IndexOf("</script>", contentStart)
        IO.File.WriteAllText(path, html[.. contentStart - 1] + json + html[contentEnd ..])

    [<EntryPoint>]
    let main args =
        match args with
        // dotnet run's working directory is the project dir, so the default
        // resolves from the source tree like build's web root does.
        | [| "--sync-arsenal" |] | [| "--sync-arsenal"; _ |] ->
            let path =
                match args with
                | [| _; explicit |] -> explicit
                | _ -> IO.Path.GetFullPath("../../website/arsenal.html", __SOURCE_DIRECTORY__)
            syncArsenal path
            printfn $"Wrote {(Protocol.arsenal ()).weapons.Length} weapons to {path}"
            0
        | _ ->
            let port =
                Environment.GetEnvironmentVariable "PORT"
                |> Option.ofObj
                |> Option.bind (fun value -> match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)
                |> Option.defaultValue 8080
            (build args port [ Commands.builtins; ChatLog.extension ]).Run()
            0
