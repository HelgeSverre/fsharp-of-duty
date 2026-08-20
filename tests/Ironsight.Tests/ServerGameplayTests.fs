namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Server
open Xunit

module ServerGameplayTests =
    open ServerTests
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
    let ``the scroll wheel switches weapons online`` () =
        // Same mask that used to swallow the number keys: the scroll bits have
        // to be let through too or the wheel is dead online only.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArena "Scroll range")
        let playerId, _ = host.TryAddPlayer("Scroller", weaponName = "Kar98k").Value
        let otherId, _ = host.TryAddPlayer("Other").Value
        TestKit.readyUp host [ playerId; otherId ]
        let heldName () =
            let player = host.Snapshot().Players[playerId]
            player.Slots[player.Active].Class.Name
        Assert.Equal("Kar98k", heldName ())
        let scroll button =
            let mutable sequence = 1L
            for _ in 1..40 do
                applyCustom sequence 0.0f 0.0f (int button) host playerId
                host.AdvanceTick()
                sequence <- sequence + 1L
        scroll InputButtons.WeaponNext
        Assert.Equal(Tuning.sidearm(host.Snapshot().Players[playerId].Team).Name, heldName ())
        // Two slots, so back the other way returns to the primary.
        scroll InputButtons.WeaponPrev
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
        let board = Protocol.leaderboard "Test Server" [| "tdm", "Team Deathmatch", 16, tdm.Snapshot(); "ffa", "Free For All", 8, (MatchHost FreeForAll).Snapshot() |]
        Assert.Equal("Test Server", board.name)
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
        let bow = arsenal.weapons |> Array.find (fun weapon -> weapon.name = Tuning.bow.Name)
        let mg42 = arsenal.weapons |> Array.find (fun weapon -> weapon.name = Tuning.mg42.Name)

        Assert.Equal(Tuning.onlineWeapons.Length + 1, arsenal.weapons.Length)
        Assert.Equal(120.0f, sniper.damagePerProjectile)
        Assert.Equal(0.18f, sniper.aimDownSightSeconds)
        Assert.Equal(8, shotgun.projectilesPerShot)
        Assert.Equal(128.0f, shotgun.maximumDamagePerShot)
        Assert.Equal(42.0f, bow.minimumDamagePerProjectile)
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
    let ``every weapon has an orbitable model on the website`` () =
        // Same bargain as the arsenal snapshot: checked in, so the page needs
        // no build step, and guarded, so it cannot go stale. Regenerate with
        // `just model-sync` after adding a weapon or reshaping one.
        let directory = IO.Path.Combine(AppContext.BaseDirectory, "wwwroot", "models")
        // Every weapon the arsenal page lists, which is one more than you can
        // carry: the MG42 is emplaced, and still has a tab.
        for weapon in Array.append Tuning.onlineWeapons [| Tuning.mg42 |] do
            let slug =
                weapon.Name.ToLowerInvariant()
                |> String.map (fun character -> if Char.IsAsciiLetterOrDigit character then character else '-')
                |> fun text -> text.Trim '-'
            let path = IO.Path.Combine(directory, slug + ".json")
            Assert.True(IO.File.Exists path, $"{weapon.Name} has no model at models/{slug}.json")
            let model = System.Text.Json.Nodes.JsonNode.Parse(IO.File.ReadAllText path)
            let mesh = Guns.meshFor weapon.Name
            // Triangle and vertex counts are enough to catch a reshaped mesh; a
            // full comparison would just be the exporter run twice.
            Assert.Equal(mesh.Indices.Length, model["tris"].AsArray().Count)
            Assert.Equal(mesh.Vertices.Length * 3, model["positions"].AsArray().Count)
            Assert.Equal(mesh.Indices.Length / 3, model["mats"].AsArray().Count)

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
    let ``barrel heat reaches the wire so the client predicts the same rate`` () =
        // Heat decides how fast a belt-fed gun cycles. If it does not reach the
        // client, prediction runs a cold gun's rate against the server's hot
        // one and every held burst mispredicts.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Heat range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        host.SetLoadout(axisId, Tuning.minigun.Name)
        TestKit.readyUp host [ allyId; axisId ]
        // Two seconds of held trigger is plenty to get the barrels warm.
        for tick in 1 .. Tuning.TickRate * 2 do
            TestKit.applyCustom (int64 tick) 0.0f 0.0f 1 host axisId
            host.AdvanceTick()
        let state = host.Snapshot()
        let served = state.Players[axisId].Slots[state.Players[axisId].Active]
        Assert.Equal(Tuning.minigun.Name, served.Class.Name)
        Assert.True(served.Heat > 0.0f, "the server never heated the gun")
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot state))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        let wire = parsed.Players |> Array.find (fun player -> player.Name = "Axis")
        Assert.Equal(served.Heat, wire.Slots[wire.Active].Heat)

    [<Fact>]
    let ``the damage indicator points back at whoever shot you`` () =
        // The server sent the shot's travel direction, which points AWAY from
        // the shooter, so the online indicator marked every hit as coming from
        // behind you. Offline rifle fire already sent the opposite.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Bearing range")
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        TestKit.readyUp host [ allyId; axisId ]
        let before = host.Snapshot()
        let shooter = before.Players[axisId].Position
        let victim = before.Players[allyId].Position
        TestKit.rifleShot host 1L axisId allyId |> ignore
        let hurt =
            host.Snapshot().Events
            |> Seq.map (fun replicated -> replicated.Event)
            |> Seq.tryPick (function PlayerHurt(towardAttacker, _) -> Some towardAttacker | _ -> None)
        match hurt with
        | None -> failwith "no PlayerHurt event reached the victim"
        | Some towardAttacker ->
            let expected = MathEx.normalizedOrZero (MathEx.horizontal (shooter - victim))
            let agreement = Vector3.Dot(MathEx.normalizedOrZero (MathEx.horizontal towardAttacker), expected)
            Assert.True(agreement > 0.9f, $"indicator points {agreement} of the way toward the shooter")

    [<Fact>]
    let ``an online bow flies a real arrow and kills with it`` () =
        // The bow used to be ballistic offline and hitscan online: the server
        // never simulated a projectile at all, so the same weapon had two
        // different physics depending on who was running it.
        let host = MatchHost(TeamDeathmatch, TestKit.streetArenaWithSpawns "Archery range")
        let archer, _ = host.TryAddPlayer("Archer", weaponName = "Bow").Value
        let target, _ = host.TryAddPlayer("Target").Value
        TestKit.readyUp host [ archer; target ]
        let start = host.Snapshot()
        let from = start.Players[archer].Position
        let victim = start.Players[target].Position
        // Draw for a second, then release by sending a frame without Fire.
        let direction = Vector3.Normalize(victim - from)
        let yaw = MathF.Atan2(direction.X, -direction.Z)
        let mutable sequence = 1L
        let mutable remaining = yaw - start.Players[archer].Yaw
        while MathF.Abs remaining > 0.0001f do
            let look = Math.Clamp(remaining, -0.25f, 0.25f)
            TestKit.applyCustom sequence 0.0f look 0 host archer
            host.AdvanceTick()
            sequence <- sequence + 1L
            remaining <- remaining - look
        // Drawn sighted: a hip-fired bow spreads enough to miss a torso at
        // twenty metres, and this test is about physics, not marksmanship.
        for _ in 1 .. Tuning.TickRate do
            TestKit.applyCustom sequence 0.0f 0.0f 3 host archer
            host.AdvanceTick()
            sequence <- sequence + 1L
        TestKit.applyCustom sequence 0.0f 0.0f 2 host archer
        host.AdvanceTick()
        // An arrow now exists, is server-owned, and is on the wire.
        let released = host.Snapshot()
        Assert.NotEmpty released.Projectiles
        Assert.All(released.Projectiles, fun projectile -> Assert.Equal(archer, projectile.Owner))
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot released))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        Assert.NotEmpty parsed.Projectiles
        Assert.Equal("arrow", parsed.Projectiles[0].Kind)
        // It travels rather than arriving: the victim is untouched on the tick
        // the string is loosed, and hit some ticks later.
        Assert.Equal(released.Players[target].Health, start.Players[target].Health)
        let mutable ticks = 0
        while ticks < Tuning.TickRate && host.Snapshot().Players[target].Health = start.Players[target].Health do
            host.AdvanceTick()
            ticks <- ticks + 1
        Assert.True(ticks > 0, "the arrow arrived instantly, which is hitscan")
        Assert.True(
            host.Snapshot().Players[target].Health < start.Players[target].Health,
            "the arrow never landed")

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

    let private closeMeleeArena name =
        LevelDsl.level name
            [ LevelDsl.street 20.0f 10.0f Mud
              LevelDsl.spawnSquad Allies 1 Vector3.Zero
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -0.82f)) ]
        |> LevelCompile.compile

    [<Fact>]
    let ``katana overhead death persists a cut descriptor on the wire`` () =
        let host = MatchHost(TeamDeathmatch, closeMeleeArena "Katana cut")
        let samurai, _ = host.TryAddPlayer("Samurai", weaponName = "Katana").Value
        let target, _ = host.TryAddPlayer("Target").Value
        TestKit.readyUp host [ samurai; target ]
        let mutable sequence = 1L
        for _ in 1..32 do
            TestKit.applyCustom sequence 0.0f 0.0f (int InputButtons.Ads) host samurai
            host.AdvanceTick()
            sequence <- sequence + 1L
        TestKit.applyCustom sequence 0.0f 0.0f (int (InputButtons.Fire ||| InputButtons.Ads)) host samurai
        host.AdvanceTick()
        let dead = host.Snapshot().Players[target]
        Assert.False dead.Alive
        Assert.True dead.Cut.IsSome
        Assert.Equal(CutNeck, dead.Cut.Value.Site)
        Assert.Equal(dead.LifeRevision, dead.Cut.Value.DeathRevision)
        Assert.InRange(dead.Cut.Value.LocalPlaneNormal.Length(), 0.999f, 1.001f)
        Assert.InRange(dead.Cut.Value.LocalBladeTangent.Length(), 0.999f, 1.001f)
        Assert.InRange(dead.Cut.Value.LocalSweepDirection.Length(), 0.999f, 1.001f)
        use document = System.Text.Json.JsonDocument.Parse(Protocol.serialize (Protocol.snapshot (host.Snapshot())))
        let parsed = Ironsight.Shell.SnapshotWire.parseSnapshot document.RootElement
        let wireTarget = parsed.Players |> Array.find (fun player -> player.Id = (let (EntityId id) = target in id))
        Assert.Equal(Some dead.Cut.Value, wireTarget.Cut)
        Assert.Equal(dead.AnimPhase, wireTarget.AnimPhase)
