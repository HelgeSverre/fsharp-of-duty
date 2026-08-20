namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.Server
open Ironsight.Shell
open Xunit

/// End-to-end tests over a real WebSocket. Everything below the socket —
/// MatchHost input handling, scoring, respawn — is covered far more cheaply in
/// ServerTests; these exist to prove the transport, handshake, snapshot pump and
/// tick loop actually work when a client connects.
///
/// Set IRONSIGHT_SMOKE_SERVER to point the tolerant scenario at a deployed
/// server instead of an in-process one.
module IntegrationTests =
    [<Literal>]
    let private Integration = "Integration"

    let private startServer = TestKit.startServer

    let private remoteServer () =
        match Environment.GetEnvironmentVariable "IRONSIGHT_SMOKE_SERVER" with
        | value when not (String.IsNullOrWhiteSpace value) -> Some(Uri value)
        | _ -> None

    let private alive (name: string) (snapshot: OnlineSnapshot) =
        MatchScript.selfOf name snapshot |> Option.isSome

    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``a configured server hosts many rooms and joins the one it is asked for`` () =
        // Three rooms on one process, two of them the same mode on different
        // maps — the case two hardcoded MatchHosts could never express.
        let config = IO.Path.Combine(IO.Path.GetTempPath(), $"ironsight-rooms-{Guid.NewGuid():N}.json")
        IO.File.WriteAllText(
            config,
            """
            { "rooms": [
                { "id": "tdm-canal",  "name": "Canal TDM",   "mode": "TeamDeathmatch", "level": "canal" },
                { "id": "tdm-omaha",  "name": "Omaha TDM",   "mode": "TeamDeathmatch", "level": "omaha" },
                { "id": "ffa-depot",  "name": "Depot FFA",   "mode": "FreeForAll",     "level": "depot" } ] }""")
        let previous = Environment.GetEnvironmentVariable "IRONSIGHT_CONFIG"
        Environment.SetEnvironmentVariable("IRONSIGHT_CONFIG", config)
        let app, uri = try startServer () finally Environment.SetEnvironmentVariable("IRONSIGHT_CONFIG", previous)
        try
            // Named room wins, and it is the *second* room of its mode — so
            // this cannot pass by falling back to "first of that mode".
            MatchScript.runIn uri TeamDeathmatch "tdm-omaha" [
                Join "Named"
                WaitUntil("the named room's own map is running", 10.0, fun snapshot ->
                    snapshot.LevelName = Ironsight.ProcGen.Levels.omahaDraw.Name)
                Leave "Named"
            ]
            // No room id: a pre-rooms client still lands in a room of its mode.
            MatchScript.run uri FreeForAll [
                Join "Legacy"
                WaitUntil("mode alone still finds a room", 10.0, fun snapshot ->
                    snapshot.Mode = FreeForAll && snapshot.LevelName = Ironsight.ProcGen.Levels.scrapDepot.Name)
                Leave "Legacy"
            ]
        finally
            app.StopAsync().GetAwaiter().GetResult()
            try IO.File.Delete config with _ -> ()

    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``a lone player sees their own chat line come back`` () =
        // The solo case on purpose: one player never reaches Warmup, so this
        // also pins that chat does not depend on the match having started.
        // Every layer passes in isolation; only a real socket covers the seam
        // between SendChat's queue, the server dispatch and the snapshot pump.
        //
        // Honours IRONSIGHT_SMOKE_SERVER so the same scenario can confirm a
        // deployment carries chat at all: a server built before chat existed
        // drops the message on its dispatch's catch-all arm and says nothing,
        // which is indistinguishable from a client bug from the outside.
        let app, uri =
            match remoteServer () with
            | Some remote -> None, remote
            | None ->
                let started, local = startServer ()
                Some started, local
        try
            MatchScript.run uri TeamDeathmatch [
                Join "Alpha"
                WaitUntil("the player appears in a snapshot", 10.0, alive "Alpha")
                Talk("Alpha", "hello")
                WaitUntil("the sender's own line comes back", 10.0, fun snapshot ->
                    snapshot.Events
                    |> Array.exists (fun event -> event.Kind = "chat" && event.Text.Contains "hello"))
                // Drive the client-side pipeline the game actually runs on the
                // received snapshot, in two steps so a failure names the stage.
                Expect("the wire event decodes to a Chat", fun snapshot ->
                    snapshot.Events
                    |> Array.choose OnlineWorld.eventToGameEvent
                    |> Array.exists (function Chat(_, _, line) -> line = "hello" | _ -> false))
                Expect("the decoded Chat becomes a HUD row", fun snapshot ->
                    let decoded =
                        snapshot.Events
                        |> Array.filter (fun event -> event.RecipientId = 0)
                        |> Array.choose OnlineWorld.eventToGameEvent
                        |> Array.toList
                    Program.Feedback.applyFeed (fun _ -> "Alpha") decoded Program.Feedback.empty
                    |> fun state -> state.Chat |> List.exists (fun item -> item.Text.Contains "hello"))
            ]
        finally
            app |> Option.iter (fun started -> started.StopAsync().GetAwaiter().GetResult())

    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``two players connect, warm up, and trade fire`` () =
        let app, uri = startServer ()
        try
            let mutable startAmmo = 0
            let mutable startPosition = Vector3.Zero
            MatchScript.run uri TeamDeathmatch [
                Join "Alpha"
                Join "Bravo"
                WaitUntil("both players appear in a snapshot", 10.0, fun snapshot ->
                    alive "Alpha" snapshot && alive "Bravo" snapshot)

                // Two ready players start Warmup, which runs for ten seconds.
                WaitUntil("the match reaches Playing", 30.0, fun snapshot -> snapshot.Phase = Playing)

                Expect("Alpha and Bravo are on opposing teams", fun snapshot ->
                    match MatchScript.selfOf "Alpha" snapshot, MatchScript.selfOf "Bravo" snapshot with
                    | Some first, Some second -> first.Team <> second.Team
                    | _ -> false)
                Expect("record Alpha's starting state", fun snapshot ->
                    match MatchScript.selfOf "Alpha" snapshot with
                    | Some self ->
                        startAmmo <- self.Ammo
                        startPosition <- self.Position
                        self.Ammo > 0
                    | None -> false)

                // Movement is authoritative: the server must move Alpha for us.
                Move("Alpha", Vector2(0.0f, 1.0f))
                WaitUntil("the server moves Alpha", 5.0, fun snapshot ->
                    match MatchScript.selfOf "Alpha" snapshot with
                    | Some self -> Vector3.Distance(self.Position, startPosition) > 0.5f
                    | None -> false)
                Move("Alpha", Vector2.Zero)

                // Aim at Bravo and hold the trigger. Spawns are picked without enemy
                // line of sight, so assert on shots leaving the barrel rather than on
                // a kill — hit resolution is covered in ServerTests.
                FaceEnemy "Alpha"
                Press("Alpha", InputButtons.Fire)
                WaitUntil("Alpha spends ammo", 5.0, fun snapshot ->
                    match MatchScript.selfOf "Alpha" snapshot with
                    | Some self -> self.Ammo < startAmmo || self.Ammo = 0
                    | None -> false)
                WaitUntil("shot events reach the other client", 5.0, fun snapshot ->
                    snapshot.Events |> Array.exists (fun event -> event.Kind = "shot"))
                Release("Alpha", InputButtons.Fire)

                // Chat is the only client message with a text payload, so this
                // is where a rename of the wire field would surface.
                Talk("Alpha", "on your left")
                WaitUntil("Alpha's chat line reaches the other client", 5.0, fun snapshot ->
                    snapshot.Events
                    |> Array.exists (fun event -> event.Kind = "chat" && event.Text = "Alpha\ton your left"))

                Leave "Alpha"
                Leave "Bravo"
            ]
        finally
            app.StopAsync().GetAwaiter().GetResult()

    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``a dropped player resumes into the same slot`` () =
        let app, uri = startServer ()
        try
            let mutable originalId = 0
            MatchScript.run uri TeamDeathmatch [
                Join "Alpha"
                Join "Bravo"
                WaitUntil("both players appear in a snapshot", 10.0, fun snapshot ->
                    alive "Alpha" snapshot && alive "Bravo" snapshot)
                Expect("record Bravo's player id", fun snapshot ->
                    match MatchScript.selfOf "Bravo" snapshot with
                    | Some self ->
                        originalId <- self.Id
                        self.Id > 0
                    | None -> false)

                Leave "Bravo"
                Wait 1.0
                // Slots are reserved for thirty seconds after a disconnect.
                Rejoin "Bravo"
                WaitUntil("Bravo returns with the same id", 10.0, fun snapshot ->
                    match MatchScript.selfOf "Bravo" snapshot with
                    | Some self -> self.Id = originalId
                    | None -> false)

                Leave "Alpha"
                Leave "Bravo"
            ]
        finally
            app.StopAsync().GetAwaiter().GetResult()

    /// The post-deploy smoke check. Deliberately tolerant: against the live server
    /// other players may be in the room and the match may be in any phase, so it
    /// only asserts what must hold everywhere — we get in, we are visible, and the
    /// simulation is advancing.
    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``the server accepts a client and streams an advancing simulation`` () =
        let app, uri =
            match remoteServer () with
            | Some remote -> None, remote
            | None ->
                let app, local = startServer ()
                Some app, local
        try
            let mutable firstTick = -1L
            MatchScript.run uri TeamDeathmatch [
                Join "Smoke"
                WaitUntil("a snapshot arrives", 15.0, fun snapshot ->
                    firstTick <- snapshot.Tick
                    true)
                Expect("the connected client is in the player list", alive "Smoke")
                // A rising tick proves both the 60 Hz match loop and the 20 Hz
                // snapshot pump are alive, which /health/ready cannot tell us.
                WaitUntil("the server tick advances", 10.0, fun snapshot -> snapshot.Tick > firstTick + 30L)
                Leave "Smoke"
            ]
        finally
            app |> Option.iter (fun host -> host.StopAsync().GetAwaiter().GetResult())

    /// The end-to-end gate for the terrain work: a real client, over a real
    /// socket, against the authoritative server, wading out of the sea and
    /// climbing the draw. Water damping, triangle collision, the slope limit
    /// and client prediction all have to agree for this to pass.
    [<Fact>]
    [<Trait("Category", Integration)>]
    let ``an attacker wades ashore and climbs the draw`` () =
        // The server picks its level from the environment at build time.
        let previous = Environment.GetEnvironmentVariable "IRONSIGHT_LEVEL"
        Environment.SetEnvironmentVariable("IRONSIGHT_LEVEL", "omaha")
        let app, uri = try startServer () finally Environment.SetEnvironmentVariable("IRONSIGHT_LEVEL", previous)
        try
            MatchScript.run uri TeamDeathmatch [
                Join "Climber"   // first in: Allies, the assaulting side
                Join "Anchor"
                WaitUntil("the match reaches Playing", 30.0, fun snapshot -> snapshot.Phase = Playing)
                // Beach-side is a Z fact, not a height fact: the Allies squad
                // spawns at z <= -23, the Axis squad at z = +25. Asserting on
                // height was flaky — one legitimate surf spawn sits on a dune
                // at y = 0.59, and which spawn a player rolls depends on the
                // server tick at round start (wall-clock timing).
                Expect("the attacker respawns on the beach side", fun snapshot ->
                    match MatchScript.selfOf "Climber" snapshot with
                    | Some self -> self.Position.Z < -20.0f
                    | None -> false)

                // West along the surf until level with the draw's breach lane.
                Face("Climber", -MathF.PI / 2.0f)
                Move("Climber", Vector2(0.0f, 1.0f))
                WaitUntil("the attacker lines up on the draw", 35.0, fun snapshot ->
                    match MatchScript.selfOf "Climber" snapshot with
                    | Some self -> self.Position.X < -11.0f
                    | None -> false)

                // Inland, across the sand and up the draw. Yaw pi faces +Z.
                Face("Climber", MathF.PI)
                WaitUntil("the attacker gains the crest", 45.0, fun snapshot ->
                    match MatchScript.selfOf "Climber" snapshot with
                    | Some self -> self.Position.Y > 6.0f
                    | None -> false)

                Leave "Climber"
                Leave "Anchor"
            ]
        finally
            app.StopAsync().GetAwaiter().GetResult()
