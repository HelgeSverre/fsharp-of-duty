namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CombatTests =
    let private soldier = TestKit.soldier 9

    let private soldierAt = TestKit.soldier

    let private openLevel = TestKit.streetArenaSized "Range" 30.0f 10.0f

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
        // And the vertical play has to be navigable, not just standable: the
        // catwalks and the derrick pad only matter if bots can get up there.
        let high = level.Nav |> Array.filter (fun node -> node.Position.Y > 3.0f)
        Assert.True(high.Length >= 4, $"only {high.Length} nav nodes above the yard floor")
        Assert.True(high |> Array.exists (fun node -> node.Neighbours.Length > 0), "high ground is not linked to anything")

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
