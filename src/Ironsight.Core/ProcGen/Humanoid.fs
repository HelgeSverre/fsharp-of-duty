namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

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

    let private stableCutNormal axis requested =
        let axis = MathEx.normalizedOrZero axis
        let requested = MathEx.normalizedOrZero requested
        if requested = Vector3.Zero then axis
        else
            let alignment = Vector3.Dot(requested, axis)
            if MathF.Abs alignment >= 0.38f then requested
            else
                let perpendicular = requested - axis * alignment |> MathEx.normalizedOrZero
                let sign = if alignment < 0.0f then -1.0f else 1.0f
                axis * (0.38f * sign) + perpendicular * MathF.Sqrt(1.0f - 0.38f * 0.38f)
                |> MathEx.normalizedOrZero

    /// A cylindrical body half whose exposed ring is the intersection with the
    /// real sword plane. Each ring vertex slides along the limb axis until it is
    /// coplanar, producing an oblique ellipse instead of a perpendicular decal.
    let private cutHalf intactRadius cutRadius material intactPoint cutPoint requestedNormal =
        let count = 12
        let axis = MathEx.normalizedOrZero (cutPoint - intactPoint)
        let planeNormal = stableCutNormal axis requestedNormal
        let capNormal = if Vector3.Dot(planeNormal, axis) >= 0.0f then planeNormal else -planeNormal
        let helper = if MathF.Abs axis.Y < 0.9f then Vector3.UnitY else Vector3.UnitX
        let tangent = Vector3.Cross(axis, helper) |> MathEx.normalizedOrZero
        let bitangent = Vector3.Cross(axis, tangent) |> MathEx.normalizedOrZero
        let sideVertices = ResizeArray<MeshVertex>()
        let sideIndices = ResizeArray<uint32>()
        let goreVertices = ResizeArray<MeshVertex>()
        let goreIndices = ResizeArray<uint32>()
        let ring angle =
            let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
            let intactOffset = radial * intactRadius
            let cutOffset = radial * cutRadius
            let slide = -Vector3.Dot(cutOffset, capNormal) / Vector3.Dot(axis, capNormal)
            intactPoint + intactOffset, cutPoint + cutOffset + axis * slide, radial
        for index in 0..count - 1 do
            let angle0 = float32 index / float32 count * MathF.Tau
            let angle1 = float32 (index + 1) / float32 count * MathF.Tau
            let start0, cut0, normal0 = ring angle0
            let start1, cut1, normal1 = ring angle1
            let sideStart = uint32 sideVertices.Count
            for position, normal in [| start0, normal0; start1, normal1; cut1, normal1; cut0, normal0 |] do
                sideVertices.Add { Position = position; Normal = normal; TexCoord = Vector2.Zero; MaterialId = Materials.id material }
            sideIndices.Add sideStart; sideIndices.Add(sideStart + 1u); sideIndices.Add(sideStart + 2u)
            sideIndices.Add sideStart; sideIndices.Add(sideStart + 2u); sideIndices.Add(sideStart + 3u)
            let baseStart = uint32 sideVertices.Count
            sideVertices.Add { Position = intactPoint; Normal = -axis; TexCoord = Vector2.Zero; MaterialId = Materials.id material }
            sideVertices.Add { Position = start1; Normal = -axis; TexCoord = Vector2.Zero; MaterialId = Materials.id material }
            sideVertices.Add { Position = start0; Normal = -axis; TexCoord = Vector2.Zero; MaterialId = Materials.id material }
            sideIndices.Add baseStart; sideIndices.Add(baseStart + 1u); sideIndices.Add(baseStart + 2u)
            let capStart = uint32 goreVertices.Count
            goreVertices.Add { Position = cutPoint; Normal = capNormal; TexCoord = Vector2.Zero; MaterialId = Materials.id PaintRed }
            goreVertices.Add { Position = cut0; Normal = capNormal; TexCoord = Vector2.Zero; MaterialId = Materials.id PaintRed }
            goreVertices.Add { Position = cut1; Normal = capNormal; TexCoord = Vector2.Zero; MaterialId = Materials.id PaintRed }
            goreIndices.Add capStart; goreIndices.Add(capStart + 1u); goreIndices.Add(capStart + 2u)
        MeshGen.union
            [| { Vertices = sideVertices.ToArray(); Indices = sideIndices.ToArray() }
               { Vertices = goreVertices.ToArray(); Indices = goreIndices.ToArray() } |]

    /// Same authored body, except the contacted segment is replaced by two
    /// independently moving halves and wet caps at their new endpoints.
    let private bodyPartsCut uniform (descriptor: CutDescriptor) proximalPlane distalPlane proximal distal (s: Skeleton) =
        let split site radius material a b =
            if descriptor.Site = site then
                [| cutHalf radius radius material a proximal proximalPlane
                   cutHalf radius radius material b distal distalPlane |]
            else [| segment radius material a b |]
        let torso =
            if descriptor.Site = CutWaist then
                let waistRadius = 0.18f + MathF.Sin(descriptor.Fraction * MathF.PI) * 0.055f
                [| cutHalf 0.17f waistRadius uniform s.Pelvis proximal proximalPlane
                   cutHalf 0.22f waistRadius uniform s.Chest distal distalPlane |]
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
        let stance = Anatomy.effectiveStance soldier
        let crouch = Anatomy.stanceDrop stance
        let strideScale = if stance = Prone then 0.35f else 1.0f
        let stride = MathF.Sin soldier.AnimPhase * 0.15f * strideScale * (1.0f - death)
        let skeleton = Anatomy.localSkeletonWithStride stance stride
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

    let mapSkeleton f (s: Skeleton) = Anatomy.map f s

    /// Standing-pose joints in world space: the seed for a client-side ragdoll
    /// starting from wherever the soldier actually was when they died.
    let worldSkeleton (soldier: Soldier) =
        let stance = Anatomy.effectiveStance soldier
        Anatomy.worldSkeleton soldier.Position soldier.Facing stance soldier.AnimPhase

    /// Corpse mesh from ragdoll-driven world-space joints. No held weapon —
    /// the body dropped it.
    let poseFromSkeleton (soldier: Soldier) (skeleton: Skeleton) =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let headless = match soldier.Behavior with DyingHeadshot _ -> true | _ -> false
        assemble uniform headless skeleton [||]

    let poseFromSkeletonCut (soldier: Soldier) (skeleton: Skeleton) (descriptor: CutDescriptor) proximal distal =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let stance = Anatomy.effectiveStance soldier
        let localPose = Anatomy.localSkeleton stance soldier.AnimPhase
        let startJoint, endJoint = Anatomy.cutRelation descriptor.Site
        let localAxis = Anatomy.point endJoint localPose - Anatomy.point startJoint localPose
        let deathRotation = Matrix4x4.CreateRotationY(-soldier.Facing)
        let initialAxis = Vector3.TransformNormal(localAxis, deathRotation)
        let deathPlane = Vector3.TransformNormal(descriptor.LocalPlaneNormal, deathRotation)
        let proximalAxis = proximal - Anatomy.point startJoint skeleton
        let distalAxis = Anatomy.point endJoint skeleton - distal
        let proximalPlane =
            Vector3.Transform(deathPlane, MathEx.rotationBetween initialAxis proximalAxis)
            |> MathEx.normalizedOrZero
        let distalPlane =
            Vector3.Transform(deathPlane, MathEx.rotationBetween initialAxis distalAxis)
            |> MathEx.normalizedOrZero
        let pieces = bodyPartsCut uniform descriptor proximalPlane distalPlane proximal distal skeleton
        Array.append pieces [| head skeleton.Head; helmet uniform (skeleton.Head + Vector3(0.0f, 0.12f, 0.0f)) |]
        |> MeshGen.union

    let mesh soldiers =
        let combined = soldiers |> Array.map pose |> MeshGen.union
        combined.Vertices, combined.Indices
