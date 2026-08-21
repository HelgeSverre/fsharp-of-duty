namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Ironsight.Shell
open Xunit

[<Collection("Process-global environment")>]
module ClientOnlineTests =
    [<Fact>]
    let ``row slot geometry round-trips between drawing and hit-testing`` () =
        let rows = { X = 100.0f; Y = 200.0f; W = 600.0f; H = 54.0f * 5.0f }
        for index in 0..4 do
            let slot = Rect.slot 54.0f index rows
            // A point in the middle of a drawn slot hits that same slot.
            let middle = Vector2(slot.X + slot.W * 0.5f, slot.Y + slot.H * 0.5f)
            Assert.Equal(Some index, Rect.slotAt 54.0f 5 middle rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(99.0f, 227.0f)) rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(400.0f, 199.0f)) rows)
        Assert.Equal(None, Rect.slotAt 54.0f 5 (Vector2(400.0f, 200.0f + 54.0f * 5.0f)) rows)
        // textY centers any scale on the slot midline (glyphs are 12px at scale 1).
        let slot = Rect.slot 54.0f 2 rows
        for scale in [ 1.0f; 1.15f; 1.25f; 1.3f ] do
            let y = Rect.textY scale slot
            Assert.Equal(slot.Y + slot.H * 0.5f, y + 6.0f * scale, 3)

    [<Fact>]
    let ``long menu pages scroll a window that keeps the selection in view`` () =
        let idle = TestKit.idleMenuInput
        let server index = { Name = $"S{index}"; Url = Uri $"ws://server-{index}.example:8080/play" }
        let rows =
            Array.init 14 (fun index ->
                { Server = server index; RoomId = $"r{index}"; RoomName = $"Room {index}"; Mode = TeamDeathmatch; Phase = "Waiting"; Players = 0; Capacity = 16; PingMs = 10; Online = true })
        let listed = { StartMenu.initial with Page = ServerList; ServerRows = Some rows }
        // 14 rows + BACK = 15 items; the window shows MaxVisibleRows of them.
        Assert.Equal(15, (StartMenu.items listed).Length)
        Assert.Equal((0, StartMenu.MaxVisibleRows), StartMenu.visibleRange listed)
        // Walk the selection to the bottom: the window follows.
        let mutable state = listed
        for _ in 1..14 do
            let struct (next, _) = StartMenu.update 1280 720 { idle with Down = true } state
            state <- next
        Assert.Equal(14, state.Selected)
        let first, visible = StartMenu.visibleRange state
        Assert.Equal(15 - StartMenu.MaxVisibleRows, first)
        Assert.Equal(StartMenu.MaxVisibleRows, visible)
        Assert.True(state.Selected >= first && state.Selected < first + visible)
        // Wrapping back to the top snaps the window back with it.
        let struct (wrapped, _) = StartMenu.update 1280 720 { idle with Down = true } state
        Assert.Equal(0, wrapped.Selected)
        Assert.Equal((0, StartMenu.MaxVisibleRows), StartMenu.visibleRange wrapped)
        // Short pages never scroll.
        Assert.Equal((0, 6), StartMenu.visibleRange StartMenu.initial)

    [<Fact>]
    let ``lethal headshot pose removes head geometry`` () =
        let world = Sim.createPaintballWorld 73UL
        let soldier = world.Soldiers[0]
        let normal = Humanoid.pose soldier
        let headless = Humanoid.pose { soldier with Behavior = DyingHeadshot(Units.seconds 0.1f) }
        Assert.True(headless.Vertices.Length < normal.Vertices.Length)
        Assert.True(headless.Indices.Length < normal.Indices.Length)

    [<Fact>]
    let ``soldier model faces its movement direction at every yaw`` () =
        let soldierAt yaw = { TestKit.soldier 1 Vector3.Zero with Facing = yaw }
        for yaw in [ 0.0f; MathF.PI / 2.0f; MathF.PI; -MathF.PI / 2.0f ] do
            let posed = Humanoid.pose (soldierAt yaw)
            let forward = MathEx.yawForward yaw
            let projection = posed.Vertices |> Array.map (fun vertex -> Vector3.Dot(vertex.Position, forward))
            // The weapon barrel reaches ~1.26 units along the facing axis; the
            // mesh must extend much further forward than backward.
            let front = Array.max projection
            let back = -Array.min projection
            Assert.True(front > 0.9f, $"yaw {yaw}: front extent {front}")
            Assert.True(front > back, $"yaw {yaw}: front {front} not ahead of back {back}")

    [<Fact>]
    let ``noise and mesh combinators are deterministic asset sources`` () =
        let point = Vector2(1.25f, -4.75f)
        Assert.Equal(Noise.fbm2 42 5 point, Noise.fbm2 42 5 point)
        let mesh =
            MeshGen.union
                [| MeshGen.wedge (Vector3(1.0f, 0.5f, 2.0f)) Brick
                   MeshGen.lathe 10 [| Vector2(0.2f, -0.5f); Vector2(0.4f, 0.0f); Vector2(0.1f, 0.5f) |] Metal |]
            |> MeshGen.paint Wood
        Assert.NotEmpty mesh.Vertices
        Assert.NotEmpty mesh.Indices
        Assert.All(mesh.Vertices, fun vertex -> Assert.Equal(Materials.id Wood, vertex.MaterialId))

    [<Fact>]
    let ``procedural weapon primitives use outward counterclockwise winding`` () =
        // The lathe sample is the real helmet profile: it shipped upside down
        // and holed precisely because nothing asserted its winding or closure.
        let meshes =
            [| MeshGen.box (Vector3.One) Wood
               MeshGen.cylinder 10 0.2f 1.0f Metal
               MeshGen.lathe 12
                   [| Vector2(0.19f, -0.035f); Vector2(0.19f, 0.01f); Vector2(0.17f, 0.08f)
                      Vector2(0.11f, 0.14f); Vector2(0.02f, 0.17f) |]
                   Metal |]
        for mesh in meshes do
            for a, b, c in TestKit.triangles mesh.Vertices mesh.Indices do
                let geometric = Vector3.Cross(b.Position - a.Position, c.Position - a.Position)
                Assert.True(Vector3.Dot(geometric, a.Normal) > 0.0f)

    [<Fact>]
    let ``a standing soldier wears a closed helmet crown, dome up`` () =
        // Regression for the see-through helmet: the crown must be the highest
        // surface, upward-facing (outward-wound, not culled), and closed. This
        // fails independently on the upside-down rotation, the missing lathe
        // caps, and any winding flip.
        let soldier = { TestKit.soldier 1 Vector3.Zero with Facing = 0.7f; AnimPhase = 0.3f }
        let vertices, indices = Humanoid.mesh [| soldier |]
        let highest = vertices |> Array.maxBy (fun vertex -> vertex.Position.Y)
        // The top of the model is helmet, not bare head poking through a hole.
        Assert.Equal(Materials.id UniformFeldgrau, highest.MaterialId)
        Assert.InRange(highest.Position.Y, 1.9f, 2.05f)
        // Looking straight down at the crown must hit an upward-facing surface
        // near the top — a hole or an inward-wound dome both fail this.
        let origin = Vector3(0.0f, 3.0f, -0.025f)
        let topHit =
            [ for a, b, c in TestKit.triangles vertices indices do
                match MathEx.rayTriangle origin -Vector3.UnitY a.Position b.Position c.Position with
                | ValueSome distance when Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Y > 0.0f -> yield 3.0f - distance
                | _ -> () ]
            |> function [] -> 0.0f | hits -> List.max hits
        Assert.True(topHit > 1.9f, $"highest upward-facing surface over the head is at {topHit}, expected a closed crown above 1.9")

    [<Fact>]
    let ``online reconciliation drops acknowledged inputs and replays newer movement`` () =
        let world = Sim.createTrainingWorld 900UL
        let snapshot =
            { Tick = 120L
              Mode = TeamDeathmatch
              LevelName = "Training Yard"
              BrokenBreakables = Set.empty
              Phase = Playing
              AlliesScore = 2
              AxisScore = 1
              Players =
                [| { TestKit.onlinePlayer 7 "Local" Allies (Vector3(-10.0f, 0.0f, 10.0f)) with
                        Health = 75.0f
                        Ammo = 20
                        WeaponName = "Kar98k Sniper"
                        Kills = 2
                        Deaths = 1
                        AcknowledgedInput = 10L }
                   { TestKit.onlinePlayer 8 "Remote" Axis (Vector3(4.0f, 0.0f, -4.0f)) with
                        Yaw = 3.14f
                        Ammo = 5
                        Reserve = 20
                        WeaponName = "M1897 Trench Gun"
                        Kills = 1
                        Deaths = 2
                        AcknowledgedInput = 9L } |]
              Grenades = [||]
              Projectiles = [||]
              Events = [||] }
        let pending =
            [ { Sequence = 10L; Move = Vector2.UnitY; Look = Vector2.Zero; Buttons = InputButtons.None }
              { Sequence = 11L; Move = Vector2.UnitY; Look = Vector2.Zero; Buttons = InputButtons.None } ]
        let reconciled, remaining = OnlineWorld.reconcile world.Level pending 7 world snapshot
        Assert.Single remaining |> ignore
        Assert.Equal(7, let (EntityId id) = reconciled.Player.Id in id)
        Assert.Equal(Units.health 75.0f, reconciled.Player.Health)
        Assert.True(reconciled.Player.Position.Z < 10.0f)
        Assert.Single reconciled.Soldiers |> ignore
        Assert.Equal(EntityId 8, reconciled.Soldiers[0].Id)

    [<Fact>]
    let ``a reloading snapshot rebuilds the weapon slot as Reloading`` () =
        // weaponFor is the only place an online WeaponSlot is built; before the
        // wire carried the timer it hardcoded Ready, so Hud.drawReloadBar's
        // Reloading case could never match online.
        let world = Sim.createTrainingWorld 900UL
        let snapshot =
            { Tick = 100L
              Mode = TeamDeathmatch
              LevelName = world.Level.Name
              BrokenBreakables = Set.empty
              Phase = Playing
              AlliesScore = 0
              AxisScore = 0
              Players =
                [| { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with ReloadRemaining = 1.5f }
                   { TestKit.onlinePlayer 2 "Remote" Axis (Vector3(4.0f, 0.0f, -4.0f)) with ReloadRemaining = 0.0f } |]
              Grenades = [||]
              Projectiles = [||]
              Events = [||] }
        let reconciled, _ = OnlineWorld.reconcile world.Level [] 1 world snapshot
        Assert.Equal(Reloading(Units.seconds 1.5f), reconciled.Player.Slots[reconciled.Player.Active].State)
        Assert.Equal(Ready, reconciled.Soldiers[0].Weapon.State)

    [<Fact>]
    let ``bow charge snapshots rebuild Drawing for local and remote players`` () =
        let world = Sim.createTrainingWorld 904UL
        let bowKit =
            [| { WeaponName = "Bow"; Ammo = 11; Reserve = 24; ReloadRemaining = 0.0f; Heat = 0.0f; LastMelee = None }
               { WeaponName = "M1911"; Ammo = 7; Reserve = 21; ReloadRemaining = 0.0f; Heat = 0.0f; LastMelee = None } |]
        let drawing id name team position charge =
            { TestKit.onlinePlayer id name team position with
                Slots = bowKit
                DrawCharge = charge }
        let snapshot =
            { Tick = 100L; Mode = TeamDeathmatch; LevelName = world.Level.Name; BrokenBreakables = Set.empty; Phase = Playing
              AlliesScore = 0; AxisScore = 0
              Players =
                [| drawing 1 "Local" Allies world.Player.Position 0.8f
                   drawing 2 "Remote" Axis (world.Player.Position - Vector3.UnitZ * 4.0f) 1.2f |]
              Grenades = [||]
              Projectiles = [||]; Events = [||] }
        let reconciled, _ = OnlineWorld.reconcile world.Level [] 1 world snapshot
        Assert.Equal(Drawing(Units.seconds 0.8f), reconciled.Player.Slots[0].State)
        Assert.Equal(Drawing(Units.seconds 1.2f), reconciled.Soldiers[0].Weapon.State)

    [<Fact>]
    let ``render interpolation smooths bow charge between simulation ticks`` () =
        let world = Sim.createTrainingWorld 905UL
        let withCharge charge =
            let bow = TestKit.slotOf world.Player "Bow"
            let slots = Array.copy world.Player.Slots
            slots[bow] <- { slots[bow] with State = Drawing(Units.seconds charge) }
            { world with Player = { world.Player with Active = bow; Slots = slots } }
        let blended = RenderInterpolation.world 0.25f (withCharge 0.2f) (withCharge 0.6f)
        match blended.Player.Slots[TestKit.slotOf world.Player "Bow"].State with
        | Drawing charge -> Assert.InRange(Units.raw charge, 0.2999f, 0.3001f)
        | other -> failwith $"interpolated bow state was {other}"

    [<Fact>]
    let ``the wire kit is rebuilt with the sidearm and the gun in hand`` () =
        // The client used to overwrite Slots with a single weapon every
        // snapshot, so no multi-slot state could survive reconciliation.
        let world = Sim.createTrainingWorld 901UL
        let kit =
            [| { WeaponName = "Kar98k"; Ammo = 3; Reserve = 15; ReloadRemaining = 0.0f; Heat = 0.0f; LastMelee = None }
               { WeaponName = "M1911"; Ammo = 7; Reserve = 14; ReloadRemaining = 0.0f; Heat = 0.0f; LastMelee = None } |]
        let withKit active switchTo switchRemaining =
            { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with
                Slots = kit
                Active = active
                SwitchTo = switchTo
                SwitchRemaining = switchRemaining }
        let snapshotOf player =
            { Tick = 100L; Mode = TeamDeathmatch; LevelName = world.Level.Name; BrokenBreakables = Set.empty; Phase = Playing
              AlliesScore = 0; AxisScore = 0; Players = [| player |]; Grenades = [||]; Projectiles = [||]; Events = [||] }
        // Pistol in hand, rifle still remembering its three rounds.
        let reconciled, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf (withKit 1 -1 0.0f))
        Assert.Equal(2, reconciled.Player.Slots.Length)
        Assert.Equal(1, reconciled.Player.Active)
        Assert.Equal("M1911", reconciled.Player.Slots[1].Class.Name)
        Assert.Equal(3, reconciled.Player.Slots[0].InMag)
        Assert.Equal(15, reconciled.Player.Slots[0].Reserve)
        // A switch in flight arrives as Switching on the outgoing slot, so the
        // viewmodel plays the raise instead of popping to the new gun.
        let switching, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf (withKit 0 1 0.2f))
        Assert.Equal(Switching(1, Units.seconds 0.2f), switching.Player.Slots[0].State)
        // A server built before kits sends no slots at all; the flat fields it
        // does send must still produce the one-weapon inventory of that era.
        let legacy =
            { TestKit.onlinePlayer 1 "Local" Allies world.Player.Position with
                Slots = [||]; WeaponName = "BAR"; Ammo = 12; Reserve = 40 }
        let old, _ = OnlineWorld.reconcile world.Level [] 1 world (snapshotOf legacy)
        Assert.Equal(1, old.Player.Slots.Length)
        Assert.Equal(0, old.Player.Active)
        Assert.Equal("BAR", old.Player.Slots[0].Class.Name)

    [<Fact>]
    let ``reconciliation from full movement state reproduces local prediction exactly`` () =
        // The QuakeWorld property: rebasing on the snapshot (position AND
        // velocity AND stance) and replaying the unacknowledged inputs must land
        // exactly where continuous local prediction landed — including through a
        // jump. Without velocity on the wire this fails by design.
        let world = Sim.createTrainingWorld 700UL
        let inputAt sequence buttons = TestKit.input sequence buttons Vector2.UnitY
        let inputs =
            [ for sequence in 1L..30L ->
                inputAt sequence (if sequence = 10L then InputButtons.Jump else InputButtons.None) ]
        // Continuous local prediction over all 30 frames.
        let final =
            inputs
            |> List.fold (fun player input -> Movement.step Tuning.TickDuration input world.Level player) world.Player
        // Server-equivalent state after frame 18 (mid-flight from the jump).
        let atAck =
            inputs
            |> List.take 18
            |> List.fold (fun player input -> Movement.step Tuning.TickDuration input world.Level player) world.Player
        let snapshot =
            { Tick = 500L
              Mode = TeamDeathmatch
              LevelName = world.Level.Name
              BrokenBreakables = Set.empty
              Phase = Playing
              AlliesScore = 0
              AxisScore = 0
              Players =
                [| { TestKit.onlinePlayer 1 "Local" Allies atAck.Position with
                        Velocity = atAck.Velocity
                        Yaw = atAck.Yaw
                        Pitch = atAck.Pitch
                        Stance = atAck.Stance
                        Ads = atAck.Ads
                        AcknowledgedInput = 18L } |]
              Grenades = [||]
              Projectiles = [||]
              Events = [||] }
        let reconciled, _ = OnlineWorld.reconcile world.Level inputs 1 world snapshot
        Assert.InRange(Vector3.Distance(reconciled.Player.Position, final.Position), 0.0f, 0.0001f)
        Assert.InRange(Vector3.Distance(reconciled.Player.Velocity, final.Velocity), 0.0f, 0.0001f)

    [<Fact>]
    let ``server directory parses tolerantly and merges by precedence`` () =
        // Junk documents and junk entries degrade to nothing, never throw.
        Assert.Empty(ServerDirectory.parse "not json at all")
        Assert.Empty(ServerDirectory.parse """{"servers": "wrong shape"}""")
        let parsed =
            ServerDirectory.parse
                """{"servers":[
                    {"name":"Good","url":"wss://one.example/play"},
                    {"name":"","url":"wss://nameless.example/play"},
                    {"name":"BadScheme","url":"https://web.example/"},
                    {"name":"Broken","url":123},
                    {"name":"Lan","url":"ws://192.168.1.10:8080/play"}]}"""
        Assert.Equal<string list>(
            [ "Good"; "Lan" ],
            parsed |> List.map (fun entry -> entry.Name))
        // First occurrence wins across lists: env/user entries shadow later ones.
        let one = { Name = "Mine"; Url = Uri "wss://one.example/play" }
        let shadowed = { Name = "Official copy"; Url = Uri "WSS://ONE.EXAMPLE/play" }
        let other = { Name = "Other"; Url = Uri "ws://two.example/play" }
        let merged = ServerDirectory.merge [ [ one ]; [ shadowed; other ] ]
        Assert.Equal<string list>([ "Mine"; "Other" ], merged |> List.map (fun entry -> entry.Name))

    [<Fact>]
    let ``Fly hostname is the online default`` () =
        let previous = System.Environment.GetEnvironmentVariable "IRONSIGHT_SERVER"
        try
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", null)
            Assert.Equal("wss://fsharp-of-duty.fly.dev/play", ServerDirectory.defaultUri().AbsoluteUri)
        finally
            System.Environment.SetEnvironmentVariable("IRONSIGHT_SERVER", previous)

    [<Fact>]
    let ``player shot origin is in front of the weapon hand position`` () =
        let world = Sim.createTrainingWorld 11UL
        let player = { world.Player with Yaw = 0.4f; Pitch = -0.1f; Ads = 0.0f }
        let weapon = player.Slots[player.Active].Class
        let origin = Ballistics.playerMuzzleOrigin player weapon
        let eye = player.Position + Vector3(0.0f, 1.62f, 0.0f)
        let forward = Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero
        Assert.True(Vector3.Dot(origin - eye, forward) > 0.5f)
        Assert.True(Vector3.Distance(origin, eye) > 0.5f)

    [<Fact>]
    let ``ragdolled corpse collapses to the ground, keeps bone lengths, and prunes on respawn`` () =
        let world = Sim.createPaintballWorld 73UL
        let soldier = { world.Soldiers[0] with Behavior = Dying(Units.seconds 0.0f) }
        let ragdolls = Ragdoll.System()
        ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, Vector3(2.0f, 0.0f, 1.0f))
        for _ in 1..240 do
            ragdolls.Step(1.0f / 60.0f, world.Level)
        let skeleton = (ragdolls.TryGet soldier.Id).Value
        // The head starts ~1.68 above the feet; after the fall the whole body
        // lies near the floor instead of standing.
        let ground = soldier.Position.Y
        Assert.True(skeleton.Head.Y < ground + 0.6f)
        Assert.True(skeleton.Chest.Y < ground + 0.6f)
        Assert.True(skeleton.Pelvis.Y > ground - 0.5f)
        // Constraints hold: the pelvis-chest bone stays near its ~0.53 rest length.
        Assert.InRange(Vector3.Distance(skeleton.Pelvis, skeleton.Chest), 0.35f, 0.7f)
        // A respawned (alive-again) soldier takes its corpse with it.
        ragdolls.Prune [| { soldier with Behavior = Idle } |]
        Assert.True((ragdolls.TryGet soldier.Id).IsNone)

    [<Fact>]
    let ``harpoon constraint pins a ragdoll chest while its limbs remain simulated`` () =
        let world = Sim.createPaintballWorld 90UL
        let soldier = { world.Soldiers[0] with Behavior = Dying(Units.seconds 0.0f) }
        let ragdolls = Ragdoll.System()
        let anchor = soldier.Position + Vector3(0.0f, 2.8f, -1.0f)
        ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, Vector3(0.0f, 0.0f, -8.0f))
        for _ in 1..60 do
            ragdolls.Step(1.0f / 60.0f, world.Level, Map.ofList [ soldier.Id, anchor ])
        let pinned = (ragdolls.TryGet soldier.Id).Value
        Assert.Equal(anchor, pinned.Chest)
        Assert.True(Vector3.Distance(pinned.Pelvis, pinned.Chest) > 0.3f)

        for _ in 1..60 do
            ragdolls.Step(1.0f / 60.0f, world.Level)
        let released = (ragdolls.TryGet soldier.Id).Value
        Assert.True(released.Chest.Y < anchor.Y - 0.5f)

    [<Fact>]
    let ``dismembered ragdoll creates two independently simulated cut anchors`` () =
        let world = Sim.createPaintballWorld 191UL
        let soldier = { world.Soldiers[0] with Behavior = Dying(Units.seconds 0.0f) }
        let localPose = Anatomy.localSkeleton soldier.Stance soldier.AnimPhase
        let localArmAxis =
            Anatomy.point JointId.LeftElbow localPose - Anatomy.point JointId.LeftShoulder localPose
            |> MathEx.normalizedOrZero
        let localPlane = MathEx.normalizedOrZero (localArmAxis + Vector3.UnitY * 0.38f)
        let cut =
            { DeathRevision = 4L
              Site = CutLeftUpperArm
              Fraction = 0.47f
              LocalPoint = Vector3.Zero
              LocalPlaneNormal = localPlane
              LocalBladeTangent = Vector3.UnitZ
              LocalSweepDirection = Vector3.UnitX
              Impulse = Vector3(-7.0f, 2.0f, 0.0f)
              CosmeticSeed = 44 }
        let ragdolls = Ragdoll.System()
        ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, Vector3.Zero, cut = cut)
        let initialDescriptor, initialProximal, initialDistal = (ragdolls.TryGetCut soldier.Id).Value
        Assert.Equal(initialProximal, initialDistal)
        let initialSkeleton = (ragdolls.TryGet soldier.Id).Value
        let cutMesh = Humanoid.poseFromSkeletonCut soldier initialSkeleton initialDescriptor initialProximal initialDistal
        let gore = cutMesh.Vertices |> Array.filter (fun vertex -> vertex.MaterialId = Materials.id PaintRed)
        Assert.NotEmpty gore
        let worldPlane =
            Vector3.TransformNormal(localPlane, Matrix4x4.CreateRotationY(-soldier.Facing))
            |> MathEx.normalizedOrZero
        Assert.All(gore, fun vertex ->
            Assert.InRange(MathF.Abs(Vector3.Dot(vertex.Position - initialProximal, worldPlane)), 0.0f, 0.0001f))
        for _ in 1..45 do ragdolls.Step(1.0f / 60.0f, world.Level)
        let descriptor, proximal, distal = (ragdolls.TryGetCut soldier.Id).Value
        Assert.Equal(cut, descriptor)
        Assert.True(Vector3.Distance(proximal, distal) > 0.12f)
        let skeleton = (ragdolls.TryGet soldier.Id).Value
        let mesh = Humanoid.poseFromSkeletonCut soldier skeleton descriptor proximal distal
        Assert.NotEmpty mesh.Vertices
        Assert.NotEmpty mesh.Indices

    [<Fact>]
    let ``every authored sever site generates finite capped corpse geometry`` () =
        let world = Sim.createPaintballWorld 192UL
        let baseline = { world.Soldiers[0] with Behavior = Dying(Units.seconds 0.0f) }
        let sites =
            [ CutNeck; CutWaist
              CutLeftUpperArm; CutLeftLowerArm; CutRightUpperArm; CutRightLowerArm
              CutLeftUpperLeg; CutLeftLowerLeg; CutRightUpperLeg; CutRightLowerLeg ]
        for index, site in sites |> List.indexed do
            let soldier = { baseline with Id = EntityId(900 + index) }
            let localPose = Anatomy.localSkeleton soldier.Stance soldier.AnimPhase
            let first, second = Anatomy.cutRelation site
            let axis = Anatomy.point second localPose - Anatomy.point first localPose |> MathEx.normalizedOrZero
            let plane = MathEx.normalizedOrZero (axis + Vector3(0.25f, 0.18f, -0.12f))
            let descriptor =
                { DeathRevision = int64 index
                  Site = site
                  Fraction = 0.43f
                  LocalPoint = Vector3.Lerp(Anatomy.point first localPose, Anatomy.point second localPose, 0.43f)
                  LocalPlaneNormal = plane
                  LocalBladeTangent = Vector3.UnitZ
                  LocalSweepDirection = Vector3.UnitX
                  Impulse = Vector3.UnitX
                  CosmeticSeed = index }
            let ragdolls = Ragdoll.System()
            ragdolls.Spawn(soldier.Id, Humanoid.worldSkeleton soldier, Vector3.Zero, cut = descriptor)
            let _, proximal, distal = (ragdolls.TryGetCut soldier.Id).Value
            let skeleton = (ragdolls.TryGet soldier.Id).Value
            let mesh = Humanoid.poseFromSkeletonCut soldier skeleton descriptor proximal distal
            let finite (value: Vector3) = Single.IsFinite value.X && Single.IsFinite value.Y && Single.IsFinite value.Z
            Assert.NotEmpty(mesh.Vertices)
            Assert.NotEmpty(mesh.Indices)
            Assert.All(mesh.Vertices, fun vertex -> Assert.True(finite vertex.Position && finite vertex.Normal, $"{site} generated a non-finite vertex"))
            Assert.Contains(mesh.Vertices, fun vertex -> vertex.MaterialId = Materials.id PaintRed)
