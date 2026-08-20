namespace Ironsight.Shell

open System
open System.Collections.Generic
open System.Numerics
open Ironsight
open Ironsight.ProcGen

/// Client-side cosmetic ragdolls for dead soldiers. Pure presentation: never
/// touches World, the sim, or the network — same tier as Particles.
module Ragdoll =
    let private jointCount = Anatomy.jointCount

    // Bone graph: anatomical chains plus stiffeners that keep the torso from
    // folding like cloth (hip/shoulder bars, spine, torso shear diagonals).
    let private bones =
        let stiffeners =
            [| JointId.LeftHip, JointId.RightHip
               JointId.LeftShoulder, JointId.RightShoulder
               JointId.Pelvis, JointId.Neck
               JointId.Pelvis, JointId.LeftShoulder
               JointId.Pelvis, JointId.RightShoulder |]
        Array.append Anatomy.treeEdges stiffeners
        |> Array.map (fun (first, second) -> Anatomy.index first, Anatomy.index second)

    // Heavier torso joints take the kill impulse fully; extremities lag and get
    // whipped around by the constraints instead.
    let private impulseWeight =
        [| 1.0f; 1.0f; 0.9f; 0.9f
           0.85f; 0.85f; 0.6f; 0.6f; 0.45f; 0.45f
           0.85f; 0.85f; 0.6f; 0.6f; 0.45f; 0.45f |]

    let private jointRadius index = if index = 3 then 0.15f else 0.08f

    type private Body =
        { Joints: Vector3[]
          Previous: Vector3[]
          Constraints: struct (int * int * float32)[]
          Cut: CutDescriptor option
          mutable RestFrames: int
          mutable Settled: bool
          Order: int }

    [<Literal>]
    let private MaxBodies = 24

    type System() =
        let bodies = Dictionary<EntityId, Body>()
        let mutable nextOrder = 0

        member _.Contains id = bodies.ContainsKey id

        /// Start a ragdoll from the soldier's last standing pose. `impulse` is
        /// a world-space velocity kick (m/s) from the killing blow.
        member _.Spawn(id, skeleton: Skeleton, impulse: Vector3, ?cut: CutDescriptor) =
            if not (bodies.ContainsKey id) then
                if bodies.Count >= MaxBodies then
                    // Drop the oldest settled corpse; if none settled yet, the oldest outright.
                    let victim =
                        bodies
                        |> Seq.sortBy (fun pair -> (not pair.Value.Settled), pair.Value.Order)
                        |> Seq.head
                    bodies.Remove victim.Key |> ignore
                let baseJoints = Anatomy.toArray skeleton
                let assumedStep = 1.0f / 60.0f
                let cutRelation, distal =
                    match cut with
                    | Some descriptor ->
                        let first, second = Anatomy.cutRelation descriptor.Site
                        let relation = Anatomy.index first, Anatomy.index second
                        let detached =
                            Anatomy.detachedJoints descriptor.Site
                            |> Set.map Anatomy.index
                        Some relation, detached
                    | None -> None, Set.empty
                let cutPoint =
                    match cutRelation, cut with
                    | Some(a, b), Some descriptor -> Vector3.Lerp(baseJoints[a], baseJoints[b], descriptor.Fraction)
                    | _ -> Vector3.Zero
                let joints =
                    if cutRelation.IsSome then Array.append baseJoints [| cutPoint; cutPoint |]
                    else baseJoints
                let previous =
                    joints
                    |> Array.mapi (fun index joint ->
                        let weight = if index < impulseWeight.Length then impulseWeight[index] else 0.72f
                        let sever =
                            match cut with
                            | Some descriptor when Set.contains index distal || index = 17 -> descriptor.Impulse
                            | _ -> Vector3.Zero
                        joint - (impulse * weight + sever) * assumedStep)
                let crossesCut a b = Set.contains a distal <> Set.contains b distal
                let ordinary =
                    bones
                    |> Array.choose (fun (a, b) ->
                        if cutRelation.IsSome && crossesCut a b then None
                        else Some(struct (a, b, Vector3.Distance(joints[a], joints[b]))))
                let constraints =
                    match cutRelation with
                    | Some(a, b) ->
                        Array.append ordinary
                            [| struct (a, 16, Vector3.Distance(joints[a], joints[16]))
                               struct (17, b, Vector3.Distance(joints[17], joints[b])) |]
                    | None -> ordinary
                bodies[id] <-
                    { Joints = joints
                      Previous = previous
                      Constraints = constraints
                      Cut = cut
                      RestFrames = 0
                      Settled = false
                      Order = nextOrder }
                nextOrder <- nextOrder + 1

        /// Drop ragdolls whose soldier respawned or left the world entirely.
        member _.Prune(soldiers: Soldier[]) =
            let corpses =
                soldiers
                |> Array.choose (fun soldier ->
                    match soldier.Behavior with
                    | Dying _ | DyingHeadshot _ -> Some soldier.Id
                    | _ -> None)
                |> HashSet
            let stale = bodies.Keys |> Seq.filter (corpses.Contains >> not) |> Seq.toArray
            for id in stale do bodies.Remove id |> ignore

        member _.Step(dt: float32, level: Level, pins: Map<EntityId, Vector3>) =
            let dt = Math.Clamp(dt, 0.0f, 0.05f)
            if dt > 0.0f then
                for pair in bodies do
                    let body = pair.Value
                    let pin = Map.tryFind pair.Key pins
                    if not body.Settled || pin.IsSome then
                        let joints = body.Joints
                        let previous = body.Previous
                        // Verlet integration: damped inertia plus gravity.
                        for index in 0 .. joints.Length - 1 do
                            let current = joints[index]
                            joints[index] <- current + (current - previous[index]) * 0.96f + Vector3(0.0f, -9.81f, 0.0f) * dt * dt
                            previous[index] <- current
                        // Fixed-length bone constraints, a few relaxation passes.
                        for _ in 1..3 do
                            body.Constraints
                            |> Array.iter (fun struct (a, b, length) ->
                                let delta = joints[b] - joints[a]
                                let distance = delta.Length()
                                if distance > 0.0001f then
                                    let correction = delta * ((distance - length) / distance * 0.5f)
                                    joints[a] <- joints[a] + correction
                                    joints[b] <- joints[b] - correction)
                            pin
                            |> Option.iter (fun anchor ->
                                joints[1] <- anchor
                                previous[1] <- anchor)
                        // Ground clamp with friction. The query point is lifted so
                        // a joint that sank into geometry this frame still finds
                        // the surface above its center.
                        // ponytail: per-joint surface probes, ~16 per corpse per
                        // frame; share one probe per body if profiling ever cares.
                        for index in 0 .. joints.Length - 1 do
                            let joint = joints[index]
                            let radius = jointRadius index
                            match Movement.surfaceUnder level (joint + Vector3(0.0f, 0.15f, 0.0f)) with
                            | ValueSome(struct (height, _)) when joint.Y < height + radius ->
                                joints[index] <- Vector3(joint.X, height + radius, joint.Z)
                                // Grounded joints bleed horizontal speed so the
                                // body slumps instead of skating.
                                let flat = Vector3(joint.X, previous[index].Y, joint.Z)
                                previous[index] <- Vector3.Lerp(previous[index], flat, 0.5f)
                            | _ -> ()
                        pin
                        |> Option.iter (fun anchor ->
                            joints[1] <- anchor
                            previous[1] <- anchor)
                        // Sleep once nothing moved for a while; the skeleton
                        // stays frozen and free to render.
                        let mutable maxMove = 0.0f
                        for index in 0 .. joints.Length - 1 do
                            maxMove <- max maxMove (Vector3.Distance(joints[index], previous[index]))
                        if pin.IsSome then
                            body.RestFrames <- 0
                            body.Settled <- false
                        elif maxMove < 0.004f then
                            body.RestFrames <- body.RestFrames + 1
                            if body.RestFrames >= 20 then body.Settled <- true
                        else
                            body.RestFrames <- 0

        member this.Step(dt: float32, level: Level) =
            this.Step(dt, level, Map.empty<EntityId, Vector3>)

        member _.TryGet id =
            match bodies.TryGetValue id with
            | true, body -> Some(Anatomy.ofArray body.Joints)
            | _ -> None

        /// Exact duplicate cut anchors, one constrained to each new body.
        /// They start coincident and diverge under local physics.
        member _.TryGetCut id =
            match bodies.TryGetValue id with
            | true, body when body.Cut.IsSome && body.Joints.Length >= 18 ->
                Some(body.Cut.Value, body.Joints[16], body.Joints[17])
            | _ -> None
