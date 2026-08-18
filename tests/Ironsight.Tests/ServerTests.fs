namespace Ironsight.Tests

open System
open System.Numerics
open System.Text.Json
open Ironsight
open Ironsight.ProcGen
open Ironsight.Server
open Xunit

module ServerTests =
    let private applyCustom sequence moveY lookX buttons (host: MatchHost) id =
        use document =
            JsonDocument.Parse(FormattableString.Invariant($"""{{"type":"input","sequence":{sequence},"moveX":0,"moveY":{moveY},"lookX":{lookX},"lookY":0,"buttons":{buttons}}}"""))
        host.ApplyInput(id, document.RootElement)

    let private applyInput sequence host id = applyCustom sequence 1.0f 0.0f 4 host id

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
        let arena = LevelDsl.level "Ghost range" [ LevelDsl.street 50.0f 20.0f Mud ] |> LevelCompile.compile
        let host = MatchHost(FreeForAll, arena)
        let thrower, _ = host.TryAddPlayer("Thrower").Value
        let ghost, _ = host.TryAddPlayer("Ghost").Value
        host.SetReady thrower
        host.SetReady ghost
        for _ in 1..721 do host.AdvanceTick()
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
        let arena =
            LevelDsl.level "Server range"
                [ LevelDsl.street 50.0f 20.0f Mud
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 12.0f))
                  LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -12.0f)) ]
            |> LevelCompile.compile
        let host = MatchHost(TeamDeathmatch, arena)
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        host.SetReady allyId
        host.SetReady axisId
        for _ in 1..721 do host.AdvanceTick()
        Assert.Equal(Playing, host.Snapshot().Phase)
        let initial = host.Snapshot()
        let shooter = initial.Players[axisId]
        let target = initial.Players[allyId]
        let direction = Vector3.Normalize(target.Position - shooter.Position)
        let rawYaw = MathF.Atan2(direction.X, -direction.Z)
        let desiredYaw = if rawYaw < shooter.Yaw - MathF.PI then rawYaw + MathF.Tau else rawYaw
        let mutable remainingLook = desiredYaw - shooter.Yaw
        let mutable sequence = 1L
        while MathF.Abs remainingLook > 0.0001f do
            let look = Math.Clamp(remainingLook, -0.25f, 0.25f)
            applyCustom sequence 0.0f look 2 host axisId
            host.AdvanceTick()
            sequence <- sequence + 1L
            remainingLook <- remainingLook - look
        for _ in 1..20 do
            applyCustom sequence 0.0f 0.0f 2 host axisId
            host.AdvanceTick()
            sequence <- sequence + 1L
        // Shots trace from the eye, so a level shot at an equal-height target
        // lands at head height: one Kar98k headshot is lethal.
        applyCustom sequence 0.0f 0.0f 3 host axisId
        host.AdvanceTick()
        let result = host.Snapshot()
        Assert.Equal(1, result.AxisScore)
        Assert.Equal(1, result.Players[axisId].Kills)
        Assert.False(result.Players[allyId].Alive)
        Assert.Equal(Units.seconds 5.0f, result.Players[allyId].RespawnIn)
        Assert.Contains(result.Events, fun event -> match event.Event with ShotFired _ -> true | _ -> false)
        Assert.NotEmpty((Protocol.snapshot result).events)

    [<Fact>]
    let ``player can move again after dying and respawning`` () =
        let arena =
            LevelDsl.level "Respawn range"
                [ LevelDsl.street 50.0f 20.0f Mud
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 12.0f))
                  LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -12.0f)) ]
            |> LevelCompile.compile
        let host = MatchHost(TeamDeathmatch, arena)
        let allyId, _ = host.TryAddPlayer("Ally").Value
        let axisId, _ = host.TryAddPlayer("Axis").Value
        host.SetReady allyId
        host.SetReady axisId
        for _ in 1..721 do host.AdvanceTick()
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
        Assert.Equal(WeaponState.Ready, player.Weapon.State)
        Assert.Equal(player.Weapon.Class.MagSize, player.Weapon.InMag)

    [<Fact>]
    let ``authoritative grenade is cooked thrown and included in snapshots`` () =
        let arena = LevelDsl.level "Grenade range" [ LevelDsl.street 50.0f 20.0f Mud ] |> LevelCompile.compile
        let host = MatchHost(FreeForAll, arena)
        let first, _ = host.TryAddPlayer("Thrower").Value
        let second, _ = host.TryAddPlayer("Witness").Value
        host.SetReady first
        host.SetReady second
        for _ in 1..721 do host.AdvanceTick()
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
    let ``crouch toggles online and survives the player round trip`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Croucher").Value
        applyCustom 1L 0.0f 0.0f (int InputButtons.Crouch) host playerId
        host.AdvanceTick()
        Assert.Equal(Crouched, host.Snapshot().Players[playerId].Stance)
        applyCustom 2L 0.0f 0.0f 0 host playerId
        host.AdvanceTick()
        Assert.Equal(Crouched, host.Snapshot().Players[playerId].Stance)
        applyCustom 3L 0.0f 0.0f (int InputButtons.Crouch) host playerId
        host.AdvanceTick()
        Assert.Equal(Standing, host.Snapshot().Players[playerId].Stance)

    [<Fact>]
    let ``selected online weapon is authoritative and replicated`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Scout", weaponName = "Kar98k Sniper").Value
        let player = host.Snapshot().Players[playerId]
        Assert.Equal("Kar98k Sniper", player.Weapon.Class.Name)
        let wire = Protocol.snapshot (host.Snapshot())
        Assert.Equal("Kar98k Sniper", wire.players[0].weapon)

    [<Fact>]
    let ``leaderboard publishes only connected players and their live loadouts`` () =
        let tdm = MatchHost TeamDeathmatch
        let onlineId, _ = tdm.TryAddPlayer("Public Hero", weaponName = "M1897 Trench Gun").Value
        let offlineId, _ = tdm.TryAddPlayer("Gone Already").Value
        tdm.RemovePlayer offlineId
        let board = Protocol.leaderboard [| tdm.Snapshot(); (MatchHost FreeForAll).Snapshot() |]
        Assert.Equal(16, board.capacityPerRoom)
        Assert.Equal(2, board.rooms.Length)
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
