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
