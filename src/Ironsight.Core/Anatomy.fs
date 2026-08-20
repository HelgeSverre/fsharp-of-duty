namespace Ironsight

open System
open System.Collections.Generic
open System.Numerics

[<RequireQualifiedAccess>]
type JointId =
    | Pelvis
    | Chest
    | Neck
    | Head
    | LeftHip
    | RightHip
    | LeftKnee
    | RightKnee
    | LeftAnkle
    | RightAnkle
    | LeftShoulder
    | RightShoulder
    | LeftElbow
    | RightElbow
    | LeftHand
    | RightHand

/// Pose joints shared by the renderer, melee hit proxies, and cosmetic ragdoll.
/// Keeping one rig prevents a visually raised arm from retaining a standing-pose
/// hitbox, and gives every cut subsystem the same anatomical graph.
type Skeleton =
    { Pelvis: Vector3; Chest: Vector3; Neck: Vector3; Head: Vector3
      LeftHip: Vector3; RightHip: Vector3
      LeftKnee: Vector3; RightKnee: Vector3
      LeftAnkle: Vector3; RightAnkle: Vector3
      LeftShoulder: Vector3; RightShoulder: Vector3
      LeftElbow: Vector3; RightElbow: Vector3
      LeftHand: Vector3; RightHand: Vector3 }

[<Struct>]
type AnatomySegment =
    { Part: BodyPart
      Site: CutSite
      StartJoint: JointId
      EndJoint: JointId
      StartPoint: Vector3
      EndPoint: Vector3
      Radius: float32
      MinSeverFraction: float32
      MaxSeverFraction: float32 }

