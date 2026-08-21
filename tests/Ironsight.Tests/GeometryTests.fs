namespace Ironsight.Tests

open System
open System.Numerics
open Ironsight
open Ironsight.ProcGen
open Xunit

/// Triangle intersection maths. These back the collision system, so the cases
/// here are hand-computed rather than golden values captured from the code.
module GeometryTests =
    // A unit triangle in the XZ plane at y = 0, facing up.
    let private a = Vector3(0.0f, 0.0f, 0.0f)
    let private b = Vector3(4.0f, 0.0f, 0.0f)
    let private c = Vector3(0.0f, 0.0f, 4.0f)

    [<Fact>]
    let ``shortest arc rotation handles aligned opposite and zero directions`` () =
        let rotate fromDirection toDirection =
            Vector3.Transform(fromDirection, MathEx.rotationBetween fromDirection toDirection)
        Assert.True(Vector3.Distance(Vector3.UnitX, rotate Vector3.UnitX Vector3.UnitX) < 0.0001f)
        Assert.True(Vector3.Distance(-Vector3.UnitY, rotate Vector3.UnitY -Vector3.UnitY) < 0.0001f)
        Assert.Equal(Vector3.UnitZ, Vector3.Transform(Vector3.UnitZ, MathEx.rotationBetween Vector3.Zero Vector3.UnitX))

    [<Fact>]
    let ``a ray straight down through a triangle hits at the drop height`` () =
        match MathEx.rayTriangle (Vector3(1.0f, 5.0f, 1.0f)) -Vector3.UnitY a b c with
        | ValueSome distance -> Assert.Equal(5.0f, distance, 4)
        | ValueNone -> failwith "expected a hit"

    [<Fact>]
    let ``a ray beside the triangle misses`` () =
        // (3, 3) is outside the hypotenuse x + z = 4.
        Assert.True((MathEx.rayTriangle (Vector3(3.0f, 5.0f, 3.0f)) -Vector3.UnitY a b c).IsNone)
        Assert.True((MathEx.rayTriangle (Vector3(-1.0f, 5.0f, 1.0f)) -Vector3.UnitY a b c).IsNone)

    [<Fact>]
    let ``a ray parallel to the triangle plane misses`` () =
        Assert.True((MathEx.rayTriangle (Vector3(1.0f, 1.0f, 1.0f)) Vector3.UnitX a b c).IsNone)

    [<Fact>]
    let ``the ray hits both faces so penetration can pair entry with exit`` () =
        // Fired from below, the same triangle must still register.
        match MathEx.rayTriangle (Vector3(1.0f, -5.0f, 1.0f)) Vector3.UnitY a b c with
        | ValueSome distance -> Assert.Equal(5.0f, distance, 4)
        | ValueNone -> failwith "expected a back-face hit"

    [<Fact>]
    let ``a ray pointing away from the triangle misses`` () =
        Assert.True((MathEx.rayTriangle (Vector3(1.0f, 5.0f, 1.0f)) Vector3.UnitY a b c).IsNone)

    [<Fact>]
    let ``closest point resolves face interior vertices and edges`` () =
        // Above the interior: straight down onto the face.
        Assert.Equal(Vector3(1.0f, 0.0f, 1.0f), MathEx.closestPointOnTriangle (Vector3(1.0f, 3.0f, 1.0f)) a b c)
        // Beyond the corner at the origin: clamps to that vertex.
        Assert.Equal(a, MathEx.closestPointOnTriangle (Vector3(-2.0f, 0.0f, -2.0f)) a b c)
        // Beside the a-b edge: clamps onto the edge, not a vertex.
        Assert.Equal(Vector3(2.0f, 0.0f, 0.0f), MathEx.closestPointOnTriangle (Vector3(2.0f, 0.0f, -3.0f)) a b c)

    [<Fact>]
    let ``a capsule resting on a surface is touching it but not inside it`` () =
        // Feet exactly on the face is the standing-still state. It must NOT
        // count as a collision, or every position on the ground is rejected.
        let resting = MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(1.0f, 0.0f, 1.0f)) a b c
        Assert.True(resting.IsNone, "standing on the floor must not read as a collision")
        // Sunk into the surface is a genuine overlap.
        let sunk = MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(1.0f, -0.2f, 1.0f)) a b c
        Assert.True(sunk.IsSome, "a capsule pushed through the face should register")

    [<Fact>]
    let ``a capsule clear of a triangle does not touch it`` () =
        // Well above, and well to the side.
        Assert.True((MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(1.0f, 4.0f, 1.0f)) a b c).IsNone)
        Assert.True((MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(9.0f, 0.0f, 9.0f)) a b c).IsNone)

    [<Fact>]
    let ``a capsule brushing a vertical triangle edge registers within its radius`` () =
        // A vertical wall triangle spanning x = 2.
        let wallA = Vector3(2.0f, 0.0f, -2.0f)
        let wallB = Vector3(2.0f, 3.0f, -2.0f)
        let wallC = Vector3(2.0f, 0.0f, 2.0f)
        let near = MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(2.0f - Tuning.PlayerRadius * 0.5f, 0.0f, 0.0f)) wallA wallB wallC
        Assert.True(near.IsSome, "inside the radius should collide")
        let clear = MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius Tuning.StandingHeight (Vector3(2.0f - Tuning.PlayerRadius * 2.0f, 0.0f, 0.0f)) wallA wallB wallC
        Assert.True(clear.IsNone, "outside the radius should not collide")

    /// The whole migration rests on this: a box and its twelve triangles must
    /// answer ray queries identically, or switching ballistics over silently
    /// changes weapon behaviour.
    [<Fact>]
    let ``a box and its twelve triangles agree on entry and exit distances`` () =
        let bounds = { Min = Vector3(-1.0f, 0.0f, -2.0f); Max = Vector3(1.5f, 3.0f, 0.5f) }
        let triangles = LevelCompile.boxTriangles bounds Brick
        Assert.Equal(12, triangles.Length)
        let rays =
            [ Vector3(0.0f, 1.0f, 8.0f), -Vector3.UnitZ           // straight on
              Vector3(-9.0f, 2.0f, -1.0f), Vector3.UnitX          // side on
              Vector3(0.2f, 9.0f, -0.7f), -Vector3.UnitY          // top down
              Vector3(-6.0f, 5.0f, 6.0f), Vector3.Normalize(Vector3(1.0f, -0.6f, -1.0f))   // diagonal
              Vector3(7.0f, 0.4f, 4.0f), Vector3.Normalize(Vector3(-1.2f, 0.2f, -1.0f)) ] // shallow
        for origin, direction in rays do
            let boxHit = MathEx.rayAabb origin direction bounds
            let triangleHits =
                triangles
                |> Array.choose (fun t -> match MathEx.rayTriangle origin direction t.A t.B t.C with ValueSome d -> Some d | ValueNone -> None)
                |> Array.sort
            match boxHit with
            | Some(struct (entry, exit, _)) ->
                Assert.True(triangleHits.Length >= 2, $"box hit but triangles did not, from {origin}")
                Assert.Equal(float entry, float (Array.min triangleHits), 3)
                Assert.Equal(float exit, float (Array.max triangleHits), 3)
            | None ->
                Assert.True(triangleHits.Length = 0, $"triangles hit but box did not, from {origin}")

    [<Fact>]
    let ``every box triangle normal points out of the box`` () =
        let bounds = { Min = Vector3(-1.0f, 0.0f, -2.0f); Max = Vector3(1.5f, 3.0f, 0.5f) }
        let centre = (bounds.Min + bounds.Max) * 0.5f
        for triangle in LevelCompile.boxTriangles bounds Wood do
            let faceCentre = (triangle.A + triangle.B + triangle.C) / 3.0f
            Assert.True(Vector3.Dot(triangle.Normal, faceCentre - centre) > 0.0f, "normal points inward")
            // Winding must agree with the stored normal, as the render mesh assumes.
            let wound = Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A)
            Assert.True(Vector3.Dot(wound, triangle.Normal) > 0.0f, "winding disagrees with normal")

    [<Fact>]
    let ``the ray walker finds triangles a coarse sampler can skip`` () =
        // A thin brush clipped by the corner of a cell the old 1.8 m sampler steps over.
        let level =
            LevelDsl.level "Walk" [ LevelDsl.street 60.0f 30.0f Mud; LevelDsl.block (Vector3(7.9f, 1.0f, 7.9f)) (Vector3(0.4f, 2.0f, 0.4f)) Brick ]
            |> LevelCompile.compile
        let origin = Vector3(-12.0f, 1.0f, -12.0f)
        let direction = Vector3.Normalize(Vector3(1.0f, 0.0f, 1.0f))
        let found =
            LevelCompile.trianglesAlongRay origin direction 40.0f level
            |> Array.filter (fun t -> t.Material = Brick)
        Assert.NotEmpty found

    /// Phase 2 gate: terrain you can walk up, and terrain you cannot.
    /// A ramp is built as a staircase of thin risers, which is what the DSL
    /// ramp primitive emits; the slope limit is what decides walkability.
    let private rampLevel riseOverRun =
        // Steps fine enough that the capsule reads them as a continuous surface.
        let steps = 40
        let run = 20.0f
        let items =
            [ yield LevelDsl.street 80.0f 30.0f Mud
              for index in 0 .. steps - 1 do
                  let depth = run / float32 steps
                  let z = -10.0f + float32 index * depth + depth * 0.5f
                  let height = (float32 index + 1.0f) * depth * riseOverRun
                  yield LevelDsl.block (Vector3(0.0f, height * 0.5f, z)) (Vector3(8.0f, height, depth)) Mud ]
        LevelDsl.level "Ramp" items |> LevelCompile.compile

    let private walkForward (level: Level) (start: Vector3) ticks =
        let world = Sim.createTrainingWorld 3UL
        let mutable player = { world.Player with Position = start; Yaw = MathF.PI; Velocity = Vector3.Zero }
        // Yaw of pi faces +Z, which is up the ramp.
        let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.None }
        for _ in 1..ticks do
            player <- Movement.step Tuning.TickDuration input level player
        player.Position

    [<Fact>]
    let ``a roofed interior is navigable, and low cover is not stood inside`` () =
        // The navmesh took only the highest surface in a column, so putting a
        // roof on a room moved every node onto the roof and left the room —
        // the entire playable space — with no navmesh at all.
        let hall roofed =
            LevelDsl.level "Hall"
                [ yield LevelDsl.street 20.0f 10.0f Concrete
                  yield LevelDsl.block (Vector3(0.0f, 1.0f, 0.0f)) (Vector3(0.3f, 2.0f, 8.0f)) Wood
                  if roofed then
                      yield LevelDsl.block (Vector3(0.0f, 7.1f, 0.0f)) (Vector3(20.0f, 0.3f, 20.0f)) Concrete ]
            |> LevelCompile.compile
        let onFloor (level: Level) = level.Nav |> Array.filter (fun node -> node.Position.Y < 1.0f)
        let indoors = hall true
        let floorNodes = onFloor indoors
        // Roofing the room must not cost it a single node of floor.
        Assert.NotEmpty floorNodes
        Assert.Equal((onFloor (hall false)).Length, floorNodes.Length)
        Assert.All(floorNodes, fun node -> Assert.NotEmpty node.Neighbours)
        // The roof is a surface too, so it gets its own layer of nodes.
        Assert.NotEmpty(indoors.Nav |> Array.filter (fun node -> node.Position.Y > 6.0f))

        // But a layer with no headroom is not a place to stand: the ground
        // under a sandbag line must not become a node inside the sandbags.
        let barricade =
            LevelDsl.level "Cover"
                [ LevelDsl.street 20.0f 8.0f Mud
                  LevelDsl.sandbags (Vector3(-4.0f, 0.0f, 0.0f)) (Vector3(4.0f, 0.0f, 0.0f)) (Some Axis) ]
            |> LevelCompile.compile
        let buried =
            barricade.Nav
            |> Array.filter (fun node ->
                node.Position.Y < 0.2f && abs node.Position.Z < 0.6f && abs node.Position.X < 4.0f)
        Assert.Empty buried

    [<Fact>]
    let ``a prop is solid: you stand on it and cannot walk through it`` () =
        // Props are how the level gets round and angled geometry out of a brush
        // set that is otherwise axis-aligned boxes. They are only worth having
        // if they collide, so this walks a player into one and onto one.
        let drum =
            MeshGen.cylinder 16 2.0f 3.0f RustedMetal |> MeshGen.rotateX (MathF.PI * 0.5f)
        let level =
            LevelDsl.level "Props"
                [ LevelDsl.street 80.0f 30.0f Sand
                  LevelDsl.prop drum (Vector3(0.0f, 1.5f, 0.0f)) 0.0f ]
            |> LevelCompile.compile
        // Dropped from above, the player comes to rest on top of the drum
        // rather than falling through it to the street.
        let landed =
            let world = Sim.createTrainingWorld 3UL
            let mutable player = { world.Player with Position = Vector3(0.0f, 6.0f, 0.0f); Velocity = Vector3.Zero }
            let still = { Sequence = 0L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.None }
            for _ in 1..120 do
                player <- Movement.step Tuning.TickDuration still level player
            player.Position
        Assert.True(landed.Y > 2.5f, $"expected to land on the drum, ended at y={landed.Y}")
        // Walking at its flank is stopped by it, well short of the far side.
        let blocked = walkForward level (Vector3(0.0f, 0.0f, -8.0f)) 180
        Assert.True(blocked.Z < -2.0f, $"expected the drum to block the walk, reached z={blocked.Z}")
        // And it is drawn, not merely collidable.
        let bare =
            LevelDsl.level "Props" [ LevelDsl.street 80.0f 30.0f Sand ] |> LevelCompile.compile
        Assert.True(level.Vertices.Length > bare.Vertices.Length, "the prop contributed no geometry")

    [<Fact>]
    let ``a player walks up a gentle slope and gains height`` () =
        // 20 degrees: tan 20 is about 0.36.
        let level = rampLevel 0.36f
        let finish = walkForward level (Vector3(0.0f, 0.0f, -12.0f)) 180
        Assert.True(finish.Z > -4.0f, $"expected to advance up the ramp, ended at z={finish.Z}")
        Assert.True(finish.Y > 2.0f, $"expected to gain height, ended at y={finish.Y}")

    [<Fact>]
    let ``a player crosses Aim Map's imported ramp`` () =
        // The ramp is only 34 degrees, but a capsule resting on it has its feet
        // above the plane. Treating that gap as airborne pinned the player near
        // the bottom even though the surface was comfortably inside the limit.
        let finish = walkForward Levels.aimMap (Vector3(-13.3f, -11.675f, 14.3f)) 180
        Assert.True(finish.Z > 20.0f, $"expected to cross the ramp, ended at z={finish.Z}")
        Assert.True(finish.Y > -8.5f, $"expected to reach the upper floor, ended at y={finish.Y}")

    [<Fact>]
    let ``a shallow approach slides along an angled wall`` () =
        let wall =
            MeshGen.box (Vector3(0.3f, 3.0f, 30.0f)) Brick
            |> MeshGen.rotateY (MathF.PI * 0.25f)
        let level =
            LevelDsl.level "Diagonal wall"
                [ LevelDsl.street 80.0f 30.0f Mud
                  LevelDsl.prop wall (Vector3(0.0f, 1.5f, 0.0f)) 0.0f ]
            |> LevelCompile.compile
        let tangent = Vector3.Normalize(Vector3(1.0f, 0.0f, 1.0f))
        let normal = Vector3.Normalize(Vector3(1.0f, 0.0f, -1.0f))
        let start = -normal * 1.2f - tangent * 8.0f
        let direction = Vector3.Normalize(tangent + normal * 0.18f)
        let yaw = MathF.Atan2(direction.X, -direction.Z)
        let world = Sim.createTrainingWorld 3UL
        let mutable player = { world.Player with Position = start; Yaw = yaw; Velocity = Vector3.Zero }
        let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.None }
        for _ in 1..120 do
            player <- Movement.step Tuning.TickDuration input level player
        let along = Vector3.Dot(player.Position - start, tangent)
        Assert.True(along > 8.0f, $"expected to keep sliding along the wall, advanced only {along}m to {player.Position}")

    [<Fact>]
    let ``existing wall contact does not pin a shallow approach`` () =
        let angle = MathF.PI / 18.0f
        let wall =
            MeshGen.box (Vector3(0.3f, 3.0f, 30.0f)) Brick
            |> MeshGen.rotateY angle
        let level =
            LevelDsl.level "Diagonal wall contact"
                [ LevelDsl.street 80.0f 30.0f Mud
                  LevelDsl.prop wall (Vector3(0.0f, 1.5f, 0.0f)) 0.0f ]
            |> LevelCompile.compile
        let normal = Vector3(MathF.Cos angle, 0.0f, -MathF.Sin angle)
        let tangent = Vector3(-MathF.Sin angle, 0.0f, -MathF.Cos angle)
        // GoldSrc BSP faces often leave the capsule microscopically inside one
        // of several coplanar triangles. That existing contact must not reject
        // a move whose only remaining component is parallel to the wall.
        let start = -normal * (0.15f + Tuning.PlayerRadius - 0.002f) - tangent * 8.0f
        let direction = Vector3.Normalize(tangent + normal * 0.18f)
        let yaw = MathF.Atan2(direction.X, -direction.Z)
        let world = Sim.createTrainingWorld 3UL
        let mutable player = { world.Player with Position = start; Yaw = yaw; Velocity = Vector3.Zero }
        let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.None }
        for _ in 1..120 do
            player <- Movement.step Tuning.TickDuration input level player
        let along = Vector3.Dot(player.Position - start, tangent)
        Assert.True(along > 8.0f, $"expected contact-safe wall sliding, advanced only {along}m to {player.Position}")

    [<Fact>]
    let ``a shallow approach slides along an imported Office wall`` () =
        // A long wall beside Office's outside spawn. This is native BSP
        // collision (including its coplanar face splits), not generated boxes.
        let start = Vector3(-18.0f, -8.390625f, -44.0f)
        let outward = Vector3.Normalize(Vector3(-0.99503726f, 0.0f, 0.099502206f))
        let tangent = Vector3.Normalize(Vector3(0.099502206f, 0.0f, 0.99503726f))
        let direction = Vector3.Normalize(tangent - outward * 0.18f)
        let yaw = MathF.Atan2(direction.X, -direction.Z)
        let world = Sim.createTrainingWorld 3UL
        let mutable player = { world.Player with Position = start; Yaw = yaw; Velocity = Vector3.Zero }
        let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.None }
        for _ in 1..60 do
            player <- Movement.step Tuning.TickDuration input Levels.office player
        let along = Vector3.Dot(player.Position - start, tangent)
        Assert.True(along > 4.5f, $"expected to slide along the Office BSP wall, advanced only {along}m to {player.Position}")

    [<Fact>]
    let ``a ladder is climbed with forward, let go with jump, and is not solid`` () =
        // A 5 m platform with a ladder up its near face, ending a little above
        // the lip so the top of the climb is level with the floor it serves.
        let items solid =
            [ yield LevelDsl.street 30.0f 10.0f Concrete
              yield LevelDsl.block (Vector3(0.0f, 2.5f, 4.0f)) (Vector3(10.0f, 5.0f, 6.0f)) Concrete
              if solid then yield LevelDsl.ladder (Vector3(0.0f, 0.0f, 0.9f)) 5.4f 0.0f ]
        let level = LevelDsl.level "Ladder" (items true) |> LevelCompile.compile
        Assert.Single level.Ladders |> ignore
        let foot = Vector3(0.0f, 0.0f, 0.6f)
        Assert.True(Movement.onLadder level foot)
        let climb buttons ticks =
            // Facing the ladder: forward is into the platform it serves.
            let world = Sim.createTrainingWorld 3UL
            let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = buttons }
            let mutable player = { world.Player with Position = foot; Velocity = Vector3.Zero; Yaw = MathF.PI }
            for _ in 1..ticks do
                player <- Movement.step Tuning.TickDuration input level player
            player.Position
        // Two seconds of forward puts you on top of the platform.
        let arrived = climb InputButtons.None 120
        Assert.True(arrived.Y > 4.9f, $"climb ended at y={arrived.Y}")
        Assert.True(arrived.Z > 1.0f, $"climb never stepped off the ladder, z={arrived.Z}")
        // Jump lets go, and holding it stops you re-attaching.
        let released = climb InputButtons.Jump 120
        Assert.True(released.Y < 1.0f, $"jump did not release the ladder, y={released.Y}")
        // Standing on one without asking to climb does not levitate you.
        let world = Sim.createTrainingWorld 3UL
        let idle = { Sequence = 0L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.None }
        let mutable parked = { world.Player with Position = foot; Velocity = Vector3.Zero }
        for _ in 1..120 do
            parked <- Movement.step Tuning.TickDuration idle level parked
        Assert.Equal(foot.Y, parked.Position.Y)
        // The rungs are drawn but not solid — a ladder you collide with is one
        // you cannot stand inside.
        let bare = LevelDsl.level "Ladder" (items false) |> LevelCompile.compile
        Assert.True(level.Vertices.Length > bare.Vertices.Length, "the ladder drew nothing")
        Assert.Equal(bare.Collision.Triangles.Length, level.Collision.Triangles.Length)
        // Bots get up it too: no rise rule would ever link a 5 m step, so the
        // ladder's two ends are stitched together explicitly.
        let nearest (level: Level) (target: Vector3) =
            level.Nav |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(node.Position, target)) |> Array.minBy snd |> fst
        let bottomNode = nearest level (Vector3(0.0f, 0.0f, 0.6f))
        let topNode = nearest level (Vector3(0.0f, 5.0f, 2.0f))
        Assert.NotEqual(bottomNode, topNode)
        Assert.Contains(topNode, level.Nav[bottomNode].Neighbours)
        Assert.Contains(bottomNode, level.Nav[topNode].Neighbours)
        let goal = level.Nav[topNode].Position
        let route = AiBrain.findPath level foot goal
        let training = Sim.createTrainingWorld 3UL
        let player =
            { training.Player with
                Position = goal
                Health = Units.health 10_000.0f }
        let bot =
            { TestKit.soldier 91 foot with
                Behavior = AdvancingTo(goal, route)
                Contacts = Map.ofList [ player.Id, struct (goal, Units.seconds 0.0f) ] }
        let _, climbed, _ = TestKit.runBrain 91UL level player [| bot |] 360
        Assert.True(
            climbed[0].Position.Y > 4.5f,
            $"bot did not climb the ladder: {climbed[0].Position}, {climbed[0].Behavior}")
        // And the same platform without a ladder stays unreachable.
        Assert.DoesNotContain(nearest bare (Vector3(0.0f, 5.0f, 2.0f)), bare.Nav[nearest bare (Vector3(0.0f, 0.0f, 0.6f))].Neighbours)

    [<Fact>]
    let ``you can jump while pressed against a wall, but not through a ceiling`` () =
        // The vertical resolve refused any upward move whenever the capsule was
        // touching geometry — and pressed against a face it always is, so a jump
        // taken within a body's width of a wall or the foot of a ramp did
        // nothing at all.
        let jumpRise (level: Level) (start: Vector3) =
            let jump = { Sequence = 1L; Move = Vector2.Zero; Look = Vector2.Zero; Buttons = InputButtons.Jump }
            let world = Sim.createTrainingWorld 5UL
            let mutable player = { world.Player with Position = start; Velocity = Vector3.Zero }
            let mutable peak = start.Y
            for _ in 1..40 do
                player <- Movement.step Tuning.TickDuration jump level player
                peak <- max peak player.Position.Y
            peak - start.Y
        let ramp =
            LevelDsl.level "Ramp foot"
                [ LevelDsl.street 40.0f 12.0f Mud
                  LevelDsl.ramp (Vector3(0.0f, 0.0f, 6.0f)) (Vector3(0.0f, 3.6f, 0.0f)) 8.0f Mud ]
            |> LevelCompile.compile
        let openGround = jumpRise ramp (Vector3(0.0f, 0.0f, -4.0f))
        Assert.True(openGround > 1.0f, $"a plain jump only rose {openGround}")
        // Every stance from touching the ramp's end face outward clears too.
        for z in [ -0.25f; -0.4f; -0.6f ] do
            let rise = jumpRise ramp (Vector3(0.0f, 0.0f, z))
            Assert.True(rise > 1.0f, $"jump at z={z}, against the ramp face, rose only {rise}")

        // A ceiling still stops one: moving up touches something new.
        let lowRoom =
            LevelDsl.level "Low room"
                [ LevelDsl.street 20.0f 8.0f Concrete
                  LevelDsl.block (Vector3(0.0f, 2.2f, 0.0f)) (Vector3(12.0f, 0.4f, 12.0f)) Concrete ]
            |> LevelCompile.compile
        let capped = jumpRise lowRoom Vector3.Zero
        Assert.InRange(capped, 0.0f, 0.35f)

    [<Fact>]
    let ``a player cannot climb a slope past the limit`` () =
        // 70 degrees is well beyond the 45 degree limit.
        let level = rampLevel 2.75f
        let finish = walkForward level (Vector3(0.0f, 0.0f, -12.0f)) 180
        Assert.True(finish.Y < 1.5f, $"expected to be stopped by the face, ended at y={finish.Y}")

    [<Fact>]
    let ``a soldier walked off a ledge ends up on the ground, not floating`` () =
        // Regression: resolveAgent applied no gravity, so AI hovered off the
        // Canal Yard bank at whatever height it left it.
        let level = Levels.canalYard
        let onBank = Vector3(18.0f, 1.2f, 0.0f)
        let offBank = Vector3(0.0f, 1.2f, 0.0f)
        let resolved = Movement.resolveAgent level onBank offBank
        Assert.True(resolved.Y < 0.5f, $"agent left the bank still at y={resolved.Y}")

    [<Fact>]
    let ``nav links a walkable ramp but not a wall of the same rise`` () =
        let ramp = rampLevel 0.36f
        let linked = ramp.Nav |> Array.filter (fun node -> node.Neighbours.Length > 0)
        Assert.True(linked.Length > ramp.Nav.Length / 2, "most of a walkable ramp should be connected")
        // Nodes partway up must exist at height, proving the probe sees stacked ground.
        let elevated = ramp.Nav |> Array.filter (fun node -> node.Position.Y > 1.0f)
        Assert.NotEmpty elevated

    /// Phase 3 gate: the Ramp primitive is a genuine slope, not a staircase.
    let private slopeLevel rise =
        LevelDsl.level "Slope"
            [ LevelDsl.street 80.0f 30.0f Mud
              LevelDsl.ramp (Vector3(0.0f, 0.0f, -10.0f)) (Vector3(0.0f, rise, 10.0f)) 16.0f Mud ]
        |> LevelCompile.compile

    [<Fact>]
    let ``a ramp is one sloped surface, walkable end to end`` () =
        // 20 m run, 7 m rise: about 19 degrees.
        let level = slopeLevel 7.0f
        let finish = walkForward level (Vector3(0.0f, 0.0f, -12.0f)) 240
        Assert.True(finish.Y > 5.5f, $"expected to reach the top of the ramp, ended at y={finish.Y}")
        Assert.True(finish.Z > 8.0f, $"expected to cross the ramp, ended at z={finish.Z}")

    [<Fact>]
    let ``a ramp past the slope limit cannot be climbed`` () =
        // 20 m run, 40 m rise: about 63 degrees.
        let level = slopeLevel 40.0f
        let finish = walkForward level (Vector3(0.0f, 0.0f, -12.0f)) 240
        Assert.True(finish.Y < 3.0f, $"expected the face to stop the player, ended at y={finish.Y}")

    [<Fact>]
    let ``a ramp carries a connected navmesh from bottom to top`` () =
        let level = slopeLevel 7.0f
        let onRamp =
            level.Nav
            |> Array.filter (fun node -> node.Position.Z > -10.0f && node.Position.Z < 10.0f && abs node.Position.X < 6.0f)
        Assert.NotEmpty onRamp
        Assert.True(onRamp |> Array.exists (fun node -> node.Position.Y > 5.0f), "no nav node near the top of the ramp")
        Assert.True(onRamp |> Array.forall (fun node -> node.Neighbours.Length > 0), "ramp nav nodes are isolated")

    [<Fact>]
    let ``a ramp is solid, so rounds cannot pass through it`` () =
        // Firing into the flank of the ramp must be blocked, which needs the
        // prism to be closed rather than a bare surface.
        let level = slopeLevel 7.0f
        let below = Vector3(0.0f, 1.0f, 9.0f)
        let across = Vector3(0.0f, 1.0f, -9.0f)
        Assert.False(Ballistics.lineOfSight below across level, "the ramp body should block sight through it")

    /// Phase 4 gate: the map has to be traversable, and the bluff has to stop
    /// you. Layout bugs are invisible in a screenshot but obvious to pathfinding.
    module Omaha =
        let private level = Levels.omahaDraw

        let private nearest (position: Vector3) =
            level.Nav |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(node.Position, position)) |> Array.minBy snd |> fst

        /// Breadth-first over the nav graph. Tests the graph itself rather than
        /// the A* on top of it, which is what a layout bug actually breaks.
        let private reachable start finish =
            let goal = nearest finish
            let seen = System.Collections.Generic.HashSet<int>()
            let queue = System.Collections.Generic.Queue<int>()
            queue.Enqueue(nearest start)
            seen.Add(nearest start) |> ignore
            let mutable found = false
            while not found && queue.Count > 0 do
                let current = queue.Dequeue()
                if current = goal then found <- true
                else
                    for neighbour in level.Nav[current].Neighbours do
                        if seen.Add neighbour then queue.Enqueue neighbour
            found

        let private standable x z =
            match LevelCompile.surfaceColumn level.Collision x z with
            | ValueSome(struct (_, normal)) -> Movement.walkableNormal normal
            | ValueNone -> false

        [<Fact>]
        let ``the map has a sea and the attackers spawn in it`` () =
            Assert.Equal(Some 0.0f, level.WaterLevel)
            let allies = level.Spawns |> Array.filter (fun struct (o, _) -> o = Some Allies)
            Assert.NotEmpty allies
            let wading = allies |> Array.filter (fun struct (_, p) -> p.Y < 0.0f)
            Assert.True(wading.Length >= allies.Length - 2, "most attacker spawns should be below the waterline")

        [<Fact>]
        let ``the defenders spawn on the crest`` () =
            for struct (owner, spawn) in level.Spawns do
                if owner = Some Axis then
                    Assert.True(spawn.Y > 4.5f, $"Axis spawn at {spawn} is not on the bluff")

        [<Fact>]
        let ``the surf reaches the crest through the draw and the gully`` () =
            let surf = Vector3(-6.0f, -0.9f, -27.0f)
            Assert.True(reachable surf (Vector3(-10.0f, 6.7f, 10.0f)), "no route up the draw")
            Assert.True(reachable surf (Vector3(25.0f, 5.4f, 10.0f)), "no route up the gully")
            Assert.True(reachable surf (Vector3(-4.0f, 5.4f, 25.0f)), "no route from the surf to the defender spawn")

        [<Fact>]
        let ``the routes up are walkable and the face between them is not`` () =
            // The draw, the scramble and the gully all climb inside the limit.
            for z in [ 0.0f; 3.0f; 6.0f ] do
                Assert.True(standable -10.0f z, $"draw not standable at z={z}")
                Assert.True(standable 25.0f z, $"gully not standable at z={z}")
            Assert.True(standable 14.0f 2.0f, "scramble not standable")
            // The bare face is a wall in its steep band.
            for x in [ -22.0f; 4.0f; 19.0f ] do
                Assert.False(standable x -2.5f, $"bluff face climbable at x={x}")

        [<Fact>]
        let ``the casemate roof shields its interior from above`` () =
            // A defender inside WN72 cannot be shot from the crest behind it.
            let inside = Vector3(-3.0f, 2.2f + 1.2f, 1.0f)
            let crestBehind = Vector3(-14.0f, 8.4f, 11.0f)
            Assert.False(Ballistics.lineOfSight crestBehind inside level, "casemate roof does not block fire from behind")

        [<Fact>]
        let ``the casemate slit sees along the beach but not out to sea`` () =
            let gunner = Vector3(-3.0f, 2.2f + 1.6f, 1.0f)
            // East along the sand: the enfilade line the 88 existed for.
            Assert.True(Ballistics.lineOfSight gunner (Vector3(18.0f, 3.0f, 1.0f)) level, "the slit cannot see along the beach")
            // Straight out to sea: blocked by the seaward wall.
            Assert.False(Ballistics.lineOfSight gunner (Vector3(-3.0f, 1.0f, -14.0f)) level, "the seaward wall is open")

    /// Oriented geometry, real trenches and heightfields — the pieces that let a
    /// level stop being axis-aligned boxes on a flat plane.
    module Terrain =
        let private compile items = LevelDsl.level "Terrain" (LevelDsl.street 80.0f 40.0f Mud :: items) |> LevelCompile.compile

        [<Fact>]
        let ``a diagonal sandbag line no longer fills its whole bounding box`` () =
            // The old lineBrush took the AABB of the endpoints, so this wall
            // became one solid 28 m square and sealed off the map.
            let level = compile [ LevelDsl.sandbags (Vector3(-14.0f, 0.0f, -14.0f)) (Vector3(14.0f, 0.0f, 14.0f)) None ]
            let heightAt x z =
                match LevelCompile.surfaceColumn level.Collision x z with
                | ValueSome(struct (height, _)) -> height
                | ValueNone -> Single.NaN
            // Off the diagonal is open ground; the old AABB filled this corner.
            Assert.True(heightAt -12.0f 12.0f < 0.2f, "sandbags are filling space away from the line they were drawn on")
            Assert.True(heightAt 12.0f -12.0f < 0.2f, "sandbags are filling the opposite corner too")
            // On the line they are still solid.
            Assert.True(heightAt 0.0f 0.0f > 1.0f, "the sandbag line is missing where it was drawn")

        [<Fact>]
        let ``a diagonal wall blocks sight across it but not beside it`` () =
            let level = compile [ LevelDsl.sandbags (Vector3(-14.0f, 0.0f, -14.0f)) (Vector3(14.0f, 0.0f, 14.0f)) None ]
            // Across the diagonal, at sandbag height.
            Assert.False(Ballistics.lineOfSight (Vector3(-6.0f, 0.5f, 0.0f)) (Vector3(6.0f, 0.5f, 0.0f)) level)
            // Parallel to it and well clear.
            Assert.True(Ballistics.lineOfSight (Vector3(-16.0f, 0.5f, 8.0f)) (Vector3(-8.0f, 0.5f, 16.0f)) level)

        [<Fact>]
        let ``a trench is cut below the ground and is walkable along its floor`` () =
            let level = compile [ LevelDsl.trench (Vector3(0.0f, 0.0f, -12.0f)) (Vector3(0.0f, 0.0f, 12.0f)) 3.0f ]
            match LevelCompile.surfaceColumn level.Collision 0.0f 0.0f with
            | ValueSome(struct (height, normal)) ->
                Assert.True(height < -0.5f, $"trench floor sits at {height}, expected it dug below ground")
                Assert.True(Movement.walkableNormal normal, "trench floor is not walkable")
            | ValueNone -> failwith "no surface inside the trench"
            // Beside the cut, the ground is back at grade — the trench is a
            // channel you can step into, not a walled corridor.
            match LevelCompile.surfaceColumn level.Collision 3.0f 0.0f with
            | ValueSome(struct (height, _)) -> Assert.True(height > -0.2f, $"ground beside the trench sits at {height}")
            | ValueNone -> failwith "no ground beside the trench"

        [<Fact>]
        let ``a trench gives cover to shelter in`` () =
            let level = compile [ LevelDsl.trench (Vector3(0.0f, 0.0f, -12.0f)) (Vector3(0.0f, 0.0f, 12.0f)) 3.0f ]
            Assert.NotEmpty(level.Cover |> Array.filter (fun point -> point.Pos.Y < 0.0f))

        [<Fact>]
        let ``a heightfield follows its height function and is solid`` () =
            // A simple ridge along X, so expected heights are known exactly.
            let ridge (x: float32) (_: float32) = 3.0f - abs x * 0.25f |> max 0.0f
            let level = compile [ LevelDsl.heightfield (Vector3(0.0f, 0.0f, 0.0f)) (Vector2(24.0f, 24.0f)) 24 ridge Mud ]
            let at x z =
                match LevelCompile.surfaceColumn level.Collision x z with
                | ValueSome(struct (height, _)) -> height
                | ValueNone -> Single.NaN
            Assert.Equal(3.0f, at 0.0f 0.0f, 1)
            Assert.Equal(2.0f, at 4.0f 0.0f, 1)
            Assert.Equal(2.0f, at -4.0f 0.0f, 1)
            // Solid: a shot through the ridge crest must be stopped.
            Assert.False(Ballistics.lineOfSight (Vector3(-15.0f, 1.0f, 0.0f)) (Vector3(15.0f, 1.0f, 0.0f)) level)

        [<Fact>]
        let ``a gentle heightfield is navigable and a sharp one is not`` () =
            let gentle = compile [ LevelDsl.heightfield Vector3.Zero (Vector2(24.0f, 24.0f)) 12 (fun x _ -> max 0.0f (3.0f - abs x * 0.25f)) Mud ]
            let elevated = gentle.Nav |> Array.filter (fun node -> node.Position.Y > 1.0f && node.Neighbours.Length > 0)
            Assert.NotEmpty elevated
            // A near-vertical spike has no standable ground on its flanks.
            let sharp = compile [ LevelDsl.heightfield Vector3.Zero (Vector2(24.0f, 24.0f)) 12 (fun x _ -> max 0.0f (12.0f - abs x * 4.0f)) Mud ]
            match LevelCompile.surfaceColumn sharp.Collision 2.0f 0.0f with
            | ValueSome(struct (_, normal)) -> Assert.False(Movement.walkableNormal normal, "a 4:1 slope should not be walkable")
            // No standable column at all on the flank is equally "not
            // navigable" — the walkable-normal check only applies when the
            // compiler did report ground there.
            | ValueNone -> ()

        [<Fact>]
        let ``rebuilding a level keeps its sloped geometry`` () =
            // OpenPath removes a brush and rebuilds; ramps must survive that.
            let level =
                compile
                    [ LevelDsl.ramp (Vector3(0.0f, 0.0f, -8.0f)) (Vector3(0.0f, 4.0f, 8.0f)) 10.0f Mud
                      LevelDsl.block (Vector3(20.0f, 1.0f, 20.0f)) (Vector3(2.0f, 2.0f, 2.0f)) Wood ]
            let before = LevelCompile.surfaceColumn level.Collision 0.0f 6.0f
            let rebuilt = LevelCompile.rebuild (Array.sub level.Brushes 0 (level.Brushes.Length - 1)) level
            let after = LevelCompile.surfaceColumn rebuilt.Collision 0.0f 6.0f
            match before, after with
            | ValueSome(struct (a, _)), ValueSome(struct (b, _)) -> Assert.Equal(float a, float b, 3)
            | _ -> failwith "the ramp did not survive the rebuild"

        [<Fact>]
        let ``wading below the waterline is slower than walking the same ground`` () =
            let dry = TestKit.streetArenaSized "Dry" 80.0f 40.0f
            let wet = LevelDsl.level "Wet" [ LevelDsl.street 80.0f 40.0f Mud; LevelDsl.water 1.0f ] |> LevelCompile.compile
            let travel level =
                let world = Sim.createTrainingWorld 3UL
                let mutable player = { world.Player with Position = Vector3(0.0f, 0.0f, 10.0f); Yaw = MathF.PI; Velocity = Vector3.Zero }
                let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.None }
                for _ in 1..90 do
                    player <- Movement.step Tuning.TickDuration input level player
                player.Position.Z - 10.0f
            let onLand = travel dry
            let inWater = travel wet
            Assert.True(inWater < onLand * 0.7f, $"wading covered {inWater} vs {onLand} on land — not slowed")

        [<Fact>]
        let ``sprint is refused while wading`` () =
            let wet = LevelDsl.level "Wet" [ LevelDsl.street 80.0f 40.0f Mud; LevelDsl.water 1.0f ] |> LevelCompile.compile
            let world = Sim.createTrainingWorld 3UL
            let mutable player = { world.Player with Position = Vector3.Zero; Yaw = MathF.PI }
            let input = { Sequence = 0L; Move = Vector2(0.0f, 1.0f); Look = Vector2.Zero; Buttons = InputButtons.Sprint }
            for _ in 1..30 do
                player <- Movement.step Tuning.TickDuration input wet player
            Assert.False(player.Sprinting, "sprinting through chest-deep water")
