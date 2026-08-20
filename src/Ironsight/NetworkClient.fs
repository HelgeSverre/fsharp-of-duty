namespace Ironsight.Shell

open System
open System.Collections.Generic
open System.Net.WebSockets
open System.Numerics
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open Ironsight

[<Struct>]
type OnlinePlayer =
    { Id: int
      Name: string
      Team: Team
      Position: Vector3
      Velocity: Vector3
      Yaw: float32
      Pitch: float32
      Stance: Stance
      Health: float32
      Alive: bool
      Ready: bool
      Ads: float32
      Ammo: int
      Reserve: int
      WeaponName: string
      ReloadRemaining: float32
      Kills: int
      Deaths: int
      BestStreak: int
      AcknowledgedInput: int64 }

[<Struct>]
type OnlineGrenade =
    { OwnerId: int
      Position: Vector3
      Fuse: float32 }

[<Struct>]
type OnlineEvent =
    { Id: int64
      Tick: int64
      Kind: string
      EntityId: int
      RecipientId: int
      Position: Vector3
      Direction: Vector3
      Value: float32
      Text: string }

type OnlineSnapshot =
    { Tick: int64
      Mode: GameMode
      LevelName: string
      Phase: MatchPhase
      AlliesScore: int
      AxisScore: int
      Players: OnlinePlayer array
      Grenades: OnlineGrenade array
      Events: OnlineEvent array }

