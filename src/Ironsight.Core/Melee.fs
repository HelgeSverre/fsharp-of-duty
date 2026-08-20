namespace Ironsight

open System
open System.Numerics

[<Struct>]
type MeleeTarget =
    { Id: EntityId
      Position: Vector3
      Yaw: float32
      Stance: Stance
      AnimPhase: float32 }

[<Struct>]
type BladePose =
    { Base: Vector3
      Tip: Vector3
      FaceNormal: Vector3
      Time: float32 }

[<Struct>]
type MeleeHit =
    { Victim: EntityId
      Part: BodyPart
      Site: CutSite option
      Point: Vector3
      ContactNormal: Vector3
      CutPlaneNormal: Vector3
      BladeTangent: Vector3
      SweepDirection: Vector3
      BodyAxis: Vector3
      Fraction: float32
      Distance: float32
      SwingTime: float32
      CrossingDepth: float32
      SeverScore: float32 }

[<RequireQualifiedAccess>]
module Melee =
    let anatomy (target: MeleeTarget) =
        Anatomy.worldSkeleton target.Position target.Yaw target.Stance target.AnimPhase
        |> Anatomy.segments

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

    /// Authoritative trajectory, in attack-time order. Damage uses the ribbons
    /// between these poses; the renderer is free to interpolate more densely.
    let bladeTrajectory attack position yaw pitch =
        let flatForward = MathEx.yawForward yaw
        let right = MathEx.yawRight yaw
        match attack with
        | KatanaSweep ->
            // Looking up/down deliberately moves the horizontal cut through the
            // complete anatomy: neutral crosses the neck, while a committed
            // downward aim can select waist and leg sever bands.
            let height = 1.47f + Math.Clamp(pitch, -1.15f, 0.75f) * 0.62f
            let hand = position + Vector3.UnitY * height + flatForward * 0.18f
            [| for sample in 0..14 do
                   let progress = float32 sample / 14.0f
                   let angle = -1.48f + 2.96f * progress
                   let direction = MathEx.normalizedOrZero (flatForward * MathF.Cos angle + right * MathF.Sin angle)
                   yield
                       { Base = hand
                         Tip = hand + direction * 2.05f
                         FaceNormal = Vector3.UnitY
                         Time = progress } |]
        | KatanaOverhead ->
            let hand = position + Vector3.UnitY * 1.18f + flatForward * 0.20f
            [| for sample in 0..14 do
                   let progress = float32 sample / 14.0f
                   let angle = 1.48f - 2.78f * progress
                   let tip = hand + flatForward * (0.40f + MathF.Cos angle * 1.58f) + Vector3.UnitY * (MathF.Sin angle * 1.58f)
                   yield
                       { Base = hand
                         Tip = tip
                         FaceNormal = right
                         Time = progress } |]

    let traceEndpoint attack position yaw pitch =
        (bladeTrajectory attack position yaw pitch |> Array.last).Tip

    let private planeFor bladeTangent sweepDirection fallback =
        let fromMotion = Vector3.Cross(bladeTangent, sweepDirection) |> MathEx.normalizedOrZero
        if fromMotion = Vector3.Zero then MathEx.normalizedOrZero fallback else fromMotion

    let private contact attackOrigin (previous: BladePose) (current: BladePose) (bladeFraction: float32) (body: AnatomySegment) =
        let previousPoint = Vector3.Lerp(previous.Base, previous.Tip, bladeFraction)
        let currentPoint = Vector3.Lerp(current.Base, current.Tip, bladeFraction)
        let struct (onBlade, onBody, motionFraction, bodyFraction, distanceSquared) =
            closestPoints previousPoint currentPoint body.StartPoint body.EndPoint
        let combinedRadius = 0.085f + body.Radius
        if distanceSquared > combinedRadius * combinedRadius then None
        else
            let bladeBase = Vector3.Lerp(previous.Base, current.Base, motionFraction)
            let bladeTip = Vector3.Lerp(previous.Tip, current.Tip, motionFraction)
            let bladeTangent = MathEx.normalizedOrZero (bladeTip - bladeBase)
            let sweepDirection = MathEx.normalizedOrZero (currentPoint - previousPoint)
            let fallback = Vector3.Lerp(previous.FaceNormal, current.FaceNormal, motionFraction)
            let cutPlane = planeFor bladeTangent sweepDirection fallback
            let bodyAxis = MathEx.normalizedOrZero (body.EndPoint - body.StartPoint)
            let contactNormal =
                let delta = onBody - onBlade
                if delta.LengthSquared() > 0.000001f then Vector3.Normalize delta
                else MathEx.normalizedOrZero (onBody - attackOrigin)
            let crossingDepth = combinedRadius - MathF.Sqrt distanceSquared
            let inSeverBand = bodyFraction >= body.MinSeverFraction && bodyFraction <= body.MaxSeverFraction
            let alignment = MathF.Abs(Vector3.Dot(cutPlane, bodyAxis))
            let minimumAlignment =
                match body.Part with
                // An overhead hit can still take the head: presentation clamps
                // an unsafe longitudinal plane toward the authored neck seam.
                | BodyHead -> 0.0f
                | BodyTorso -> 0.28f
                | _ -> 0.20f
            let site = if inSeverBand && alignment >= minimumAlignment then Some body.Site else None
            let bandCenter = (body.MinSeverFraction + body.MaxSeverFraction) * 0.5f
            let severScore =
                crossingDepth
                + alignment * 0.055f
                - MathF.Abs(bodyFraction - bandCenter) * 0.012f
            Some
                { Victim = EntityId 0 // supplied after selecting a target
                  Part = body.Part
                  Site = site
                  Point = onBody
                  ContactNormal = contactNormal
                  CutPlaneNormal = cutPlane
                  BladeTangent = bladeTangent
                  SweepDirection = sweepDirection
                  BodyAxis = bodyAxis
                  Fraction = bodyFraction
                  Distance = Vector3.Distance(attackOrigin, onBlade)
                  SwingTime = previous.Time + (current.Time - previous.Time) * motionFraction
                  CrossingDepth = crossingDepth
                  SeverScore = severScore }

    let resolve attack attackerPosition yaw pitch canHit (level: Level) (targets: MeleeTarget array) =
        let trajectory = bladeTrajectory attack attackerPosition yaw pitch
        let origin = attackerPosition + Vector3.UnitY * (Ballistics.eyeHeight Standing * 0.70f)
        let hits =
            targets
            |> Array.choose (fun target ->
                if not (canHit target) then None
                else
                    let candidates =
                        anatomy target
                        |> Array.collect (fun body ->
                            [| for poseIndex in 0..trajectory.Length - 2 do
                                   let previous = trajectory[poseIndex]
                                   let current = trajectory[poseIndex + 1]
                                   // Stations turn each moving point on the edge
                                   // into a capsule lane, approximating the full
                                   // swept ribbon without angular-CCD tunnelling.
                                   for station in 1..14 do
                                       let bladeFraction = float32 station / 14.0f
                                       match contact origin previous current bladeFraction body with
                                       | Some candidate -> yield { candidate with Victim = target.Id }
                                       | _ -> () |])
                    match candidates with
                    | [||] -> None
                    | values ->
                        let firstVisible ordered =
                            ordered
                            |> Array.tryFind (fun candidate -> Ballistics.lineOfSight origin candidate.Point level)
                        // The blade may enter at a hand and cross the neck a
                        // moment later. Try valid crossings by geometric quality,
                        // then fall back to the earliest visible damage contact.
                        // LOS runs only until a winner is found, not once for
                        // every overlapping sweep station.
                        values
                        |> Array.filter _.Site.IsSome
                        |> Array.sortBy (fun hit -> -hit.SeverScore, hit.SwingTime, hit.Distance, hit.Part)
                        |> firstVisible
                        |> Option.orElseWith (fun () ->
                            values
                            |> Array.sortBy (fun hit -> hit.SwingTime, -hit.CrossingDepth, hit.Distance, hit.Part)
                            |> firstVisible))
            |> Array.sortBy (fun hit -> hit.SwingTime, hit.Distance, hit.Victim)
        match attack with
        | KatanaOverhead -> hits |> Array.truncate 1
        | KatanaSweep -> hits |> Array.truncate 3

    let private clampPlaneToSegment minimumAlignment axis plane =
        let axis = MathEx.normalizedOrZero axis
        let plane = MathEx.normalizedOrZero plane
        let alignment = Vector3.Dot(plane, axis)
        if axis = Vector3.Zero then plane
        elif plane = Vector3.Zero then axis
        elif MathF.Abs alignment >= minimumAlignment then plane
        else
            let perpendicular = plane - axis * alignment |> MathEx.normalizedOrZero
            let sign = if alignment < 0.0f then -1.0f else 1.0f
            axis * (minimumAlignment * sign)
            + perpendicular * MathF.Sqrt(1.0f - minimumAlignment * minimumAlignment)
            |> MathEx.normalizedOrZero

    let tryMakeCut deathRevision victimPosition yaw attack seed (hit: MeleeHit) =
        hit.Site
        |> Option.map (fun site ->
            let inverse = Matrix4x4.CreateTranslation(-victimPosition) * Matrix4x4.CreateRotationY(yaw)
            let minimumPlaneAlignment = match site with CutNeck | CutWaist -> 0.62f | _ -> 0.38f
            let worldPlane = clampPlaneToSegment minimumPlaneAlignment hit.BodyAxis hit.CutPlaneNormal
            let impulseScale = match attack with KatanaSweep -> 7.0f | KatanaOverhead -> 8.5f
            let cutImpulse =
                hit.SweepDirection * (impulseScale * 0.72f)
                - hit.ContactNormal * (impulseScale * 0.28f)
                + Vector3.UnitY * 1.4f
            { DeathRevision = deathRevision
              Site = site
              Fraction = Math.Clamp(hit.Fraction, 0.12f, 0.88f)
              LocalPoint = Vector3.Transform(hit.Point, inverse)
              LocalPlaneNormal = Vector3.TransformNormal(worldPlane, Matrix4x4.CreateRotationY(yaw)) |> MathEx.normalizedOrZero
              LocalBladeTangent = Vector3.TransformNormal(hit.BladeTangent, Matrix4x4.CreateRotationY(yaw)) |> MathEx.normalizedOrZero
              LocalSweepDirection = Vector3.TransformNormal(hit.SweepDirection, Matrix4x4.CreateRotationY(yaw)) |> MathEx.normalizedOrZero
              Impulse = cutImpulse
              CosmeticSeed = seed })
