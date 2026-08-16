namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module Guns =
    let private placed position mesh = MeshGen.translate position mesh

    let private rotationFromZ (direction: Vector3) =
        let target = MathEx.normalizedOrZero direction
        let dot = Vector3.Dot(Vector3.UnitZ, target)
        if dot < -0.9999f then Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI)
        else
            let axis = Vector3.Cross(Vector3.UnitZ, target)
            Quaternion.Normalize(Quaternion(axis.X, axis.Y, axis.Z, 1.0f + dot))

    let private limb radius material (startPoint: Vector3) (endPoint: Vector3) =
        let delta = endPoint - startPoint
        MeshGen.cylinder 9 radius (max 0.01f (delta.Length())) material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(rotationFromZ delta) * Matrix4x4.CreateTranslation((startPoint + endPoint) * 0.5f))

    let private rifleArms =
        MeshGen.union
            [| limb 0.115f UniformOlive (Vector3(0.34f, -0.62f, 0.34f)) (Vector3(0.18f, -0.24f, -0.04f))
               limb 0.082f Skin (Vector3(0.18f, -0.24f, -0.04f)) (Vector3(0.07f, -0.08f, -0.25f))
               limb 0.108f UniformOlive (Vector3(-0.43f, -0.58f, 0.04f)) (Vector3(-0.25f, -0.23f, -0.34f))
               limb 0.080f Skin (Vector3(-0.25f, -0.23f, -0.34f)) (Vector3(-0.07f, -0.04f, -0.57f)) |]

    let private pistolArms =
        MeshGen.union
            [| limb 0.115f UniformOlive (Vector3(0.32f, -0.63f, 0.30f)) (Vector3(0.13f, -0.24f, -0.02f))
               limb 0.084f Skin (Vector3(0.13f, -0.24f, -0.02f)) (Vector3(0.04f, -0.08f, -0.20f))
               limb 0.110f UniformOlive (Vector3(-0.32f, -0.62f, 0.24f)) (Vector3(-0.11f, -0.25f, -0.02f))
               limb 0.082f Skin (Vector3(-0.11f, -0.25f, -0.02f)) (Vector3(-0.03f, -0.07f, -0.22f)) |]

    let private kar98k =
        MeshGen.union
            [| MeshGen.wedge (Vector3(0.23f, 0.28f, 0.72f)) Wood |> placed (Vector3(0.0f, -0.09f, 0.08f))
               MeshGen.box (Vector3(0.24f, 0.29f, 0.035f)) Metal |> placed (Vector3(0.0f, -0.09f, 0.455f))
               MeshGen.box (Vector3(0.16f, 0.16f, 0.30f)) Metal |> placed (Vector3(0.0f, 0.02f, -0.45f))
               MeshGen.box (Vector3(0.14f, 0.12f, 0.46f)) Wood |> placed (Vector3(0.0f, 0.0f, -0.71f))
               MeshGen.cylinder 10 0.035f 0.72f Metal |> placed (Vector3(0.0f, 0.07f, -0.94f))
               MeshGen.box (Vector3(0.035f, 0.10f, 0.035f)) Metal |> placed (Vector3(0.0f, 0.15f, -1.25f))
               MeshGen.box (Vector3(0.035f, 0.08f, 0.08f)) Metal |> placed (Vector3(0.0f, 0.13f, -0.38f))
               MeshGen.cylinder 8 0.025f 0.20f Metal |> MeshGen.rotateX 1.5708f |> placed (Vector3(0.12f, 0.02f, -0.38f))
               MeshGen.box (Vector3(0.10f, 0.34f, 0.16f)) Wood |> MeshGen.rotateX -0.28f |> placed (Vector3(0.0f, -0.18f, 0.20f)) |]

    let private kar98kSniper =
        MeshGen.union
            [| kar98k
               // Scope tube, objective/ocular bells, adjustment turret and mounts.
               MeshGen.cylinder 14 0.058f 0.54f Metal |> placed (Vector3(0.0f, 0.20f, -0.46f))
               MeshGen.cylinder 14 0.082f 0.10f Metal |> placed (Vector3(0.0f, 0.20f, -0.75f))
               MeshGen.cylinder 14 0.073f 0.09f Metal |> placed (Vector3(0.0f, 0.20f, -0.17f))
               MeshGen.cylinder 10 0.032f 0.10f Metal
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.285f, -0.43f))
               MeshGen.box (Vector3(0.045f, 0.12f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.125f, -0.62f))
               MeshGen.box (Vector3(0.045f, 0.12f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.125f, -0.30f)) |]

    let private thompson =
        MeshGen.union
            [| MeshGen.box (Vector3(0.17f, 0.18f, 0.42f)) Metal |> placed (Vector3(0.0f, 0.03f, -0.32f))
               MeshGen.cylinder 10 0.045f 0.58f Metal |> placed (Vector3(0.0f, 0.06f, -0.82f))
               MeshGen.box (Vector3(0.16f, 0.26f, 0.18f)) Wood |> MeshGen.rotateX -0.35f |> placed (Vector3(0.0f, -0.15f, -0.42f))
               MeshGen.box (Vector3(0.13f, 0.46f, 0.12f)) Metal |> placed (Vector3(0.0f, -0.19f, -0.31f))
               MeshGen.box (Vector3(0.20f, 0.25f, 0.55f)) Wood |> MeshGen.rotateX 0.15f |> placed (Vector3(0.0f, -0.06f, 0.18f))
               MeshGen.box (Vector3(0.055f, 0.10f, 0.035f)) Metal |> placed (Vector3(0.0f, 0.18f, -0.54f)) |]

    let private m1911 =
        MeshGen.union
            [| MeshGen.box (Vector3(0.16f, 0.15f, 0.48f)) Metal |> placed (Vector3(0.0f, 0.05f, -0.28f))
               MeshGen.box (Vector3(0.13f, 0.28f, 0.18f)) Wood |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.15f, -0.08f))
               MeshGen.cylinder 10 0.035f 0.24f Metal |> placed (Vector3(0.0f, 0.06f, -0.62f))
               MeshGen.box (Vector3(0.08f, 0.08f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.16f, -0.12f)) |]

    let private m1897 =
        MeshGen.union
            [| // Walnut stock and a long ribbed pump distinguish the trench gun silhouette.
               MeshGen.wedge (Vector3(0.25f, 0.30f, 0.74f)) Wood |> placed (Vector3(0.0f, -0.10f, 0.14f))
               MeshGen.box (Vector3(0.19f, 0.20f, 0.43f)) Metal |> placed (Vector3(0.0f, 0.02f, -0.33f))
               MeshGen.cylinder 12 0.043f 0.86f Metal |> placed (Vector3(0.0f, 0.08f, -0.96f))
               MeshGen.cylinder 12 0.034f 0.78f Metal |> placed (Vector3(0.0f, -0.015f, -0.89f))
               MeshGen.box (Vector3(0.23f, 0.17f, 0.34f)) Wood |> placed (Vector3(0.0f, -0.09f, -0.74f))
               MeshGen.box (Vector3(0.055f, 0.09f, 0.045f)) Metal |> placed (Vector3(0.0f, 0.18f, -0.51f))
               MeshGen.box (Vector3(0.045f, 0.075f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.16f, -1.38f))
               MeshGen.box (Vector3(0.11f, 0.32f, 0.15f)) Wood |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, -0.19f, 0.17f)) |]

    let forWeapon name =
        let weapon =
            match name with
            | "Thompson" -> thompson
            | "M1911" -> m1911
            | "M1897 Trench Gun" -> m1897
            | "Kar98k Sniper" -> kar98kSniper
            | _ -> kar98k
        let arms = if name = "M1911" then pistolArms else rifleArms
        MeshGen.union [| weapon; arms |]
