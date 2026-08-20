namespace Ironsight.Tests

open System
open System.Collections.Generic
open System.Numerics
open System.Threading
open System.Threading.Tasks
open Ironsight
open Ironsight.Shell

/// A scripted multiplayer match. Acts set *intent* on a named bot; a background
/// pump turns that intent into input frames at tick rate, so a script never has
/// to spell out individual frames.
type Act =
    | Join of bot: string
    | JoinAs of bot: string * weapon: string
    | Leave of bot: string
    /// Reconnect using the session token handed out at the original join.
    | Rejoin of bot: string
    | Move of bot: string * direction: Vector2
    | Aim of bot: string * delta: Vector2
    /// Turn to face the nearest hostile player in the latest snapshot.
    | FaceEnemy of bot: string
    /// Hold a compass heading in radians, steered every tick. Yaw 0 faces -Z.
    /// Unlike Aim this persists, because a bot that has to walk somewhere needs
    /// to keep pointing there rather than drift.
    | Face of bot: string * yaw: float32
    | Talk of bot: string * text: string
    | Press of bot: string * buttons: InputButtons
    | Release of bot: string * buttons: InputButtons
    | Wait of seconds: float
    | WaitUntil of label: string * timeout: float * check: (OnlineSnapshot -> bool)
    | Expect of label: string * check: (OnlineSnapshot -> bool)

