namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CombatTests =
    let private soldier = TestKit.soldier 9

    let private soldierAt = TestKit.soldier

    let private openLevel = TestKit.streetArenaSized "Range" 30.0f 10.0f

    let private runSpecial ticks level player soldiers projectile =
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

    let private runSpecialWithStatus ticks level player soldiers projectiles statuses =
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
    let ``physical toybox weapons stay offline while bow is deliberately online`` () =
        let online = Tuning.onlineWeapons |> Array.map _.Name |> Set.ofArray
        Assert.DoesNotContain(Tuning.paintballMarker.Name, online)
        Assert.DoesNotContain(Tuning.nerfBlaster.Name, online)
        Assert.DoesNotContain(Tuning.bazooka.Name, online)
        Assert.DoesNotContain(Tuning.flamethrower.Name, online)
        Assert.DoesNotContain(Tuning.superSoaker.Name, online)
        Assert.DoesNotContain(Tuning.nailgun.Name, online)
        Assert.DoesNotContain(Tuning.harpoonGun.Name, online)
        Assert.Contains(Tuning.bow.Name, online)
        Assert.All(Tuning.onlineWeapons |> Array.filter (fun weapon -> weapon.Name <> Tuning.bow.Name), fun weapon -> Assert.Equal(Hitscan, weapon.Mechanism))
        Assert.Equal(Bow, Tuning.bow.Mechanism)
        Assert.All(Tuning.specialWeapons, fun weapon -> Assert.Equal(4, Tuning.categoryOf weapon))

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
                Player = { original.Player with Position = Vector3(0.0f, 0.0f, 5.0f); Active = 19 }
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
    let ``bow limbs flex inward and its single sight pin is centred for ADS`` () =
        let rest = Guns.bowForDraw 0.0f
        let drawn = Guns.bowForDraw 1.0f
        let verticalSpan mesh =
            let ys = mesh.Vertices |> Array.map (fun vertex -> vertex.Position.Y)
            Array.max ys - Array.min ys
        let pin =
            rest.Vertices
            |> Array.filter (fun vertex -> vertex.MaterialId = Materials.id PaintGreen)
            |> Array.map _.Position
        let pinLo = pin |> Array.reduce (fun left right -> Vector3.Min(left, right))
        let pinHi = pin |> Array.reduce (fun left right -> Vector3.Max(left, right))
        let pinCentre = (pinLo + pinHi) * 0.5f
        Assert.True(verticalSpan drawn < verticalSpan rest - 0.10f, "draw should pull both limb tips inward")
        Assert.InRange(pinCentre.X, -0.1801f, -0.1799f)
        Assert.InRange(pinCentre.Y, 0.2099f, 0.2101f)

    [<Fact>]
    let ``offline trigger pulls spawn each special projectile and consume its ammo`` () =
        let original = Sim.createTrainingWorld 76UL
        for slot, expected in [ 12, PaintBall original.PaintColor; 13, NerfDart; 14, BazookaRocket ] do
            let world =
                { original with
                    Level = openLevel
                    Player = { original.Player with Position = Vector3(0.0f, 0.0f, 5.0f); Active = slot }
                    Soldiers = [||] }
            let before = world.Player.Slots[slot].InMag
            let struct (fired, events) = Sim.step (TestKit.input 1L InputButtons.Fire Vector2.Zero) world
            Assert.Equal(before - 1, fired.Player.Slots[slot].InMag)
            Assert.Single fired.SpecialProjectiles |> ignore
            Assert.Equal(expected, fired.SpecialProjectiles[0].Kind)
            Assert.Contains(events, function ShotFired(Some id, _, _, name) when id = world.Player.Id && name = world.Player.Slots[slot].Class.Name -> true | _ -> false)

    [<Fact>]
    let ``continuous specials and nailgun use their intended resolution path`` () =
        let original = Sim.createTrainingWorld 83UL
        let fire slot =
            let world =
                { original with
                    Level = openLevel
                    Player = { original.Player with Position = Vector3(0.0f, 0.0f, 5.0f); Active = slot }
                    Soldiers = [||] }
            let before = world.Player.Slots[slot].InMag
            let struct (fired, events) = Sim.step (TestKit.input 1L InputButtons.Fire Vector2.Zero) world
            Assert.Equal(before - 1, fired.Player.Slots[slot].InMag)
            fired, events

        let flameWorld, flameEvents = fire 15
        Assert.Empty flameWorld.SpecialProjectiles
        Assert.Contains(flameEvents, function FlameStream _ -> true | _ -> false)

        let waterWorld, _ = fire 16
        Assert.Equal(SpecialProjectiles.WaterDropletsPerPulse, waterWorld.SpecialProjectiles.Length)
        Assert.All(waterWorld.SpecialProjectiles, fun projectile -> Assert.Equal(WaterDroplet, projectile.Kind))

        let nailWorld, _ = fire 17
        Assert.Single nailWorld.SpecialProjectiles |> ignore
        Assert.Equal(NailRound, nailWorld.SpecialProjectiles[0].Kind)

        let harpoonWorld, _ = fire 18
        Assert.Single harpoonWorld.SpecialProjectiles |> ignore
        Assert.Equal(HarpoonRound [], harpoonWorld.SpecialProjectiles[0].Kind)

    [<Fact>]
    let ``flame stream deals contact damage then ignites and burns a dry target`` () =
        let world = Sim.createTrainingWorld 84UL
        let target = soldier Vector3.Zero
        let mutable player = world.Player
        let mutable soldiers = [| target |]
        let mutable statuses = Map.empty
        let events = ResizeArray<GameEvent>()
        for _ in 1..4 do
            let nextPlayer, nextSoldiers, nextStatuses, emitted =
                SpecialProjectiles.applyFlameJet world.Player.Id (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ openLevel player soldiers statuses
            player <- nextPlayer
            soldiers <- nextSoldiers
            statuses <- nextStatuses
            events.AddRange emitted
        Assert.Equal(Units.health 88.0f, soldiers[0].Health)
        Assert.True(statuses[target.Id].BurningFor > Units.seconds 0.0f)
        Assert.Contains(events, function Ignited(id, _) when id = target.Id -> true | _ -> false)
        Assert.Contains(events, function FlameImpact(position, normal) when position.Z > -0.3f && normal.Z > 0.9f -> true | _ -> false)

        let _, burned, _, burnEvents = SpecialProjectiles.stepElemental (Units.seconds 1.0f) player soldiers statuses
        Assert.Equal(Units.health 80.0f, burned[0].Health)
        Assert.Contains(burnEvents, function Burning(id, _) when id = target.Id -> true | _ -> false)

        let _, lowHit, _, _ =
            SpecialProjectiles.applyFlameJet world.Player.Id (Vector3(0.0f, 0.35f, 5.0f)) -Vector3.UnitZ openLevel world.Player [| target |] Map.empty
        Assert.Equal(Units.health 97.0f, lowHit[0].Health)

        let wall =
            LevelDsl.level "Flame wall"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(20.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let _, _, _, wallEvents =
            SpecialProjectiles.applyFlameJet world.Player.Id (Vector3(0.0f, 1.0f, 1.0f)) -Vector3.UnitZ wall world.Player [||] Map.empty
        Assert.Contains(wallEvents, function FlameImpact(_, normal) when normal.Z > 0.9f -> true | _ -> false)

    [<Fact>]
    let ``water packets deal low aggregate damage merge wetness and extinguish fire`` () =
        let world = Sim.createTrainingWorld 85UL
        let target = soldier Vector3.Zero
        let burning =
            Map.ofList
                [ target.Id,
                  { WetFor = Units.seconds 0.0f
                    BurningFor = Units.seconds 3.0f
                    Heat = Units.seconds 0.5f
                    BurnOwner = Some world.Player.Id } ]
        let droplet =
            { Owner = world.Player.Id
              Kind = WaterDroplet
              Position = Vector3(0.0f, 1.0f, 0.5f)
              Velocity = -Vector3.UnitZ * SpecialProjectiles.WaterSpeed
              DistanceTravelled = 0.0f
              Bounces = 0
              Remaining = Units.seconds 2.0f }
        let _, marks, _, soaked, statuses, events =
            runSpecialWithStatus 1 openLevel world.Player [| target |] (Array.replicate SpecialProjectiles.WaterDropletsPerPulse droplet) burning
        Assert.InRange(Units.raw soaked[0].Health, 99.24f, 99.26f)
        Assert.True(statuses[target.Id].WetFor > Units.seconds 14.0f)
        Assert.Equal(Units.seconds 0.0f, statuses[target.Id].BurningFor)
        Assert.Single(marks |> Array.filter (function WetPatch _ -> true | _ -> false)) |> ignore
        Assert.Contains(events, function Extinguished(id, _) when id = target.Id -> true | _ -> false)

    [<Fact>]
    let ``water burst is deterministic and one tank sustains thirty seconds`` () =
        let mutable firstRng = Rng.create 901UL
        let mutable secondRng = Rng.create 901UL
        let first = SpecialProjectiles.spawnWaterBurst (EntityId 1) Vector3.Zero -Vector3.UnitZ &firstRng
        let second = SpecialProjectiles.spawnWaterBurst (EntityId 1) Vector3.Zero -Vector3.UnitZ &secondRng
        Assert.Equal<SpecialProjectile array>(first, second)
        Assert.Equal(30.0f, float32 Tuning.superSoaker.MagSize / (Tuning.superSoaker.RoundsPerMin / 60.0f))
        Assert.Equal(Tuning.superSoaker.MagSize, (Tuning.weaponSlot Tuning.superSoaker 1).Reserve)

    [<Fact>]
    let ``nails deal sixty-six damage and stick or ricochet by incidence`` () =
        let level =
            LevelDsl.level "Nail wall"
                [ LevelDsl.street 40.0f 20.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(30.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 86UL
        let target = soldier Vector3.Zero
        let nail position velocity =
            { Owner = world.Player.Id
              Kind = NailRound
              Position = position
              Velocity = velocity
              DistanceTravelled = 0.0f
              Bounces = 0
              Remaining = Units.seconds 4.0f }
        let direct = nail (Vector3(0.0f, 1.0f, 1.0f)) (-Vector3.UnitZ * SpecialProjectiles.NailSpeed)
        let _, marks, _, wounded, _ = runSpecial 1 openLevel world.Player [| target |] direct
        Assert.Equal(Units.health 34.0f, wounded[0].Health)
        Assert.Contains(marks, function StuckNail(_, _, Some id, _) when id = target.Id -> true | _ -> false)

        let wallHit = nail (Vector3(0.0f, 1.0f, 1.0f)) (-Vector3.UnitZ * SpecialProjectiles.NailSpeed)
        let active, wallMarks, _, _, _ = runSpecial 1 level world.Player [||] wallHit
        Assert.Empty active
        Assert.Contains(wallMarks, function StuckNail(_, _, None, _) -> true | _ -> false)

        let shallow = nail (Vector3(-1.0f, 1.0f, 0.28f)) (Vector3(70.0f, 0.0f, -12.0f))
        let ricocheted, shallowMarks, _, _, _ = runSpecial 1 level world.Player [||] shallow
        Assert.Single ricocheted |> ignore
        Assert.Equal(1, ricocheted[0].Bounces)
        Assert.True(ricocheted[0].Velocity.Z > 0.0f)
        Assert.Empty shallowMarks

    [<Fact>]
    let ``harpoon skewers a line of targets carries three and embeds in the wall`` () =
        let level =
            LevelDsl.level "Harpoon gallery"
                [ LevelDsl.street 50.0f 20.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, -6.0f)) (Vector3(30.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 88UL
        // The first target sits just outside the ordinary torso radius. The
        // harpoon's broad swept tip still catches it.
        let targets =
            [| soldierAt 91 (Vector3(0.30f, 0.0f, 2.0f))
               soldierAt 92 (Vector3(-0.20f, 0.0f, 0.0f))
               soldierAt 93 (Vector3(0.18f, 0.0f, -2.0f))
               { soldierAt 94 (Vector3.Zero + Vector3(0.0f, 0.0f, -4.0f)) with Team = Allies } |]
        let projectile =
            SpecialProjectiles.spawn world.Player.Id PaintRed Harpoon (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ
            |> Option.get
        let active, marks, _, skeweredTargets, events = runSpecial 30 level world.Player targets projectile
        Assert.Empty active
        Assert.All(skeweredTargets, fun target -> Assert.Equal(Units.health 0.0f, target.Health))
        Assert.Equal(4, events |> List.filter (function HarpoonSkewer _ -> true | _ -> false) |> List.length)
        let embedded =
            marks
            |> Array.choose (function EmbeddedHarpoon(tip, direction, victims) -> Some(tip, direction, victims) | _ -> None)
        Assert.Single embedded |> ignore
        let tip, direction, victims = embedded[0]
        Assert.InRange(tip.Z, -5.9f, -5.7f)
        Assert.True(direction.Z < -0.9f)
        Assert.Equal(SpecialProjectiles.MaxSkeweredVictims, victims.Length)
        Assert.Equal<EntityId list>([ EntityId 91; EntityId 92; EntityId 93 ], victims |> List.map _.Victim)
        Assert.Contains(events, function HarpoonEmbedded(_, normal) when normal.Z > 0.9f -> true | _ -> false)

    [<Fact>]
    let ``harpoon keeps a flat long-range trajectory under reduced gravity`` () =
        let world = Sim.createTrainingWorld 89UL
        let projectile =
            SpecialProjectiles.spawn world.Player.Id PaintRed Harpoon (Vector3(0.0f, 10.0f, 5.0f)) -Vector3.UnitZ
            |> Option.get
        let active, _, _, _, _ = runSpecial 60 openLevel world.Player [||] projectile
        Assert.Single active |> ignore
        Assert.InRange(active[0].Position.Z, -100.1f, -99.9f)
        Assert.InRange(active[0].Position.Y, 8.1f, 8.4f)
        Assert.InRange(active[0].Velocity.Y, -3.5f, -3.3f)

    [<Fact>]
    let ``paintball is a flat one-shot kill and leaves the round color on its victim`` () =
        let world = Sim.createTrainingWorld 77UL
        let target = soldier Vector3.Zero
        let projectile =
            SpecialProjectiles.spawn world.Player.Id PaintPurple Paintball (Vector3(0.0f, 1.0f, 5.0f)) -Vector3.UnitZ
            |> Option.get
        let active, marks, _, updated, events = runSpecial 8 openLevel world.Player [| target |] projectile
        Assert.Empty active
        Assert.Equal(Units.health 0.0f, updated[0].Health)
        Assert.Contains(HitConfirmed(target.Id, true), events)
        Assert.Contains(events, function PaintImpact(_, _, PaintPurple) -> true | _ -> false)
        Assert.Contains(marks, function PaintSplat(_, _, PaintPurple, Some id, _) when id = target.Id -> true | _ -> false)

    [<Fact>]
    let ``paintballs splatter head-on and ricochet at a shallow angle`` () =
        let level =
            LevelDsl.level "Paint bounce"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(20.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 78UL
        let headOn =
            SpecialProjectiles.spawn world.Player.Id PaintGreen Paintball (Vector3(0.0f, 1.0f, 1.0f)) -Vector3.UnitZ
            |> Option.get
        let active, marks, _, _, events = runSpecial 1 level world.Player [||] headOn
        Assert.Empty active
        Assert.Contains(marks, function PaintSplat(_, _, PaintGreen, None, _) -> true | _ -> false)
        Assert.Contains(events, function PaintImpact(_, _, PaintGreen) -> true | _ -> false)

        let shallow =
            { headOn with
                Position = Vector3(-1.0f, 1.0f, 0.28f)
                Velocity = Vector3(70.0f, 0.0f, -12.0f) }
        let ricocheted, shallowMarks, _, _, _ = runSpecial 1 level world.Player [||] shallow
        Assert.Single ricocheted |> ignore
        Assert.Equal(1, ricocheted[0].Bounces)
        Assert.True(ricocheted[0].Velocity.Z > 0.0f)
        Assert.Empty shallowMarks

    [<Fact>]
    let ``foam darts stick head-on and ricochet at a shallow angle`` () =
        let level =
            LevelDsl.level "Dart wall"
                [ LevelDsl.street 40.0f 20.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(30.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 79UL
        let headOn =
            SpecialProjectiles.spawn world.Player.Id PaintRed FoamDart (Vector3(0.0f, 1.0f, 1.0f)) -Vector3.UnitZ
            |> Option.get
        let active, marks, _, _, events = runSpecial 1 level world.Player [||] headOn
        Assert.Empty active
        Assert.Contains(marks, function StuckDart(_, _, None, _) -> true | _ -> false)
        Assert.Contains(events, function DartImpact(_, _, true) -> true | _ -> false)

        let shallow =
            { headOn with
                Position = Vector3(-1.0f, 1.0f, 0.28f)
                Velocity = Vector3(70.0f, 0.0f, -12.0f) }
        let ricocheted, shallowMarks, _, _, _ = runSpecial 1 level world.Player [||] shallow
        Assert.Single ricocheted |> ignore
        Assert.Equal(1, ricocheted[0].Bounces)
        Assert.True(ricocheted[0].Velocity.Z > 0.0f)
        Assert.Empty shallowMarks

    [<Fact>]
    let ``bazooka duds before five metres and explodes after arming`` () =
        let level =
            LevelDsl.level "Rocket wall"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(20.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let world = Sim.createTrainingWorld 80UL
        let rocket =
            SpecialProjectiles.spawn world.Player.Id PaintRed Rocket (Vector3(0.0f, 1.0f, 1.0f)) -Vector3.UnitZ
            |> Option.get
        let _, _, _, _, dudEvents = runSpecial 1 level world.Player [||] rocket
        Assert.Contains(dudEvents, function RocketDud _ -> true | _ -> false)
        Assert.DoesNotContain(dudEvents, function Explosion _ -> true | _ -> false)

        let armed = { rocket with DistanceTravelled = SpecialProjectiles.RocketArmingDistance }
        let _, _, _, _, armedEvents = runSpecial 1 level world.Player [||] armed
        Assert.Contains(armedEvents, function Explosion(_, radius) when radius = SpecialProjectiles.RocketBlastRadius -> true | _ -> false)

    [<Fact>]
    let ``bazooka backblast hurts characters and a blocked shooter behind the tube`` () =
        let world = Sim.createTrainingWorld 82UL
        let player = { world.Player with Position = Vector3.Zero }
        let behind = soldierAt 88 (Vector3(0.0f, 0.0f, 2.0f))
        let _, scorched, events =
            SpecialProjectiles.applyBackblast (Vector3(0.0f, 1.35f, -1.2f)) -Vector3.UnitZ openLevel player [| behind |]
        Assert.True(scorched[0].Health < behind.Health)
        Assert.Contains(events, function Backblast(_, direction) when direction.Z > 0.9f -> true | _ -> false)

        let blockedLevel =
            LevelDsl.level "Blocked backblast"
                [ LevelDsl.street 30.0f 10.0f Mud
                  LevelDsl.block (Vector3(0.0f, 1.0f, 1.0f)) (Vector3(8.0f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let burned, _, hurtEvents =
            SpecialProjectiles.applyBackblast (Vector3(0.0f, 1.35f, -1.2f)) -Vector3.UnitZ blockedLevel player [||]
        Assert.Equal(Units.health 0.0f, burned.Health)
        Assert.Contains(hurtEvents, function PlayerHurt(_, health) when health = Units.health 0.0f -> true | _ -> false)

    [<Fact>]
    let ``round reset clears special projectiles and persistent marks`` () =
        let world = Sim.createPaintballWorld 81UL
        let projectile =
            SpecialProjectiles.spawn world.Player.Id world.PaintColor Paintball (world.Player.Position + Vector3.UnitY) -Vector3.UnitZ
            |> Option.get
        let dirty =
            { world with
                SpecialProjectiles = [| projectile |]
                PersistentMarks = [| PaintSplat(Vector3.Zero, Vector3.UnitY, world.PaintColor, None, Vector3.Zero) |]
                ElementalStatus =
                    Map.ofList
                        [ world.Player.Id,
                          { WetFor = Units.seconds 5.0f
                            BurningFor = Units.seconds 0.0f
                            Heat = Units.seconds 0.0f
                            BurnOwner = None } ]
                Round = Some { world.Round.Value with ResetIn = Some(Units.seconds 0.0f) } }
        let struct (reset, _) = Sim.step (TestKit.input 1L InputButtons.None Vector2.Zero) dirty
        Assert.Empty reset.SpecialProjectiles
        Assert.Empty reset.PersistentMarks
        Assert.Empty reset.ElementalStatus
        Assert.Equal(2, reset.Round.Value.Number)
        Assert.Contains(reset.PaintColor, SpecialProjectiles.paintPalette)
        Assert.NotEqual(world.PaintColor, reset.PaintColor)