/// Pure JSON -> snapshot decoding, lifted out of OnlineClient so the wire
/// format can be round-trip tested against Protocol.snapshot without a socket.
[<RequireQualifiedAccess>]
module SnapshotWire =
    let parseTeam = function "Allies" -> Allies | _ -> Axis
    let parseStance = function "Crouched" -> Crouched | "Prone" -> Prone | _ -> Standing
    let parsePhase = function "Warmup" -> Warmup | "Playing" -> Playing | "Results" -> Results | _ -> Waiting
    let parseMode = function "FreeForAll" -> FreeForAll | _ -> TeamDeathmatch

    let getString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.String -> property.GetString() |> Option.ofObj |> Option.defaultValue ""
        | _ -> ""

    let getFloat (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetSingle() with true, value -> value | _ -> 0.0f
        | _ -> 0.0f

    let getInt (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetInt32() with true, value -> value | _ -> 0
        | _ -> 0

    let getInt64 (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetInt64() with true, value -> value | _ -> 0L
        | _ -> 0L

    let getBool (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.True -> true
        | _ -> false

    let parseSnapshot (root: JsonElement) =
        let players =
            root.GetProperty("players").EnumerateArray()
            |> Seq.map (fun value ->
                { Id = getInt "id" value
                  Name = getString "name" value
                  Team = getString "team" value |> parseTeam
                  Position = Vector3(getFloat "x" value, getFloat "y" value, getFloat "z" value)
                  Velocity = Vector3(getFloat "vx" value, getFloat "vy" value, getFloat "vz" value)
                  Yaw = getFloat "yaw" value
                  Pitch = getFloat "pitch" value
                  Stance = getString "stance" value |> parseStance
                  Health = getFloat "health" value
                  Alive = getBool "alive" value
                  Ready = getBool "ready" value
                  Ads = getFloat "ads" value
                  Ammo = getInt "ammo" value
                  Reserve = getInt "reserve" value
                  WeaponName = getString "weapon" value
                  ReloadRemaining = getFloat "reloadRemaining" value
                  Kills = getInt "kills" value
                  Deaths = getInt "deaths" value
                  BestStreak = getInt "bestStreak" value
                  AcknowledgedInput = getInt64 "acknowledgedInput" value })
            |> Seq.toArray
        let grenades =
            match root.TryGetProperty "grenades" with
            | true, values ->
                values.EnumerateArray()
                |> Seq.map (fun value ->
                    { OwnerId = getInt "ownerId" value
                      Position = Vector3(getFloat "x" value, getFloat "y" value, getFloat "z" value)
                      Fuse = getFloat "fuse" value })
                |> Seq.toArray
            | _ -> [||]
        let events =
            match root.TryGetProperty "events" with
            | true, values ->
                values.EnumerateArray()
                |> Seq.map (fun value ->
                    { Id = getInt64 "id" value
                      Tick = getInt64 "tick" value
                      Kind = getString "kind" value
                      EntityId = getInt "entityId" value
                      RecipientId = getInt "recipientId" value
                      Position = Vector3(getFloat "x" value, getFloat "y" value, getFloat "z" value)
                      Direction = Vector3(getFloat "dx" value, getFloat "dy" value, getFloat "dz" value)
                      Value = getFloat "value" value
                      Text = getString "text" value })
                |> Seq.toArray
            | _ -> [||]
        { Tick = getInt64 "tick" root
          Mode = getString "mode" root |> parseMode
          LevelName = getString "level" root
          Phase = getString "phase" root |> parsePhase
          AlliesScore = getInt "alliesScore" root
          AxisScore = getInt "axisScore" root
          Players = players
          Grenades = grenades
          Events = events }

type OnlineClient(serverUri: Uri, playerName: string, requestedMode: GameMode, weaponName: string, ?resumeToken: string) =
    let socket = new ClientWebSocket()
    let cancellation = new CancellationTokenSource()
    let inputChannel = Channel.CreateBounded<InputFrame>(BoundedChannelOptions(4, FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true))
    let snapshots = Queue<OnlineSnapshot>()
    let snapshotGate = obj ()
    let mutable playerId = 0
    let requestedSessionToken = defaultArg resumeToken ""
    let mutable sessionToken = requestedSessionToken
    let mutable levelName = ""
    let mutable mapHash = ""
    // Estimated offset between the server tick counter and the local monotonic
    // clock, in ticks. Smoothing it (instead of re-anchoring on every snapshot
    // arrival) keeps the interpolation target advancing steadily through
    // network jitter rather than jumping each time a snapshot lands.
    let mutable clockOffsetTicks = nan
    let mutable latestServerTick = 0L

    let localTicks () =
        float (System.Diagnostics.Stopwatch.GetTimestamp()) / float System.Diagnostics.Stopwatch.Frequency * float Tuning.TickRate
    let mutable receiveTask: Task = Task.CompletedTask
    let mutable sendTask: Task = Task.CompletedTask
    let mutable closing = 0
    let mutable disposed = 0
    let receiveBuffer = Array.zeroCreate<byte> (256 * 1024)

    let jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let sendJson value = task {
        let bytes = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)
        do! socket.SendAsync(ReadOnlyMemory bytes, WebSocketMessageType.Text, true, cancellation.Token)
    }

    let receiveDocument () = task {
        let mutable total = 0
        let mutable finished = false
        let mutable closed = false
        while not finished && not closed do
            let! result = socket.ReceiveAsync(Memory(receiveBuffer, total, receiveBuffer.Length - total), cancellation.Token)
            if result.MessageType = WebSocketMessageType.Close then closed <- true
            else
                total <- total + result.Count
                finished <- result.EndOfMessage
                if total = receiveBuffer.Length && not finished then invalidOp "Server message exceeds 256 KiB."
        if closed then return None
        else return Some(JsonDocument.Parse(ReadOnlyMemory(receiveBuffer, 0, total)))
    }

    let addSnapshot snapshot =
        lock snapshotGate (fun () ->
            if snapshots.Count = 0 || snapshot.Tick > (snapshots |> Seq.last).Tick then
                snapshots.Enqueue snapshot
                while snapshots.Count > 32 do snapshots.Dequeue() |> ignore
                let observed = float snapshot.Tick - localTicks ()
                clockOffsetTicks <-
                    if Double.IsNaN clockOffsetTicks then observed
                    else clockOffsetTicks + 0.1 * (observed - clockOffsetTicks)
                latestServerTick <- snapshot.Tick)

    let receiveLoop () = task {
        try
            while socket.State = WebSocketState.Open && not cancellation.IsCancellationRequested do
                let! incoming = receiveDocument ()
                match incoming with
                | None -> cancellation.Cancel()
                | Some document ->
                    use document = document
                    let root = document.RootElement
                    if SnapshotWire.getString "type" root = "snapshot" then SnapshotWire.parseSnapshot root |> addSnapshot
        with
        | :? OperationCanceledException -> ()
        | :? WebSocketException -> cancellation.Cancel()
        | error ->
            Console.Error.WriteLine($"Receive loop failed: {error.Message}")
            cancellation.Cancel()
    }

    // Loadout requests ride the sender loop so the socket never sees two
    // concurrent SendAsync calls. null = nothing pending.
    let mutable pendingLoadout: string = null
    // Chat rides the same loop, but queued rather than a single slot: two lines
    // typed inside one sender pass must both go out, not clobber each other.
    let pendingChat = Collections.Concurrent.ConcurrentQueue<string>()

    let senderLoop () = task {
        try
            while! inputChannel.Reader.WaitToReadAsync(cancellation.Token) do
                match Interlocked.Exchange(&pendingLoadout, null) with
                | null -> ()
                | weapon -> do! sendJson {| ``type`` = "loadout"; version = 1; weapon = weapon |}
                let mutable line = Unchecked.defaultof<string>
                while pendingChat.TryDequeue(&line) do
                    do! sendJson {| ``type`` = "chat"; version = 1; text = line |}
                let mutable input = Unchecked.defaultof<InputFrame>
                while inputChannel.Reader.TryRead(&input) do
                    let payload =
                        {| ``type`` = "input"
                           version = 1
                           sequence = input.Sequence
                           moveX = input.Move.X
                           moveY = input.Move.Y
                           lookX = input.Look.X
                           lookY = input.Look.Y
                           estimatedServerTick = latestServerTick
                           buttons = int input.Buttons |}
                    do! sendJson payload
        with
        | :? OperationCanceledException -> ()
        | :? ObjectDisposedException -> ()
        | :? WebSocketException -> cancellation.Cancel()
    }

    let interpolatePlayer (amount: float32) (first: OnlinePlayer) (second: OnlinePlayer) =
        { second with
            Position = Vector3.Lerp(first.Position, second.Position, amount)
            Yaw = MathEx.lerpAngle first.Yaw second.Yaw amount
            Pitch = first.Pitch + (second.Pitch - first.Pitch) * amount }

    member _.ServerUri = serverUri
    member _.PlayerId = playerId
    member _.SessionToken = sessionToken
    /// Announced by the server's welcome; "" on servers predating map sync.
    member _.LevelName = levelName
    member _.MapHash = mapHash
    member _.Connected = socket.State = WebSocketState.Open && not cancellation.IsCancellationRequested

    member _.ConnectAsync() = task {
        socket.Options.KeepAliveInterval <- TimeSpan.FromSeconds 10.0
        socket.Options.KeepAliveTimeout <- TimeSpan.FromSeconds 20.0
        use timeout = CancellationTokenSource.CreateLinkedTokenSource cancellation.Token
        timeout.CancelAfter(TimeSpan.FromSeconds 8.0)
        do! socket.ConnectAsync(serverUri, timeout.Token)
        let modeName = if requestedMode = FreeForAll then "FreeForAll" else "TeamDeathmatch"
        do!
            sendJson
                {| ``type`` = "hello"
                   version = 1
                   name = playerName
                   mode = modeName
                   weapon = weaponName
                   sessionToken = requestedSessionToken |}
        let! welcome = receiveDocument ()
        match welcome with
        | None -> invalidOp "Server closed before welcome."
        | Some document ->
            use document = document
            let root = document.RootElement
            if SnapshotWire.getString "type" root <> "welcome" || SnapshotWire.getInt "version" root <> 1 then invalidOp "Incompatible server welcome."
            playerId <- SnapshotWire.getInt "playerId" root
            sessionToken <- SnapshotWire.getString "sessionToken" root
            levelName <- SnapshotWire.getString "level" root
            mapHash <- SnapshotWire.getString "mapHash" root
        do! sendJson {| ``type`` = "ready"; version = 1 |}
        receiveTask <- receiveLoop () :> Task
        sendTask <- senderLoop () :> Task
    }

    member _.QueueInput(input: InputFrame) = inputChannel.Writer.TryWrite input |> ignore

    /// Ask the server to change loadout; applied server-side on next spawn (or
    /// immediately outside live play). Sent on the next sender-loop pass.
    member _.RequestLoadout(weaponName: string) = Interlocked.Exchange(&pendingLoadout, weaponName) |> ignore

    /// Queue a chat line for the next sender-loop pass. The server sanitizes
    /// and rate-limits it; the reply arrives as a Chat event on the snapshot.
    member _.SendChat(text: string) = pendingChat.Enqueue text

    member _.TryLatestSnapshot() =
        lock snapshotGate (fun () -> if snapshots.Count = 0 then None else Some(snapshots |> Seq.last))

    member _.TryInterpolatedSnapshot() =
        lock snapshotGate (fun () ->
            if snapshots.Count = 0 then None
            elif snapshots.Count = 1 then Some(snapshots.Peek())
            else
                let history = snapshots.ToArray()
                // Render ~100 ms (6 ticks) behind the estimated server clock so
                // there is nearly always a newer snapshot to interpolate toward.
                let targetTick = localTicks () + clockOffsetTicks - 6.0
                let mutable first = history[0]
                let mutable second = history[1]
                for index in 1..history.Length - 1 do
                    if float history[index].Tick <= targetTick then
                        first <- history[index]
                        second <- history[min (history.Length - 1) (index + 1)]
                let denominator = max 1.0 (float (second.Tick - first.Tick))
                let amount = float32 (Math.Clamp((targetTick - float first.Tick) / denominator, 0.0, 1.5))
                let firstById = first.Players |> Array.map (fun player -> player.Id, player) |> Map.ofArray
                let players =
                    second.Players
                    |> Array.map (fun player ->
                        match Map.tryFind player.Id firstById with
                        | Some previous -> interpolatePlayer amount previous player
                        | None -> player)
                let grenades =
                    second.Grenades
                    |> Array.mapi (fun index grenade ->
                        if index < first.Grenades.Length && first.Grenades[index].OwnerId = grenade.OwnerId then
                            { grenade with Position = Vector3.Lerp(first.Grenades[index].Position, grenade.Position, amount) }
                        else grenade)
                Some { second with Players = players; Grenades = grenades })

    member _.CloseAsync() = task {
        if Interlocked.CompareExchange(&closing, 1, 0) = 0 then
            if socket.State = WebSocketState.Open then
                try do! sendJson {| ``type`` = "leave"; version = 1 |}
                with _ -> ()
            cancellation.Cancel()
            inputChannel.Writer.TryComplete() |> ignore
            socket.Abort()
            try do! Task.WhenAll(receiveTask, sendTask)
            with _ -> ()
    }

    interface IDisposable with
        member _.Dispose() =
            if Interlocked.Exchange(&disposed, 1) = 0 then
                try cancellation.Cancel()
                with :? ObjectDisposedException -> ()
                inputChannel.Writer.TryComplete() |> ignore
                socket.Abort()
                socket.Dispose()
                cancellation.Dispose()