[<RequireQualifiedAccess>]
module MatchScript =
    type private Bot =
        { Name: string
          mutable Client: OnlineClient
          mutable Move: Vector2
          mutable Look: Vector2
          mutable Buttons: InputButtons
          mutable Heading: float32 voption
          mutable Sequence: int64
          mutable Token: string
          mutable Weapon: string }

    let private describe (bot: Bot) =
        match bot.Client.TryLatestSnapshot() with
        | None -> $"%s{bot.Name}: no snapshot received"
        | Some snapshot ->
            let players =
                snapshot.Players
                |> Array.map (fun player ->
                    $"    #%d{player.Id} %s{player.Name} %A{player.Team} pos=(%.1f{player.Position.X}, %.1f{player.Position.Y}, %.1f{player.Position.Z}) hp=%.0f{player.Health} alive=%b{player.Alive} ready=%b{player.Ready} ammo=%d{player.Ammo} k/d=%d{player.Kills}/%d{player.Deaths}")
                |> String.concat "\n"
            let events =
                snapshot.Events
                |> Array.map (fun event -> event.Kind)
                |> Array.countBy id
                |> Array.map (fun (kind, count) -> $"%s{kind}x%d{count}")
                |> String.concat " "
            $"%s{bot.Name} (id %d{bot.Client.PlayerId}) sees tick %d{snapshot.Tick} phase %A{snapshot.Phase} score %d{snapshot.AlliesScore}-%d{snapshot.AxisScore} events [%s{events}]\n%s{players}"

    /// Yaw/pitch that points `from` at `target`, matching the renderer's convention
    /// of yaw measured around +Y from -Z and eyes roughly at standing height.
    let private aimAt (from: Vector3) (target: Vector3) =
        let delta = target - from
        let yaw = MathF.Atan2(delta.X, -delta.Z)
        let flat = MathF.Sqrt(delta.X * delta.X + delta.Z * delta.Z)
        let pitch = MathF.Atan2(delta.Y, max 0.001f flat)
        yaw, pitch

    /// Drives `script` against a live server, connecting a real `OnlineClient` per
    /// named bot. Raises with the failing label and a snapshot dump on any
    /// unsatisfied Expect/WaitUntil.
    /// `room` names a room on a multi-room server; blank asks the server to
    /// pick one of `mode`, which is what every pre-rooms client does.
    let runIn (serverUri: Uri) (mode: GameMode) (room: string) (script: Act list) =
        let bots = Dictionary<string, Bot>()
        let ordered = ResizeArray<Bot>()
        use pumpCancellation = new CancellationTokenSource()

        let connect (bot: Bot) resume =
            let client = new OnlineClient(serverUri, bot.Name, mode, bot.Weapon, ?resumeToken = resume, room = room)
            client.ConnectAsync().GetAwaiter().GetResult()
            bot.Client <- client
            bot.Token <- client.SessionToken

        let bot name =
            match bots.TryGetValue name with
            | true, existing -> existing
            | _ -> failwith $"Bot '%s{name}' has not joined."

        let snapshotOf (bot: Bot) =
            match bot.Client.TryLatestSnapshot() with
            | Some snapshot -> snapshot
            | None -> failwith $"Bot '%s{bot.Name}' has not received a snapshot yet.\n%s{describe bot}"

        // The pump is the only thing that sends input. One loop for every bot keeps
        // ordering simple and stays far below the server's 120 messages/second limit.
        // ponytail: wall-clock paced, so scripts assert on outcomes rather than exact
        // ticks. Drive MatchHost.AdvanceTick directly if frame-exact replay is needed.
        let pump =
            Task.Run(fun () ->
                task {
                    while not pumpCancellation.IsCancellationRequested do
                        lock ordered (fun () ->
                            for entry in ordered do
                                if entry.Client.Connected then
                                    // Steer toward a held heading. The server clamps
                                    // look deltas, so this converges over a few ticks
                                    // rather than snapping.
                                    match entry.Heading, entry.Client.TryLatestSnapshot() with
                                    | ValueSome target, Some snapshot ->
                                        match snapshot.Players |> Array.tryFind (fun player -> player.Id = entry.Client.PlayerId) with
                                        | Some self ->
                                            let error = MathF.Atan2(MathF.Sin(target - self.Yaw), MathF.Cos(target - self.Yaw))
                                            entry.Look <- Vector2(Math.Clamp(error, -0.2f, 0.2f), entry.Look.Y)
                                        | None -> ()
                                    | _ -> ()
                                    entry.Sequence <- entry.Sequence + 1L
                                    entry.Client.QueueInput
                                        { Sequence = entry.Sequence
                                          Move = entry.Move
                                          Look = entry.Look
                                          Buttons = entry.Buttons }
                                    // Look is a per-frame delta; holding it would spin the bot.
                                    entry.Look <- Vector2.Zero)
                        do! Task.Delay(1000 / Tuning.TickRate)
                }
                :> Task)

        let pause (seconds: float) = Thread.Sleep(TimeSpan.FromSeconds seconds)

        let rec step act =
            match act with
            | Join name -> step (JoinAs(name, "Thompson"))
            | JoinAs(name, weapon) ->
                let entry =
                    { Name = name
                      Client = Unchecked.defaultof<OnlineClient>
                      Move = Vector2.Zero
                      Look = Vector2.Zero
                      Buttons = InputButtons.None
                      Heading = ValueNone
                      Sequence = 0L
                      Token = ""
                      Weapon = weapon }
                connect entry None
                bots[name] <- entry
                lock ordered (fun () -> ordered.Add entry)
            | Leave name ->
                let entry = bot name
                entry.Client.CloseAsync().GetAwaiter().GetResult()
                (entry.Client :> IDisposable).Dispose()
            | Rejoin name ->
                let entry = bot name
                connect entry (Some entry.Token)
            | Move(name, direction) -> (bot name).Move <- direction
            | Aim(name, delta) -> (bot name).Look <- delta
            | Face(name, yaw) -> (bot name).Heading <- ValueSome yaw
            | Talk(name, text) -> (bot name).Client.SendChat text
            | Press(name, buttons) ->
                let entry = bot name
                entry.Buttons <- entry.Buttons ||| buttons
            | Release(name, buttons) ->
                let entry = bot name
                entry.Buttons <- entry.Buttons &&& ~~~buttons
            | FaceEnemy name ->
                let entry = bot name
                let snapshot = snapshotOf entry
                match snapshot.Players |> Array.tryFind (fun player -> player.Id = entry.Client.PlayerId) with
                | None -> failwith $"Bot '%s{name}' is not in the snapshot.\n%s{describe entry}"
                | Some self ->
                    let hostile =
                        snapshot.Players
                        |> Array.filter (fun player -> player.Id <> self.Id && player.Alive && (mode = FreeForAll || player.Team <> self.Team))
                        |> Array.sortBy (fun player -> Vector3.DistanceSquared(player.Position, self.Position))
                        |> Array.tryHead
                    match hostile with
                    | None -> failwith $"Bot '%s{name}' found no hostile to face.\n%s{describe entry}"
                    | Some target ->
                        let eye = Vector3(0.0f, Tuning.StandingHeight * 0.9f, 0.0f)
                        let yaw, pitch = aimAt (self.Position + eye) (target.Position + eye)
                        // Look is a delta, so steer by the remaining error each frame.
                        entry.Look <- Vector2(MathF.Atan2(MathF.Sin(yaw - self.Yaw), MathF.Cos(yaw - self.Yaw)), pitch - self.Pitch)
            | Wait seconds -> pause seconds
            | WaitUntil(label, timeout, check) ->
                let deadline = DateTime.UtcNow.AddSeconds timeout
                let mutable satisfied = false
                while not satisfied && DateTime.UtcNow < deadline do
                    satisfied <- ordered |> Seq.exists (fun entry -> entry.Client.TryLatestSnapshot() |> Option.exists check)
                    if not satisfied then Thread.Sleep 50
                if not satisfied then
                    let dump = ordered |> Seq.map describe |> String.concat "\n"
                    failwith $"Timed out after %.1f{timeout}s waiting for: %s{label}\n%s{dump}"
            | Expect(label, check) ->
                let satisfied = ordered |> Seq.exists (fun entry -> entry.Client.TryLatestSnapshot() |> Option.exists check)
                if not satisfied then
                    let dump = ordered |> Seq.map describe |> String.concat "\n"
                    failwith $"Expectation failed: %s{label}\n%s{dump}"

        try
            for act in script do
                step act
        finally
            pumpCancellation.Cancel()
            try pump.Wait(TimeSpan.FromSeconds 2.0) |> ignore with _ -> ()
            for entry in ordered do
                try entry.Client.CloseAsync().GetAwaiter().GetResult() with _ -> ()
                try (entry.Client :> IDisposable).Dispose() with _ -> ()

    /// Any room of the requested mode, the way a client that predates rooms
    /// connects. Most scripts want this.
    let run (serverUri: Uri) (mode: GameMode) (script: Act list) = runIn serverUri mode "" script

    /// The snapshot the named bot currently sees, for assertions that need one
    /// specific viewpoint rather than "any connected bot".
    let selfOf (playerName: string) (snapshot: OnlineSnapshot) =
        snapshot.Players |> Array.tryFind (fun player -> player.Name = playerName)
