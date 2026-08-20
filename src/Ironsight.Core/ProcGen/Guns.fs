namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module Guns =
    let private placed position mesh = MeshGen.translate position mesh

    let private limb radius material (startPoint: Vector3) (endPoint: Vector3) =
        let delta = endPoint - startPoint
        MeshGen.cylinder 9 radius (max 0.01f (delta.Length())) material
        |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ delta) * Matrix4x4.CreateTranslation((startPoint + endPoint) * 0.5f))

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
               // Walnut stock: wrist, grip, butt, buttplate. Every piece
               // overlaps the one ahead of it — a `wedge` cannot serve as a
               // butt here because its forward end is a knife edge along the
               // BOTTOM, so the wrist would be hollow exactly where the stock
               // has to meet the receiver.
               MeshGen.box (Vector3(0.16f, 0.15f, 0.52f)) Wood |> placed (Vector3(0.0f, -0.035f, -0.10f))
               MeshGen.box (Vector3(0.11f, 0.22f, 0.15f)) Wood |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, -0.16f, 0.02f))
               MeshGen.box (Vector3(0.20f, 0.27f, 0.42f)) Wood |> MeshGen.rotateX 0.06f |> placed (Vector3(0.0f, -0.09f, 0.26f))
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
               // Long enough to bite into the receiver; at 0.55 the rotated
               // box only just touched its rear face.
               MeshGen.box (Vector3(0.20f, 0.25f, 0.62f)) Wood |> MeshGen.rotateX 0.15f |> placed (Vector3(0.0f, -0.06f, 0.15f))
               MeshGen.box (Vector3(0.055f, 0.10f, 0.035f)) Metal |> placed (Vector3(0.0f, 0.18f, -0.54f)) |]

    let private m1911 =
        MeshGen.union
            [| // Frame with a slightly wider slide riding on top.
               MeshGen.box (Vector3(0.14f, 0.09f, 0.44f)) Metal |> placed (Vector3(0.0f, 0.01f, -0.26f))
               MeshGen.box (Vector3(0.16f, 0.09f, 0.48f)) Metal |> placed (Vector3(0.0f, 0.09f, -0.28f))
               // Wooden grip with an exposed hammer spur at the rear.
               MeshGen.box (Vector3(0.13f, 0.28f, 0.18f)) Wood |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.15f, -0.08f))
               // Sits against the rear of the slide, not floating behind it.
               MeshGen.box (Vector3(0.05f, 0.07f, 0.03f)) Metal |> MeshGen.rotateX 0.55f |> placed (Vector3(0.0f, 0.115f, -0.02f))
               // Trigger guard loop under the frame.
               MeshGen.box (Vector3(0.10f, 0.02f, 0.13f)) Metal |> placed (Vector3(0.0f, -0.075f, -0.28f))
               MeshGen.box (Vector3(0.02f, 0.05f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.05f, -0.34f))
               MeshGen.box (Vector3(0.02f, 0.05f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.05f, -0.22f))
               // Barrel and front/rear sights.
               MeshGen.cylinder 10 0.035f 0.24f Metal |> placed (Vector3(0.0f, 0.06f, -0.62f))
               MeshGen.box (Vector3(0.025f, 0.04f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.155f, -0.50f))
               MeshGen.box (Vector3(0.08f, 0.05f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.155f, -0.06f)) |]

    let private luger =
        MeshGen.union
            [| // Slim frame under a rounded receiver; the whole gun is narrower
               // than the M1911 so the two pistols read differently at a glance.
               MeshGen.box (Vector3(0.11f, 0.08f, 0.34f)) Metal |> placed (Vector3(0.0f, 0.01f, -0.20f))
               MeshGen.cylinder 10 0.048f 0.30f Metal |> placed (Vector3(0.0f, 0.085f, -0.22f))
               // Long thin exposed barrel — no slide wrapping it. Runs back
               // into the receiver; at 0.36 it floated well clear of it.
               MeshGen.cylinder 10 0.026f 0.50f Metal |> placed (Vector3(0.0f, 0.085f, -0.59f))
               // Toggle-lock knuckle: the sideways disc pair at the rear of the
               // receiver is the Luger's signature.
               MeshGen.cylinder 8 0.038f 0.17f Metal |> MeshGen.rotateY (MathF.PI * 0.5f) |> placed (Vector3(0.0f, 0.135f, -0.03f))
               MeshGen.box (Vector3(0.06f, 0.05f, 0.10f)) Metal |> placed (Vector3(0.0f, 0.125f, -0.09f))
               // Steeply raked wooden grip, far more angled than the M1911.
               MeshGen.box (Vector3(0.10f, 0.26f, 0.13f)) Wood |> MeshGen.rotateX -0.60f |> placed (Vector3(0.0f, -0.13f, -0.02f))
               // Trigger guard loop.
               MeshGen.box (Vector3(0.09f, 0.02f, 0.11f)) Metal |> placed (Vector3(0.0f, -0.065f, -0.24f))
               MeshGen.box (Vector3(0.02f, 0.045f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.045f, -0.28f))
               MeshGen.box (Vector3(0.02f, 0.045f, 0.02f)) Metal |> placed (Vector3(0.0f, -0.045f, -0.19f))
               // Front blade at the muzzle, notch on the knuckle hump.
               MeshGen.box (Vector3(0.02f, 0.04f, 0.025f)) Metal |> placed (Vector3(0.0f, 0.13f, -0.82f))
               MeshGen.box (Vector3(0.05f, 0.03f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.165f, -0.03f)) |]

    let private m1897 =
        MeshGen.union
            [| // Walnut stock and a long ribbed pump distinguish the trench gun
               // silhouette. Wrist then butt, both overlapping the receiver
               // chain — see the Kar98k stock for why this is not a wedge.
               // Narrower than the receiver so their side faces do not end up
               // coplanar and z-fighting.
               MeshGen.box (Vector3(0.18f, 0.17f, 0.52f)) Wood |> placed (Vector3(0.0f, -0.02f, -0.02f))
               MeshGen.box (Vector3(0.23f, 0.29f, 0.36f)) Wood |> MeshGen.rotateX 0.06f |> placed (Vector3(0.0f, -0.09f, 0.33f))
               MeshGen.box (Vector3(0.19f, 0.20f, 0.43f)) Metal |> placed (Vector3(0.0f, 0.02f, -0.33f))
               MeshGen.cylinder 12 0.043f 0.86f Metal |> placed (Vector3(0.0f, 0.08f, -0.96f))
               MeshGen.cylinder 12 0.034f 0.78f Metal |> placed (Vector3(0.0f, -0.015f, -0.89f))
               MeshGen.box (Vector3(0.23f, 0.17f, 0.34f)) Wood |> placed (Vector3(0.0f, -0.09f, -0.74f))
               MeshGen.box (Vector3(0.055f, 0.09f, 0.045f)) Metal |> placed (Vector3(0.0f, 0.18f, -0.51f))
               MeshGen.box (Vector3(0.045f, 0.075f, 0.04f)) Metal |> placed (Vector3(0.0f, 0.16f, -1.38f))
               MeshGen.box (Vector3(0.11f, 0.30f, 0.15f)) Wood |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, -0.19f, 0.10f)) |]

    let private m1Garand =
        MeshGen.union
            [| // Full-length walnut stock with the long upper handguard. Wrist
               // and butt are separate overlapping boxes so the stock stays
               // joined to the receiver — see the Kar98k stock for why.
               // Width differs from the receiver's on purpose: matching it
               // exactly would leave the two side faces coplanar and z-fighting.
               MeshGen.box (Vector3(0.16f, 0.14f, 0.50f)) Wood |> placed (Vector3(0.0f, -0.02f, -0.12f))
               MeshGen.box (Vector3(0.10f, 0.22f, 0.15f)) Wood |> MeshGen.rotateX -0.28f |> placed (Vector3(0.0f, -0.15f, 0.02f))
               MeshGen.box (Vector3(0.20f, 0.26f, 0.40f)) Wood |> MeshGen.rotateX 0.06f |> placed (Vector3(0.0f, -0.08f, 0.25f))
               MeshGen.box (Vector3(0.14f, 0.10f, 0.78f)) Wood |> placed (Vector3(0.0f, 0.02f, -0.62f))
               MeshGen.box (Vector3(0.12f, 0.055f, 0.62f)) Wood |> placed (Vector3(0.0f, 0.125f, -0.66f))
               // Receiver hump with the en-bloc clip well and op-rod nub.
               MeshGen.box (Vector3(0.15f, 0.15f, 0.30f)) Metal |> placed (Vector3(0.0f, 0.045f, -0.34f))
               MeshGen.box (Vector3(0.05f, 0.05f, 0.16f)) Metal |> placed (Vector3(0.09f, 0.05f, -0.30f))
               // Barrel, gas cylinder underneath, front sight wings.
               MeshGen.cylinder 12 0.032f 0.44f Metal |> placed (Vector3(0.0f, 0.075f, -1.14f))
               MeshGen.cylinder 8 0.020f 0.30f Metal |> placed (Vector3(0.0f, 0.015f, -1.08f))
               MeshGen.box (Vector3(0.02f, 0.09f, 0.02f)) Metal |> placed (Vector3(0.0f, 0.13f, -1.30f))
               MeshGen.box (Vector3(0.08f, 0.05f, 0.02f)) Metal |> placed (Vector3(0.0f, 0.115f, -1.30f))
               // Rear aperture sight block.
               MeshGen.box (Vector3(0.07f, 0.05f, 0.05f)) Metal |> placed (Vector3(0.0f, 0.145f, -0.24f)) |]

    let private leeEnfield =
        MeshGen.union
            [| kar98k
               // The Enfield silhouette: a protruding 10-round box magazine and
               // the snub nose cap wrapping the muzzle.
               MeshGen.box (Vector3(0.10f, 0.16f, 0.18f)) Metal |> MeshGen.rotateX -0.12f |> placed (Vector3(0.0f, -0.11f, -0.50f))
               MeshGen.box (Vector3(0.13f, 0.11f, 0.10f)) Wood |> placed (Vector3(0.0f, 0.075f, -1.16f)) |]

    let private stg44 =
        MeshGen.union
            [| // Stamped-metal receiver with the gas tube above the barrel.
               MeshGen.box (Vector3(0.15f, 0.17f, 0.46f)) Metal |> placed (Vector3(0.0f, 0.04f, -0.30f))
               MeshGen.cylinder 10 0.035f 0.46f Metal |> placed (Vector3(0.0f, 0.115f, -0.76f))
               // Runs back into the receiver. At 0.34 it reached neither the
               // receiver nor the gas tube above it and floated in mid-air.
               MeshGen.cylinder 10 0.028f 0.53f Metal |> placed (Vector3(0.0f, 0.03f, -0.765f))
               // Long banana magazine, approximated as two angled segments.
               MeshGen.box (Vector3(0.075f, 0.30f, 0.11f)) Metal |> MeshGen.rotateX -0.16f |> placed (Vector3(0.0f, -0.17f, -0.44f))
               // Overlaps the upper segment: rotating each about its own centre
               // walks the lower one away in z as well as y.
               MeshGen.box (Vector3(0.07f, 0.22f, 0.10f)) Metal |> MeshGen.rotateX -0.42f |> placed (Vector3(0.0f, -0.31f, -0.44f))
               // Pistol grip and wooden butt stock.
               MeshGen.box (Vector3(0.10f, 0.22f, 0.12f)) Wood |> MeshGen.rotateX -0.40f |> placed (Vector3(0.0f, -0.15f, -0.04f))
               // Reaches forward into the receiver rather than starting behind it.
               MeshGen.box (Vector3(0.13f, 0.15f, 0.56f)) Wood |> placed (Vector3(0.0f, 0.00f, 0.17f))
               // Hooded front sight and rear ladder.
               MeshGen.box (Vector3(0.05f, 0.09f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.17f, -1.00f))
               MeshGen.box (Vector3(0.06f, 0.05f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.145f, -0.30f)) |]

    let private mp40 =
        MeshGen.union
            [| // Bare tube receiver with a short ribbed barrel and resting bar.
               MeshGen.cylinder 12 0.052f 0.34f Metal |> placed (Vector3(0.0f, 0.05f, -0.26f))
               MeshGen.cylinder 10 0.030f 0.30f Metal |> placed (Vector3(0.0f, 0.05f, -0.58f))
               MeshGen.box (Vector3(0.04f, 0.03f, 0.10f)) Metal |> placed (Vector3(0.0f, -0.01f, -0.66f))
               // Straight vertical magazine doubling as the front grip. Tall
               // enough to reach the round receiver's underside, which sits
               // above y=0 once the tube's curvature is accounted for.
               MeshGen.box (Vector3(0.075f, 0.38f, 0.10f)) Metal |> placed (Vector3(0.0f, -0.17f, -0.36f))
               // Bakelite grip frame and folded stock rails going back.
               MeshGen.box (Vector3(0.10f, 0.20f, 0.11f)) Wood |> MeshGen.rotateX -0.35f |> placed (Vector3(0.0f, -0.13f, -0.02f))
               MeshGen.box (Vector3(0.12f, 0.10f, 0.16f)) Metal |> placed (Vector3(0.0f, 0.01f, 0.02f))
               MeshGen.cylinder 6 0.014f 0.30f Metal |> MeshGen.rotateX 0.10f |> placed (Vector3(0.05f, 0.03f, 0.20f))
               MeshGen.cylinder 6 0.014f 0.30f Metal |> MeshGen.rotateX 0.10f |> placed (Vector3(-0.05f, 0.03f, 0.20f))
               // Front post and rear notch sights.
               MeshGen.box (Vector3(0.02f, 0.06f, 0.02f)) Metal |> placed (Vector3(0.0f, 0.11f, -0.70f))
               MeshGen.box (Vector3(0.05f, 0.04f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.12f, -0.16f)) |]

    let private fg42 =
        MeshGen.union
            [| // Straight-line receiver and stock, barrel on the bore axis.
               MeshGen.box (Vector3(0.14f, 0.15f, 0.52f)) Metal |> placed (Vector3(0.0f, 0.04f, -0.28f))
               // Stock and barrel both run into the receiver; at their old
               // lengths each stopped just short of it, leaving a visible gap.
               MeshGen.box (Vector3(0.12f, 0.13f, 0.44f)) Wood |> placed (Vector3(0.0f, 0.03f, 0.16f))
               MeshGen.cylinder 12 0.032f 0.62f Metal |> placed (Vector3(0.0f, 0.05f, -0.83f))
               // Signature side-mounted magazine sticking out to the left.
               MeshGen.box (Vector3(0.26f, 0.075f, 0.11f)) Metal |> placed (Vector3(-0.20f, 0.05f, -0.36f))
               // Sharply raked pistol grip.
               MeshGen.box (Vector3(0.09f, 0.22f, 0.11f)) Metal |> MeshGen.rotateX -0.62f |> placed (Vector3(0.0f, -0.13f, -0.10f))
               // Scope tube with bells, sitting on the receiver rather than
               // hovering a centimetre above it.
               MeshGen.cylinder 14 0.050f 0.42f Metal |> placed (Vector3(0.0f, 0.160f, -0.34f))
               MeshGen.cylinder 14 0.068f 0.08f Metal |> placed (Vector3(0.0f, 0.160f, -0.57f))
               MeshGen.cylinder 14 0.062f 0.08f Metal |> placed (Vector3(0.0f, 0.160f, -0.11f))
               // Folded bipod legs hugging the barrel, muzzle device up front.
               MeshGen.cylinder 6 0.012f 0.42f Metal |> placed (Vector3(0.045f, 0.005f, -0.86f))
               MeshGen.cylinder 6 0.012f 0.42f Metal |> placed (Vector3(-0.045f, 0.005f, -0.86f))
               MeshGen.lathe 10 [| Vector2(0.034f, -0.05f); Vector2(0.048f, 0.0f); Vector2(0.036f, 0.05f) |] Metal
               |> placed (Vector3(0.0f, 0.05f, -1.18f)) |]

    let private bar =
        MeshGen.union
            [| // Heavy receiver and long barrel.
               MeshGen.box (Vector3(0.16f, 0.18f, 0.50f)) Metal |> placed (Vector3(0.0f, 0.03f, -0.30f))
               MeshGen.cylinder 12 0.036f 0.62f Metal |> placed (Vector3(0.0f, 0.06f, -0.86f))
               MeshGen.cylinder 10 0.024f 0.40f Metal |> placed (Vector3(0.0f, 0.005f, -0.90f))
               // Bottom 20-round magazine.
               MeshGen.box (Vector3(0.08f, 0.22f, 0.13f)) Metal |> MeshGen.rotateX -0.10f |> placed (Vector3(0.0f, -0.15f, -0.38f))
               // Wooden butt, grip and forearm. Wrist then butt — see the
               // Kar98k stock for why this is not a wedge.
               MeshGen.box (Vector3(0.15f, 0.15f, 0.42f)) Wood |> placed (Vector3(0.0f, -0.01f, -0.06f))
               MeshGen.box (Vector3(0.19f, 0.26f, 0.36f)) Wood |> MeshGen.rotateX 0.06f |> placed (Vector3(0.0f, -0.07f, 0.27f))
               MeshGen.box (Vector3(0.10f, 0.22f, 0.13f)) Wood |> MeshGen.rotateX -0.32f |> placed (Vector3(0.0f, -0.16f, 0.02f))
               MeshGen.box (Vector3(0.15f, 0.10f, 0.26f)) Wood |> placed (Vector3(0.0f, -0.02f, -0.62f))
               // Folded bipod at the muzzle and flash hider.
               MeshGen.cylinder 6 0.013f 0.46f Metal |> placed (Vector3(0.05f, 0.0f, -1.00f))
               MeshGen.cylinder 6 0.013f 0.46f Metal |> placed (Vector3(-0.05f, 0.0f, -1.00f))
               MeshGen.lathe 10 [| Vector2(0.038f, -0.05f); Vector2(0.050f, 0.0f); Vector2(0.040f, 0.05f) |] Metal
               |> placed (Vector3(0.0f, 0.06f, -1.20f))
               // Rear sight ladder.
               MeshGen.box (Vector3(0.06f, 0.06f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.155f, -0.24f)) |]

    let private minigun =
        // Six barrels on a ring around the spin axis, a slab receiver, a
        // feed chute to the hip box, and spade grips at the back.
        let barrel index =
            let angle = float32 index * MathF.Tau / 6.0f
            MeshGen.cylinder 8 0.026f 0.86f Metal
            |> placed (Vector3(MathF.Cos angle * 0.075f, 0.06f + MathF.Sin angle * 0.075f, -0.92f))
        MeshGen.union
            [| yield! [ for index in 0..5 -> barrel index ]
               // Barrel clamp rings, fore and aft of the cluster.
               yield MeshGen.lathe 12 [| Vector2(0.088f, -0.04f); Vector2(0.105f, 0.0f); Vector2(0.088f, 0.04f) |] Metal
                     |> placed (Vector3(0.0f, 0.06f, -1.28f))
               yield MeshGen.lathe 12 [| Vector2(0.086f, -0.04f); Vector2(0.100f, 0.0f); Vector2(0.086f, 0.04f) |] Metal
                     |> placed (Vector3(0.0f, 0.06f, -0.62f))
               // Receiver housing the rotor, and the motor bulge on its flank.
               yield MeshGen.box (Vector3(0.24f, 0.26f, 0.44f)) Metal |> placed (Vector3(0.0f, 0.05f, -0.28f))
               yield MeshGen.box (Vector3(0.14f, 0.16f, 0.22f)) Metal |> placed (Vector3(0.15f, 0.05f, -0.30f))
               // Flexible feed chute down into the ammo box on the hip.
               yield MeshGen.box (Vector3(0.09f, 0.09f, 0.26f)) Metal |> MeshGen.rotateX -0.55f
                     |> placed (Vector3(-0.13f, -0.10f, -0.16f))
               yield MeshGen.box (Vector3(0.22f, 0.24f, 0.20f)) Metal |> placed (Vector3(-0.17f, -0.24f, -0.02f))
               // Spade grips: crossbar with a handle at each end.
               yield MeshGen.box (Vector3(0.30f, 0.05f, 0.06f)) Metal |> placed (Vector3(0.0f, 0.02f, 0.06f))
               yield MeshGen.cylinder 8 0.028f 0.20f Wood |> MeshGen.rotateX (MathF.PI * 0.5f)
                     |> placed (Vector3(0.13f, -0.04f, 0.12f))
               yield MeshGen.cylinder 8 0.028f 0.20f Wood |> MeshGen.rotateX (MathF.PI * 0.5f)
                     |> placed (Vector3(-0.13f, -0.04f, 0.12f)) |]

    /// Every weapon by name, so callers can iterate the set without repeating
    /// the list. `forWeapon` falls back to the Kar98k for anything unlisted.
    let names =
        [| "Thompson"; "M1911"; "Luger P08"; "M1897 Trench Gun"; "Kar98k"; "Kar98k Sniper"
           "M1 Garand"; "STG-44"; "MP40"; "Lee-Enfield"; "FG42"; "BAR"; "M134 Minigun" |]

    /// The gun alone, without the arms holding it — what the geometry preview
    /// in tools/GunPreview.fsx inspects.
    let meshFor name =
        match name with
        | "Thompson" -> thompson
        | "M1911" -> m1911
        | "Luger P08" -> luger
        | "M1897 Trench Gun" -> m1897
        | "Kar98k Sniper" -> kar98kSniper
        | "M1 Garand" -> m1Garand
        | "STG-44" -> stg44
        | "MP40" -> mp40
        | "Lee-Enfield" -> leeEnfield
        | "FG42" -> fg42
        | "BAR" -> bar
        | "M134 Minigun" -> minigun
        | _ -> kar98k

    let forWeapon name =
        let arms = if name = "M1911" || name = "Luger P08" then pistolArms else rifleArms
        MeshGen.union [| meshFor name; arms |]
