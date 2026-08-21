namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CombatTests =
    let internal soldier = TestKit.soldier 9

    let internal soldierAt = TestKit.soldier

    let internal openLevel = TestKit.streetArenaSized "Range" 30.0f 10.0f

    let internal runSpecial ticks level player soldiers projectile =
        let mutable active = [| projectile |]
        let mutable marks = [||]
        let mutable currentPlayer = player
        let mutable currentSoldiers = soldiers
        let events = ResizeArray<GameEvent>()
        for _ in 1..ticks do
            let next, nextMarks, nextPlayer, nextSoldiers, emitted =
                SpecialProjectiles.step Tuning.TickDuration level currentPlayer currentSoldiers active marks
            active <- next
            marks <- nextMarks
            currentPlayer <- nextPlayer
            currentSoldiers <- nextSoldiers
            events.AddRange emitted
        active, marks, currentPlayer, currentSoldiers, List.ofSeq events

    let internal runSpecialWithStatus ticks level player soldiers projectiles statuses =
        let mutable active = projectiles
        let mutable marks = [||]
        let mutable currentPlayer = player
        let mutable currentSoldiers = soldiers
        let mutable currentStatus = statuses
        let events = ResizeArray<GameEvent>()
        for _ in 1..ticks do
            let next, nextMarks, nextPlayer, nextSoldiers, nextStatus, emitted =
                SpecialProjectiles.stepWithStatus Tuning.TickDuration level currentPlayer currentSoldiers active marks currentStatus
            active <- next
            marks <- nextMarks
            currentPlayer <- nextPlayer
            currentSoldiers <- nextSoldiers
            currentStatus <- nextStatus
            events.AddRange emitted
        active, marks, currentPlayer, currentSoldiers, currentStatus, List.ofSeq events

    [<Fact>]
    let ``level DSL compiles one source into collision render navigation cover and spawns`` () =
        let level = Levels.canalYard
        Assert.NotEmpty level.Brushes
        Assert.NotEmpty level.Vertices
        Assert.NotEmpty level.Indices
        Assert.NotEmpty level.Nav
        Assert.NotEmpty level.Cover
        Assert.Equal(16, level.Spawns.Length)
        Assert.Equal(0, level.Vertices.Length % 24)
        Assert.Equal(0, level.Indices.Length % 36)

    [<Fact>]
    let ``compiled brush triangles wind toward their outward normals`` () =
        let level = TestKit.streetArenaSized "Winding" 10.0f 5.0f
        for a, b, c in TestKit.triangles level.Vertices level.Indices do
            let geometricNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position)
            Assert.True(Vector3.Dot(geometricNormal, a.Normal) > 0.0f, "a compiled triangle has reversed winding")

    [<Fact>]
    let ``paintball arenas compile with nav cover and spawns for both teams`` () =
        for level in [ Levels.scrapDepot; Levels.canalYard ] do
            Assert.NotEmpty level.Nav
            Assert.NotEmpty level.Cover
            let team wanted = level.Spawns |> Array.filter (fun struct (owner, _) -> owner = Some wanted)
            Assert.True((team Allies).Length >= 8, $"{level.Name}: too few Allies spawns")
            Assert.True((team Axis).Length >= 8, $"{level.Name}: too few Axis spawns")
        let depot = Sim.createScrapDepotWorld 11UL
        Assert.True(depot.Round.IsSome)
        Assert.Equal(5, depot.Soldiers |> Array.filter (fun soldier -> soldier.Team = Axis) |> Array.length)
        let canal = Sim.createCanalYardWorld 11UL
        Assert.True(canal.Round.IsSome)
        Assert.Equal(5, canal.Soldiers |> Array.filter (fun soldier -> soldier.Team = Axis) |> Array.length)
        // Axis spawns on the raised canal bank must snap onto the bank surface.
        let bankSpawns =
            Levels.canalYard.Spawns
            |> Array.filter (fun struct (owner, position) -> owner = Some Axis && position.X >= 12.0f)
        Assert.All(bankSpawns, fun struct (_, position) -> Assert.True(position.Y > 1.0f))

    [<Fact>]
    let ``every map on the offline menu actually loads that map`` () =
        // createOfflineWorld used to keep its own alias table beside the level
        // registry's, so a map added to the menu loaded the paintball arena
        // instead — silently, because the fallback is a real map.
        for alias in Levels.offlineAliases do
            let expected = (Levels.byAlias alias).Value
            let world = Sim.createOfflineWorld alias 7UL
            Assert.Equal(expected.Name, world.Level.Name)
            Assert.NotEmpty(world.Soldiers |> Array.filter (fun soldier -> soldier.Team = Axis))
            Assert.True(world.Round.IsSome, $"{alias} has no round")

    [<Fact>]
    let ``killhouse is an indoors map you can fight through`` () =
        let level = Levels.killhouse
        Assert.Single level.Ladders |> ignore
        // Roofed, and the roof is over your head rather than under your feet.
        Assert.True(level.Bounds.Max.Y > 7.0f)
        let nearest (target: Vector3) =
            level.Nav |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(node.Position, target)) |> Array.minBy snd |> fst
        // Spawns land on the floor of the hall, not on top of the building.
        for struct (_, position) in level.Spawns do
            Assert.InRange(position.Y, -0.1f, 0.5f)
        // Everything that matters is reachable from a spawn: the far team, and
        // the tower deck by way of its ladder.
        let reachable =
            let seen = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<int>()
            let start = nearest (level.Spawns |> Array.pick (fun struct (team, p) -> if team = Some Allies then Some p else None))
            queue.Enqueue start
            seen.Add start |> ignore
            while queue.Count > 0 do
                for neighbour in level.Nav[queue.Dequeue()].Neighbours do
                    if seen.Add neighbour then queue.Enqueue neighbour
            seen
        let axisSpawn = level.Spawns |> Array.pick (fun struct (team, p) -> if team = Some Axis then Some p else None)
        Assert.True(reachable.Contains(nearest axisSpawn), "the far spawn is unreachable")
        Assert.True(reachable.Contains(nearest (Vector3(0.3f, 4.9f, 1.3f))), "the tower deck is unreachable")

    [<Fact>]
    let ``rust compiles with a standing derrick and reachable high ground`` () =
        let level = Levels.rust
        Assert.NotEmpty level.Nav
        Assert.NotEmpty level.Cover
        for team in [ Allies; Axis ] do
            let spawns = level.Spawns |> Array.filter (fun struct (owner, _) -> owner = Some team)
            Assert.True(spawns.Length >= 8, $"{team}: too few spawns ({spawns.Length})")
            // No spawn may be inside the compound's cover.
            for struct (_, position) in spawns do
                let obstructed =
                    LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) level
                    |> Array.filter (fun brush -> brush.Bounds.Max.Y > position.Y + 0.1f)
                    |> Array.exists (fun brush ->
                        MathEx.capsuleIntersectsAabb Tuning.PlayerRadius Tuning.StandingHeight position brush.Bounds)
                Assert.False(obstructed, $"{team} spawn buried at {position}")
        // The derrick is the silhouette: it has to actually reach into the sky.
        Assert.True(level.Bounds.Max.Y > 24.0f, $"derrick too short, world tops out at {level.Bounds.Max.Y}")
        // And the vertical play has to be navigable, not just standable. Nav
        // now keeps only what a spawn can reach, so a node above the yard floor
        // existing at all means something can walk to it — the derrick pad's
        // ramp was 55 degrees and ran through a building, and this is the
        // assertion that caught both.
        let high = level.Nav |> Array.filter (fun node -> node.Position.Y > 3.0f)
        Assert.True(high.Length >= 4, $"only {high.Length} reachable nav nodes above the yard floor")

    [<Fact>]
    let ``dust two preserves its routes and both teams can cross the map`` () =
        let level = Levels.dust2
        Assert.Equal("Dust II", level.Name)
        Assert.Equal(9932, level.Collision.Triangles.Length)
        Assert.InRange(level.Bounds.Max.X - level.Bounds.Min.X, 111.9f, 112.1f)
        Assert.InRange(level.Bounds.Max.Z - level.Bounds.Min.Z, 132.7f, 132.9f)
        Assert.Contains(level.Vertices, fun vertex -> Materials.isImported vertex.MaterialId && vertex.TexCoord <> Vector2.Zero)
        for team in [ Allies; Axis ] do
            let spawns = level.Spawns |> Array.filter (fun struct (owner, _) -> owner = Some team)
            Assert.Equal(8, spawns.Length)
            Assert.All(spawns, fun struct (_, position) ->
                Assert.NotEmpty(LevelCompile.surfaceLayers level.Collision position.X position.Z)
                Assert.InRange(position.Y, -0.1f, 3.3f))
        let nearest (target: Vector3) =
            level.Nav
            |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(node.Position, target))
            |> Array.minBy snd
            |> fst
        let reachable =
            let seen = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<int>()
            let axisSpawn = level.Spawns |> Array.pick (fun struct (owner, position) -> if owner = Some Axis then Some position else None)
            queue.Enqueue(nearest axisSpawn)
            while queue.Count > 0 do
                let current = queue.Dequeue()
                if seen.Add current then
                    for next in level.Nav[current].Neighbours do queue.Enqueue next
            seen
        let alliesSpawn = level.Spawns |> Array.pick (fun struct (owner, position) -> if owner = Some Allies then Some position else None)
        Assert.True(reachable.Contains(nearest alliesSpawn), "the opposing spawn is unreachable")

        // The defining T-spawn pick through the gap in mid double doors. A
        // sealed or shifted door leaf makes Dust II wrong even if its outer
        // silhouette still resembles the map.
        let eye = Vector3(30.0f, 1.6f, -48.0f)
        let throughDoors = Vector3(8.0f, 1.6f, 10.0f) - eye
        let distance = throughDoors.Length()
        let direction = throughDoors / distance
        let obstruction =
            LevelCompile.trianglesAlongRay eye direction distance level
            |> Array.tryPick (fun triangle ->
                match MathEx.rayTriangle eye direction triangle.A triangle.B triangle.C with
                | ValueSome hit when hit > 0.05f && hit < distance - 0.05f -> Some hit
                | _ -> None)
        Assert.True(obstruction.IsNone, $"mid double-door sightline is blocked at {obstruction}")

    let private assertNativeCounterStrikeMap expectedName expectedTriangles expectedTextures expectedColumns allies axis (level: Level) =
        Assert.Equal(expectedName, level.Name)
        Assert.Equal(expectedTriangles, level.Indices.Length / 3)
        Assert.Contains(level.Vertices, fun vertex -> Materials.isImported vertex.MaterialId && vertex.TexCoord <> Vector2.Zero)
        match level.TextureAtlas with
        | Some(RgbaAtlas(pixels, width, height, columns, rows, tileSize)) ->
            Assert.Equal(expectedColumns, columns)
            Assert.Equal(128, tileSize)
            Assert.Equal(columns * tileSize, width)
            Assert.Equal(rows * tileSize, height)
            Assert.Equal(width * height * 4, pixels.Length)
            let layers = (expectedTextures + columns - 1) / columns
            Assert.Equal(layers, rows)
        | atlas -> Assert.Fail($"expected a decoded BSP texture atlas, got {atlas}")
        for team, count in [ Allies, allies; Axis, axis ] do
            let spawns = level.Spawns |> Array.filter (fun struct (owner, _) -> owner = Some team)
            Assert.Equal(count, spawns.Length)
            Assert.All(spawns, fun struct (_, position) ->
                Assert.NotEmpty(LevelCompile.surfaceLayers level.Collision position.X position.Z))
        let nearest (target: Vector3) =
            level.Nav
            |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(node.Position, target))
            |> Array.minBy snd
            |> fst
        let axisSpawn = level.Spawns |> Array.pick (fun struct (owner, position) -> if owner = Some Axis then Some position else None)
        let alliesSpawn = level.Spawns |> Array.pick (fun struct (owner, position) -> if owner = Some Allies then Some position else None)
        let target = nearest alliesSpawn
        let seen = System.Collections.Generic.HashSet<int>()
        let queue = System.Collections.Generic.Queue<int>()
        queue.Enqueue(nearest axisSpawn)
        while queue.Count > 0 do
            let current = queue.Dequeue()
            if seen.Add current then
                for next in level.Nav[current].Neighbours do queue.Enqueue next
        Assert.True(seen.Contains target, "the opposing Counter-Strike spawn is unreachable")

    [<Fact>]
    let ``pool day loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "Pool Day" 4754 31 6 16 16 Levels.poolDay

    [<Fact>]
    let ``office loads geometry textures entities and routes directly from BSP30`` () =
        let level = Levels.office
        assertNativeCounterStrikeMap "Office" 20806 214 15 10 10 level
        Assert.Equal(10, level.Breakables.Length)
        Assert.All(level.Breakables, fun item ->
            Assert.NotEmpty item.Triangles
            Assert.All(item.Triangles, fun triangle -> Assert.Equal(Glass, triangle.Material)))

    [<Fact>]
    let ``shooting glass removes its rendering collision and sight obstruction`` () =
        let glassMesh =
            MeshGen.box (Vector3(2.0f, 2.0f, 0.12f)) Glass
            |> MeshGen.translate (Vector3(0.0f, 1.0f, -2.0f))
        let glassBounds =
            { Min = Vector3(-1.0f, 0.0f, -2.06f)
              Max = Vector3(1.0f, 2.0f, -1.94f) }
        let level =
            LevelDsl.level "Glass range"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.breakableWorld 42 glassMesh glassBounds
                  LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 2.0f))
                  LevelDsl.spawnSquad Axis 1 (Vector3(5.0f, 0.0f, -5.0f))
                  LevelDsl.objective "Break the glass" ]
            |> LevelCompile.compile
        let beforeTriangles = level.Collision.Triangles.Length
        let beforeVertices = level.Vertices.Length
        Assert.False(Ballistics.lineOfSight (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(0.0f, 1.0f, -4.0f)) level)
        let world = Sim.createWorld level "Break the glass" 41UL
        let fire = TestKit.input 1L InputButtons.Fire Vector2.Zero
        let struct (after, events) = Sim.step fire world
        Assert.Contains(events, function GlassBroken(42, _) -> true | _ -> false)
        Assert.Contains(42, after.Level.BrokenBreakables)
        Assert.Empty after.Level.Breakables
        Assert.Equal(level.Revision + 1, after.Level.Revision)
        Assert.Equal(beforeTriangles - 12, after.Level.Collision.Triangles.Length)
        Assert.Equal(beforeVertices - 36, after.Level.Vertices.Length)
        Assert.True(Ballistics.lineOfSight (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(0.0f, 1.0f, -4.0f)) after.Level)

    [<Fact>]
    let ``aim map loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "Aim Map" 2307 18 5 16 16 Levels.aimMap

    [<Fact>]
    let ``awp india loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "AWP India" 2930 18 5 16 16 Levels.awpIndia

    [<Fact>]
    let ``rats 2 loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "Rats 2" 9967 134 12 10 10 Levels.rats2

    [<Fact>]
    let ``iceworld loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "Iceworld" 553 5 3 12 12 Levels.iceworld

    [<Fact>]
    let ``snow loads geometry textures entities and routes directly from BSP30`` () =
        assertNativeCounterStrikeMap "Snow" 2526 9 3 16 16 Levels.snow

    [<Fact>]
    let ``head capsule applies lethal multiplier`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 0.0f 1.5f Rifle openLevel targets
        Assert.Equal(Units.health 0.0f, updated[0].Health)
        Assert.Contains(HitConfirmed(EntityId 9, true), events)
        Assert.Contains(events, function BloodImpact(_, _, true) -> true | _ -> false)
        Assert.Contains(events, function HeadGib _ -> true | _ -> false)
        Assert.Matches("DyingHeadshot.*", string updated[0].Behavior)

    [<Fact>]
    let ``pistol damage falls off over distance while rifle damage carries`` () =
        Assert.Equal(1.0f, Tuning.damageFalloff Pistol 5.0f)
        Assert.Equal(0.5f, Tuning.damageFalloff Pistol 60.0f)
        Assert.Equal(1.0f, Tuning.damageFalloff Rifle 150.0f)
        let shoot distance =
            let updated, _ =
                Ballistics.applyShot (Vector3(0.0f, 1.0f, distance)) -Vector3.UnitZ Tuning.luger.Damage 0.0f Tuning.luger.HeadshotMultiplier Pistol openLevel [| soldier Vector3.Zero |]
            updated[0].Health
        Assert.True(shoot 28.0f > shoot 5.0f)

    [<Fact>]
    let ``kar98k headshot is a one shot kill while a thompson headshot is not`` () =
        let targets = [| soldier Vector3.Zero |]
        let kar98kUpdated, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ Tuning.kar98k.Damage 0.0f Tuning.kar98k.HeadshotMultiplier Rifle openLevel targets
        Assert.Equal(Units.health 0.0f, kar98kUpdated[0].Health)
        let thompsonUpdated, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ Tuning.thompson.Damage 0.0f Tuning.thompson.HeadshotMultiplier Smg openLevel [| soldier Vector3.Zero |]
        Assert.True(thompsonUpdated[0].IsAlive)

    [<Fact>]
    let ``kar98k penetrates thin wood with reduced damage`` () =
        let wall =
            { Bounds = { Min = Vector3(-1.0f, 0.0f, 2.0f); Max = Vector3(1.0f, 2.0f, 2.1f) }
              Material = Wood }
        let level = LevelCompile.rebuild (Array.append openLevel.Brushes [| wall |]) openLevel
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle level [| soldier Vector3.Zero |]
        Assert.InRange(Units.raw updated[0].Health, 38.0f, 40.0f)
        Assert.Contains(events, function Impact(_, _, Wood) -> true | _ -> false)
        Assert.Contains(events, function HitConfirmed(EntityId 9, false) -> true | _ -> false)
        Assert.Contains(events, function BloodImpact(_, _, false) -> true | _ -> false)
        Assert.DoesNotContain(events, function HeadGib _ -> true | _ -> false)

    [<Fact>]
    let ``brick stops a rifle round before the target`` () =
        let wall =
            { Bounds = { Min = Vector3(-1.0f, 0.0f, 2.0f); Max = Vector3(1.0f, 2.0f, 2.3f) }
              Material = Brick }
        let level = LevelCompile.rebuild (Array.append openLevel.Brushes [| wall |]) openLevel
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle level [| soldier Vector3.Zero |]
        Assert.Equal(Units.health 100.0f, updated[0].Health)
        Assert.DoesNotContain(events, function HitConfirmed _ -> true | _ -> false)

    [<Fact>]
    let ``center screen shots land on the torso not the head`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.4f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle openLevel targets
        Assert.InRange(Units.raw updated[0].Health, 14.0f, 16.0f)
        Assert.Contains(events, function HitConfirmed(EntityId 9, false) -> true | _ -> false)
        Assert.Contains(events, function BloodImpact(_, _, false) -> true | _ -> false)
        Assert.DoesNotContain(events, function HeadGib _ -> true | _ -> false)

    [<Fact>]
    let ``legs receive reduced damage`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, _ =
            Ballistics.applyShot (Vector3(0.0f, 0.5f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle openLevel targets
        Assert.InRange(Units.raw updated[0].Health, 44.0f, 45.5f)

    [<Fact>]
    let ``crouched target is missed high and hit low`` () =
        let crouched = { soldier Vector3.Zero with Stance = Crouched }
        let over, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.7f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle openLevel [| crouched |]
        Assert.Equal(Units.health 100.0f, over[0].Health)
        let low, _ =
            Ballistics.applyShot (Vector3(0.0f, 0.9f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle openLevel [| crouched |]
        Assert.True(low[0].Health < Units.health 100.0f)

    [<Fact>]
    let ``rifle overpenetrates the first body into a second`` () =
        let targets = [| soldierAt 1 Vector3.Zero; soldierAt 2 (Vector3(0.0f, 0.0f, -1.5f)) |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f Rifle openLevel targets
        Assert.True(updated[0].Health < Units.health 100.0f)
        Assert.True(updated[1].Health < Units.health 100.0f)
        Assert.Contains(events, function HitConfirmed(EntityId 2, false) -> true | _ -> false)

    [<Fact>]
    let ``eye trace clears cover the camera can see over`` () =
        // Chest-high sandbags between shooter and target: the eye (1.62) sees
        // over the 1.5 lip, so the shot must connect. The old muzzle-origin
        // trace started ~1.4 high and buried the round in the bags.
        let wall =
            { Bounds = { Min = Vector3(-3.0f, 0.0f, 2.5f); Max = Vector3(3.0f, 1.5f, 3.0f) }
              Material = Sandbag }
        let level = LevelCompile.rebuild (Array.append openLevel.Brushes [| wall |]) openLevel
        let world = Sim.createTrainingWorld 21UL
        let player =
            { world.Player with
                Position = Vector3(0.0f, 0.0f, 5.0f)
                Yaw = 0.0f
                Pitch = -0.015f
                Ads = 1.0f }
        let world = { world with Level = level; Player = player; Soldiers = [| soldierAt 44 (Vector3(0.0f, 0.0f, -3.0f)) |] }
        let struct (_, events) = Sim.step (TestKit.input 1L InputButtons.Fire Vector2.Zero) world
        Assert.Contains(events, function HitConfirmed(EntityId 44, _) -> true | _ -> false)

    [<Fact>]
    let ``the offline sandbox carries every weapon the picker offers`` () =
        // The loadout menu equips by matching a name against the carried slots,
        // so a weapon missing here is one the picker silently refuses to select.
        let world = Sim.createTrainingWorld 3UL
        let carried = world.Player.Slots |> Array.map (fun slot -> slot.Class.Name) |> Set.ofArray
        for weapon in Tuning.onlineWeapons do
            Assert.True(carried.Contains weapon.Name, $"{weapon.Name} is in the picker but not carried offline")

    [<Fact>]
    let ``standing muzzle origin sits at torso height`` () =
        let world = Sim.createTrainingWorld 3UL
        let standing = { world.Player with Stance = Standing; Ads = 0.0f }
        let origin = Ballistics.playerMuzzleOrigin standing standing.Slots[standing.Active].Class
        Assert.InRange(origin.Y - standing.Position.Y, 1.30f, 1.55f)

    [<Fact>]
    let ``grenade cooking release and radial damage are deterministic`` () =
        let world = Sim.createTrainingWorld 7UL
        let cooking, noneThrown = Grenades.stepHand Tuning.TickDuration true world.Player
        Assert.True(noneThrown.IsNone)
        let released, thrown = Grenades.stepHand Tuning.TickDuration false cooking
        Assert.Equal(GrenadeIdle 2, released.Grenade)
        let grenade = thrown.Value
        let closeTarget = soldier (grenade.Position + Vector3(0.0f, -1.0f, -0.5f))
        let updated, events = Grenades.applyExplosions openLevel [| grenade.Position |] [| closeTarget |]
        Assert.True(updated[0].Health < Units.health 100.0f)
        Assert.Contains(events, function Explosion(_, radius) when radius = Grenades.BlastRadius -> true | _ -> false)

    [<Fact>]
    let ``the previewed grenade path matches where the simulation actually throws it`` () =
        // The aiming preview is only useful if it is the same ballistics the sim
        // runs. Aim into a wall so the comparison covers a bounce, not just a
        // free-flight parabola.
        let level =
            LevelDsl.level "Bounce" [ LevelDsl.street 30.0f 10.0f Mud; LevelDsl.block (Vector3(0.0f, 1.5f, -8.0f)) (Vector3(8.0f, 3.0f, 0.6f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 7UL
        let player = { world.Player with Position = Vector3(0.0f, 0.0f, 0.0f); Yaw = 0.0f; Pitch = 0.1f; Velocity = Vector3(1.0f, 0.0f, -2.0f) }
        let steps = 120
        let predicted = Grenades.predictPath level steps player

        let _, thrown = Grenades.stepHand Tuning.TickDuration false { player with Grenade = Cooking(Units.seconds 4.0f, 3) }
        let simulated =
            let positions = ResizeArray<Vector3>()
            let mutable active = [| thrown.Value |]
            for _ in 1..steps do
                let next, _ = Grenades.stepProjectilesOwned Tuning.TickDuration level active
                active <- next
                if next.Length > 0 then positions.Add next[0].Position
            positions

        Assert.NotEmpty predicted
        // The preview stops once the grenade settles, so it is a prefix of the run.
        Assert.True(predicted.Length <= simulated.Count)
        for index in 0 .. predicted.Length - 1 do
            Assert.Equal(simulated[index], predicted[index])
        // A pure parabola only ever travels further along -Z; the path must turn
        // back on itself somewhere, which only a bounce can do.
        let reverses = Seq.pairwise predicted |> Seq.exists (fun (before, after) -> after.Z > before.Z + 0.01f)
        Assert.True(reverses, "the predicted path never bounced back off the wall")

    [<Fact>]
    let ``every weapon in the arsenal is one the server can resolve`` () =
        // The projectile weapons used to be offline-only because the match host
        // could not simulate one. It can now, so the whole arsenal is online —
        // and every mechanism in it needs an authoritative path, or picking that
        // weapon in a match is picking a gun that does nothing.
        let online = Tuning.onlineWeapons |> Array.map _.Name |> Set.ofArray
        for weapon in Tuning.specialWeapons do
            Assert.True(online.Contains weapon.Name, $"{weapon.Name} is not selectable online")
        Assert.Equal(Bow, Tuning.bow.Mechanism)
        Assert.Equal(Katana, Tuning.katana.Mechanism)
        // Keys that mean something: a bow is a precision weapon and a blade is
        // what you swap to. The rest of the toys sit with the heavies.
        Assert.All(
            Tuning.specialWeapons
            |> Array.filter (fun weapon -> weapon.Mechanism <> Bow && weapon.Mechanism <> Katana),
            fun weapon -> Assert.Equal(4, Tuning.categoryOf weapon))
        Assert.Equal(3, Tuning.categoryOf Tuning.bow)
        Assert.Equal(2, Tuning.categoryOf Tuning.katana)

    [<Fact>]
    let ``a launched projectile is zeroed on the crosshair, not the muzzle`` () =
        // Projectiles leave the muzzle, which sits below the eye the reticle
        // belongs to. Fired parallel they landed that offset low at every range,
        // and the arrow's own drop was added on top of it.
        let level = TestKit.streetArenaSized "Range" 200.0f 40.0f
        let world = Sim.createTrainingWorld 41UL
        let player = { world.Player with Position = Vector3.Zero; Yaw = 0.0f; Pitch = 0.0f; Ads = 1.0f }
        let eye = Ballistics.playerEyeOrigin player
        let aim = Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero
        let muzzle = Ballistics.playerMuzzleOrigin player Tuning.bow
        Assert.True(muzzle.Y < eye.Y, "this test is pointless if the muzzle is not below the eye")
        let mutable rng = Rng.create 7UL
        let mutable flying =
            SpecialProjectiles.launch (EntityId 1) PaintRed Tuning.bow Tuning.bow.Damage eye aim muzzle &rng
        let mutable worstMiss = 0.0f
        // Out to the convergence range the shot must land on the reticle, not
        // under it. A hand's width over twenty-five metres is the budget.
        let mutable travelled = 0.0f
        while flying.Length > 0 && travelled < SpecialProjectiles.ConvergeDistance do
            let active, _, _, _, _, _ =
                SpecialProjectiles.stepWith (fun _ _ -> false) Tuning.TickDuration level player [||] flying [||] Map.empty
            flying <- active
            if flying.Length > 0 then
                travelled <- -flying[0].Position.Z
                worstMiss <- max worstMiss (abs (flying[0].Position.Y - eye.Y))
        Assert.InRange(worstMiss, 0.0f, 0.12f)

    [<Fact>]
    let ``bow draw power rises holds and fatigues back to minimum`` () =
        let power seconds = Tuning.drawPower (Units.seconds seconds)
        Assert.Equal(Tuning.MinimumDrawPower, power 0.0f)
        Assert.InRange(power 0.5f, 0.674f, 0.676f)
        Assert.Equal(1.0f, power 1.0f)
        Assert.Equal(1.0f, power 1.6f)
        Assert.InRange(power 2.2f, 0.674f, 0.676f)
        Assert.InRange(power 2.8f, Tuning.MinimumDrawPower - 0.0001f, Tuning.MinimumDrawPower + 0.0001f)
        Assert.InRange(power 20.0f, Tuning.MinimumDrawPower - 0.0001f, Tuning.MinimumDrawPower + 0.0001f)

    [<Fact>]
    let ``bow holds without firing then releases one charge-scaled shot`` () =
        let mutable rng = Rng.create 2201UL
        let mutable slot = Tuning.weaponSlot Tuning.bow 2
        let before = slot.InMag
        for _ in 1..Tuning.TickRate do
            let struct (next, shots) = Weapons.step Tuning.TickDuration 0.0f Standing true false 1.0f &rng slot
            Assert.Empty shots
            slot <- next
        match slot.State with
        | Drawing charge -> Assert.InRange(Units.raw charge, 0.99f, 1.01f)
        | other -> failwith $"bow should be drawing, was {other}"
        let struct (released, shots) = Weapons.step Tuning.TickDuration 0.0f Standing false false 1.0f &rng slot
        Assert.Single shots |> ignore
        Assert.InRange(Units.raw shots[0].Damage, 119.9f, 120.1f)
        Assert.Equal(before - 1, released.InMag)
        Assert.True(match released.State with Cooling _ -> true | _ -> false)

    [<Fact>]
    let ``bow reload wins over an in-progress draw`` () =
        let mutable rng = Rng.create 2202UL
        let slot =
            { Tuning.weaponSlot Tuning.bow 2 with
                State = Drawing(Units.seconds 0.7f)
                InMag = 5 }
        let struct (reloading, shots) = Weapons.step Tuning.TickDuration 0.0f Standing true true 1.0f &rng slot
        Assert.Empty shots
        Assert.True(match reloading.State with Reloading _ -> true | _ -> false)

    [<Fact>]
    let ``offline bow release spawns a physical arrow carrying draw damage`` () =
        let original = Sim.createTrainingWorld 2203UL
        let mutable world =
            { original with
                Level = openLevel
                Player = { original.Player with Position = Vector3(0.0f, 0.0f, 5.0f); Active = TestKit.slotOf original.Player "Bow" }
                Soldiers = [||] }
        for sequence in 1L..int64 Tuning.TickRate do
            let struct (next, _) = Sim.step (TestKit.input sequence InputButtons.Fire Vector2.Zero) world
            world <- next
        let struct (released, events) = Sim.step (TestKit.input 100L InputButtons.None Vector2.Zero) world
        Assert.Single released.SpecialProjectiles |> ignore
        match released.SpecialProjectiles[0].Kind with
        | ArrowRound damage -> Assert.InRange(Units.raw damage, 119.9f, 120.1f)
        | other -> failwith $"expected arrow, got {other}"
        Assert.InRange(released.SpecialProjectiles[0].Velocity.Length(), 86.0f, 89.0f)
        Assert.Contains(events, function ShotFired(_, _, _, "Bow") -> true | _ -> false)

    [<Fact>]
    let ``arrows stick head-on and ricochet at shallow incidence`` () =
        let level =
            LevelDsl.level "Arrow wall"
                [ LevelDsl.street 40.0f 20.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(30.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 2204UL
        let arrow position velocity =
            { Owner = world.Player.Id
              Kind = ArrowRound(Units.health 72.0f)
              Position = position
              Velocity = velocity
              DistanceTravelled = 0.0f
              Bounces = 0
              Remaining = Units.seconds 6.0f }
        let direct = arrow (Vector3(0.0f, 1.0f, 1.0f)) (-Vector3.UnitZ * SpecialProjectiles.ArrowSpeed)
        let active, marks, _, _, events = runSpecial 1 level world.Player [||] direct
        Assert.Empty active
        Assert.Contains(marks, function StuckArrow(_, direction, None, _) when direction.Z < -0.9f -> true | _ -> false)
        Assert.Contains(events, function ArrowImpact(_, _, true) -> true | _ -> false)

        let shallow = arrow (Vector3(-1.0f, 1.0f, 0.28f)) (Vector3(70.0f, 0.0f, -12.0f))
        let ricocheted, shallowMarks, _, _, _ = runSpecial 1 level world.Player [||] shallow
        Assert.Single ricocheted |> ignore
        Assert.Equal(1, ricocheted[0].Bounces)
        Assert.True(ricocheted[0].Velocity.Z > 0.0f)
        Assert.Empty shallowMarks

    [<Fact>]
    let ``glancing arrows embed in enemies and emit blood instead of bouncing`` () =
        let world = Sim.createTrainingWorld 2205UL
        let victim = TestKit.soldier 41 Vector3.Zero
        // Skim the outer edge of the torso capsule. This incidence is shallow
        // enough to ricochet from a wall, but flesh must always catch the arrow.
        let arrow =
            { Owner = world.Player.Id
              Kind = ArrowRound(Units.health 72.0f)
              Position = Vector3(-1.0f, 1.10f, 0.25f)
              Velocity = Vector3.UnitX * SpecialProjectiles.ArrowSpeed
              DistanceTravelled = 0.0f
              Bounces = 0
              Remaining = Units.seconds 6.0f }
        let active, marks, _, soldiers, events = runSpecial 1 openLevel world.Player [| victim |] arrow
        Assert.Empty active
        Assert.InRange(Units.raw soldiers[0].Health, 27.9f, 28.1f)
        Assert.Contains(
            marks,
            function
            | StuckArrow(_, direction, Some target, _) -> target = victim.Id && direction.X > 0.9f
            | _ -> false)
        Assert.Contains(events, function ArrowImpact(_, _, true) -> true | _ -> false)
        Assert.Contains(events, function BloodImpact(_, direction, false) when direction.X > 0.9f -> true | _ -> false)

    [<Fact>]
    let ``speargun bands contract after firing and compact nailgun stays tool-sized`` () =
        let loaded = Guns.harpoonGunForLoad 1.0f
        let fired = Guns.harpoonGunForLoad 0.0f
        Assert.True(loaded.Vertices.Length > fired.Vertices.Length, "loaded spear geometry should disappear on firing")
        Assert.True((loaded.Vertices |> Array.minBy (fun vertex -> vertex.Position.Z)).Position.Z < -2.2f)
        Assert.True((fired.Vertices |> Array.minBy (fun vertex -> vertex.Position.Z)).Position.Z > -1.8f)
        let nail = Guns.meshFor "Nailgun"
        let lo = nail.Vertices |> Array.map _.Position |> Array.reduce (fun left right -> Vector3.Min(left, right))
        let hi = nail.Vertices |> Array.map _.Position |> Array.reduce (fun left right -> Vector3.Max(left, right))
        let bounds = hi - lo
        Assert.True(bounds.Z < 1.0f && bounds.Y < 0.8f, $"nailgun bounds were {bounds}")

    [<Fact>]
    let ``laser pointer is literal keychain geometry rather than a pistol`` () =
        let mesh = Guns.meshFor "Laser Pointer"
        let positions = mesh.Vertices |> Array.map _.Position
        let bounds =
            (positions |> Array.reduce (fun left right -> Vector3.Max(left, right)))
            - (positions |> Array.reduce (fun left right -> Vector3.Min(left, right)))
        let materials = mesh.Vertices |> Array.map _.MaterialId |> Set.ofArray
        Assert.True(bounds.X < 0.11f && bounds.Z < 0.70f, $"pointer body/keychain bounds were {bounds}")
        Assert.True(bounds.Y > 0.32f, "the dangling keyring should define the silhouette")
        Assert.Contains(Materials.id Metal, materials)
        Assert.Contains(Materials.id Plaster, materials)
        Assert.Contains(Materials.id PaintRed, materials)
        Assert.Contains(Materials.id ToolBlack, materials)

    [<Fact>]
    let ``laser is perfectly accurate flat damage and stops at the first hit`` () =
        Assert.Equal(Laser, Tuning.laserPointer.Mechanism)
        Assert.Equal(Units.health 20.0f, Tuning.laserPointer.Damage)
        Assert.Equal(0.0f, Tuning.laserPointer.HipSpread)
        Assert.Equal(0.0f, Tuning.laserPointer.AdsSpread)
        Assert.Equal(0.0f, Tuning.laserPointer.Penetration)
        Assert.All(Tuning.laserPointer.Recoil, fun recoil -> Assert.Equal(Vector2.Zero, recoil))

        let origin = Vector3(0.0f, 1.1f, 5.0f)
        let targets = [| soldierAt 91 Vector3.Zero; soldierAt 92 (Vector3(0.0f, 0.0f, -4.0f)) |]
        let updated, endpoint, events =
            Ballistics.applyLaserFiltered (fun _ -> true) origin -Vector3.UnitZ Tuning.laserPointer.Damage openLevel targets
        Assert.Equal(Units.health 80.0f, updated[0].Health)
        Assert.Equal(Units.health 100.0f, updated[1].Health)
        Assert.Contains(HitConfirmed(EntityId 91, false), events)
        Assert.DoesNotContain(events, function HitConfirmed(EntityId 92, _) -> true | _ -> false)
        Assert.True(endpoint.Z > -1.0f, "beam continued through its first target")

        // It is a 10 km gameplay ray, well beyond any map, and does not inherit
        // the pistol damage falloff even after kilometres of travel.
        let farTarget = soldierAt 93 Vector3.Zero
        let farUpdated, _, _ =
            Ballistics.applyLaserFiltered (fun _ -> true) (Vector3(0.0f, 1.1f, 5000.0f)) -Vector3.UnitZ Tuning.laserPointer.Damage openLevel [| farTarget |]
        Assert.Equal(Units.health 80.0f, farUpdated[0].Health)
        let _, clearEndpoint, _ =
            Ballistics.applyLaserFiltered (fun _ -> true) origin -Vector3.UnitZ Tuning.laserPointer.Damage openLevel [||]
        Assert.InRange(Vector3.Distance(origin, clearEndpoint), Ballistics.LaserRange - 0.01f, Ballistics.LaserRange + 0.01f)

        let blockedLevel =
            LevelDsl.level "Blocked laser"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 2.0f)) (Vector3(2.0f, 2.0f, 0.20f)) Metal ]
            |> LevelCompile.compile
        let blocked, wallEndpoint, _ =
            Ballistics.applyLaserFiltered (fun _ -> true) origin -Vector3.UnitZ Tuning.laserPointer.Damage blockedLevel [| soldierAt 94 Vector3.Zero |]
        Assert.Equal(Units.health 100.0f, blocked[0].Health)
        Assert.InRange(wallEndpoint.Z, 1.89f, 2.11f)
