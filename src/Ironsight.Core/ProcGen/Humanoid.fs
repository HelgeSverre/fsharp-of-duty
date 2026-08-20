namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

/// Joint positions for one soldier. Local space when produced by the pose
/// math, world space when driven by the client-side ragdoll.
type Skeleton =
    { Pelvis: Vector3; Chest: Vector3; Neck: Vector3; Head: Vector3
      LeftHip: Vector3; RightHip: Vector3
      LeftKnee: Vector3; RightKnee: Vector3
      LeftAnkle: Vector3; RightAnkle: Vector3
      LeftShoulder: Vector3; RightShoulder: Vector3
      LeftElbow: Vector3; RightElbow: Vector3
      LeftHand: Vector3; RightHand: Vector3 }

[<RequireQualifiedAccess>]
module Humanoid =
    let private segment radius material (startPoint: Vector3) (endPoint: Vector3) =
        let delta = endPoint - startPoint
        let length = max 0.01f (delta.Length())
        let center = (startPoint + endPoint) * 0.5f
        MeshGen.cylinder 8 radius length material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ delta) * Matrix4x4.CreateTranslation center)

    let private taperedBody material (bottom: Vector3) (top: Vector3) =
        let delta = top - bottom
        let length = delta.Length()
        let profile =
            [| Vector2(0.17f, -length * 0.50f)
               Vector2(0.24f, -length * 0.34f)
               Vector2(0.27f, length * 0.25f)
               Vector2(0.22f, length * 0.50f) |]
        MeshGen.lathe 10 profile material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ delta) * Matrix4x4.CreateTranslation((bottom + top) * 0.5f))

    let private head center =
        MeshGen.lathe 10
            [| Vector2(0.03f, -0.17f); Vector2(0.13f, -0.13f); Vector2(0.16f, -0.02f)
               Vector2(0.15f, 0.10f); Vector2(0.09f, 0.17f); Vector2(0.02f, 0.19f) |]
            Skin
        // System.Numerics is row-vector: CreateRotationX(+pi/2) maps the lathe
        // axis +Z to -Y and builds the dome upside down. -pi/2 maps +Z to +Y.
        |> MeshGen.rotateX (-MathF.PI * 0.5f)
        |> MeshGen.translate center

    let private helmet material center =
        MeshGen.lathe 12
            [| Vector2(0.19f, -0.035f); Vector2(0.19f, 0.01f); Vector2(0.17f, 0.08f)
               Vector2(0.11f, 0.14f); Vector2(0.02f, 0.17f) |]
            material
        // Same row-vector convention as the head: -pi/2 puts the crown on top.
        |> MeshGen.rotateX (-MathF.PI * 0.5f)
        |> MeshGen.translate center

    let private localSkeleton crouch stride =
        let opposite = -stride
        let pelvis = Vector3(0.0f, 0.90f - crouch, 0.0f)
        let chest = Vector3(0.0f, 1.43f - crouch, -0.015f)
        { Pelvis = pelvis
          Chest = chest
          Neck = Vector3(0.0f, 1.51f - crouch, -0.02f)
          Head = Vector3(0.0f, 1.68f - crouch, -0.025f)
          LeftHip = pelvis + Vector3(-0.12f, -0.02f, 0.0f)
          RightHip = pelvis + Vector3(0.12f, -0.02f, 0.0f)
          LeftKnee = Vector3(-0.13f, 0.49f - crouch * 0.55f, stride)
          RightKnee = Vector3(0.13f, 0.49f - crouch * 0.55f, opposite)
          LeftAnkle = Vector3(-0.13f, 0.10f, -stride * 0.72f)
          RightAnkle = Vector3(0.13f, 0.10f, -opposite * 0.72f)
          LeftShoulder = chest + Vector3(-0.26f, -0.01f, 0.0f)
          RightShoulder = chest + Vector3(0.26f, -0.01f, 0.0f)
          LeftElbow = Vector3(-0.25f, 1.19f - crouch, -0.20f)
          RightElbow = Vector3(0.27f, 1.18f - crouch, -0.10f)
          LeftHand = Vector3(-0.11f, 1.28f - crouch, -0.46f)
          RightHand = Vector3(0.10f, 1.27f - crouch, -0.31f) }

    /// Everything except the head, helmet, and any held weapon, built directly
    /// between the skeleton's joints so it works posed or ragdolled.
    let private bodyParts uniform (s: Skeleton) =
        [| taperedBody uniform s.Pelvis s.Chest
           MeshGen.box (Vector3(0.34f, 0.20f, 0.25f)) uniform |> MeshGen.translate s.Pelvis
           segment 0.085f uniform s.LeftHip s.LeftKnee
           segment 0.078f uniform s.LeftKnee s.LeftAnkle
           segment 0.085f uniform s.RightHip s.RightKnee
           segment 0.078f uniform s.RightKnee s.RightAnkle
           MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (s.LeftAnkle + Vector3(0.0f, -0.045f, -0.10f))
           MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (s.RightAnkle + Vector3(0.0f, -0.045f, -0.10f))
           segment 0.075f uniform s.LeftShoulder s.LeftElbow
           segment 0.068f uniform s.LeftElbow s.LeftHand
           segment 0.075f uniform s.RightShoulder s.RightElbow
           segment 0.068f uniform s.RightElbow s.RightHand
           segment 0.075f Skin (s.LeftHand - Vector3(0.0f, 0.0f, 0.04f)) (s.LeftHand + Vector3(0.0f, 0.0f, 0.08f))
           segment 0.075f Skin (s.RightHand - Vector3(0.0f, 0.0f, 0.04f)) (s.RightHand + Vector3(0.0f, 0.0f, 0.08f))
           segment 0.08f uniform s.Chest s.Neck |]

    let private cutCap radius direction point =
        MeshGen.cylinder 12 (radius * 1.04f) 0.024f PaintRed
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ direction) * Matrix4x4.CreateTranslation point)

    /// Same authored body, except the contacted segment is replaced by two
    /// independently moving halves and wet caps at their new endpoints.
    let private bodyPartsCut uniform (descriptor: CutDescriptor) proximal distal (s: Skeleton) =
        let split site radius material a b =
            if descriptor.Site = site then
                let direction = MathEx.normalizedOrZero (b - a)
                [| segment radius material a proximal
                   segment radius material distal b
                   cutCap radius direction proximal
                   cutCap radius -direction distal |]
            else [| segment radius material a b |]
        let torso =
            if descriptor.Site = CutWaist then split CutWaist 0.22f uniform s.Pelvis s.Chest
            else [| taperedBody uniform s.Pelvis s.Chest |]
        let neckCut =
            if descriptor.Site = CutNeck then split CutNeck 0.105f Skin s.Neck s.Head
            else [||]
        [| yield! torso
           MeshGen.box (Vector3(0.34f, 0.20f, 0.25f)) uniform |> MeshGen.translate s.Pelvis
           yield! split CutLeftUpperLeg 0.085f uniform s.LeftHip s.LeftKnee
           yield! split CutLeftLowerLeg 0.078f uniform s.LeftKnee s.LeftAnkle
           yield! split CutRightUpperLeg 0.085f uniform s.RightHip s.RightKnee
           yield! split CutRightLowerLeg 0.078f uniform s.RightKnee s.RightAnkle
           MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (s.LeftAnkle + Vector3(0.0f, -0.045f, -0.10f))
           MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (s.RightAnkle + Vector3(0.0f, -0.045f, -0.10f))
           yield! split CutLeftUpperArm 0.075f uniform s.LeftShoulder s.LeftElbow
           yield! split CutLeftLowerArm 0.068f uniform s.LeftElbow s.LeftHand
           yield! split CutRightUpperArm 0.075f uniform s.RightShoulder s.RightElbow
           yield! split CutRightLowerArm 0.068f uniform s.RightElbow s.RightHand
           segment 0.075f Skin (s.LeftHand - Vector3(0.0f, 0.0f, 0.04f)) (s.LeftHand + Vector3(0.0f, 0.0f, 0.08f))
           segment 0.075f Skin (s.RightHand - Vector3(0.0f, 0.0f, 0.04f)) (s.RightHand + Vector3(0.0f, 0.0f, 0.08f))
           segment 0.08f uniform s.Chest s.Neck
           yield! neckCut |]

    let private assemble uniform headless (s: Skeleton) extras =
        let parts = Array.append (bodyParts uniform s) extras
        if headless then MeshGen.union parts
        else
            Array.append parts [| head s.Head; helmet uniform (s.Head + Vector3(0.0f, 0.12f, 0.0f)) |]
            |> MeshGen.union

    let pose (soldier: Soldier) =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let death =
            match soldier.Behavior with
            | Dying time
            | DyingHeadshot time -> MathEx.clamp01 (Units.raw time / 0.7f)
            | _ -> 0.0f
        let headless = match soldier.Behavior with DyingHeadshot _ -> true | _ -> false
        let crouch = match soldier.Behavior with InCover(cover, _) when cover.Crouch -> 0.34f | _ -> 0.0f
        let stride = MathF.Sin soldier.AnimPhase * 0.15f * (1.0f - death)
        let skeleton = localSkeleton crouch stride
        let heldWeapon =
            if soldier.Weapon.Class.Kind = MachineGun then
                MeshGen.union
                    [| MeshGen.box (Vector3(0.15f, 0.15f, 0.58f)) Metal |> MeshGen.translate (Vector3(0.0f, 1.24f - crouch, -0.48f))
                       MeshGen.cylinder 10 0.032f 0.78f Metal |> MeshGen.translate (Vector3(0.0f, 1.27f - crouch, -1.14f))
                       MeshGen.box (Vector3(0.18f, 0.14f, 0.26f)) Wood |> MeshGen.translate (Vector3(0.0f, 1.22f - crouch, -0.05f)) |]
            else
                MeshGen.union
                    [| MeshGen.box (Vector3(0.10f, 0.10f, 0.70f)) Wood |> MeshGen.translate (Vector3(0.0f, 1.27f - crouch, -0.37f))
                       MeshGen.cylinder 8 0.025f 0.56f Metal |> MeshGen.translate (Vector3(0.0f, 1.30f - crouch, -0.98f)) |]
        // A shouldered period weapon gives the silhouette an immediate WWII read.
        let local = assemble uniform headless skeleton [| heldWeapon |]
        let worldTransform =
            Matrix4x4.CreateRotationX(death * MathF.PI * 0.5f)
            * Matrix4x4.CreateRotationY(-soldier.Facing)
            * Matrix4x4.CreateTranslation(soldier.Position + Vector3(0.0f, death * 0.25f, 0.0f))
        MeshGen.transform worldTransform local

    let mapSkeleton f (s: Skeleton) =
        { Pelvis = f s.Pelvis; Chest = f s.Chest; Neck = f s.Neck; Head = f s.Head
          LeftHip = f s.LeftHip; RightHip = f s.RightHip
          LeftKnee = f s.LeftKnee; RightKnee = f s.RightKnee
          LeftAnkle = f s.LeftAnkle; RightAnkle = f s.RightAnkle
          LeftShoulder = f s.LeftShoulder; RightShoulder = f s.RightShoulder
          LeftElbow = f s.LeftElbow; RightElbow = f s.RightElbow
          LeftHand = f s.LeftHand; RightHand = f s.RightHand }

    /// Standing-pose joints in world space: the seed for a client-side ragdoll
    /// starting from wherever the soldier actually was when they died.
    let worldSkeleton (soldier: Soldier) =
        let crouch = match soldier.Behavior with InCover(cover, _) when cover.Crouch -> 0.34f | _ -> 0.0f
        let transform =
            Matrix4x4.CreateRotationY(-soldier.Facing) * Matrix4x4.CreateTranslation soldier.Position
        localSkeleton crouch (MathF.Sin soldier.AnimPhase * 0.15f)
        |> mapSkeleton (fun joint -> Vector3.Transform(joint, transform))

    /// Corpse mesh from ragdoll-driven world-space joints. No held weapon —
    /// the body dropped it.
    let poseFromSkeleton (soldier: Soldier) (skeleton: Skeleton) =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let headless = match soldier.Behavior with DyingHeadshot _ -> true | _ -> false
        assemble uniform headless skeleton [||]

    let poseFromSkeletonCut (soldier: Soldier) (skeleton: Skeleton) descriptor proximal distal =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let pieces = bodyPartsCut uniform descriptor proximal distal skeleton
        Array.append pieces [| head skeleton.Head; helmet uniform (skeleton.Head + Vector3(0.0f, 0.12f, 0.0f)) |]
        |> MeshGen.union

    let mesh soldiers =
        let combined = soldiers |> Array.map pose |> MeshGen.union
        combined.Vertices, combined.Indices
