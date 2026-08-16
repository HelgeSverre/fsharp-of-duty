namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CoreTests =
    let input sequence buttons move =
        { Sequence = sequence; Move = move; Look = Vector2.Zero; Buttons = buttons }

    [<Fact>]
    let ``paintball killhouse is a bounded player versus four fight`` () =
        let world = Sim.createPaintballWorld 43UL
        let allies = world.Soldiers |> Array.filter (fun soldier -> soldier.Team = Allies)
        let axis = world.Soldiers |> Array.filter (fun soldier -> soldier.Team = Axis)
        Assert.Equal("Paintball Killhouse", world.Level.Name)
        Assert.Empty(allies)
        Assert.Equal(4, axis.Length)
        Assert.Equal(5, 1 + allies.Length + axis.Length)
        Assert.Equal("Thompson", world.Player.Slots[world.Player.Active].Class.Name)
        Assert.Equal(8, world.Level.Spawns |> Array.filter (fun struct (team, _) -> team = Some Allies) |> Array.length)
        Assert.Equal(8, world.Level.Spawns |> Array.filter (fun struct (team, _) -> team = Some Axis) |> Array.length)
        Assert.All(world.Level.Spawns, fun struct (_, position) ->
            Assert.InRange(position.X, world.Level.Bounds.Min.X, world.Level.Bounds.Max.X)
            Assert.InRange(position.Z, world.Level.Bounds.Min.Z, world.Level.Bounds.Max.Z)
            let obstructed =
                LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) world.Level
                |> Array.filter (fun brush -> brush.Bounds.Max.Y > 0.1f)
                |> Array.exists (fun brush -> MathEx.capsuleIntersectsAabb Tuning.PlayerRadius Tuning.StandingHeight position brush.Bounds)
            Assert.False(obstructed, $"Spawn intersects generated cover at {position}"))
        Assert.True(world.Level.Brushes.Length < 250)

    [<Fact>]
    let ``paintball rounds score and automatically reset combatants`` () =
        let original = Sim.createPaintballWorld 143UL
        let defeated =
            { original with
                Soldiers =
                    original.Soldiers
                    |> Array.map (fun soldier -> { soldier with Health = Units.health 0.0f; Behavior = Dying(Units.seconds 0.0f) }) }
        let struct (scored, events) = Sim.step (input 1L InputButtons.None Vector2.Zero) defeated
        let scoredRound = scored.Round.Value
        Assert.Equal(1, scoredRound.PlayerScore)
        Assert.True(scoredRound.ResetIn.IsSome)
        Assert.Contains(events, fun event -> event = Subtitle("MARSHAL", "ROUND WON"))
        let mutable reset = scored
        for tick in 2L..100L do
            reset <- let struct (next, _) = Sim.step (input tick InputButtons.None Vector2.Zero) reset in next
        let nextRound = reset.Round.Value
        Assert.Equal(2, nextRound.Number)
        Assert.Equal(1, nextRound.PlayerScore)
        Assert.True(reset.Player.Health > Units.health 0.0f)
        Assert.All(reset.Soldiers, fun soldier -> Assert.True(soldier.Health > Units.health 0.0f))

    [<Fact>]
    let ``generated battlefield contains terrain settlements and hundreds of troops`` () =
        let world = Sim.createBattlefieldWorld 44UL
        Assert.Equal("Normandy Battlefield", world.Level.Name)
        Assert.True(world.Level.Brushes.Length > 1000)
        Assert.True(world.Level.BrushGrid.Cells.Count > 1000)
        Assert.True(world.Soldiers.Length >= 220)
        Assert.True(world.Level.Nav.Length > 1000)
        Assert.NotEmpty(world.Level.MountedGuns)
        Assert.Contains(world.Soldiers, fun soldier -> soldier.Weapon.Class.Name = "MG42")

    [<Fact>]
    let ``mounted gun crew remains at its generated emplacement`` () =
        let mutable world = Sim.createStalingradWorld 144UL
        let gunner = world.Soldiers |> Array.find (fun soldier -> soldier.Weapon.Class.Name = "MG42")
        let initialPosition = gunner.Position
        for tick in 1L..180L do
            let struct (next, _) = Sim.step (input tick InputButtons.None Vector2.Zero) world
            world <- next
        let updated = world.Soldiers |> Array.find (fun soldier -> soldier.Id = gunner.Id)
        Assert.Equal(initialPosition, updated.Position)

    [<Fact>]
    let ``mass enemy contact produces sustained fire without instant player death`` () =
        let arena = LevelDsl.level "Aim pacing" [ LevelDsl.street 100.0f 60.0f Mud ] |> LevelCompile.compile
        let baseline = Sim.createTrainingWorld 45UL
        let player = { baseline.Player with Position = Vector3.Zero; Yaw = 0.0f }
        let enemies =
            Array.init 50 (fun index ->
                let x = (float32 (index % 10) - 4.5f) * 2.0f
                let z = -28.0f - float32 (index / 10) * 1.4f
                let weapon = { Tuning.weaponSlot Tuning.kar98k 4 with State = Cooling(Units.seconds (float32 (index % 60) / 60.0f)) }
                { Id = EntityId(100 + index); Team = Axis; Position = Vector3(x, 0.0f, z); Facing = MathF.PI
                  Health = Units.health 100.0f; Behavior = AdvancingTo(player.Position, []); Weapon = weapon; Squad = 2
                  Contacts = Map.add player.Id (struct (player.Position, Units.seconds 0.0f)) Map.empty; Suppression = 0.0f; AnimPhase = float32 index })
        let mutable world = { baseline with Player = player; Soldiers = enemies; Level = arena; Objectives = [||]; Script = { baseline.Script with Rules = [||] } }
        let mutable hostileShots = 0
        for tick in 1L..180L do
            let struct (next, events) = Sim.step (input tick InputButtons.None Vector2.Zero) world
            world <- next
            hostileShots <- hostileShots + (events |> List.filter (function ShotFired(Some(EntityId id), _, _, _) when id >= 100 -> true | _ -> false) |> List.length)
        Assert.True(hostileShots > 0)
        Assert.True(world.Player.Health > Units.health 0.0f)

    [<Fact>]
    let ``enemy fire uses a deliberately loose player facing accuracy model`` () =
        Assert.InRange(Tuning.EnemyMaxPlayerShooters, 1, 8)
        Assert.True(Tuning.EnemyAimSpreadMultiplier >= 2.0f)
        Assert.True(Tuning.EnemyDamageScale <= 0.25f)

    [<Fact>]
    let ``movement and sighting favor fast arcade response`` () =
        Assert.True(Tuning.WalkSpeed >= 5.0f)
        Assert.True(Tuning.WalkSpeed * Tuning.SprintMultiplier >= 8.0f)
        Assert.True(Tuning.kar98kSniper.AdsTime <= Units.seconds 0.20f)
        Assert.True(Tuning.thompson.AdsTime <= Units.seconds 0.14f)

    [<Fact>]
    let ``simulation is deterministic for equal seed and inputs`` () =
        let inputs = [ for tick in 1L..180L -> input tick InputButtons.Sprint (Vector2(0.2f, 1.0f)) ]
        let run () =
            inputs
            |> List.fold (fun world frame -> let struct (next, _) = Sim.step frame world in next) (Sim.createTrainingWorld 42UL)
        Assert.Equal(run (), run ())

    [<Fact>]
    let ``weapon enforces cooldown and consumes one round`` () =
        let world = Sim.createTrainingWorld 1UL
        let struct (fired, events) = Sim.step (input 1L InputButtons.Fire Vector2.Zero) world
        let struct (cooled, secondEvents) = Sim.step (input 2L InputButtons.Fire Vector2.Zero) fired
        Assert.Equal(world.Player.Slots[0].InMag - 1, fired.Player.Slots[0].InMag)
        Assert.Single(events |> List.filter (function ShotFired(Some(EntityId 1), _, _, _) -> true | _ -> false)) |> ignore
        Assert.Empty(secondEvents |> List.filter (function ShotFired(Some(EntityId 1), _, _, _) -> true | _ -> false))
        Assert.Equal(fired.Player.Slots[0].InMag, cooled.Player.Slots[0].InMag)

    [<Fact>]
    let ``a reload tap completes without keeping reload held`` () =
        let initial = Sim.createTrainingWorld 11UL
        let slots = Array.copy initial.Player.Slots
        slots[0] <- { slots[0] with InMag = 1; Reserve = 12 }
        let mutable world = { initial with Player = { initial.Player with Slots = slots } }
        world <- let struct (next, _) = Sim.step (input 1L InputButtons.Reload Vector2.Zero) world in next
        Assert.True(match world.Player.Slots[0].State with Reloading _ -> true | _ -> false)
        for sequence in 2L..180L do
            world <- let struct (next, _) = Sim.step (input sequence InputButtons.None Vector2.Zero) world in next
        let weapon = world.Player.Slots[0]
        Assert.Equal(weapon.Class.MagSize, weapon.InMag)
        Assert.Equal(Ready, weapon.State)
        Assert.True(Tuning.kar98k.ReloadTime < Units.seconds 2.6f)

    [<Fact>]
    let ``number key switches campaign weapon after transition`` () =
        let mutable world = Sim.createTrainingWorld 17UL
        world <- let struct (next, _) = Sim.step (input 1L InputButtons.Weapon2 Vector2.Zero) world in next
        for sequence in 2L..24L do
            world <- let struct (next, _) = Sim.step (input sequence InputButtons.None Vector2.Zero) world in next
        Assert.Equal(1, world.Player.Active)
        Assert.Equal("Thompson", world.Player.Slots[world.Player.Active].Class.Name)

    [<Fact>]
    let ``fourth slot equips a scoped high damage sniper rifle`` () =
        let mutable world = Sim.createTrainingWorld 18UL
        world <- let struct (next, _) = Sim.step (input 1L InputButtons.Weapon4 Vector2.Zero) world in next
        for sequence in 2L..24L do
            world <- let struct (next, _) = Sim.step (input sequence InputButtons.None Vector2.Zero) world in next
        let sniper = world.Player.Slots[world.Player.Active].Class
        Assert.Equal(3, world.Player.Active)
        Assert.Equal("Kar98k Sniper", sniper.Name)
        Assert.True(sniper.Damage > Tuning.kar98k.Damage)

    [<Fact>]
    let ``fifth slot equips a pellet shotgun without replacing the sniper`` () =
        let mutable world = Sim.createTrainingWorld 19UL
        world <- let struct (next, _) = Sim.step (input 1L InputButtons.Weapon5 Vector2.Zero) world in next
        for sequence in 2L..24L do
            world <- let struct (next, _) = Sim.step (input sequence InputButtons.None Vector2.Zero) world in next
        let beforeAmmo = world.Player.Slots[world.Player.Active].InMag
        let struct (fired, events) = Sim.step (input 25L InputButtons.Fire Vector2.Zero) world
        let shots =
            events
            |> List.filter (function ShotFired(Some(EntityId 1), _, _, "M1897 Trench Gun") -> true | _ -> false)
        let mutable rng = Rng.create 91UL
        let struct (_, pellets) = Weapons.step Tuning.TickDuration true false 0.0f &rng (Tuning.weaponSlot Tuning.m1897 5)
        Assert.Equal(4, world.Player.Active)
        Assert.Equal("M1897 Trench Gun", world.Player.Slots[world.Player.Active].Class.Name)
        Assert.Equal(1, shots.Length)
        Assert.Equal(8, pellets.Length)
        Assert.Equal(beforeAmmo - 1, fired.Player.Slots[fired.Player.Active].InMag)

    [<Fact>]
    let ``bolt action requires trigger release before another shot`` () =
        let mutable world = Sim.createTrainingWorld 23UL
        let mutable shots = 0
        for sequence in 1L..120L do
            let struct (next, events) = Sim.step (input sequence InputButtons.Fire Vector2.Zero) world
            world <- next
            shots <- shots + (events |> List.filter (function ShotFired(Some(EntityId 1), _, _, _) -> true | _ -> false) |> List.length)
        Assert.Equal(1, shots)

    [<Fact>]
    let ``jump follows gravity and lands on generated ground`` () =
        let mutable world = Sim.createTrainingWorld 29UL
        let startY = world.Player.Position.Y
        world <- let struct (next, _) = Sim.step (input 1L InputButtons.Jump Vector2.Zero) world in next
        Assert.True(world.Player.Position.Y > startY)
        for sequence in 2L..120L do
            world <- let struct (next, _) = Sim.step (input sequence InputButtons.None Vector2.Zero) world in next
        Assert.InRange(world.Player.Position.Y, 0.0f, 0.002f)
        Assert.Equal(0.0f, world.Player.Velocity.Y)

    [<Fact>]
    let ``controller preserves generated terrain step height`` () =
        let level =
            LevelDsl.level "Terrain step"
                [ LevelDsl.street 20.0f 12.0f Mud
                  LevelDsl.block (Vector3(0.0f, 0.125f, -1.0f)) (Vector3(4.0f, 0.25f, 2.0f)) Mud
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 0.45f)) ]
            |> LevelCompile.compile
        let mutable player = { (Sim.createTrainingWorld 51UL).Player with Position = Vector3(0.0f, 0.0f, 0.45f) }
        let mutable highest = 0.0f
        for tick in 1L..30L do
            player <- Movement.step Tuning.TickDuration (input tick InputButtons.None Vector2.UnitY) level player
            highest <- max highest player.Position.Y
        Assert.InRange(highest, 0.249f, 0.251f)

    [<Fact>]
    let ``walking emits a generated-surface footstep event`` () =
        let world = Sim.createTrainingWorld 31UL
        let struct (_, events) = Sim.step (input 1L InputButtons.None Vector2.UnitY) world
        Assert.Contains(events, fun event -> match event with FootStep(_, Mud) -> true | _ -> false)

    [<Fact>]
    let ``friendly kill does not score in team deathmatch`` () =
        let makePlayer id team =
            { Id = EntityId id; Name = string id; Team = team; Position = Vector3.Zero; Velocity = Vector3.Zero
              Yaw = 0.0f; Pitch = 0.0f; Stance = Standing; Sprinting = false; Ads = 0.0f
              Health = Units.health 100.0f; RegenIn = Units.seconds 0.0f; Weapon = Tuning.weaponSlot Tuning.kar98k 2
              Grenade = GrenadeIdle 3; Connected = true; Ready = true; Alive = true; RespawnIn = Units.seconds 0.0f; SpawnProtection = Units.seconds 0.0f
              Kills = 0; Deaths = 0; LastInputSequence = 0L }
        let state =
            { Multiplayer.create TeamDeathmatch with
                Players = [ EntityId 1, makePlayer 1 Allies; EntityId 2, makePlayer 2 Allies ] |> Map.ofList }
        let result = Multiplayer.recordKill (EntityId 1) (EntityId 2) state
        Assert.Equal(0, result.AlliesScore)
        Assert.True(result.Players[EntityId 2].Alive)

    [<Fact>]
    let ``team deathmatch hostile kill scores and marks victim dead`` () =
        let makePlayer id team =
            { Id = EntityId id; Name = string id; Team = team; Position = Vector3.Zero; Velocity = Vector3.Zero
              Yaw = 0.0f; Pitch = 0.0f; Stance = Standing; Sprinting = false; Ads = 0.0f
              Health = Units.health 100.0f; RegenIn = Units.seconds 0.0f; Weapon = Tuning.weaponSlot Tuning.kar98k 2
              Grenade = GrenadeIdle 3; Connected = true; Ready = true; Alive = true; RespawnIn = Units.seconds 0.0f; SpawnProtection = Units.seconds 0.0f
              Kills = 0; Deaths = 0; LastInputSequence = 0L }
        let state =
            { Multiplayer.create TeamDeathmatch with
                Players = [ EntityId 1, makePlayer 1 Allies; EntityId 2, makePlayer 2 Axis ] |> Map.ofList }
        let result = Multiplayer.recordKill (EntityId 1) (EntityId 2) state
        Assert.Equal(1, result.AlliesScore)
        Assert.Equal(1, result.Players[EntityId 1].Kills)
        Assert.False(result.Players[EntityId 2].Alive)

    [<Fact>]
    let ``names are trimmed and limited to 24 unicode scalars`` () =
        let name = "  " + String.replicate 30 "⚙" + "  "
        let sanitized = Multiplayer.sanitizeName name
        Assert.Equal(24, sanitized.EnumerateRunes() |> Seq.length)
