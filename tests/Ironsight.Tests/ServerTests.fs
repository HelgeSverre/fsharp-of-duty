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
    let ``input flooding cannot advance movement faster than server ticks`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Runner").Value
        let before = host.Snapshot().Players[playerId].Position
        applyInput 1 host playerId
        applyInput 2 host playerId
        applyInput 3 host playerId
        host.AdvanceTick()
        let player = host.Snapshot().Players[playerId]
        let distance = Vector3.Distance(before, player.Position)
        Assert.InRange(distance, 0.0f, Tuning.WalkSpeed * Tuning.SprintMultiplier / float32 Tuning.TickRate + 0.001f)
        Assert.Equal(3L, player.LastInputSequence)

    [<Fact>]
    let ``far future input sequence is rejected`` () =
        let host = MatchHost FreeForAll
        let playerId, _ = host.TryAddPlayer("Time traveler").Value
        applyInput 10000 host playerId
        host.AdvanceTick()
        Assert.Equal(-1L, host.Snapshot().Players[playerId].LastInputSequence)

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
        applyCustom sequence 0.0f 0.0f 3 host axisId
        host.AdvanceTick()
        sequence <- sequence + 1L
        for _ in 1..85 do
            applyCustom sequence 0.0f 0.0f 2 host axisId
            host.AdvanceTick()
            sequence <- sequence + 1L
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
    let ``selected online weapon is authoritative and replicated`` () =
        let host = MatchHost TeamDeathmatch
        let playerId, _ = host.TryAddPlayer("Scout", weaponName = "Kar98k Sniper").Value
        let player = host.Snapshot().Players[playerId]
        Assert.Equal("Kar98k Sniper", player.Weapon.Class.Name)
        let wire = Protocol.snapshot (host.Snapshot())
        Assert.Equal("Kar98k Sniper", wire.players[0].weapon)
