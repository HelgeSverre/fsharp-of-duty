namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CombatTests =
    let private soldier position =
        { Id = EntityId 9
          Team = Axis
          Position = position
          Facing = 0.0f
          Stance = Standing
          Health = Units.health 100.0f
          Behavior = Idle
          Weapon = Tuning.weaponSlot Tuning.kar98k 2
          Squad = 1
          Contacts = Map.empty
          Suppression = 0.0f
          AnimPhase = 0.0f }

    let private soldierAt id position = { soldier position with Id = EntityId id }

    let private openLevel =
        LevelDsl.level "Range" [ LevelDsl.street 30.0f 10.0f Mud ] |> LevelCompile.compile

    [<Fact>]
    let ``level DSL compiles one source into collision render navigation cover and spawns`` () =
        let level = Levels.stalingradStreet
        Assert.NotEmpty level.Brushes
        Assert.NotEmpty level.Vertices
        Assert.NotEmpty level.Indices
        Assert.NotEmpty level.Nav
        Assert.NotEmpty level.Cover
        Assert.Equal(10, level.Spawns.Length)
        Assert.Equal(0, level.Vertices.Length % 24)
        Assert.Equal(0, level.Indices.Length % 36)

    [<Fact>]
    let ``compiled brush triangles wind toward their outward normals`` () =
        let level = LevelDsl.level "Winding" [ LevelDsl.street 10.0f 5.0f Mud ] |> LevelCompile.compile
        for triangle in 0..level.Indices.Length / 3 - 1 do
            let a = level.Vertices[int level.Indices[triangle * 3]]
            let b = level.Vertices[int level.Indices[triangle * 3 + 1]]
            let c = level.Vertices[int level.Indices[triangle * 3 + 2]]
            let geometricNormal = Vector3.Cross(b.Position - a.Position, c.Position - a.Position)
            Assert.True(Vector3.Dot(geometricNormal, a.Normal) > 0.0f, $"Triangle {triangle} has reversed winding")

    [<Fact>]
    let ``head capsule applies lethal multiplier`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 0.0f 1.5f openLevel targets
        Assert.Equal(Units.health 0.0f, updated[0].Health)
        Assert.Contains(HitConfirmed(EntityId 9, true), events)
        Assert.Contains(events, function BloodImpact(_, _, true) -> true | _ -> false)
        Assert.Contains(events, function HeadGib _ -> true | _ -> false)
        Assert.Matches("DyingHeadshot.*", string updated[0].Behavior)

    [<Fact>]
    let ``kar98k headshot is a one shot kill while a thompson headshot is not`` () =
        let targets = [| soldier Vector3.Zero |]
        let kar98kUpdated, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ Tuning.kar98k.Damage 0.0f Tuning.kar98k.HeadshotMultiplier openLevel targets
        Assert.Equal(Units.health 0.0f, kar98kUpdated[0].Health)
        let thompsonUpdated, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.6f, 5.0f)) -Vector3.UnitZ Tuning.thompson.Damage 0.0f Tuning.thompson.HeadshotMultiplier openLevel [| soldier Vector3.Zero |]
        Assert.True(thompsonUpdated[0].Health > Units.health 0.0f)

    [<Fact>]
    let ``kar98k penetrates thin wood with reduced damage`` () =
        let wall =
            { Bounds = { Min = Vector3(-1.0f, 0.0f, 2.0f); Max = Vector3(1.0f, 2.0f, 2.1f) }
              Material = Wood }
        let level = LevelCompile.rebuild (Array.append openLevel.Brushes [| wall |]) openLevel
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f level [| soldier Vector3.Zero |]
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
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f level [| soldier Vector3.Zero |]
        Assert.Equal(Units.health 100.0f, updated[0].Health)
        Assert.DoesNotContain(events, function HitConfirmed _ -> true | _ -> false)

    [<Fact>]
    let ``center screen shots land on the torso not the head`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.4f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f openLevel targets
        Assert.InRange(Units.raw updated[0].Health, 14.0f, 16.0f)
        Assert.Contains(events, function HitConfirmed(EntityId 9, false) -> true | _ -> false)
        Assert.Contains(events, function BloodImpact(_, _, false) -> true | _ -> false)
        Assert.DoesNotContain(events, function HeadGib _ -> true | _ -> false)

    [<Fact>]
    let ``legs receive reduced damage`` () =
        let targets = [| soldier Vector3.Zero |]
        let updated, _ =
            Ballistics.applyShot (Vector3(0.0f, 0.5f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f openLevel targets
        Assert.InRange(Units.raw updated[0].Health, 44.0f, 45.5f)

    [<Fact>]
    let ``crouched target is missed high and hit low`` () =
        let crouched = { soldier Vector3.Zero with Stance = Crouched }
        let over, _ =
            Ballistics.applyShot (Vector3(0.0f, 1.7f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f openLevel [| crouched |]
        Assert.Equal(Units.health 100.0f, over[0].Health)
        let low, _ =
            Ballistics.applyShot (Vector3(0.0f, 0.9f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f openLevel [| crouched |]
        Assert.True(low[0].Health < Units.health 100.0f)

    [<Fact>]
    let ``rifle overpenetrates the first body into a second`` () =
        let targets = [| soldierAt 1 Vector3.Zero; soldierAt 2 (Vector3(0.0f, 0.0f, -1.5f)) |]
        let updated, events =
            Ballistics.applyShot (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ (Units.health 85.0f) 18.0f 1.5f openLevel targets
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
        let struct (_, events) = Sim.step { Sequence = 1L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.Fire } world
        Assert.Contains(events, function HitConfirmed(EntityId 44, _) -> true | _ -> false)

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
        Assert.Contains(events, function Explosion(_, radius) when radius = 6.0f -> true | _ -> false)
