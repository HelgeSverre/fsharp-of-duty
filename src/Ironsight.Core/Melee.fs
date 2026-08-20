namespace Ironsight

open System
open System.Numerics

[<Struct>]
type AnatomicalCapsule =
    { Part: BodyPart
      StartPoint: Vector3
      EndPoint: Vector3
      Radius: float32 }

[<Struct>]
type MeleeTarget =
    { Id: EntityId
      Position: Vector3
      Yaw: float32
      Stance: Stance }

[<Struct>]
type MeleeHit =
    { Victim: EntityId
      Part: BodyPart
      Point: Vector3
      Normal: Vector3
      Fraction: float32
      Distance: float32 }

[<RequireQualifiedAccess>]
module Melee =
    let private stanceDrop = function
        | Standing -> 0.0f
        | Crouched -> 0.34f
        | Prone -> 0.88f

    let private capsule part radius a b =
        { Part = part; StartPoint = a; EndPoint = b; Radius = radius }

    let anatomy (target: MeleeTarget) =
        let drop = stanceDrop target.Stance
        let up height = target.Position + Vector3.UnitY * max 0.08f (height - drop)
        let right = MathEx.yawRight target.Yaw
        let forward = MathEx.yawForward target.Yaw
        let offset side height forwardAmount = up height + right * side + forward * forwardAmount
        [| capsule BodyHead 0.16f (up 1.54f) (up 1.82f)
           capsule BodyTorso 0.27f (up 0.82f) (up 1.50f)
           capsule BodyLeftUpperArm 0.095f (offset -0.25f 1.42f 0.0f) (offset -0.31f 1.14f 0.14f)
           capsule BodyLeftLowerArm 0.082f (offset -0.31f 1.14f 0.14f) (offset -0.14f 1.22f 0.42f)
           capsule BodyRightUpperArm 0.095f (offset 0.25f 1.42f 0.0f) (offset 0.31f 1.14f 0.10f)
           capsule BodyRightLowerArm 0.082f (offset 0.31f 1.14f 0.10f) (offset 0.14f 1.20f 0.36f)
           capsule BodyLeftUpperLeg 0.12f (offset -0.12f 0.88f 0.0f) (offset -0.14f 0.48f 0.0f)
           capsule BodyLeftLowerLeg 0.105f (offset -0.14f 0.48f 0.0f) (offset -0.13f 0.10f 0.0f)
           capsule BodyRightUpperLeg 0.12f (offset 0.12f 0.88f 0.0f) (offset 0.14f 0.48f 0.0f)
           capsule BodyRightLowerLeg 0.105f (offset 0.14f 0.48f 0.0f) (offset 0.13f 0.10f 0.0f) |]

    let closestPoints p1 q1 p2 q2 =
        let d1 = q1 - p1
        let d2 = q2 - p2
        let r = p1 - p2
        let a = Vector3.Dot(d1, d1)
        let e = Vector3.Dot(d2, d2)
        let f = Vector3.Dot(d2, r)
        let mutable s = 0.0f
        let mutable t = 0.0f
        if a <= 0.000001f && e <= 0.000001f then ()
        elif a <= 0.000001f then t <- MathEx.clamp01 (f / e)
        else
            let c = Vector3.Dot(d1, r)
            if e <= 0.000001f then s <- MathEx.clamp01 (-c / a)
            else
                let b = Vector3.Dot(d1, d2)
                let denominator = a * e - b * b
                s <- if abs denominator > 0.000001f then MathEx.clamp01 ((b * f - c * e) / denominator) else 0.0f
                t <- (b * s + f) / e
                if t < 0.0f then
                    t <- 0.0f
                    s <- MathEx.clamp01 (-c / a)
                elif t > 1.0f then
                    t <- 1.0f
                    s <- MathEx.clamp01 ((b - c) / a)
        let first = p1 + d1 * s
        let second = p2 + d2 * t
        struct (first, second, s, t, Vector3.DistanceSquared(first, second))

    let private weaponSegments attack position yaw pitch =
        let forward = Ballistics.directionFromAngles yaw pitch Vector2.Zero
        let flatForward = MathEx.yawForward yaw
        let right = MathEx.yawRight yaw
        match attack with
        | ChainContact ->
            let handle = position + Vector3.UnitY * 1.14f + flatForward * 0.24f + right * 0.05f
            [| struct (handle, handle + forward * 0.92f, 0.14f) |]
        | KatanaSweep ->
            let hand = position + Vector3.UnitY * 1.18f + flatForward * 0.18f
            [| for sample in 0..14 do
                   let angle = -1.48f + 2.96f * float32 sample / 14.0f
                   let direction = MathEx.normalizedOrZero (flatForward * MathF.Cos angle + right * MathF.Sin angle)
                   yield struct (hand, hand + direction * 2.05f, 0.085f) |]
        | KatanaOverhead ->
            let hand = position + Vector3.UnitY * 1.18f + flatForward * 0.20f
            [| for sample in 0..14 do
                   let angle = 1.42f - 2.62f * float32 sample / 14.0f
                   let tip = hand + flatForward * (0.40f + MathF.Cos angle * 1.58f) + Vector3.UnitY * (MathF.Sin angle * 1.58f)
                   yield struct (hand, tip, 0.085f) |]

    let traceEndpoint attack position yaw pitch =
        let segments = weaponSegments attack position yaw pitch
        let struct (_, endpoint, _) = segments[segments.Length - 1]
        endpoint

    let private hitCapsule attackOrigin (segmentStart, segmentEnd, weaponRadius) (capsule: AnatomicalCapsule) =
        let struct (onWeapon, onBody, _, bodyFraction, distanceSquared) =
            closestPoints segmentStart segmentEnd capsule.StartPoint capsule.EndPoint
        let radius = weaponRadius + capsule.Radius
        if distanceSquared <= radius * radius then
            let normal =
                let delta = onBody - onWeapon
                if delta.LengthSquared() > 0.000001f then Vector3.Normalize delta
                else MathEx.normalizedOrZero (onBody - attackOrigin)
            Some(onBody, normal, bodyFraction, Vector3.Distance(attackOrigin, onWeapon))
        else None

    let resolve attack attackerPosition yaw pitch canHit (level: Level) (targets: MeleeTarget array) =
        let segments = weaponSegments attack attackerPosition yaw pitch
        let origin = attackerPosition + Vector3.UnitY * (Ballistics.eyeHeight Standing * 0.70f)
        let hits =
            targets
            |> Array.choose (fun target ->
                if not (canHit target) then None
                else
                    anatomy target
                    |> Array.collect (fun body ->
                        segments
                        |> Array.choose (fun struct (a, b, radius) ->
                            hitCapsule origin (a, b, radius) body
                            |> Option.bind (fun (point, normal, fraction, distance) ->
                                if Ballistics.lineOfSight origin point level then
                                    Some { Victim = target.Id; Part = body.Part; Point = point; Normal = normal; Fraction = fraction; Distance = distance }
                                else None)))
                    |> function
                        | [||] -> None
                        | values -> Some(Array.minBy (fun hit -> hit.Distance) values))
            |> Array.sortBy (fun hit -> hit.Distance)
        match attack with
        | ChainContact | KatanaOverhead -> hits |> Array.truncate 1
        | KatanaSweep -> hits |> Array.truncate 3

    let cutSite = function
        | BodyHead -> CutNeck
        | BodyTorso -> CutWaist
        | BodyLeftUpperArm -> CutLeftUpperArm
        | BodyLeftLowerArm -> CutLeftLowerArm
        | BodyRightUpperArm -> CutRightUpperArm
        | BodyRightLowerArm -> CutRightLowerArm
        | BodyLeftUpperLeg -> CutLeftUpperLeg
        | BodyLeftLowerLeg -> CutLeftLowerLeg
        | BodyRightUpperLeg -> CutRightUpperLeg
        | BodyRightLowerLeg -> CutRightLowerLeg

    let makeCut deathRevision victimPosition yaw attack seed (hit: MeleeHit) =
        let inverse = Matrix4x4.CreateTranslation(-victimPosition) * Matrix4x4.CreateRotationY(yaw)
        let impulseScale = match attack with ChainContact -> 3.8f | KatanaSweep -> 7.0f | KatanaOverhead -> 8.5f
        { DeathRevision = deathRevision
          Site = cutSite hit.Part
          Fraction = Math.Clamp(hit.Fraction, 0.12f, 0.88f)
          LocalPoint = Vector3.Transform(hit.Point, inverse)
          LocalNormal = Vector3.TransformNormal(hit.Normal, Matrix4x4.CreateRotationY(yaw)) |> MathEx.normalizedOrZero
          Impulse = -hit.Normal * impulseScale + Vector3.UnitY * 1.4f
          CosmeticSeed = seed }
