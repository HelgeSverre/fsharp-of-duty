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
