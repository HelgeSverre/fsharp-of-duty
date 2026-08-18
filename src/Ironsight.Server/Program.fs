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
    type private MatchDirectory(level: Level) =
        member val TeamDeathmatch = MatchHost(TeamDeathmatch, level)
        member val FreeForAll = MatchHost(FreeForAll, level)

        member this.Leaderboard() =
            Protocol.leaderboard [| this.TeamDeathmatch.Snapshot(); this.FreeForAll.Snapshot() |]

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

    let private send value (socket: WebSocket) cancellationToken = task {
        let bytes = Protocol.serialize value
        do! socket.SendAsync(ReadOnlyMemory bytes, WebSocketMessageType.Text, true, cancellationToken)
    }

    let private handleSocket (matches: MatchDirectory) (context: HttpContext) = task {
        if not context.WebSockets.IsWebSocketRequest then
            context.Response.StatusCode <- StatusCodes.Status400BadRequest
        else
            use! socket = context.WebSockets.AcceptWebSocketAsync()
            let cancellationToken = context.RequestAborted
            let! hello = receiveMessage socket cancellationToken
            match hello with
            | None -> ()
            | Some document ->
                use document = document
                let root = document.RootElement
                match Protocol.tryString "type" root, Protocol.tryString "name" root, Protocol.tryInt64 "version" root with
                | Some "hello", Some name, Some version when version = int64 Protocol.Version ->
                    let host =
                        match Protocol.tryString "mode" root with
                        | Some value when String.Equals(value, "FreeForAll", StringComparison.OrdinalIgnoreCase) -> matches.FreeForAll
                        | _ -> matches.TeamDeathmatch
                    let resumeToken = Protocol.tryString "sessionToken" root
                    let weaponName = Protocol.tryString "weapon" root
                    match host.TryAddPlayer(name, ?weaponName = weaponName, ?sessionToken = resumeToken) with
                    | None ->
                        do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "The match is full.", cancellationToken)
                    | Some(playerId, token) ->
                    try
                        try
                            do! send (Protocol.welcome playerId token) socket cancellationToken
                            let mutable connected = true
                            let mutable pendingReceive = receiveMessage socket cancellationToken
                            // The snapshot timer must keep its own cadence. Starting a
                            // fresh delay after every message meant a client streaming
                            // inputs at tick rate starved itself of snapshots forever.
                            let mutable snapshotDelay = Task.Delay(50, cancellationToken)
                            let mutable rateWindow = Stopwatch.GetTimestamp()
                            let mutable messagesInWindow = 0
                            while connected && socket.State = WebSocketState.Open && not cancellationToken.IsCancellationRequested do
                                let! completed = Task.WhenAny(pendingReceive, snapshotDelay)
                                if Object.ReferenceEquals(completed, snapshotDelay) then
                                    do! send (host.Snapshot() |> Protocol.snapshot) socket cancellationToken
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
                                            let message = inputDocument.RootElement
                                            match Protocol.tryString "type" message with
                                            | Some "input" -> host.ApplyInput(playerId, message)
                                            | Some "ready" -> host.SetReady playerId
                                            | Some "leave" -> connected <- false
                                            | _ -> ()
                                        if connected then pendingReceive <- receiveMessage socket cancellationToken
                                    | None -> connected <- false
                        with
                        | :? WebSocketException -> ()
                        | :? OperationCanceledException -> ()
                    finally
                        host.RemovePlayer playerId
                | _ ->
                    do! socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "First message must be a compatible hello.", cancellationToken)
    }

    /// Builds the configured application without starting it. Tests bind port 0
    /// to get an ephemeral port; the real process passes the PORT env value.
    let build (args: string array) (port: int) =
        let sourceWebRoot = IO.Path.GetFullPath("../../website", __SOURCE_DIRECTORY__)
        let options =
            WebApplicationOptions(
                Args = args,
                WebRootPath = if IO.Directory.Exists sourceWebRoot then sourceWebRoot else null)
        let builder = WebApplication.CreateBuilder options
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}") |> ignore
        let matchLevel =
            match Environment.GetEnvironmentVariable "IRONSIGHT_LEVEL" with
            | value when String.Equals(value, "stalingrad", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.stalingradStreet
            | value when String.Equals(value, "training", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.trainingYard
            | value when String.Equals(value, "battlefield", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.battlefield
            | value when String.Equals(value, "depot", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.scrapDepot
            | value when String.Equals(value, "canal", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.canalYard
            | value when String.Equals(value, "omaha", StringComparison.OrdinalIgnoreCase) -> Ironsight.ProcGen.Levels.omahaDraw
            | _ -> Ironsight.ProcGen.Levels.paintballArena
        let matches = MatchDirectory(matchLevel)
        builder.Services.AddSingleton matches |> ignore
        builder.Services.AddHostedService(fun _ ->
            { new BackgroundService() with
                override _.ExecuteAsync cancellationToken = task {
                    use timer = new PeriodicTimer(TimeSpan.FromSeconds(1.0 / float Tuning.TickRate))
                    while! timer.WaitForNextTickAsync cancellationToken do
                        matches.TeamDeathmatch.AdvanceTick()
                        matches.FreeForAll.AdvanceTick()
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
        app.Map("/play", Action<IApplicationBuilder>(fun branch ->
            branch.Run(fun context -> handleSocket matches context) |> ignore)) |> ignore
        app

    [<EntryPoint>]
    let main args =
        let port =
            Environment.GetEnvironmentVariable "PORT"
            |> Option.ofObj
            |> Option.bind (fun value -> match Int32.TryParse value with true, parsed -> Some parsed | _ -> None)
            |> Option.defaultValue 8080
        (build args port).Run()
        0
