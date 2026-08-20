namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Server
open Xunit

module ServerTests =
    let private applyCustom = TestKit.applyCustom
    let private applyInput = TestKit.applyInput

    /// Ticks out the one-per-second chat/command/announce cooldowns. Measured
    /// in ticks, so no test ever sleeps.
    let private waitOutCooldown (host: MatchHost) =
        for _ in 1 .. int Tuning.TickRate do host.AdvanceTick()

    [<Fact>]
    let ``burst inputs are buffered and applied at the server tick rate`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Runner").Value
        let before = host.Snapshot().Players[playerId].Position
        let maxTickDistance = Tuning.WalkSpeed * Tuning.SprintMultiplier / float32 Tuning.TickRate + 0.001f
        applyInput 1 host playerId
        applyInput 2 host playerId
        applyInput 3 host playerId
        host.AdvanceTick()
        let afterOne = host.Snapshot().Players[playerId]
        // The fresh player has a single input credit, so one tick applies one
        // frame; the rest stay buffered instead of being dropped and falsely
        // acknowledged.
        Assert.InRange(Vector3.Distance(before, afterOne.Position), 0.0f, maxTickDistance)
        Assert.Equal(1L, afterOne.LastInputSequence)
        host.AdvanceTick()
        host.AdvanceTick()
        let drained = host.Snapshot().Players[playerId]
        Assert.Equal(3L, drained.LastInputSequence)
        Assert.InRange(Vector3.Distance(before, drained.Position), 0.0f, 3.0f * maxTickDistance)

    [<Fact>]
    let ``sustained input flooding cannot exceed one applied frame per tick`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Flooder").Value
        let before = host.Snapshot().Players[playerId].Position
        let maxTickDistance = Tuning.WalkSpeed * Tuning.SprintMultiplier / float32 Tuning.TickRate + 0.001f
        let mutable sequence = 1L
        for _ in 1..30 do
            // Two frames arrive every tick — double the legitimate rate.
            applyInput sequence host playerId
            applyInput (sequence + 1L) host playerId
            host.AdvanceTick()
            sequence <- sequence + 2L
        let player = host.Snapshot().Players[playerId]
        // 30 ticks grant 30 credits (plus the startup bank), so at most 31 of
        // the 60 sent frames may have moved the player: no speed advantage.
        Assert.InRange(Vector3.Distance(before, player.Position), 0.0f, 31.0f * maxTickDistance)

    [<Fact>]
    let ``player keeps simulating while the input stream stalls`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Jumper").Value
        applyCustom 1L 0.0f 0.0f (int InputButtons.Jump) host playerId
        host.AdvanceTick()
        Assert.True(host.Snapshot().Players[playerId].Position.Y > 0.0f)
        // No further input: gravity must still bring the player back down
        // instead of freezing them mid-air until the next packet.
        for _ in 1..120 do host.AdvanceTick()
        Assert.True(host.Snapshot().Players[playerId].Position.Y <= 0.01f)

    [<Fact>]
    let ``disconnected reserved player is not a hittable ghost`` () =
        let arena = TestKit.streetArena "Ghost range"
        let host = MatchHost(FreeForAll, arena)
        let thrower, _ = host.TryAddPlayer("Thrower").Value
        let ghost, _ = host.TryAddPlayer("Ghost").Value
        TestKit.readyUp host [ thrower; ghost ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        // Both spawned at the arena origin. Disconnect the ghost, then cook a
        // grenade until it pops in hand right next to the reserved body.
        host.RemovePlayer ghost
        let mutable sequence = 1L
        for _ in 1..260 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Grenade) host thrower
            host.AdvanceTick()
            sequence <- sequence + 1L
        let state = host.Snapshot()
        Assert.True(state.Players[ghost].Alive)
        Assert.Equal(0, state.Players[ghost].Deaths)
        Assert.Equal(0, state.Players[thrower].Kills)

    [<Fact>]
    let ``stale input sequence is rejected after a newer sequence`` () =
        let host = MatchHost FreeForAll
        let playerId, _ = host.TryAddPlayer("Replayer").Value
        applyInput 5 host playerId
        host.AdvanceTick()
        Assert.Equal(5L, host.Snapshot().Players[playerId].LastInputSequence)
        applyInput 3 host playerId
        host.AdvanceTick()
        Assert.Equal(5L, host.Snapshot().Players[playerId].LastInputSequence)

    [<Fact>]
    let ``far future input sequence resynchronizes the input window`` () =
        // A stalled server can fall many client frames behind. The ceiling that
        // rejected far-future sequences could never close again, bricking the
        // player's input stream for the rest of the match.
        let host = MatchHost FreeForAll
        let playerId, _ = host.TryAddPlayer("Time traveler").Value
        applyInput 10000 host playerId
        host.AdvanceTick()
        Assert.Equal(10000L, host.Snapshot().Players[playerId].LastInputSequence)
        applyInput 10001 host playerId
        host.AdvanceTick()
        Assert.Equal(10001L, host.Snapshot().Players[playerId].LastInputSequence)

    [<Fact>]
    let ``authoritative rifle hit awards team score and starts victim respawn`` () =
        let arena = TestKit.streetArenaWithSpawns "Server range"
        let host = MatchHost(TeamDeathmatch, arena)
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        TestKit.rifleShot host 1L axisId allyId |> ignore
        let result = host.Snapshot()
        Assert.Equal(1, result.AxisScore)
        Assert.Equal(1, result.Players[axisId].Kills)
        Assert.False(result.Players[allyId].Alive)
        Assert.Equal(Units.seconds 5.0f, result.Players[allyId].RespawnIn)
        Assert.Contains(result.Events, fun event -> match event.Event with ShotFired _ -> true | _ -> false)
        // The kill feed's only data source: killer, victim, and the weapon that
        // did it, broadcast (no recipient) so every client can render the row.
        let kill =
            result.Events
            |> List.pick (fun event -> match event.Event with Kill(killer, victim, weapon, headshot) -> Some(event.Recipient, killer, victim, weapon, headshot) | _ -> None)
        let recipient, killer, victim, weapon, headshot = kill
        Assert.Equal(None, recipient)
        Assert.Equal(Some axisId, killer)
        Assert.Equal(allyId, victim)
        Assert.Equal(result.Players[axisId].Slots[result.Players[axisId].Active].Class.Name, weapon)
        Assert.True headshot
        Assert.NotEmpty((Protocol.snapshot result).events)

    [<Fact>]
    let ``joining and leaving emit lifecycle events`` () =
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Lifecycle range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        let joins =
            host.Snapshot().Events
            |> List.choose (fun event -> match event.Event with PlayerJoined(id, name) -> Some(id, name) | _ -> None)
        Assert.Equal<(EntityId * string) list>([ allyId, "Ally"; axisId, "Axis" ], joins)
        // Announced on disconnect rather than on grace expiry, so the feed
        // matches the moment other players see them drop.
        waitOutCooldown host
        host.RemovePlayer axisId
        Assert.Contains(host.Snapshot().Events, fun event -> event.Event = PlayerLeft(axisId, "Axis"))

    [<Fact>]
    let ``reconnect cycling cannot flood the feed with lifecycle rows`` () =
        // A client holding a session token can leave and resume as fast as it
        // can handshake; each cycle used to broadcast two feed rows into every
        // other player's kill feed, which holds five rows for five seconds.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Churn range")
        let playerId, token = host.TryAddPlayer("Flooder").Value
        let lifecycleRows () =
            host.Snapshot().Events
            |> List.filter (fun event -> match event.Event with PlayerJoined _ | PlayerLeft _ -> true | _ -> false)
            |> List.length
        for _ in 1..20 do
            host.RemovePlayer playerId
            Assert.Equal(Some(playerId, token), host.TryAddPlayer("Flooder", sessionToken = token))
        // The join already spent this second's announcement, so twenty cycles
        // inside it add nothing. Events are only pruned by AdvanceTick.
        Assert.Equal(1, lifecycleRows ())
        waitOutCooldown host
        host.RemovePlayer playerId
        Assert.Equal(1, lifecycleRows ())

    [<Fact>]
    let ``an enqueued event broadcasts unless it names a recipient`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Ally").Value
        host.Enqueue(PhaseChanged "Warmup")
        host.Enqueue(PhaseChanged "Playing", playerId)
        let announced =
            host.Snapshot().Events
            |> List.choose (fun event -> match event.Event with PhaseChanged phase -> Some(phase, event.Recipient) | _ -> None)
        Assert.Equal<(string * EntityId option) list>([ "Warmup", None; "Playing", Some playerId ], announced)

    [<Fact>]
    let ``chat is sanitized and throttled to one line per second`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Ally").Value
        let lines () =
            host.Snapshot().Events
            |> List.choose (fun event -> match event.Event with Chat(sender, name, line) -> Some(sender, name, line) | _ -> None)
        // The tab would otherwise split the wire encoding into a forged name.
        host.Chat(playerId, "  push \tB  ")
        // Dropped, not kicked: the socket-level limiter would close the
        // connection instead, and losing a line beats losing a player.
        host.Chat(playerId, "and again")
        Assert.Equal<(EntityId option * string * string) list>([ Some playerId, "Ally", "push B" ], lines ())
        // Blank once sanitized: nothing to say, nothing to send.
        host.Chat(playerId, "\r\n")
        for _ in 1 .. int Tuning.TickRate do host.AdvanceTick()
        host.Chat(playerId, "reloading")
        Assert.Equal<(EntityId option * string * string) list>([ Some playerId, "Ally", "reloading" ], lines ())
        // An unknown id (a player already removed) is a no-op, not a crash.
        host.Chat(EntityId 999, "ghost")
        Assert.Single(lines ()) |> ignore

    /// Whispered command output, oldest first. Events are only pruned by
    /// AdvanceTick, so a test that does not tick sees the whole conversation.
    [<Fact>]
    let ``the banned address is the client's, not the proxy's`` () =
        // Behind Fly every socket's peer is the edge proxy. Banning that would
        // ban every player at once, so Fly-Client-IP wins whenever it is set.
        Assert.Equal("203.0.113.7", Bans.clientAddress "203.0.113.7" "198.51.100.9" "172.16.0.1")
        // Other proxies: first hop of X-Forwarded-For, the client end of the chain.
        Assert.Equal("198.51.100.9", Bans.clientAddress "" "198.51.100.9, 172.16.0.1" "172.16.0.1")
        Assert.Equal("198.51.100.9", Bans.clientAddress "  " " 198.51.100.9 " "172.16.0.1")
        // Direct connection: the socket's peer is genuinely the client.
        Assert.Equal("172.16.0.1", Bans.clientAddress "" "" "172.16.0.1")
        // Nothing at all must not become a ban that matches everyone.
        Assert.False(Bans.isBanned (Bans.clientAddress "" "" ""))

    [<Fact>]
    let ``chat log formats a transcript line per room`` () =
        let stamp = DateTimeOffset(2026, 8, 20, 8, 40, 7, TimeSpan.Zero)
        Assert.Equal("2026-08-20T08:40:07Z [TeamDeathmatch] Ally: on your left", ChatLog.format stamp TeamDeathmatch "Ally" "on your left")
        // Server lines carry no sender; they must still be attributable.
        Assert.Equal("2026-08-20T08:40:07Z [FreeForAll] *: the server is going down", ChatLog.format stamp FreeForAll "" "the server is going down")

    [<Fact>]
    let ``extension hooks are optional and carried through the registry`` () =
        // The hooks exist for future use, so the contract worth pinning is that
        // an extension without them is still a valid registration.
        let bare = ServerExtension.empty "bare"
        Assert.True(bare.OnEvent.IsNone && bare.OnTick.IsNone && bare.Commands.IsEmpty)
        let seen = ResizeArray<string>()
        let hooked =
            { ServerExtension.empty "hooked" with
                OnEvent = Some(fun _ event -> match event.Event with Chat(_, name, _) -> seen.Add name | _ -> ())
                OnTick = Some(fun _ state -> seen.Add $"tick:{state.Tick}") }
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Ally").Value
        host.Chat(playerId, "on your left")
        let state = host.Snapshot()
        for event in state.Events do hooked.OnEvent |> Option.iter (fun hook -> hook host event)
        hooked.OnTick |> Option.iter (fun hook -> hook host state)
        Assert.Contains("Ally", seen)
        Assert.Contains($"tick:{state.Tick}", seen)

    [<Fact>]
    let ``a room config fills in every omitted rule`` () =
        let rooms =
            ServerConfig.parse Levels.paintballArena """
            { "rooms": [ { "id": "sniper", "name": "Sniper Alley", "mode": "FreeForAll", "level": "omaha",
                           "scoreLimit": 20, "timeLimit": 300, "maxPlayers": 8 },
                         { "id": "bare", "mode": "TeamDeathmatch" } ] }"""
        Assert.Equal(2, rooms.Length)
        Assert.Equal("Sniper Alley", rooms[0].Name)
        Assert.Equal(FreeForAll, rooms[0].Mode)
        Assert.Equal(Levels.omahaDraw.Name, rooms[0].Level.Name)
        Assert.Equal(20, rooms[0].ScoreLimit)
        Assert.Equal(Units.seconds 300.0f, rooms[0].TimeLimit)
        Assert.Equal(8, rooms[0].MaxPlayers)
        // Everything omitted falls back to what a room ran on before rooms
        // were configurable, and the name defaults to the id.
        Assert.Equal("bare", rooms[1].Name)
        Assert.Equal(Levels.paintballArena.Name, rooms[1].Level.Name)
        Assert.Equal(Multiplayer.scoreLimit TeamDeathmatch, rooms[1].ScoreLimit)
        Assert.Equal(Multiplayer.defaultTimeLimit, rooms[1].TimeLimit)
        Assert.Equal(ServerConfig.DefaultMaxPlayers, rooms[1].MaxPlayers)

    [<Fact>]
    let ``a broken room config fails loudly rather than being ignored`` () =
        // A server config that is silently dropped is an operator trap: the
        // server would run, on rules nobody asked for.
        let fails (json: string) = Assert.ThrowsAny<exn>(fun () -> ServerConfig.parse Levels.paintballArena json |> ignore)
        fails """{ "rooms": [ { "id": "a", "mode": "Deathmatch" } ] }" """ |> ignore
        fails """{ "rooms": [ { "id": "a", "mode": "TeamDeathmatch", "level": "atlantis" } ] }""" |> ignore
        fails """{ "rooms": [ { "mode": "TeamDeathmatch" } ] }""" |> ignore
        fails """{ "rooms": [ { "id": "dup", "mode": "TeamDeathmatch" }, { "id": "DUP", "mode": "FreeForAll" } ] }""" |> ignore
        fails """{ "rooms": [] }""" |> ignore
        fails "not json at all" |> ignore

    [<Fact>]
    let ``no config file leaves the two rooms this server always had`` () =
        let rooms = ServerConfig.defaultRooms Levels.canalYard
        Assert.Equal<string array>([| "tdm"; "ffa" |], rooms |> Array.map (fun room -> room.Id))
        Assert.Equal<GameMode array>([| TeamDeathmatch; FreeForAll |], rooms |> Array.map (fun room -> room.Mode))
        for room in rooms do
            Assert.Equal(Levels.canalYard.Name, room.Level.Name)
            Assert.Equal(ServerConfig.DefaultMaxPlayers, room.MaxPlayers)
            Assert.Equal(Multiplayer.scoreLimit room.Mode, room.ScoreLimit)

    [<Fact>]
    let ``per-room rules govern the match they belong to`` () =
        // A room's score limit ends its round, and its own cap is what refuses
        // the next joiner — neither is the process-wide constant any more.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Rules range", scoreLimit = 1, maxPlayers = 3)
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        Assert.True(host.TryAddPlayer("Third").IsSome)
        Assert.True(host.TryAddPlayer("Fourth").IsNone)
        Assert.Equal(3, host.Capacity)
        Assert.False host.HasRoom
        TestKit.readyUp host [ allyId; axisId ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        TestKit.rifleShot host 1L axisId allyId |> ignore
        // One kill is the whole match at scoreLimit 1.
        Assert.Equal(Results, host.Snapshot().Phase)

    let private repliesTo (host: MatchHost) playerId =
        host.Snapshot().Events
        |> List.choose (fun event ->
            match event.Recipient, event.Event with
            | Some target, Chat(None, "", text) when target = playerId -> Some text
            | _ -> None)

    [<Fact>]
    let ``ban resolves the address before the kick drops it`` () =
        // The address table is keyed by live connection, so reading it after
        // the disconnect would find nothing left to ban.
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let opId, _ = host.TryAddPlayer("Ally").Value
        let targetId, _ = host.TryAddPlayer("Griefer").Value
        Bans.remember targetId "203.0.113.50"
        Assert.True(host.TryElevate(opId, "hunter2"))
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host opId "/ban Griefer"
        Assert.Contains("Banned Griefer (203.0.113.50).", repliesTo host opId)
        Assert.True(host.IsKicked targetId)
        Assert.True(Bans.isBanned "203.0.113.50")
        // An address nobody banned still connects.
        Assert.False(Bans.isBanned "203.0.113.51")
        Bans.forget targetId

    [<Fact>]
    let ``help lists only the commands the caller may run`` () =
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Commands.handleChat [ Commands.builtins ] host playerId "/help"
        let listed = repliesTo host playerId
        Assert.Contains(listed, fun usage -> usage.StartsWith "/op")
        Assert.DoesNotContain(listed, fun usage -> usage.StartsWith "/kick")
        // Visibility, not just execution: a verb the caller cannot see is
        // reported as unknown rather than as forbidden.
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/kick Ally"
        Assert.Equal("Unknown command '/kick'. Try /help.", List.last (repliesTo host playerId))
        Assert.True(host.TryElevate(playerId, "hunter2"))
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/help"
        Assert.Contains(repliesTo host playerId, fun usage -> usage.StartsWith "/kick")

    [<Fact>]
    let ``commands share the chat cooldown instead of bypassing it`` () =
        // /help was the cheapest amplifier in the game: unthrottled, and each
        // reply is an O(n) append to the event list every client serializes.
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Flooder").Value
        for _ in 1..30 do Commands.handleChat [ Commands.builtins ] host playerId "/help"
        let oneListing = List.length (repliesTo host playerId)
        Assert.InRange(oneListing, 1, Commands.builtins.Commands.Length)
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/help"
        Assert.Equal(oneListing, List.length (repliesTo host playerId))

    [<Fact>]
    let ``a command hidden behind a control character is not republished as chat`` () =
        // TrimStart drops whitespace but not C0 scalars, so this used to miss
        // the slash test and land in everyone's chat log with the key in it.
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Commands.handleChat [ Commands.builtins ] host playerId "\u0001/op hunter2"
        Assert.True(host.IsOp playerId)
        Assert.DoesNotContain(host.Snapshot().Events, fun event ->
            match event.Event with
            | Chat(_, _, text) -> event.Recipient.IsNone && text.Contains "hunter2"
            | _ -> false)

    [<Fact>]
    let ``op elevates only on the configured key`` () =
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Assert.False(host.TryElevate(playerId, "wrong"))
        Assert.False(host.IsOp playerId)
        // Guesses are throttled to one per second, so the right key only lands
        // after the cooldown the wrong one just started.
        Assert.False(host.TryElevate(playerId, "hunter2"))
        for _ in 1 .. int Tuning.TickRate do host.AdvanceTick()
        Assert.True(host.TryElevate(playerId, "hunter2"))
        Assert.True(host.IsOp playerId)
        // Elevation is per-connection: a reserved slot comes back unprivileged.
        host.RemovePlayer playerId
        Assert.False(host.IsOp playerId)

    [<Fact>]
    let ``op never elevates when no key is configured`` () =
        // An unconfigured server has no ops at all — never "everyone is op".
        let host = MatchHost(TeamDeathmatch, opKey = "")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Assert.False(host.TryElevate(playerId, ""))
        Assert.False(host.TryElevate(playerId, "hunter2"))
        Assert.False(host.IsOp playerId)

    [<Fact>]
    let ``a command answers the caller alone and is never broadcast`` () =
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Commands.handleChat [ Commands.builtins ] host playerId "/op hunter2"
        let chatter =
            host.Snapshot().Events
            |> List.choose (fun event -> match event.Event with Chat(_, _, text) -> Some(event.Recipient, text) | _ -> None)
        // The key must never land in anyone else's chat log.
        Assert.True(chatter |> List.forall (fun (recipient, _) -> recipient = Some playerId))
        Assert.DoesNotContain(chatter, fun (_, text) -> text.Contains "hunter2")
        // A plain line still broadcasts, so the slash is doing the routing.
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "regrouping"
        Assert.Contains(host.Snapshot().Events, fun event -> event.Event = Chat(Some playerId, "Ally", "regrouping"))

    [<Fact>]
    let ``a chat line comes back to its own sender`` () =
        // Chat echoes off the server rather than being drawn locally on send,
        // so the sender's log is ordered and worded identically to everyone
        // else's. The whisper filter must not mistake the sender for a
        // recipient and cut the broadcast back out of their own snapshot.
        let host = MatchHost TeamDeathmatch
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        host.Chat(allyId, "on your left")
        let state = host.Snapshot()
        let chatOnWire viewer =
            (Protocol.snapshotFor viewer state).events
            |> Array.filter (fun event -> event.kind = "chat")
            |> Array.map (fun event -> event.text)
        Assert.Equal<string array>(chatOnWire allyId, chatOnWire axisId)
        Assert.Contains(chatOnWire allyId, fun text -> text.Contains "on your left")

    [<Fact>]
    let ``a whisper is dropped from every other viewer's wire snapshot`` () =
        // The recipient tag rides along on the wire, so filtering it only in
        // the client left /op keys readable to anyone with a patched one.
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        Commands.handleChat [ Commands.builtins ] host allyId "/op hunter2"
        let state = host.Snapshot()
        let chatOnWire viewer =
            (Protocol.snapshotFor viewer state).events
            |> Array.filter (fun event -> event.kind = "chat")
        Assert.Contains(chatOnWire allyId, fun event -> event.text.EndsWith "You are now an op.")
        Assert.Empty(chatOnWire axisId)
        // Broadcasts still reach everyone; only the addressed rows are cut.
        Assert.Contains((Protocol.snapshotFor axisId state).events, fun event -> event.kind = "joined")

    [<Fact>]
    let ``say broadcasts a server line and restart ends the round`` () =
        let host = MatchHost(TeamDeathmatch, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Commands.handleChat [ Commands.builtins ] host playerId "/say the server is going down"
        // Refused before elevation, and refused as an unknown verb so a
        // non-op cannot even confirm the command exists.
        Assert.Equal("Unknown command '/say'. Try /help.", List.last (repliesTo host playerId))
        Assert.True(host.TryElevate(playerId, "hunter2"))
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/say the server is going down"
        // Sender None is what the client renders as a highlighted server line.
        Assert.Contains(host.Snapshot().Events, fun event ->
            event.Recipient.IsNone && event.Event = Chat(None, "", "the server is going down"))
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/restart"
        Assert.Equal(Warmup, host.Snapshot().Phase)
        Assert.Contains(host.Snapshot().Events, fun event -> event.Event = PhaseChanged "Warmup")

    [<Fact>]
    let ``kick flags the named player for his own loop to drop`` () =
        let host = MatchHost TeamDeathmatch
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        Assert.True((host.Kick "nobody").IsNone)
        Assert.Equal(Some "Axis", host.Kick "axis")
        Assert.True(host.IsKicked axisId)
        Assert.False(host.IsKicked allyId)
        // RemovePlayer runs in handleSocket's finally; the flag must not
        // survive into a rejoin on the same reserved slot.
        host.RemovePlayer axisId
        Assert.False(host.IsKicked axisId)

    [<Fact>]
    let ``map accepts builtin aliases only and applies between rounds`` () =
        let host = MatchHost(TeamDeathmatch, Levels.canalYard, opKey = "hunter2")
        let playerId, _ = host.TryAddPlayer("Ally").Value
        Assert.True(host.TryElevate(playerId, "hunter2"))
        Commands.handleChat [ Commands.builtins ] host playerId "/map somebody-elses-map.ironmap"
        Assert.Equal("Unknown map. Builtins only.", List.last (repliesTo host playerId))
        waitOutCooldown host
        Commands.handleChat [ Commands.builtins ] host playerId "/map omaha"
        // Deferred: swapping the level under a live round would teleport
        // everyone into different geometry.
        Assert.Equal(Levels.canalYard.Name, host.Snapshot().LevelName)
        // Restart is the same warmup reset the Results -> Warmup arm runs.
        host.Restart()
        Assert.Equal(Warmup, host.Snapshot().Phase)
        Assert.Equal(Levels.omahaDraw.Name, host.Snapshot().LevelName)

    [<Fact>]
    let ``the welcome names the level its host runs now, not the boot one`` () =
        let host = MatchHost(TeamDeathmatch, Levels.canalYard, opKey = "hunter2")
        let playerId, token = host.TryAddPlayer("Ally").Value
        let bootHash = "cafef00d"
        let atBoot = Protocol.welcomeFor playerId token Levels.canalYard.Name bootHash "tdm" (host.Snapshot())
        Assert.Equal(Levels.canalYard.Name, atBoot.level)
        Assert.Equal(bootHash, atBoot.mapHash)
        Assert.True(host.TryElevate(playerId, "hunter2"))
        Commands.handleChat [ Commands.builtins ] host playerId "/map omaha"
        host.Restart()
        let afterSwap = Protocol.welcomeFor playerId token Levels.canalYard.Name bootHash "tdm" (host.Snapshot())
        Assert.Equal(Levels.omahaDraw.Name, afterSwap.level)
        // /maps/{hash} only ever serves the boot map's bytes, so the swapped
        // builtin must travel by name alone rather than by an unreachable hash.
        Assert.Equal("", afterSwap.mapHash)

    [<Fact>]
    let ``each phase transition is announced exactly once`` () =
        // Collected per tick, not from a final snapshot: the server retains an
        // event for only 12 ticks and warmup alone runs 600.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Phase range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        host.SetReady allyId
        host.SetReady axisId
        let seen = ResizeArray<string>()
        for _ in 1..721 do
            host.AdvanceTick()
            for event in host.Snapshot().Events do
                match event.Event with
                | PhaseChanged phase when event.Tick = host.Snapshot().Tick -> seen.Add phase
                | _ -> ()
        Assert.Equal(Playing, host.Snapshot().Phase)
        Assert.Equal<string list>([ "Warmup"; "Playing" ], List.ofSeq seen)

    [<Fact>]
    let ``player can move again after dying and respawning`` () =
        let arena = TestKit.streetArenaWithSpawns "Respawn range"
        let host = MatchHost(TeamDeathmatch, arena)
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        // The axis rifleman already faces the ally (spawn yaw = PI). ADS, then
        // fire a few Kar98k shots until the ally is dead.
        let mutable sequence = 1L
        for _ in 1..30 do
            applyCustom sequence 0.0f 0.0f 2 host axisId
            host.AdvanceTick()
            sequence <- sequence + 1L
        for _ in 1..3 do
            applyCustom sequence 0.0f 0.0f 3 host axisId
            host.AdvanceTick()
            sequence <- sequence + 1L
            for _ in 1..80 do
                applyCustom sequence 0.0f 0.0f 2 host axisId
                host.AdvanceTick()
                sequence <- sequence + 1L
        Assert.False(host.Snapshot().Players[allyId].Alive)
        // Respawn takes 5 s = 300 ticks. The real client continues sending
        // numbered no-op inputs while dead; the server must acknowledge them.
        let mutable allySequence = 1L
        for _ in 1..310 do
            applyCustom allySequence 0.0f 0.0f 0 host allyId
            host.AdvanceTick()
            allySequence <- allySequence + 1L
        let respawned = host.Snapshot()
        Assert.True(respawned.Players[allyId].Alive)
        let before = respawned.Players[allyId].Position
        applyCustom allySequence 1.0f 0.0f 4 host allyId
        host.AdvanceTick()
        allySequence <- allySequence + 1L
        applyCustom allySequence 1.0f 0.0f 4 host allyId
        host.AdvanceTick()
        allySequence <- allySequence + 1L
        applyCustom allySequence 1.0f 0.0f 4 host allyId
        host.AdvanceTick()
        let moved = host.Snapshot().Players[allyId]
        Assert.True(Vector3.Distance(before, moved.Position) > 0.2f)
        Assert.Equal(allySequence, moved.LastInputSequence)

    [<Fact>]
    let ``shots are not spent during warmup`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Trigger-happy").Value
        let witnessId, _ = host.TryAddPlayer("Witness").Value
        host.SetReady playerId
        host.SetReady witnessId
        let mutable sequence = 1L
        for _ in 1..500 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Fire) host playerId
            host.AdvanceTick()
            sequence <- sequence + 1L
        let player = host.Snapshot().Players[playerId]
        Assert.Equal(Warmup, host.Snapshot().Phase)
        let held = player.Slots[player.Active]
        Assert.Equal(WeaponState.Ready, held.State)
        Assert.Equal(held.Class.MagSize, held.InMag)

    [<Fact>]
    let ``authoritative grenade is cooked thrown and included in snapshots`` () =
        let arena = TestKit.streetArena "Grenade range"
        let host = MatchHost(FreeForAll, arena)
        let first, _ = host.TryAddPlayer("Thrower").Value
        let second, _ = host.TryAddPlayer("Witness").Value
        TestKit.readyUp host [ first; second ]
        applyCustom 1L 0.0f 0.0f (int InputButtons.Grenade) host first
        host.AdvanceTick()
        applyCustom 2L 0.0f 0.0f 0 host first
        host.AdvanceTick()
        let state = host.Snapshot()
        Assert.Single state.Grenades |> ignore
        Assert.Equal(first, state.Grenades[0].Owner)
        Assert.Equal(GrenadeIdle 2, state.Players[first].Grenade)
        let wire = Protocol.snapshot state
        Assert.Single wire.grenades |> ignore

    [<Fact>]
    let ``an online player carries a primary and the team's sidearm`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Rifleman", weaponName = "Kar98k").Value
        let player = host.Snapshot().Players[playerId]
        Assert.Equal(2, player.Slots.Length)
        Assert.Equal(0, player.Active)
        Assert.Equal("Kar98k", player.Slots[0].Class.Name)
        // Issued, not chosen — the picker only ever names a primary.
        Assert.Equal(Tuning.sidearm(player.Team).Name, player.Slots[1].Class.Name)
        Assert.Equal(Pistol, player.Slots[1].Class.Kind)

    [<Fact>]
    let ``the number keys switch weapons online`` () =
        // Weapon1-5 were masked off server-side, so these presses used to reach
        // the sim as nothing at all. This is the end-to-end proof they don't.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArena "Switch range")
        let playerId, _ = host.TryAddPlayer("Switcher", weaponName = "Kar98k").Value
        let otherId, _ = host.TryAddPlayer("Other").Value
        TestKit.readyUp host [ playerId; otherId ]
        let heldName () =
            let player = host.Snapshot().Players[playerId]
            player.Slots[player.Active].Class.Name
        Assert.Equal("Kar98k", heldName ())
        // Key 3 is the pistol category. The raise takes 0.35 s, so hold the
        // press across enough ticks for it to land.
        let mutable sequence = 1L
        for _ in 1..40 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Weapon3) host playerId
            host.AdvanceTick()
            sequence <- sequence + 1L
        Assert.Equal(Tuning.sidearm(host.Snapshot().Players[playerId].Team).Name, heldName ())
        // And back to the primary, which proves Active round-trips both ways.
        for _ in 1..40 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Weapon1) host playerId
            host.AdvanceTick()
            sequence <- sequence + 1L
        Assert.Equal("Kar98k", heldName ())

    [<Fact>]
    let ``ammunition spent on one weapon survives a switch away and back`` () =
        // Per-slot state is the point of carrying a kit: the sidearm must not
        // share the rifle's magazine, and neither may be silently refilled.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArena "Ammo range")
        let playerId, _ = host.TryAddPlayer("Gunner", weaponName = "Thompson").Value
        let otherId, _ = host.TryAddPlayer("Other").Value
        TestKit.readyUp host [ playerId; otherId ]
        let slotsOf () = host.Snapshot().Players[playerId].Slots
        let mutable sequence = 1L
        for _ in 1..20 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Fire) host playerId
            host.AdvanceTick()
            sequence <- sequence + 1L
        let spent = slotsOf().[0].InMag
        Assert.True(spent < Tuning.thompson.MagSize, "the primary should have fired rounds")
        for _ in 1..40 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Weapon3) host playerId
            host.AdvanceTick()
            sequence <- sequence + 1L
        // The sidearm is untouched and the primary still remembers what it spent.
        Assert.Equal(Tuning.sidearm(host.Snapshot().Players[playerId].Team).MagSize, slotsOf().[1].InMag)
        Assert.Equal(spent, slotsOf().[0].InMag)

    [<Fact>]
    let ``loadout change applies instantly outside live play and stages during it`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Switcher").Value
        // Waiting phase: swap on the spot, full ammunition.
        host.SetLoadout(playerId, "STG-44")
        let primaryOf id = host.Snapshot().Players[id].Slots[0].Class.Name
        Assert.Equal("STG-44", primaryOf playerId)
        // The issued sidearm rides along and is not the player's to pick.
        Assert.Equal(Tuning.sidearm(host.Snapshot().Players[playerId].Team).Name, host.Snapshot().Players[playerId].Slots[1].Class.Name)
        // Unknown weapon names are ignored.
        host.SetLoadout(playerId, "Raygun")
        Assert.Equal("STG-44", primaryOf playerId)
        let otherId, _ = host.TryAddPlayer("Other").Value
        TestKit.readyUp host [ playerId; otherId ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        // Live round: the request must not re-roll the weapon in hand.
        host.SetLoadout(playerId, "BAR")
        Assert.Equal("STG-44", primaryOf playerId)

    [<Fact>]
    let ``mid-round loadout request arms on the next spawn`` () =
        let arena = TestKit.streetArena "Loadout range"
        let host = MatchHost(FreeForAll, arena)
        let subject, _ = host.TryAddPlayer("Subject").Value
        let witness, _ = host.TryAddPlayer("Witness").Value
        TestKit.readyUp host [ subject; witness ]
        host.SetLoadout(subject, "BAR")
        let mutable sequence = 1L
        // Two grenades cooked to detonation in hand kill the subject.
        for _ in 1..520 do
            applyCustom sequence 0.0f 0.0f (int InputButtons.Grenade) host subject
            host.AdvanceTick()
            sequence <- sequence + 1L
        Assert.False(host.Snapshot().Players[subject].Alive)
        Assert.Equal("Thompson", host.Snapshot().Players[subject].Slots[0].Class.Name)
        for _ in 1..320 do
            applyCustom sequence 0.0f 0.0f 0 host subject
            host.AdvanceTick()
            sequence <- sequence + 1L
        let respawned = host.Snapshot().Players[subject]
        Assert.True(respawned.Alive)
        Assert.Equal("BAR", respawned.Slots[0].Class.Name)
        Assert.Equal(Tuning.bar.MagSize, respawned.Slots[0].InMag)

    [<Fact>]
    let ``session token reclaims reserved identity after disconnect`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, token = host.TryAddPlayer("Original").Value
        host.RemovePlayer playerId
        Assert.False(host.Snapshot().Players[playerId].Connected)
        let resumedId, resumedToken = host.TryAddPlayer("Returned", sessionToken = token).Value
        Assert.Equal(playerId, resumedId)
        Assert.Equal(token, resumedToken)
        Assert.True(host.Snapshot().Players[playerId].Connected)
        Assert.Equal("Returned", host.Snapshot().Players[playerId].Name)

    [<Fact>]
    let ``crouch is hold-based on the server`` () =
        // Toggle mode is purely a client input-layer latch; the wire and the
        // simulation only ever see "crouch button held right now".
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Croucher").Value
        applyCustom 1L 0.0f 0.0f (int InputButtons.Crouch) host playerId
        host.AdvanceTick()
        Assert.Equal(Crouched, host.Snapshot().Players[playerId].Stance)
        applyCustom 2L 0.0f 0.0f (int InputButtons.Crouch) host playerId
        host.AdvanceTick()
        Assert.Equal(Crouched, host.Snapshot().Players[playerId].Stance)
        applyCustom 3L 0.0f 0.0f 0 host playerId
        host.AdvanceTick()
        Assert.Equal(Standing, host.Snapshot().Players[playerId].Stance)

    [<Fact>]
    let ``selected online weapon is authoritative and replicated`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Scout", weaponName = "Kar98k Sniper").Value
        let player = host.Snapshot().Players[playerId]
        Assert.Equal("Kar98k Sniper", player.Slots[0].Class.Name)
        let wire = Protocol.snapshot (host.Snapshot())
        Assert.Equal("Kar98k Sniper", wire.players[0].weapon)

    [<Fact>]
    let ``leaderboard publishes only connected players and their live loadouts`` () =
        let tdm = MatchHost TeamDeathmatch
        let onlineId, _ = tdm.TryAddPlayer("Public Hero", weaponName = "M1897 Trench Gun").Value
        let offlineId, _ = tdm.TryAddPlayer("Gone Already").Value
        tdm.RemovePlayer offlineId
        let board = Protocol.leaderboard [| "tdm", "Team Deathmatch", 16, tdm.Snapshot(); "ffa", "Free For All", 8, (MatchHost FreeForAll).Snapshot() |]
        // Legacy field: the largest room, for clients predating per-room capacity.
        Assert.Equal(16, board.capacityPerRoom)
        Assert.Equal(2, board.rooms.Length)
        Assert.Equal("tdm", board.rooms[0].id)
        Assert.Equal("Team Deathmatch", board.rooms[0].name)
        Assert.Equal(8, board.rooms[1].capacity)
        Assert.Single(board.rooms[0].players) |> ignore
        let (EntityId expectedId) = onlineId
        Assert.Equal(expectedId, board.rooms[0].players[0].id)
        Assert.Equal("Public Hero", board.rooms[0].players[0].name)
        Assert.Equal("M1897 Trench Gun", board.rooms[0].players[0].weapon)

    [<Fact>]
    let ``arsenal statistics are generated from gameplay tuning`` () =
        let arsenal = Protocol.arsenal ()
        let sniper = arsenal.weapons |> Array.find (fun weapon -> weapon.name = Tuning.kar98kSniper.Name)
        let shotgun = arsenal.weapons |> Array.find (fun weapon -> weapon.name = Tuning.m1897.Name)
        let mg42 = arsenal.weapons |> Array.find (fun weapon -> weapon.name = Tuning.mg42.Name)

        Assert.Equal(Tuning.onlineWeapons.Length + 1, arsenal.weapons.Length)
        Assert.Equal(120.0f, sniper.damagePerProjectile)
        Assert.Equal(0.18f, sniper.aimDownSightSeconds)
        Assert.Equal(8, shotgun.projectilesPerShot)
        Assert.Equal(128.0f, shotgun.maximumDamagePerShot)
        Assert.Equal("Mounted weapon", mg42.availability)

    [<Fact>]
    let ``bundled arsenal fallback in the website matches live tuning`` () =
        // Guards the checked-in snapshot against drift; regenerate with
        // `just arsenal-sync` when tuning changes.
        let html = IO.File.ReadAllText(IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "arsenal.html"))
        let openTag = "<script id=\"arsenal-fallback\" type=\"application/json\">"
        let start = html.IndexOf openTag + openTag.Length
        let bundled = System.Text.Json.Nodes.JsonNode.Parse(html[start .. html.IndexOf("</script>", start) - 1])
        let live = System.Text.Json.JsonSerializer.SerializeToNode(Protocol.arsenal ())
        Assert.Equal(live["weapons"].ToJsonString(), bundled["weapons"].ToJsonString())

    [<Fact>]
    let ``match returns to waiting once every player slot has expired`` () =
        // Zero grace: removed players expire on the very next tick instead of
        // after the production 30 s reconnect window.
        let host = MatchHost(TeamDeathmatch, disconnectGrace = TimeSpan.Zero)
        let first, _ = host.TryAddPlayer("First").Value
        let second, _ = host.TryAddPlayer("Second").Value
        TestKit.readyUp host [ first; second ]
        Assert.Equal(Playing, host.Snapshot().Phase)
        host.RemovePlayer first
        host.RemovePlayer second
        host.AdvanceTick()
        let state = host.Snapshot()
        Assert.Equal(Waiting, state.Phase)
        Assert.True state.Players.IsEmpty
        Assert.Equal(0, state.AlliesScore)
        Assert.Equal(0, state.AxisScore)

    [<Fact>]
    let ``server snapshot round-trips through the client wire parser`` () =
        // The writer (Protocol.snapshot) and the reader (SnapshotWire) are
        // independently-maintained mirrors; the reader's getters default a
        // missing or renamed field to 0/false/"", so only a full round trip
        // catches drift between them.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Wire range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        // Move and fire so velocity, look, and events all carry real values.
        TestKit.applyCustom 1L 1.0f 0.2f 3 host axisId
        host.AdvanceTick()
        let state = host.Snapshot()
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot state))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        Assert.Equal(state.Tick, parsed.Tick)
        Assert.Equal(state.Mode, parsed.Mode)
        Assert.Equal(state.Phase, parsed.Phase)
        Assert.Equal(state.AlliesScore, parsed.AlliesScore)
        Assert.Equal(state.AxisScore, parsed.AxisScore)
        let server = state.Players[axisId]
        let wire = parsed.Players |> Array.find (fun player -> player.Name = "Axis")
        Assert.Equal((let (EntityId id) = server.Id in id), wire.Id)
        Assert.Equal(server.Team, wire.Team)
        Assert.Equal(server.Position, wire.Position)
        Assert.Equal(server.Velocity, wire.Velocity)
        Assert.Equal(server.Yaw, wire.Yaw)
        Assert.Equal(server.Pitch, wire.Pitch)
        Assert.Equal(server.Stance, wire.Stance)
        Assert.Equal(Units.raw server.Health, wire.Health)
        Assert.Equal(server.Alive, wire.Alive)
        Assert.Equal(server.Ready, wire.Ready)
        Assert.Equal(server.Ads, wire.Ads)
        // The whole kit crosses, and the flat fields still mirror the slot in
        // hand for clients that predate kits.
        let held = server.Slots[server.Active]
        Assert.Equal(server.Slots.Length, wire.Slots.Length)
        Assert.Equal(server.Active, wire.Active)
        Assert.Equal<string array>(
            server.Slots |> Array.map (fun slot -> slot.Class.Name),
            wire.Slots |> Array.map (fun slot -> slot.WeaponName))
        Assert.Equal(held.InMag, wire.Ammo)
        Assert.Equal(held.Reserve, wire.Reserve)
        Assert.Equal(held.Class.Name, wire.WeaponName)
        Assert.Equal((match held.State with Reloading remaining -> Units.raw remaining | _ -> 0.0f), wire.ReloadRemaining)
        Assert.Equal(server.Kills, wire.Kills)
        Assert.Equal(server.Deaths, wire.Deaths)
        Assert.Equal(server.BestStreak, wire.BestStreak)
        Assert.Equal(server.LastInputSequence, wire.AcknowledgedInput)
        Assert.NotEmpty parsed.Events
        let shot = parsed.Events |> Array.find (fun event -> event.Kind = "shot")
        Assert.False(String.IsNullOrEmpty shot.Text)

    [<Fact>]
    let ``pressing reload puts a non-zero reloadRemaining on the wire`` () =
        // The server always simulated the reload; PlayerSnapshot just never
        // carried the timer, so the client's reload bar had nothing to draw.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Reload range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        // Reload engages only on a part-empty magazine, and only once the shot's
        // cooldown has expired.
        TestKit.applyCustom 1L 0.0f 0.0f 1 host axisId
        // The Kar98k's bolt cycle runs well over a second, so wait it out.
        for _ in 1..100 do host.AdvanceTick()
        TestKit.applyCustom 2L 0.0f 0.0f 8 host axisId
        host.AdvanceTick()
        let state = host.Snapshot()
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot state))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        let wire = parsed.Players |> Array.find (fun player -> player.Name = "Axis")
        Assert.True(wire.ReloadRemaining > 0.0f, "reload timer must reach the client")
        match state.Players[axisId].Slots[state.Players[axisId].Active].State with
        | Reloading remaining -> Assert.Equal(Units.raw remaining, wire.ReloadRemaining)
        | other -> failwith $"server weapon should be reloading, was {other}"

    [<Fact>]
    let ``a kill streak reaches the wire and is cleared at round end`` () =
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Streak range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        TestKit.rifleShot host 1L axisId allyId |> ignore
        let state = host.Snapshot()
        Assert.Equal(1, state.Players[axisId].Streak)
        Assert.Equal(1, state.Players[axisId].BestStreak)
        Assert.Equal(0, state.Players[allyId].Streak)
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot state))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        let wire = parsed.Players |> Array.find (fun player -> player.Name = "Axis")
        Assert.Equal(1, wire.BestStreak)
        // Deliberately the natural route rather than /restart's shortcut: this
        // asserts the Playing -> Results -> Warmup arm itself, so it rides out
        // the full 600s time limit — ~37k ticks, the slowest test in the suite.
        let mutable ticks = 0
        while host.Snapshot().Phase <> Warmup && ticks < 60000 do
            host.AdvanceTick()
            ticks <- ticks + 1
        Assert.Equal(Warmup, host.Snapshot().Phase)
        let reset = host.Snapshot().Players[axisId]
        Assert.Equal(0, reset.Kills)
        Assert.Equal(0, reset.Streak)
        Assert.Equal(0, reset.BestStreak)

    [<Fact>]
    let ``lifecycle events survive the wire round trip`` () =
        // Kill squeezes killer/victim/weapon/headshot into the shared event DTO
        // with no dedicated fields, so a silent mismatch between the writer's
        // packing and the reader's unpacking is the likely drift here.
        let originals =
            [ Kill(Some(EntityId 7), EntityId 3, "Kar98k", true)
              Kill(None, EntityId 3, "GRENADE", false)
              PlayerJoined(EntityId 7, "Ally")
              PlayerLeft(EntityId 7, "Ally")
              PhaseChanged "Results"
              // Both halves of a chat line share one text field across a tab.
              Chat(Some(EntityId 7), "Ally", "on your left")
              Chat(None, "", "MATCH STARTING") ]
        let state =
            { Multiplayer.create TeamDeathmatch with
                Events = originals |> List.mapi (fun index event -> { Id = int64 index + 1L; Tick = 0L; Recipient = None; Event = event }) }
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot state))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        let decoded = parsed.Events |> Array.choose Ironsight.Shell.OnlineWorld.eventToGameEvent |> Array.toList
        Assert.Equal<GameEvent list>(originals, decoded)

    [<Fact>]
    let ``lag compensation rewinds targets to the shooter's estimated tick`` () =
        // The ally turns ~90 degrees on the spot, then sprints off the axis
        // rifleman's fire line for `runTicks`. A shot aimed straight down the
        // original line hits only if the server rewinds the ally to where the
        // shooter's (lagged) estimated tick saw them — standing on the line.
        let run (estimatedTickFor: MatchHost -> int64) =
            let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Rewind range")
            let allyId, _ = host.TryAddPlayer("Ally").Value
            let axisId, _ = host.TryAddPlayer("Axis").Value
            TestKit.readyUp host [ allyId; axisId ]
            let mutable sequence = 1L
            for _ in 1..7 do
                TestKit.applyCustom sequence 0.0f 0.25f 0 host allyId
                // The rifleman pre-aims (ADS ramps over several ticks) so the
                // eventual single shot flies at ADS spread, not hip spread.
                TestKit.applyCustom sequence 0.0f 0.0f 2 host axisId
                host.AdvanceTick()
                sequence <- sequence + 1L
            let lineTick = host.Snapshot().Tick
            let onLine = host.Snapshot().Players[allyId].Position
            let runTicks = 11
            for _ in 1..runTicks do
                TestKit.applyCustom sequence 1.0f 0.0f 4 host allyId
                TestKit.applyCustom sequence 0.0f 0.0f 2 host axisId
                host.AdvanceTick()
                sequence <- sequence + 1L
            let offLine = host.Snapshot().Players[allyId].Position
            // The setup only proves anything if the sprint actually cleared
            // the widest hit capsule.
            Assert.True(Vector3.Distance(onLine, offLine) > 0.6f, "ally failed to leave the fire line")
            TestKit.applyCustomAt sequence 0.0f 0.0f 3 (estimatedTickFor host) host axisId
            host.AdvanceTick()
            Assert.True(host.Snapshot().Tick - lineTick <= 12L, "test outran the 12-tick history window")
            host.Snapshot().Events
            |> List.exists (fun event -> match event.Event with HitConfirmed _ -> true | _ -> false)
        // Estimated tick = when the ally was still on the line: the rewind hits.
        Assert.True(run (fun host -> host.Snapshot().Tick - 11L))
        // Estimated tick = now: no rewind, the ally has left the line, miss.
        Assert.False(run (fun host -> host.Snapshot().Tick))