[<RequireQualifiedAccess>]
module Anatomy =
    let jointCount = 16

    let stanceDrop = function
        | Standing -> 0.0f
        | Crouched -> 0.34f
        | Prone -> 0.82f

    let effectiveStance (soldier: Soldier) =
        match soldier.Behavior with
        | InCover(cover, _) when cover.Crouch -> Crouched
        | _ -> soldier.Stance

    let localSkeletonWithStride stance stride =
        let drop = stanceDrop stance
        let opposite = -stride
        let floor height = max 0.08f height
        let pelvis = Vector3(0.0f, floor (0.90f - drop), 0.0f)
        let chest = Vector3(0.0f, floor (1.43f - drop), -0.015f)
        { Pelvis = pelvis
          Chest = chest
          Neck = Vector3(0.0f, floor (1.51f - drop), -0.02f)
          Head = Vector3(0.0f, floor (1.68f - drop), -0.025f)
          LeftHip = pelvis + Vector3(-0.12f, -0.02f, 0.0f)
          RightHip = pelvis + Vector3(0.12f, -0.02f, 0.0f)
          LeftKnee = Vector3(-0.13f, floor (0.49f - drop * 0.55f), stride)
          RightKnee = Vector3(0.13f, floor (0.49f - drop * 0.55f), opposite)
          LeftAnkle = Vector3(-0.13f, 0.10f, -stride * 0.72f)
          RightAnkle = Vector3(0.13f, 0.10f, -opposite * 0.72f)
          LeftShoulder = chest + Vector3(-0.26f, -0.01f, 0.0f)
          RightShoulder = chest + Vector3(0.26f, -0.01f, 0.0f)
          LeftElbow = Vector3(-0.25f, floor (1.19f - drop), -0.20f)
          RightElbow = Vector3(0.27f, floor (1.18f - drop), -0.10f)
          LeftHand = Vector3(-0.11f, floor (1.28f - drop), -0.46f)
          RightHand = Vector3(0.10f, floor (1.27f - drop), -0.31f) }

    let localSkeleton stance phase =
        let strideScale = if stance = Prone then 0.35f else 1.0f
        localSkeletonWithStride stance (MathF.Sin phase * 0.15f * strideScale)

    let map transform (skeleton: Skeleton) =
        { Pelvis = transform skeleton.Pelvis; Chest = transform skeleton.Chest
          Neck = transform skeleton.Neck; Head = transform skeleton.Head
          LeftHip = transform skeleton.LeftHip; RightHip = transform skeleton.RightHip
          LeftKnee = transform skeleton.LeftKnee; RightKnee = transform skeleton.RightKnee
          LeftAnkle = transform skeleton.LeftAnkle; RightAnkle = transform skeleton.RightAnkle
          LeftShoulder = transform skeleton.LeftShoulder; RightShoulder = transform skeleton.RightShoulder
          LeftElbow = transform skeleton.LeftElbow; RightElbow = transform skeleton.RightElbow
          LeftHand = transform skeleton.LeftHand; RightHand = transform skeleton.RightHand }

    let worldSkeleton position yaw stance phase =
        let transform = Matrix4x4.CreateRotationY(-yaw) * Matrix4x4.CreateTranslation position
        localSkeleton stance phase |> map (fun joint -> Vector3.Transform(joint, transform))

    let toArray (skeleton: Skeleton) =
        [| skeleton.Pelvis; skeleton.Chest; skeleton.Neck; skeleton.Head
           skeleton.LeftHip; skeleton.RightHip; skeleton.LeftKnee; skeleton.RightKnee
           skeleton.LeftAnkle; skeleton.RightAnkle
           skeleton.LeftShoulder; skeleton.RightShoulder; skeleton.LeftElbow; skeleton.RightElbow
           skeleton.LeftHand; skeleton.RightHand |]

    let ofArray (joints: Vector3[]) =
        { Pelvis = joints[0]; Chest = joints[1]; Neck = joints[2]; Head = joints[3]
          LeftHip = joints[4]; RightHip = joints[5]; LeftKnee = joints[6]; RightKnee = joints[7]
          LeftAnkle = joints[8]; RightAnkle = joints[9]
          LeftShoulder = joints[10]; RightShoulder = joints[11]
          LeftElbow = joints[12]; RightElbow = joints[13]
          LeftHand = joints[14]; RightHand = joints[15] }

    let index = function
        | JointId.Pelvis -> 0 | JointId.Chest -> 1 | JointId.Neck -> 2 | JointId.Head -> 3
        | JointId.LeftHip -> 4 | JointId.RightHip -> 5
        | JointId.LeftKnee -> 6 | JointId.RightKnee -> 7
        | JointId.LeftAnkle -> 8 | JointId.RightAnkle -> 9
        | JointId.LeftShoulder -> 10 | JointId.RightShoulder -> 11
        | JointId.LeftElbow -> 12 | JointId.RightElbow -> 13
        | JointId.LeftHand -> 14 | JointId.RightHand -> 15

    let point joint (skeleton: Skeleton) =
        match joint with
        | JointId.Pelvis -> skeleton.Pelvis | JointId.Chest -> skeleton.Chest
        | JointId.Neck -> skeleton.Neck | JointId.Head -> skeleton.Head
        | JointId.LeftHip -> skeleton.LeftHip | JointId.RightHip -> skeleton.RightHip
        | JointId.LeftKnee -> skeleton.LeftKnee | JointId.RightKnee -> skeleton.RightKnee
        | JointId.LeftAnkle -> skeleton.LeftAnkle | JointId.RightAnkle -> skeleton.RightAnkle
        | JointId.LeftShoulder -> skeleton.LeftShoulder | JointId.RightShoulder -> skeleton.RightShoulder
        | JointId.LeftElbow -> skeleton.LeftElbow | JointId.RightElbow -> skeleton.RightElbow
        | JointId.LeftHand -> skeleton.LeftHand | JointId.RightHand -> skeleton.RightHand

    /// The tree establishes semantic ownership. Ragdoll stiffeners are separate:
    /// after removing a cut edge this tree says which joints form the fragment.
    let treeEdges =
        [| JointId.Pelvis, JointId.Chest; JointId.Chest, JointId.Neck; JointId.Neck, JointId.Head
           JointId.Pelvis, JointId.LeftHip; JointId.Pelvis, JointId.RightHip
           JointId.LeftHip, JointId.LeftKnee; JointId.LeftKnee, JointId.LeftAnkle
           JointId.RightHip, JointId.RightKnee; JointId.RightKnee, JointId.RightAnkle
           JointId.Chest, JointId.LeftShoulder; JointId.LeftShoulder, JointId.LeftElbow
           JointId.LeftElbow, JointId.LeftHand; JointId.Chest, JointId.RightShoulder
           JointId.RightShoulder, JointId.RightElbow; JointId.RightElbow, JointId.RightHand |]

    let cutRelation = function
        | CutNeck -> JointId.Neck, JointId.Head
        | CutWaist -> JointId.Pelvis, JointId.Chest
        | CutLeftUpperArm -> JointId.LeftShoulder, JointId.LeftElbow
        | CutLeftLowerArm -> JointId.LeftElbow, JointId.LeftHand
        | CutRightUpperArm -> JointId.RightShoulder, JointId.RightElbow
        | CutRightLowerArm -> JointId.RightElbow, JointId.RightHand
        | CutLeftUpperLeg -> JointId.LeftHip, JointId.LeftKnee
        | CutLeftLowerLeg -> JointId.LeftKnee, JointId.LeftAnkle
        | CutRightUpperLeg -> JointId.RightHip, JointId.RightKnee
        | CutRightLowerLeg -> JointId.RightKnee, JointId.RightAnkle

    let detachedJoints site =
        let proximal, distal = cutRelation site
        let neighbours = Dictionary<JointId, ResizeArray<JointId>>()
        let add first second =
            match neighbours.TryGetValue first with
            | true, values -> values.Add second
            | _ -> neighbours[first] <- ResizeArray [ second ]
        for first, second in treeEdges do
            if not ((first = proximal && second = distal) || (first = distal && second = proximal)) then
                add first second
                add second first
        let visited = HashSet<JointId>()
        let pending = Stack<JointId>()
        pending.Push distal
        while pending.Count > 0 do
            let current = pending.Pop()
            if visited.Add current then
                match neighbours.TryGetValue current with
                | true, values -> for neighbour in values do pending.Push neighbour
                | _ -> ()
        visited |> Seq.toList |> Set.ofList

    let segments (skeleton: Skeleton) =
        let segment part site startJoint endJoint radius minFraction maxFraction =
            { Part = part
              Site = site
              StartJoint = startJoint
              EndJoint = endJoint
              StartPoint = point startJoint skeleton
              EndPoint = point endJoint skeleton
              Radius = radius
              MinSeverFraction = minFraction
              MaxSeverFraction = maxFraction }
        [| segment BodyHead CutNeck JointId.Neck JointId.Head 0.16f 0.0f 0.48f
           segment BodyTorso CutWaist JointId.Pelvis JointId.Chest 0.27f 0.06f 0.42f
           segment BodyLeftUpperArm CutLeftUpperArm JointId.LeftShoulder JointId.LeftElbow 0.095f 0.14f 0.86f
           segment BodyLeftLowerArm CutLeftLowerArm JointId.LeftElbow JointId.LeftHand 0.082f 0.14f 0.86f
           segment BodyRightUpperArm CutRightUpperArm JointId.RightShoulder JointId.RightElbow 0.095f 0.14f 0.86f
           segment BodyRightLowerArm CutRightLowerArm JointId.RightElbow JointId.RightHand 0.082f 0.14f 0.86f
           segment BodyLeftUpperLeg CutLeftUpperLeg JointId.LeftHip JointId.LeftKnee 0.12f 0.14f 0.86f
           segment BodyLeftLowerLeg CutLeftLowerLeg JointId.LeftKnee JointId.LeftAnkle 0.105f 0.14f 0.86f
           segment BodyRightUpperLeg CutRightUpperLeg JointId.RightHip JointId.RightKnee 0.12f 0.14f 0.86f
           segment BodyRightLowerLeg CutRightLowerLeg JointId.RightKnee JointId.RightAnkle 0.105f 0.14f 0.86f |]
