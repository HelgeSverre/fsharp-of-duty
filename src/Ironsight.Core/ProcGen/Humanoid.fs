namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module Humanoid =
    let private rotationFromZ (direction: Vector3) =
        let target = MathEx.normalizedOrZero direction
        let dot = Vector3.Dot(Vector3.UnitZ, target)
        if dot < -0.9999f then Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
        else
            let axis = Vector3.Cross(Vector3.UnitZ, target)
            Quaternion.Normalize(Quaternion(axis.X, axis.Y, axis.Z, 1.0f + dot))

    let private segment radius material (startPoint: Vector3) (endPoint: Vector3) =
        let delta = endPoint - startPoint
        let length = max 0.01f (delta.Length())
        let center = (startPoint + endPoint) * 0.5f
        MeshGen.cylinder 8 radius length material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(rotationFromZ delta) * Matrix4x4.CreateTranslation center)

    let private taperedBody material (bottom: Vector3) (top: Vector3) =
        let delta = top - bottom
        let length = delta.Length()
        let profile =
            [| Vector2(0.17f, -length * 0.50f)
               Vector2(0.24f, -length * 0.34f)
               Vector2(0.27f, length * 0.25f)
               Vector2(0.22f, length * 0.50f) |]
        MeshGen.lathe 10 profile material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(rotationFromZ delta) * Matrix4x4.CreateTranslation((bottom + top) * 0.5f))

    let private head center =
        MeshGen.lathe 10
            [| Vector2(0.03f, -0.17f); Vector2(0.13f, -0.13f); Vector2(0.16f, -0.02f)
               Vector2(0.15f, 0.10f); Vector2(0.09f, 0.17f); Vector2(0.02f, 0.19f) |]
            Skin
        |> MeshGen.rotateX (MathF.PI * 0.5f)
        |> MeshGen.translate center

    let private helmet material center =
        MeshGen.lathe 12
            [| Vector2(0.19f, -0.035f); Vector2(0.19f, 0.01f); Vector2(0.17f, 0.08f)
               Vector2(0.11f, 0.14f); Vector2(0.02f, 0.17f) |]
            material
        |> MeshGen.rotateX (MathF.PI * 0.5f)
        |> MeshGen.translate center

    let pose (soldier: Soldier) =
        let uniform = if soldier.Team = Allies then UniformOlive else UniformFeldgrau
        let death =
            match soldier.Behavior with
            | Dying time
            | DyingHeadshot time -> MathEx.clamp01 (Units.raw time / 0.7f)
            | _ -> 0.0f
        let headless = match soldier.Behavior with DyingHeadshot _ -> true | _ -> false
        let crouch = match soldier.Behavior with InCover(cover, _) when cover.Crouch -> 0.34f | _ -> 0.0f
        let stride = MathF.Sin soldier.AnimPhase * 0.15f
        let opposite = -stride
        let pelvis = Vector3(0.0f, 0.90f - crouch, 0.0f)
        let chest = Vector3(0.0f, 1.43f - crouch, -0.015f)
        let neck = Vector3(0.0f, 1.51f - crouch, -0.02f)
        let headCenter = Vector3(0.0f, 1.68f - crouch, -0.025f)
        let leftHip, rightHip = pelvis + Vector3(-0.12f, -0.02f, 0.0f), pelvis + Vector3(0.12f, -0.02f, 0.0f)
        let leftKnee = Vector3(-0.13f, 0.49f - crouch * 0.55f, stride)
        let rightKnee = Vector3(0.13f, 0.49f - crouch * 0.55f, opposite)
        let leftAnkle = Vector3(-0.13f, 0.10f, -stride * 0.72f)
        let rightAnkle = Vector3(0.13f, 0.10f, -opposite * 0.72f)
        let leftShoulder = chest + Vector3(-0.26f, -0.01f, 0.0f)
        let rightShoulder = chest + Vector3(0.26f, -0.01f, 0.0f)
        let leftElbow = Vector3(-0.25f, 1.19f - crouch, -0.20f)
        let rightElbow = Vector3(0.27f, 1.18f - crouch, -0.10f)
        let leftHand = Vector3(-0.11f, 1.28f - crouch, -0.46f)
        let rightHand = Vector3(0.10f, 1.27f - crouch, -0.31f)
        let heldWeapon =
            if soldier.Weapon.Class.Name = "MG42" then
                MeshGen.union
                    [| MeshGen.box (Vector3(0.15f, 0.15f, 0.58f)) Metal |> MeshGen.translate (Vector3(0.0f, 1.24f - crouch, -0.48f))
                       MeshGen.cylinder 10 0.032f 0.78f Metal |> MeshGen.translate (Vector3(0.0f, 1.27f - crouch, -1.14f))
                       MeshGen.box (Vector3(0.18f, 0.14f, 0.26f)) Wood |> MeshGen.translate (Vector3(0.0f, 1.22f - crouch, -0.05f)) |]
            else
                MeshGen.union
                    [| MeshGen.box (Vector3(0.10f, 0.10f, 0.70f)) Wood |> MeshGen.translate (Vector3(0.0f, 1.27f - crouch, -0.37f))
                       MeshGen.cylinder 8 0.025f 0.56f Metal |> MeshGen.translate (Vector3(0.0f, 1.30f - crouch, -0.98f)) |]
        let bodyParts =
            [| taperedBody uniform pelvis chest
               MeshGen.box (Vector3(0.34f, 0.20f, 0.25f)) uniform |> MeshGen.translate pelvis
               segment 0.085f uniform leftHip leftKnee
               segment 0.078f uniform leftKnee leftAnkle
               segment 0.085f uniform rightHip rightKnee
               segment 0.078f uniform rightKnee rightAnkle
               MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (leftAnkle + Vector3(0.0f, -0.045f, -0.10f))
               MeshGen.box (Vector3(0.18f, 0.10f, 0.34f)) uniform |> MeshGen.translate (rightAnkle + Vector3(0.0f, -0.045f, -0.10f))
               segment 0.075f uniform leftShoulder leftElbow
               segment 0.068f uniform leftElbow leftHand
               segment 0.075f uniform rightShoulder rightElbow
               segment 0.068f uniform rightElbow rightHand
               segment 0.075f Skin (leftHand - Vector3(0.0f, 0.0f, 0.04f)) (leftHand + Vector3(0.0f, 0.0f, 0.08f))
               segment 0.075f Skin (rightHand - Vector3(0.0f, 0.0f, 0.04f)) (rightHand + Vector3(0.0f, 0.0f, 0.08f))
               segment 0.08f uniform chest neck
               // A shouldered period weapon gives the silhouette an immediate WWII read.
               heldWeapon |]
        let local =
            if headless then MeshGen.union bodyParts
            else
                Array.append bodyParts [| head headCenter; helmet uniform (headCenter + Vector3(0.0f, 0.12f, 0.0f)) |]
                |> MeshGen.union
        let worldTransform =
            Matrix4x4.CreateRotationX(death * 1.35f)
            * Matrix4x4.CreateRotationY(soldier.Facing)
            * Matrix4x4.CreateTranslation(soldier.Position + Vector3(0.0f, death * 0.12f, 0.0f))
        MeshGen.transform worldTransform local

    let mesh soldiers =
        let combined = soldiers |> Array.map pose |> MeshGen.union
        combined.Vertices, combined.Indices
