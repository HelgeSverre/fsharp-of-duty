namespace Ironsight

open System
open System.Numerics

[<Struct>]
type Aabb =
    { Min: Vector3
      Max: Vector3 }

[<RequireQualifiedAccess>]
module MathEx =
    let clamp01 (value: float32) = Math.Clamp(value, 0.0f, 1.0f)

    let horizontal (value: Vector3) = Vector3(value.X, 0.0f, value.Z)

    let normalizedOrZero (value: Vector3) =
        if value.LengthSquared() > 0.000001f then Vector3.Normalize value else Vector3.Zero

    let yawForward yaw = Vector3(MathF.Sin yaw, 0.0f, -MathF.Cos yaw)

    let yawRight yaw = Vector3(MathF.Cos yaw, 0.0f, MathF.Sin yaw)

    /// Shortest-arc angle interpolation (radians): wraps through +/-pi so a
    /// lerp from just under pi to just over -pi turns the short way, not the
    /// long way around the circle.
    let lerpAngle (from: float32) (``to``: float32) (amount: float32) =
        let delta = MathF.IEEERemainder(``to`` - from, MathF.PI * 2.0f)
        from + delta * amount

    let overlapsPoint (point: Vector3) (box: Aabb) =
        point.X >= box.Min.X && point.X <= box.Max.X
        && point.Y >= box.Min.Y && point.Y <= box.Max.Y
        && point.Z >= box.Min.Z && point.Z <= box.Max.Z

    let clampPoint (point: Vector3) (box: Aabb) = Vector3.Clamp(point, box.Min, box.Max)

    let capsuleIntersectsAabb radius height feet box =
        let center = feet + Vector3(0.0f, height * 0.5f, 0.0f)
        let halfLine = max 0.0f (height * 0.5f - radius)
        let segmentMin = center - Vector3(0.0f, halfLine, 0.0f)
        let segmentMax = center + Vector3(0.0f, halfLine, 0.0f)
        let closestY = Math.Clamp((box.Min.Y + box.Max.Y) * 0.5f, segmentMin.Y, segmentMax.Y)
        let point = Vector3(center.X, closestY, center.Z)
        let closest = clampPoint point box
        Vector3.DistanceSquared(point, closest) < radius * radius

    let raySphere (origin: Vector3) (direction: Vector3) (center: Vector3) radius =
        let offset = origin - center
        let b = Vector3.Dot(offset, direction)
        let c = Vector3.Dot(offset, offset) - radius * radius
        let discriminant = b * b - c
        if discriminant < 0.0f then None
        else
            let near = -b - MathF.Sqrt discriminant
            let far = -b + MathF.Sqrt discriminant
            if near >= 0.0f then Some near elif far >= 0.0f then Some far else None

    let rayCapsule (origin: Vector3) (direction: Vector3) (startPoint: Vector3) (endPoint: Vector3) radius =
        let segment = endPoint - startPoint
        let offset = origin - startPoint
        let segmentLengthSq = Vector3.Dot(segment, segment)
        let segmentRay = Vector3.Dot(segment, direction)
        let segmentOffset = Vector3.Dot(segment, offset)
        let rayOffset = Vector3.Dot(direction, offset)
        let offsetSq = Vector3.Dot(offset, offset)
        let a = segmentLengthSq - segmentRay * segmentRay
        let b = segmentLengthSq * rayOffset - segmentOffset * segmentRay
        let c = segmentLengthSq * offsetSq - segmentOffset * segmentOffset - radius * radius * segmentLengthSq
        let discriminant = b * b - a * c
        let bodyHit =
            if MathF.Abs a < 0.000001f || discriminant < 0.0f then None
            else
                let distance = (-b - MathF.Sqrt discriminant) / a
                let height = segmentOffset + distance * segmentRay
                if distance >= 0.0f && height > 0.0f && height < segmentLengthSq then Some distance else None
        [ bodyHit; raySphere origin direction startPoint radius; raySphere origin direction endPoint radius ]
        |> List.choose id
        |> function [] -> None | values -> Some(List.min values)

    /// Möller-Trumbore. Returns the distance along `direction` at which the ray
    /// crosses the triangle, hitting either face — back faces matter because
    /// penetration needs the exit surface as well as the entry one.
    let rayTriangle (origin: Vector3) (direction: Vector3) (a: Vector3) (b: Vector3) (c: Vector3) =
        let edge1 = b - a
        let edge2 = c - a
        let pointVector = Vector3.Cross(direction, edge2)
        let determinant = Vector3.Dot(edge1, pointVector)
        // Near-zero determinant means the ray runs parallel to the triangle.
        if MathF.Abs determinant < 0.000001f then ValueNone
        else
            let inverse = 1.0f / determinant
            let toOrigin = origin - a
            let u = Vector3.Dot(toOrigin, pointVector) * inverse
            if u < -0.000001f || u > 1.000001f then ValueNone
            else
                let qVector = Vector3.Cross(toOrigin, edge1)
                let v = Vector3.Dot(direction, qVector) * inverse
                if v < -0.000001f || u + v > 1.000001f then ValueNone
                else
                    let distance = Vector3.Dot(edge2, qVector) * inverse
                    if distance >= 0.0f then ValueSome distance else ValueNone

    /// Closest point to `point` within triangle abc, by Voronoi region — the
    /// standard Ericson construction, covering the three vertices, three edges
    /// and the face interior.
    let closestPointOnTriangle (point: Vector3) (a: Vector3) (b: Vector3) (c: Vector3) =
        let ab = b - a
        let ac = c - a
        let ap = point - a
        let d1 = Vector3.Dot(ab, ap)
        let d2 = Vector3.Dot(ac, ap)
        if d1 <= 0.0f && d2 <= 0.0f then a
        else
            let bp = point - b
            let d3 = Vector3.Dot(ab, bp)
            let d4 = Vector3.Dot(ac, bp)
            if d3 >= 0.0f && d4 <= d3 then b
            else
                let vc = d1 * d4 - d3 * d2
                if vc <= 0.0f && d1 >= 0.0f && d3 <= 0.0f then a + ab * (d1 / (d1 - d3))
                else
                    let cp = point - c
                    let d5 = Vector3.Dot(ab, cp)
                    let d6 = Vector3.Dot(ac, cp)
                    if d6 >= 0.0f && d5 <= d6 then c
                    else
                        let vb = d5 * d2 - d1 * d6
                        if vb <= 0.0f && d2 >= 0.0f && d6 <= 0.0f then a + ac * (d2 / (d2 - d6))
                        else
                            let va = d3 * d6 - d5 * d4
                            if va <= 0.0f && (d4 - d3) >= 0.0f && (d5 - d6) >= 0.0f then
                                b + (c - b) * ((d4 - d3) / ((d4 - d3) + (d5 - d6)))
                            else
                                let denominator = 1.0f / (va + vb + vc)
                                a + ab * (vb * denominator) + ac * (vc * denominator)

    /// Squared distance between two segments, and the closest point on the first.
    let private closestBetweenSegments (p1: Vector3) (q1: Vector3) (p2: Vector3) (q2: Vector3) =
        let d1 = q1 - p1
        let d2 = q2 - p2
        let r = p1 - p2
        let a = Vector3.Dot(d1, d1)
        let e = Vector3.Dot(d2, d2)
        let f = Vector3.Dot(d2, r)
        let mutable s = 0.0f
        let mutable t = 0.0f
        if a <= 0.000001f && e <= 0.000001f then ()
        elif a <= 0.000001f then t <- clamp01 (f / e)
        else
            let c = Vector3.Dot(d1, r)
            if e <= 0.000001f then s <- clamp01 (-c / a)
            else
                let b = Vector3.Dot(d1, d2)
                let denominator = a * e - b * b
                s <- if denominator <> 0.0f then clamp01 ((b * f - c * e) / denominator) else 0.0f
                t <- (b * s + f) / e
                if t < 0.0f then
                    t <- 0.0f
                    s <- clamp01 (-c / a)
                elif t > 1.0f then
                    t <- 1.0f
                    s <- clamp01 ((b - c) / a)
        let closest1 = p1 + d1 * s
        let closest2 = p2 + d2 * t
        struct (Vector3.DistanceSquared(closest1, closest2), closest1)

    /// Whether a vertical capsule standing at `feet` overlaps triangle abc, and
    /// if so the triangle point it is closest to. Replaces the AABB capsule test
    /// once geometry stops being boxes.
    let capsuleIntersectsTriangle radius height (feet: Vector3) (a: Vector3) (b: Vector3) (c: Vector3) =
        // The capsule's core segment runs between the sphere centres, so the
        // caps account for the first and last `radius` of its height.
        let lower = feet + Vector3(0.0f, min radius (height * 0.5f), 0.0f)
        let upper = feet + Vector3(0.0f, max (height - radius) (height * 0.5f), 0.0f)
        // A segment passing clean through the face registers no edge or endpoint
        // proximity, so test the crossing separately.
        let through =
            let direction = upper - lower
            let length = direction.Length()
            if length < 0.000001f then ValueNone
            else
                match rayTriangle lower (direction / length) a b c with
                | ValueSome distance when distance <= length -> ValueSome(lower + direction / length * distance)
                | _ -> ValueNone
        match through with
        | ValueSome point -> ValueSome point
        | ValueNone ->
            let mutable bestDistance = Single.PositiveInfinity
            let mutable bestPoint = Vector3.Zero
            let consider (distanceSquared: float32) (point: Vector3) =
                if distanceSquared < bestDistance then
                    bestDistance <- distanceSquared
                    bestPoint <- point
            for struct (edgeStart, edgeEnd) in [| struct (a, b); struct (b, c); struct (c, a) |] do
                let struct (distanceSquared, _) = closestBetweenSegments lower upper edgeStart edgeEnd
                // Recover the triangle-side point for the caller.
                let struct (_, onEdge) = closestBetweenSegments edgeStart edgeEnd lower upper
                consider distanceSquared onEdge
            for endpoint in [| lower; upper |] do
                let onFace = closestPointOnTriangle endpoint a b c
                consider (Vector3.DistanceSquared(endpoint, onFace)) onFace
            if bestDistance < radius * radius then ValueSome bestPoint else ValueNone

    let rayAabb (origin: Vector3) (direction: Vector3) (box: Aabb) =
        let mutable entry = 0.0f
        let mutable exit = Single.PositiveInfinity
        let mutable normal = Vector3.Zero
        let mutable valid = true
        let test originAxis directionAxis minimum maximum minimumNormal maximumNormal =
            if MathF.Abs directionAxis < 0.000001f then
                if originAxis < minimum || originAxis > maximum then valid <- false
            else
                let first = (minimum - originAxis) / directionAxis
                let second = (maximum - originAxis) / directionAxis
                let nearDistance, farDistance, nearNormal =
                    if first <= second then first, second, minimumNormal else second, first, maximumNormal
                if nearDistance > entry then
                    entry <- nearDistance
                    normal <- nearNormal
                exit <- min exit farDistance
                if exit < entry then valid <- false
        test origin.X direction.X box.Min.X box.Max.X (-Vector3.UnitX) Vector3.UnitX
        test origin.Y direction.Y box.Min.Y box.Max.Y (-Vector3.UnitY) Vector3.UnitY
        test origin.Z direction.Z box.Min.Z box.Max.Z (-Vector3.UnitZ) Vector3.UnitZ
        if valid && exit >= 0.0f then Some(struct (max 0.0f entry, exit, normal)) else None
