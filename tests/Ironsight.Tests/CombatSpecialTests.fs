namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

module CombatSpecialTests =
    open CombatTests
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
        // By name, not by slot: the sandbox is derived from the arsenal, so its
        // indices move every time a weapon is added.
        for name, expected in
            [ "Paintball Marker", PaintBall original.PaintColor; "Nerf Blaster", NerfDart; "Bazooka", BazookaRocket ] do
            let slot = TestKit.slotOf original.Player name
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
        // By name, not by slot: see the sandbox note above.
        let fire name =
            let slot = TestKit.slotOf original.Player name
            let world =
                { original with
                    Level = openLevel
                    Player = { original.Player with Position = Vector3(0.0f, 0.0f, 5.0f); Active = slot }
                    Soldiers = [||] }
            let before = world.Player.Slots[slot].InMag
            let struct (fired, events) = Sim.step (TestKit.input 1L InputButtons.Fire Vector2.Zero) world
            Assert.Equal(before - 1, fired.Player.Slots[slot].InMag)
            fired, events

        let flameWorld, flameEvents = fire "Flamethrower"
        Assert.Empty flameWorld.SpecialProjectiles
        Assert.Contains(flameEvents, function FlameStream _ -> true | _ -> false)

        let waterWorld, _ = fire "Super Soaker"
        Assert.Equal(SpecialProjectiles.WaterDropletsPerPulse, waterWorld.SpecialProjectiles.Length)
        Assert.All(waterWorld.SpecialProjectiles, fun projectile -> Assert.Equal(WaterDroplet, projectile.Kind))

        let nailWorld, _ = fire "Nailgun"
        Assert.Single nailWorld.SpecialProjectiles |> ignore
        Assert.Equal(NailRound, nailWorld.SpecialProjectiles[0].Kind)

        let harpoonWorld, _ = fire "Harpoon Gun"
        Assert.Single harpoonWorld.SpecialProjectiles |> ignore
        Assert.Equal(HarpoonRound [], harpoonWorld.SpecialProjectiles[0].Kind)

        let laserWorld, laserEvents = fire "Laser Pointer"
        Assert.Empty laserWorld.SpecialProjectiles
        Assert.Contains(laserEvents, function LaserBeam(origin, endpoint) -> Vector3.Distance(origin, endpoint) > 1.0f | _ -> false)

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
                Dismemberments =
                    Map.ofList
                        [ EntityId 99,
                          { DeathRevision = 1L; Site = CutLeftLowerArm; Fraction = 0.5f
                            LocalPoint = Vector3.Zero; LocalPlaneNormal = Vector3.UnitX
                            LocalBladeTangent = Vector3.UnitZ; LocalSweepDirection = Vector3.UnitX
                            Impulse = Vector3.UnitX; CosmeticSeed = 1 } ]
                Round = Some { world.Round.Value with ResetIn = Some(Units.seconds 0.0f) } }
        let struct (reset, _) = Sim.step (TestKit.input 1L InputButtons.None Vector2.Zero) dirty
        Assert.Empty reset.SpecialProjectiles
        Assert.Empty reset.PersistentMarks
        Assert.Empty reset.ElementalStatus
        Assert.Empty reset.Dismemberments
        Assert.Equal(2, reset.Round.Value.Number)
        Assert.Contains(reset.PaintColor, SpecialProjectiles.paintPalette)
        Assert.NotEqual(world.PaintColor, reset.PaintColor)

    [<Fact>]
    let ``katana left to right sweep reaches far deduplicates and can cleave three targets`` () =
        let targets =
            [| { Id = EntityId 71; Position = Vector3(-0.55f, 0.0f, -0.75f); Yaw = MathF.PI; Stance = Standing; AnimPhase = 0.0f }
               { Id = EntityId 72; Position = Vector3(0.0f, 0.0f, -0.95f); Yaw = MathF.PI; Stance = Standing; AnimPhase = 0.0f }
               { Id = EntityId 73; Position = Vector3(0.55f, 0.0f, -0.75f); Yaw = MathF.PI; Stance = Standing; AnimPhase = 0.0f } |]
        let hits = Melee.resolve KatanaSweep Vector3.Zero 0.0f 0.0f (fun _ -> true) openLevel targets
        Assert.Equal(3, hits.Length)
        Assert.Equal(3, hits |> Array.map _.Victim |> Array.distinct |> Array.length)
        Assert.All(hits, fun hit -> Assert.Equal(BodyHead, hit.Part))
        Assert.True((Melee.traceEndpoint KatanaSweep Vector3.Zero 0.0f 0.0f).X > 1.8f)

        let longRange =
            { Id = EntityId 75
              Position = Vector3(0.0f, 0.0f, -1.82f)
              Yaw = MathF.PI
              Stance = Standing
              AnimPhase = 0.0f }
        Assert.Single(Melee.resolve KatanaSweep Vector3.Zero 0.0f 0.0f (fun _ -> true) openLevel [| longRange |])

    [<Fact>]
    let ``katana contact retains ordered blade motion and a real cutting plane`` () =
        let target =
            { Id = EntityId 751
              Position = Vector3(0.0f, 0.0f, -0.82f)
              Yaw = MathF.PI
              Stance = Standing
              AnimPhase = 0.7f }
        let hit = Assert.Single(Melee.resolve KatanaSweep Vector3.Zero 0.0f 0.0f (fun _ -> true) openLevel [| target |])
        Assert.True(hit.Site.IsSome)
        Assert.InRange(hit.SwingTime, 0.0f, 1.0f)
        Assert.InRange(hit.BladeTangent.Length(), 0.999f, 1.001f)
        Assert.InRange(hit.SweepDirection.Length(), 0.999f, 1.001f)
        Assert.InRange(hit.CutPlaneNormal.Length(), 0.999f, 1.001f)
        Assert.InRange(MathF.Abs(Vector3.Dot(hit.CutPlaneNormal, hit.BladeTangent)), 0.0f, 0.001f)
        Assert.InRange(MathF.Abs(Vector3.Dot(hit.CutPlaneNormal, hit.SweepDirection)), 0.0f, 0.001f)

    [<Fact>]
    let ``katana look height selects authored waist and leg sever bands`` () =
        let target =
            { Id = EntityId 752
              Position = Vector3(0.0f, 0.0f, -0.82f)
              Yaw = MathF.PI
              Stance = Standing
              AnimPhase = 0.0f }
        let hitAt pitch = Assert.Single(Melee.resolve KatanaSweep Vector3.Zero 0.0f pitch (fun _ -> true) openLevel [| target |])
        Assert.Equal(Some CutWaist, (hitAt -0.75f).Site)
        Assert.Contains(
            (hitAt -1.15f).Site,
            [ Some CutLeftUpperLeg; Some CutRightUpperLeg; Some CutLeftLowerLeg; Some CutRightLowerLeg ])

    [<Fact>]
    let ``shared anatomy pose moves leg proxies and derives detached components from the cut graph`` () =
        let still = Anatomy.localSkeleton Standing 0.0f
        let stepping = Anatomy.localSkeleton Standing (MathF.PI * 0.5f)
        let stillLeg = Anatomy.segments still |> Array.find (fun segment -> segment.Part = BodyLeftUpperLeg)
        let steppingLeg = Anatomy.segments stepping |> Array.find (fun segment -> segment.Part = BodyLeftUpperLeg)
        Assert.NotEqual(stillLeg.EndPoint.Z, steppingLeg.EndPoint.Z)
        let neckMatches = (Anatomy.detachedJoints CutNeck) = Set.ofList [ JointId.Head ]
        Assert.True(neckMatches)
        let detachedArm = Anatomy.detachedJoints CutLeftUpperArm
        let armMatches = detachedArm = Set.ofList [ JointId.LeftElbow; JointId.LeftHand ]
        Assert.True(armMatches)

    [<Fact>]
    let ``katana secondary is a top down sweep without entering ADS`` () =
        let damageFor ads expectedAttack =
            let mutable rng = Rng.create 920UL
            let slot = Tuning.weaponSlot Tuning.katana 0
            let struct (_, shots) = Weapons.step Tuning.TickDuration 0.0f Standing true false ads &rng slot
            Assert.Single shots |> ignore
            Assert.Equal(Some expectedAttack, shots[0].Melee)
            shots[0].Damage
        Assert.Equal(damageFor 0.0f KatanaSweep, damageFor 1.0f KatanaOverhead)
        Assert.Equal(Units.health 110.0f, damageFor 0.0f KatanaSweep)

        let baseWorld = Sim.createTrainingWorld 920UL
        let active = baseWorld.Player.Slots |> Array.findIndex (fun slot -> slot.Class.Mechanism = Katana)
        let victim =
            { TestKit.soldier 76 (Vector3(0.0f, 0.0f, -1.55f)) with
                Facing = MathF.PI }
        let world =
            { baseWorld with
                Level = openLevel
                Player = { baseWorld.Player with Position = Vector3.Zero; Yaw = 0.0f; Pitch = 0.0f; Ads = 0.0f; Active = active }
                Soldiers = [| victim |] }
        let struct (after, events) = Sim.step (TestKit.input 1L InputButtons.Ads Vector2.Zero) world
        Assert.Equal(0.0f, after.Player.Ads)
        Assert.Equal(Some KatanaOverhead, after.Player.Slots[active].LastMelee)
        Assert.Equal(Units.health 110.0f, Tuning.katana.Damage)
        match after.Player.Slots[active].State with
        | Cooling remaining -> Assert.InRange(Units.raw remaining, 0.399f, 0.401f)
        | state -> failwith $"katana should be recovering from its sweep, was {state}"
        Assert.True(after.Soldiers[0].IsDead)
        Assert.Contains(events, function MeleeTrace(_, _, _, KatanaOverhead) -> true | _ -> false)

    [<Fact>]
    let ``lethal primary katana sweep decapitates and stores the exact neck cut`` () =
        let baseWorld = Sim.createTrainingWorld 919UL
        let active = baseWorld.Player.Slots |> Array.findIndex (fun slot -> slot.Class.Mechanism = Katana)
        let victim =
            { TestKit.soldier 74 (Vector3(0.0f, 0.0f, -0.82f)) with
                Facing = MathF.PI
                Health = Units.health 100.0f }
        let world =
            { baseWorld with
                Level = openLevel
                Player = { baseWorld.Player with Position = Vector3.Zero; Yaw = 0.0f; Pitch = 0.0f; Active = active }
                Soldiers = [| victim |] }
        let struct (after, events) = Sim.step (TestKit.input 1L InputButtons.Fire Vector2.Zero) world
        Assert.True(after.Soldiers[0].IsDead)
        Assert.True(Map.containsKey victim.Id after.Dismemberments)
        let cut = after.Dismemberments[victim.Id]
        Assert.Equal(CutNeck, cut.Site)
        Assert.InRange(cut.Fraction, 0.12f, 0.88f)
        Assert.Contains(events, function Dismembered(id, _, descriptor) when id = victim.Id && descriptor = cut -> true | _ -> false)
