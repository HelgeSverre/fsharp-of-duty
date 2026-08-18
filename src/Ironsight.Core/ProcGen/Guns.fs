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
            [| // Tapered lathe barrel: thick chamber stepping down to a slim muzzle.
               MeshGen.lathe 14
                   [| Vector2(0.050f, -0.35f); Vector2(0.046f, -0.22f); Vector2(0.036f, -0.05f)
                      Vector2(0.028f, 0.20f); Vector2(0.024f, 0.35f) |]
                   Metal
               |> placed (Vector3(0.0f, 0.075f, -0.95f))
               // Cleaning rod tucked under the barrel.
               MeshGen.cylinder 6 0.008f 0.44f Metal |> placed (Vector3(0.0f, 0.015f, -0.96f))
               // Wooden handguard cradling the top half of the barrel.
               MeshGen.box (Vector3(0.12f, 0.06f, 0.34f)) Wood |> placed (Vector3(0.0f, 0.135f, -0.80f))
               // Barrel band and front sight band ring the barrel.
               MeshGen.cylinder 10 0.041f 0.05f Metal |> placed (Vector3(0.0f, 0.075f, -0.66f))
               MeshGen.cylinder 10 0.039f 0.03f Metal |> placed (Vector3(0.0f, 0.075f, -1.22f))
               // Front sight blade.
               MeshGen.box (Vector3(0.02f, 0.10f, 0.02f)) Metal |> placed (Vector3(0.0f, 0.135f, -1.26f))
               // Receiver block and its top bridge.
               MeshGen.box (Vector3(0.15f, 0.16f, 0.34f)) Metal |> placed (Vector3(0.0f, 0.03f, -0.46f))
               MeshGen.box (Vector3(0.13f, 0.05f, 0.20f)) Metal |> placed (Vector3(0.0f, 0.135f, -0.42f))
               // Rear sight ladder.
               MeshGen.box (Vector3(0.02f, 0.06f, 0.02f)) Metal |> placed (Vector3(0.0f, 0.175f, -0.36f))
               // Bolt: body along the receiver, stem out to the right, lathe knob.
               MeshGen.cylinder 8 0.022f 0.30f Metal |> placed (Vector3(0.11f, 0.05f, -0.42f))
               MeshGen.cylinder 8 0.018f 0.14f Metal
               |> MeshGen.rotateY (MathF.PI * 0.5f)
               |> placed (Vector3(0.16f, 0.05f, -0.34f))
               MeshGen.lathe 10
                   [| Vector2(0.0f, -0.03f); Vector2(0.026f, -0.02f); Vector2(0.030f, 0.0f); Vector2(0.020f, 0.02f); Vector2(0.0f, 0.03f) |]
                   Metal
               |> placed (Vector3(0.24f, 0.03f, -0.34f))
               // Trigger guard loop and magazine floor plate.
               MeshGen.box (Vector3(0.10f, 0.02f, 0.16f)) Metal |> placed (Vector3(0.0f, -0.06f, -0.40f))
               MeshGen.box (Vector3(0.02f, 0.07f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.02f, -0.34f))
               MeshGen.box (Vector3(0.02f, 0.07f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.02f, -0.48f))
               // Walnut stock, angled grip and metal buttplate.
               MeshGen.wedge (Vector3(0.22f, 0.30f, 0.60f)) Wood |> placed (Vector3(0.0f, -0.11f, 0.16f))
               MeshGen.box (Vector3(0.10f, 0.30f, 0.16f)) Wood |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, -0.21f, 0.20f))
               MeshGen.box (Vector3(0.20f, 0.26f, 0.03f)) Metal |> placed (Vector3(0.0f, -0.10f, 0.46f)) |]

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
            [| // Receiver with the raised bolt housing on top.
               MeshGen.box (Vector3(0.17f, 0.18f, 0.42f)) Metal |> placed (Vector3(0.0f, 0.03f, -0.32f))
               MeshGen.box (Vector3(0.12f, 0.05f, 0.32f)) Metal |> placed (Vector3(0.0f, 0.14f, -0.30f))
               // Charging knob riding on the bolt housing.
               MeshGen.cylinder 8 0.020f 0.05f Metal |> placed (Vector3(0.0f, 0.185f, -0.22f))
               // Barrel with cooling fins near the chamber.
               MeshGen.cylinder 10 0.045f 0.58f Metal |> placed (Vector3(0.0f, 0.06f, -0.82f))
               yield!
                   [| for index in 0..4 ->
                        MeshGen.cylinder 10 0.058f 0.016f Metal
                        |> placed (Vector3(0.0f, 0.06f, -0.585f - float32 index * 0.034f)) |]
               // Cutts compensator flaring at the muzzle.
               MeshGen.lathe 10
                   [| Vector2(0.046f, -0.055f); Vector2(0.056f, -0.02f); Vector2(0.056f, 0.035f); Vector2(0.038f, 0.055f) |]
                   Metal
               |> placed (Vector3(0.0f, 0.06f, -1.13f))
               // Wooden foregrip and box magazine.
               MeshGen.box (Vector3(0.16f, 0.26f, 0.18f)) Wood |> MeshGen.rotateX -0.35f |> placed (Vector3(0.0f, -0.15f, -0.42f))
               MeshGen.box (Vector3(0.13f, 0.46f, 0.12f)) Metal |> placed (Vector3(0.0f, -0.19f, -0.31f))
               // Distinct pistol grip behind the trigger, ahead of the stock.
               MeshGen.box (Vector3(0.11f, 0.24f, 0.13f)) Wood |> MeshGen.rotateX -0.42f |> placed (Vector3(0.0f, -0.155f, -0.055f))
               MeshGen.box (Vector3(0.20f, 0.25f, 0.55f)) Wood |> MeshGen.rotateX 0.15f |> placed (Vector3(0.0f, -0.06f, 0.18f))
               MeshGen.box (Vector3(0.055f, 0.10f, 0.035f)) Metal |> placed (Vector3(0.0f, 0.18f, -0.54f)) |]

    let private m1911 =
        MeshGen.union
            [| // Frame with a slightly wider slide riding on top.
               MeshGen.box (Vector3(0.14f, 0.09f, 0.44f)) Metal |> placed (Vector3(0.0f, 0.01f, -0.26f))
               MeshGen.box (Vector3(0.16f, 0.09f, 0.48f)) Metal |> placed (Vector3(0.0f, 0.09f, -0.28f))
               // Wooden grip with an exposed hammer spur at the rear.
               MeshGen.box (Vector3(0.13f, 0.28f, 0.18f)) Wood |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.15f, -0.08f))
               MeshGen.box (Vector3(0.05f, 0.07f, 0.03f)) Metal |> MeshGen.rotateX 0.55f |> placed (Vector3(0.0f, 0.115f, 0.005f))
               // Trigger guard loop under the frame.
               MeshGen.box (Vector3(0.10f, 0.02f, 0.13f)) Metal |> placed (Vector3(0.0f, -0.075f, -0.28f))
               MeshGen.box (Vector3(0.02f, 0.05f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.05f, -0.34f))
               MeshGen.box (Vector3(0.02f, 0.05f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.05f, -0.22f))
               // Barrel and front/rear sights.
               MeshGen.cylinder 10 0.035f 0.24f Metal |> placed (Vector3(0.0f, 0.06f, -0.62f))
               MeshGen.box (Vector3(0.025f, 0.04f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.155f, -0.50f))
               MeshGen.box (Vector3(0.08f, 0.05f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.155f, -0.06f)) |]

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
