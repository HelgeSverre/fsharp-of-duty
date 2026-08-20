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

    let private hoopYZ segments radius thickness material (centre: Vector3) =
        Array.init segments (fun index ->
            let angle = MathF.Tau * float32 index / float32 segments
            let nextAngle = MathF.Tau * float32 (index + 1) / float32 segments
            let point value = centre + Vector3(0.0f, MathF.Cos(value) * radius, MathF.Sin(value) * radius)
            limb thickness material (point angle) (point nextAngle))
        |> MeshGen.union

    /// Four triangular cutting fins around the shaft. The point is local Z=0,
    /// the ferrule/shaft connection is at +Z, and the 45-degree clocking makes
    /// the blades read as an X when viewed head-on.
    let private broadhead material =
        let blade =
            MeshGen.wedge (Vector3(0.007f, 0.074f, 0.14f)) material
            |> placed (Vector3(0.0f, 0.037f, 0.07f))
        let blades =
            Array.init 4 (fun index ->
                let angle = MathF.PI * 0.25f + float32 index * MathF.PI * 0.5f
                blade |> MeshGen.transform (Matrix4x4.CreateRotationZ angle))
        MeshGen.union
            [| yield! blades
               MeshGen.cylinder 6 0.013f 0.045f material |> placed (Vector3(0.0f, 0.0f, 0.118f)) |]

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

    // Tippmann 98-inspired: long receiver, vertical feed neck, oversized
    // hopper and a rear air bottle make it unmistakable even in silhouette.
    let private paintballMarker =
        MeshGen.union
            [| MeshGen.box (Vector3(0.18f, 0.18f, 0.52f)) Metal |> placed (Vector3(0.0f, 0.03f, -0.30f))
               MeshGen.cylinder 12 0.035f 0.72f Metal |> placed (Vector3(0.0f, 0.055f, -0.88f))
               MeshGen.box (Vector3(0.12f, 0.24f, 0.14f)) Metal |> MeshGen.rotateX -0.34f |> placed (Vector3(0.0f, -0.15f, -0.09f))
               MeshGen.box (Vector3(0.13f, 0.19f, 0.20f)) Metal |> placed (Vector3(0.0f, -0.08f, -0.48f))
               // Feed neck and broad ellipsoidal gravity hopper.
               MeshGen.cylinder 10 0.045f 0.16f Metal |> MeshGen.rotateX (MathF.PI * 0.5f) |> placed (Vector3(0.0f, 0.20f, -0.31f))
               MeshGen.lathe 16
                   [| Vector2(0.02f, -0.14f); Vector2(0.15f, -0.10f); Vector2(0.20f, 0.0f)
                      Vector2(0.15f, 0.10f); Vector2(0.02f, 0.14f) |]
                   PaintBlue
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.39f, -0.28f))
               // Angled ASA and compressed-air bottle behind the grip.
               MeshGen.cylinder 10 0.045f 0.18f Metal |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.015f, 0.035f))
               MeshGen.cylinder 14 0.085f 0.46f Metal |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.06f, 0.22f))
               MeshGen.cylinder 14 0.095f 0.06f Metal |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.02f, 0.46f))
               MeshGen.box (Vector3(0.025f, 0.08f, 0.03f)) Metal |> placed (Vector3(0.0f, 0.11f, -0.83f)) |]

    // Deliberately toy-like rather than a real firearm: chunky blue shell,
    // orange muzzle, top priming rail, and a large removable box magazine.
    let private nerfBlaster =
        MeshGen.union
            [| MeshGen.box (Vector3(0.24f, 0.25f, 0.62f)) FoamBlue |> placed (Vector3(0.0f, 0.03f, -0.28f))
               MeshGen.box (Vector3(0.18f, 0.10f, 0.52f)) FoamOrange |> placed (Vector3(0.0f, 0.15f, -0.29f))
               MeshGen.cylinder 12 0.075f 0.24f FoamOrange |> placed (Vector3(0.0f, 0.04f, -0.70f))
               MeshGen.cylinder 12 0.095f 0.08f FoamOrange |> placed (Vector3(0.0f, 0.04f, -0.86f))
               MeshGen.box (Vector3(0.13f, 0.28f, 0.16f)) FoamBlue |> MeshGen.rotateX -0.35f |> placed (Vector3(0.0f, -0.18f, -0.05f))
               MeshGen.box (Vector3(0.16f, 0.36f, 0.20f)) FoamOrange |> MeshGen.rotateX -0.08f |> placed (Vector3(0.0f, -0.22f, -0.36f))
               MeshGen.box (Vector3(0.18f, 0.19f, 0.42f)) FoamBlue |> placed (Vector3(0.0f, 0.03f, 0.20f))
               MeshGen.box (Vector3(0.20f, 0.23f, 0.06f)) FoamOrange |> placed (Vector3(0.0f, 0.02f, 0.43f))
               MeshGen.box (Vector3(0.07f, 0.05f, 0.24f)) FoamOrange |> placed (Vector3(0.0f, 0.23f, -0.24f))
               MeshGen.box (Vector3(0.03f, 0.08f, 0.03f)) FoamOrange |> placed (Vector3(0.0f, 0.13f, -0.69f)) |]

    // M1-style tube launcher: simple olive tube, flared rear venturi, sights,
    // trigger grip and shoulder rest. The tube stays slim enough to see over.
    let private bazooka =
        MeshGen.union
            [| MeshGen.cylinder 18 0.095f 1.72f UniformOlive |> placed (Vector3(0.0f, 0.06f, -0.48f))
               MeshGen.cylinder 18 0.115f 0.10f Metal |> placed (Vector3(0.0f, 0.06f, -1.38f))
               MeshGen.lathe 16
                   [| Vector2(0.16f, -0.13f); Vector2(0.12f, -0.06f); Vector2(0.095f, 0.06f); Vector2(0.095f, 0.13f) |]
                   UniformOlive
               |> placed (Vector3(0.0f, 0.06f, 0.44f))
               MeshGen.box (Vector3(0.12f, 0.30f, 0.13f)) Wood |> MeshGen.rotateX -0.18f |> placed (Vector3(0.0f, -0.16f, -0.38f))
               MeshGen.box (Vector3(0.30f, 0.06f, 0.13f)) Metal |> placed (Vector3(0.0f, -0.03f, 0.12f))
               MeshGen.box (Vector3(0.04f, 0.22f, 0.04f)) Metal |> placed (Vector3(-0.13f, 0.17f, -0.76f))
               MeshGen.box (Vector3(0.04f, 0.16f, 0.04f)) Metal |> placed (Vector3(-0.13f, 0.15f, -0.18f))
               MeshGen.box (Vector3(0.03f, 0.04f, 0.60f)) Metal |> placed (Vector3(-0.13f, 0.24f, -0.47f))
               // Loaded olive warhead peeking from the muzzle.
               MeshGen.lathe 14
                   [| Vector2(0.0f, -0.12f); Vector2(0.055f, -0.05f); Vector2(0.055f, 0.10f); Vector2(0.038f, 0.16f) |]
                   UniformOlive
               |> placed (Vector3(0.0f, 0.06f, -1.48f)) |]

    // Boring Company-inspired compact propane torch: a white rifle-like shell,
    // black furniture, exposed burner tube and a bottle tucked underneath.
    let private flamethrower =
        MeshGen.union
            [| MeshGen.box (Vector3(0.22f, 0.24f, 0.62f)) Plaster |> placed (Vector3(0.0f, 0.04f, -0.30f))
               MeshGen.box (Vector3(0.16f, 0.12f, 0.44f)) ToolBlack |> placed (Vector3(0.0f, 0.17f, -0.28f))
               MeshGen.cylinder 14 0.050f 0.48f Metal |> placed (Vector3(0.0f, 0.055f, -0.82f))
               MeshGen.cylinder 14 0.075f 0.12f ToolBlack |> placed (Vector3(0.0f, 0.055f, -1.12f))
               MeshGen.box (Vector3(0.13f, 0.30f, 0.15f)) ToolBlack |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, -0.18f, -0.10f))
               MeshGen.box (Vector3(0.17f, 0.18f, 0.48f)) ToolBlack |> placed (Vector3(0.0f, 0.02f, 0.25f))
               MeshGen.box (Vector3(0.22f, 0.25f, 0.07f)) ToolBlack |> placed (Vector3(0.0f, 0.01f, 0.51f))
               MeshGen.cylinder 14 0.105f 0.38f Plaster |> MeshGen.rotateX (MathF.PI * 0.5f) |> placed (Vector3(0.0f, -0.18f, -0.43f))
               MeshGen.cylinder 10 0.035f 0.15f Metal |> MeshGen.rotateX (MathF.PI * 0.5f) |> placed (Vector3(0.0f, -0.04f, -0.43f))
               MeshGen.box (Vector3(0.08f, 0.06f, 0.30f)) ToolBlack |> placed (Vector3(0.0f, 0.25f, -0.34f)) |]

    // Oversized pressure toy with an obvious water reservoir, pump sleeve and
    // bright safety colours. WaterBlue reads as translucent in the flat shader.
    let private superSoaker =
        MeshGen.union
            [| MeshGen.box (Vector3(0.25f, 0.28f, 0.64f)) WaterBlue |> placed (Vector3(0.0f, 0.04f, -0.30f))
               MeshGen.box (Vector3(0.20f, 0.13f, 0.58f)) PaintYellow |> placed (Vector3(0.0f, 0.18f, -0.28f))
               MeshGen.cylinder 14 0.070f 0.42f FoamOrange |> placed (Vector3(0.0f, 0.06f, -0.83f))
               MeshGen.cylinder 12 0.040f 0.18f WaterBlue |> placed (Vector3(0.0f, 0.06f, -1.13f))
               MeshGen.box (Vector3(0.16f, 0.30f, 0.16f)) FoamOrange |> MeshGen.rotateX -0.28f |> placed (Vector3(0.0f, -0.19f, -0.07f))
               MeshGen.lathe 16
                   [| Vector2(0.05f, -0.25f); Vector2(0.15f, -0.20f); Vector2(0.17f, 0.15f); Vector2(0.09f, 0.24f) |]
                   WaterBlue
               |> placed (Vector3(0.0f, 0.00f, 0.24f))
               MeshGen.box (Vector3(0.24f, 0.12f, 0.30f)) FoamOrange |> placed (Vector3(0.0f, -0.10f, -0.70f))
               MeshGen.box (Vector3(0.18f, 0.08f, 0.20f)) PaintYellow |> placed (Vector3(0.0f, 0.27f, -0.18f)) |]

    // Compact pneumatic framing nailer. The tall yellow driver housing, black
    // horizontal fastener magazine and arched rear handle deliberately follow
    // the familiar DeWalt contractor-tool silhouette instead of looking like
    // a conventional firearm.
    let private nailgun =
        MeshGen.union
            [| // Tall driver/motor body and its grey service cap.
               MeshGen.box (Vector3(0.30f, 0.46f, 0.36f)) PaintYellow |> placed (Vector3(0.0f, 0.10f, -0.39f))
               MeshGen.box (Vector3(0.31f, 0.13f, 0.35f)) Metal |> placed (Vector3(0.0f, 0.385f, -0.39f))
               MeshGen.box (Vector3(0.23f, 0.055f, 0.27f)) ToolBlack |> placed (Vector3(0.0f, 0.47f, -0.39f))
               // Rear pneumatic cylinder with a black collar and air fitting.
               MeshGen.cylinder 14 0.145f 0.52f PaintYellow
               |> MeshGen.rotateY (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.22f, 0.07f))
               MeshGen.cylinder 14 0.150f 0.10f ToolBlack
               |> MeshGen.rotateY (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.22f, 0.38f))
               MeshGen.cylinder 10 0.040f 0.16f Metal
               |> MeshGen.rotateY (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.22f, 0.50f))
               // Rubberised bridge grip and the little trigger tucked below it.
               MeshGen.box (Vector3(0.25f, 0.19f, 0.52f)) ToolBlack |> placed (Vector3(0.0f, 0.13f, -0.01f))
               MeshGen.box (Vector3(0.11f, 0.16f, 0.08f)) Metal |> MeshGen.rotateX -0.22f |> placed (Vector3(0.0f, -0.02f, -0.18f))
               // Long black nail magazine underneath, with a rear latch.
               MeshGen.box (Vector3(0.25f, 0.18f, 1.02f)) ToolBlack |> placed (Vector3(0.0f, -0.25f, -0.08f))
               MeshGen.box (Vector3(0.27f, 0.045f, 0.88f)) Metal |> placed (Vector3(0.0f, -0.19f, -0.12f))
               MeshGen.box (Vector3(0.27f, 0.16f, 0.15f)) ToolBlack |> placed (Vector3(0.0f, -0.24f, 0.49f))
               MeshGen.cylinder 10 0.040f 0.27f PaintYellow
               |> MeshGen.rotateY (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, -0.18f, 0.31f))
               // Narrow safety nose and yellow no-mar foot at the business end.
               MeshGen.box (Vector3(0.16f, 0.42f, 0.15f)) ToolBlack |> placed (Vector3(0.0f, -0.13f, -0.66f))
               MeshGen.box (Vector3(0.10f, 0.18f, 0.10f)) Metal |> placed (Vector3(0.0f, -0.42f, -0.67f))
               MeshGen.box (Vector3(0.15f, 0.07f, 0.12f)) PaintYellow |> placed (Vector3(0.0f, -0.53f, -0.67f)) |]
        |> MeshGen.scale (Vector3(0.58f, 0.70f, 0.70f))

    // Long rail-style diver speargun: laminated central stock, twin black
    // rubber power bands, compact pistol grip, muzzle bridge and a spear riding
    // openly along the top rail. Its silhouette follows common band spearguns.
    let private harpoonGunBody =
        MeshGen.union
            [| // Warm laminated rail above the dark structural barrel.
               MeshGen.box (Vector3(0.13f, 0.11f, 1.95f)) Wood |> placed (Vector3(0.0f, 0.06f, -0.66f))
               MeshGen.box (Vector3(0.17f, 0.08f, 1.82f)) ToolBlack |> placed (Vector3(0.0f, -0.035f, -0.62f))
               MeshGen.box (Vector3(0.19f, 0.16f, 0.42f)) ToolBlack |> placed (Vector3(0.0f, 0.02f, 0.54f))
               // Compact moulded grip and oversized trigger guard.
               MeshGen.box (Vector3(0.18f, 0.42f, 0.18f)) ToolBlack |> MeshGen.rotateX -0.20f |> placed (Vector3(0.0f, -0.25f, 0.55f))
               MeshGen.box (Vector3(0.27f, 0.065f, 0.06f)) ToolBlack |> placed (Vector3(0.0f, -0.08f, 0.37f))
               MeshGen.box (Vector3(0.035f, 0.20f, 0.06f)) ToolBlack |> placed (Vector3(-0.12f, -0.17f, 0.42f))
               MeshGen.box (Vector3(0.035f, 0.20f, 0.06f)) ToolBlack |> placed (Vector3(0.12f, -0.17f, 0.42f))
               MeshGen.box (Vector3(0.27f, 0.045f, 0.06f)) ToolBlack |> placed (Vector3(0.0f, -0.27f, 0.46f))
               MeshGen.box (Vector3(0.025f, 0.11f, 0.035f)) Metal |> MeshGen.rotateX -0.28f |> placed (Vector3(0.0f, -0.14f, 0.39f))
               // Muzzle bridge gathers the paired bands around the spear rail.
               MeshGen.box (Vector3(0.28f, 0.22f, 0.10f)) ToolBlack |> placed (Vector3(0.0f, -0.01f, -1.68f))
               MeshGen.box (Vector3(0.17f, 0.11f, 0.14f)) Metal |> placed (Vector3(0.0f, 0.09f, -1.68f))
               MeshGen.box (Vector3(0.025f, 0.18f, 0.18f)) ToolBlack |> placed (Vector3(-0.13f, -0.04f, -1.66f))
               MeshGen.box (Vector3(0.025f, 0.18f, 0.18f)) ToolBlack |> placed (Vector3(0.13f, -0.04f, -1.66f))
               // Low-profile rear line guide.
               MeshGen.box (Vector3(0.07f, 0.06f, 0.06f)) Metal |> placed (Vector3(0.0f, 0.145f, 0.27f)) |]

    /// Animated power train for the speargun. `load` is zero immediately
    /// after firing and one when the bands have been drawn back onto a loaded
    /// spear. The bands really change length and position rather than being a
    /// texture or a viewmodel-only wobble.
    let harpoonGunForLoad load =
        let load = MathEx.clamp01 load
        let rearZ = -1.36f + load * 1.62f
        let rearY = -0.025f + load * 0.17f
        let band left =
            let side = if left then -1.0f else 1.0f
            let anchor = Vector3(side * 0.105f, -0.055f, -1.68f)
            let wishbone = Vector3(side * (0.09f - load * 0.055f), rearY, rearZ)
            limb 0.030f ToolBlack anchor wishbone
        let wishbone =
            limb 0.010f Metal
                (Vector3(-0.09f + load * 0.055f, rearY, rearZ))
                (Vector3(0.09f - load * 0.055f, rearY, rearZ))
        let spear =
            if load < 0.18f then MeshGen.empty
            else
                // Slide the replacement spear back along the rail during the
                // reload, then let the wishbone visibly catch its rear notch.
                let slide = (1.0f - load) * -0.48f
                MeshGen.union
                    [| MeshGen.cylinder 10 0.018f 2.32f Metal |> placed (Vector3(0.0f, 0.145f, -0.90f + slide))
                       MeshGen.lathe 10 [| Vector2(0.0f, -0.13f); Vector2(0.050f, 0.01f); Vector2(0.018f, 0.12f) |] Metal
                       |> placed (Vector3(0.0f, 0.145f, -2.17f + slide))
                       MeshGen.box (Vector3(0.12f, 0.018f, 0.14f)) Metal
                       |> MeshGen.rotateY 0.52f
                       |> placed (Vector3(0.0f, 0.145f, -2.07f + slide)) |]
        MeshGen.union [| harpoonGunBody; band true; band false; wishbone; spear |]

    let private harpoonGun = harpoonGunForLoad 1.0f

    /// Recurve bow with a genuinely moving string, nock and limb stack. The
    /// string pulls both tips inward and toward the archer while the working
    /// limbs visibly unload their forward curve.
    let bowForDraw draw =
        let draw = MathEx.clamp01 draw
        let centre = Vector3(0.0f, 0.0f, -0.31f)
        let flex = draw * draw * (3.0f - 2.0f * draw)
        let signed side y z = Vector3(0.0f, side * y, z)
        // -Z is downrange. The working limbs belly toward the target before
        // their tips recurve back toward the archer; the old profile had this
        // backwards and read as a bow being fired inside-out in side view.
        let root side = signed side 0.14f -0.32f
        let inner side = signed side (0.36f - flex * 0.02f) (-0.43f + flex * 0.05f)
        let belly side = signed side (0.62f - flex * 0.07f) (-0.54f + flex * 0.12f)
        let recurve side = signed side (0.76f - flex * 0.11f) (-0.40f + flex * 0.13f)
        let tip side = signed side (0.84f - flex * 0.16f) (-0.11f + flex * 0.16f)
        let topTip = tip 1.0f
        let bottomTip = tip -1.0f
        // At brace height the nock shares the tips' Z plane, so the resting
        // string is straight. Pulling it toward +Z creates the draw triangle.
        let nock = Vector3(0.0f, 0.0f, topTip.Z + draw * 0.43f)
        // Renderer ADS offsets are the exact inverse in X/Y, putting this
        // single distance pin on screen centre at rest.
        let sightCentre = Vector3(-0.18f, 0.21f, -0.255f)
        let sightPin =
            MeshGen.union
                [| limb 0.0035f Metal
                       (Vector3(sightCentre.X + 0.072f, sightCentre.Y, sightCentre.Z))
                       (Vector3(sightCentre.X + 0.010f, sightCentre.Y, sightCentre.Z))
                   MeshGen.cylinder 8 0.006f 0.014f PaintGreen |> placed sightCentre |]
        MeshGen.union
            [| // Laminated working limbs sweep downrange, then the short
               // recurved ends hook back behind the riser to meet the string.
               limb 0.030f Wood (root 1.0f) (inner 1.0f)
               limb 0.026f Wood (inner 1.0f) (belly 1.0f)
               limb 0.022f Wood (belly 1.0f) (recurve 1.0f)
               limb 0.018f Metal (recurve 1.0f) topTip
               limb 0.030f Wood (root -1.0f) (inner -1.0f)
               limb 0.026f Wood (inner -1.0f) (belly -1.0f)
               limb 0.022f Wood (belly -1.0f) (recurve -1.0f)
               limb 0.018f Metal (recurve -1.0f) bottomTip
               // Rounded rubber grip: circular in cross-section with tapered
               // ends where it meets the limb roots, rather than a gun-like
               // rectangular receiver block.
               MeshGen.lathe 14
                   [| Vector2(0.044f, -0.15f); Vector2(0.062f, -0.12f)
                      Vector2(0.070f, -0.045f); Vector2(0.070f, 0.045f)
                      Vector2(0.062f, 0.12f); Vector2(0.044f, 0.15f) |]
                   ToolBlack
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> placed centre
               // String and small metal nocking loop.
               limb 0.008f ToolBlack topTip nock
               limb 0.008f ToolBlack nock bottomTip
               MeshGen.cylinder 8 0.016f 0.06f Metal |> MeshGen.rotateY (MathF.PI * 0.5f) |> placed nock
               // Right-handed recurve sight: a slim adjustment rail and one
               // distance pin sit left of the riser, with no enclosing guard
               // to clutter the sight picture.
               MeshGen.box (Vector3(0.018f, 0.22f, 0.022f)) Metal
               |> placed (Vector3(-0.078f, 0.17f, sightCentre.Z))
               limb 0.009f Metal
                   (Vector3(-0.078f, sightCentre.Y, sightCentre.Z))
                   (Vector3(sightCentre.X + 0.072f, sightCentre.Y, sightCentre.Z))
               sightPin
               // Loaded arrow. It shifts back with the nock while its point
               // stays aimed down -Z, making the draw obvious in first person.
               MeshGen.cylinder 8 0.0075f 1.10f Wood |> placed (Vector3(0.0f, 0.015f, nock.Z - 0.49f))
               broadhead Metal |> placed (Vector3(0.0f, 0.015f, nock.Z - 1.18f))
               MeshGen.box (Vector3(0.052f, 0.008f, 0.14f)) PaintRed |> MeshGen.rotateY 0.30f |> placed (Vector3(0.0f, 0.015f, nock.Z + 0.05f))
               MeshGen.box (Vector3(0.008f, 0.052f, 0.14f)) PaintRed |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, 0.015f, nock.Z + 0.05f)) |]

    let private bow = bowForDraw 0.0f

    /// Literal bargain-bin keychain laser pointer: a slim silver tube with a
    /// wraparound warning label, proud push button, black emitter and the
    /// oversized split ring that makes these things unmistakable.
    let private laserPointer =
        let chainStart = Vector3(0.0f, -0.015f, 0.165f)
        let chainMiddle = Vector3(0.0f, -0.105f, 0.225f)
        let ringCentre = Vector3(0.0f, -0.245f, 0.305f)
        MeshGen.union
            [| // Aluminium barrel and stepped end caps.
               MeshGen.cylinder 18 0.052f 0.46f Metal |> placed (Vector3(0.0f, 0.0f, -0.09f))
               MeshGen.cylinder 18 0.058f 0.055f Metal |> placed (Vector3(0.0f, 0.0f, -0.347f))
               MeshGen.cylinder 18 0.034f 0.010f ToolBlack |> placed (Vector3(0.0f, 0.0f, -0.380f))
               MeshGen.cylinder 18 0.058f 0.035f Metal |> placed (Vector3(0.0f, 0.0f, 0.158f))
               // Paper safety label and the familiar red DANGER stripe.
               MeshGen.cylinder 18 0.0535f 0.135f Plaster |> placed (Vector3(0.0f, 0.0f, 0.040f))
               MeshGen.cylinder 18 0.055f 0.025f PaintRed |> placed (Vector3(0.0f, 0.0f, -0.012f))
               MeshGen.cylinder 18 0.055f 0.010f ToolBlack |> placed (Vector3(0.0f, 0.0f, 0.025f))
               // Chrome push button standing proud of the tube.
               MeshGen.cylinder 12 0.024f 0.020f Metal
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> placed (Vector3(0.0f, 0.061f, -0.125f))
               // Rear eyelet, two chunky chain links, and split keyring.
               hoopYZ 10 0.031f 0.010f Metal chainStart
               limb 0.010f Metal (chainStart + Vector3(0.0f, -0.025f, 0.020f)) chainMiddle
               hoopYZ 10 0.038f 0.010f Metal chainMiddle
               limb 0.010f Metal (chainMiddle + Vector3(0.0f, -0.030f, 0.025f)) (ringCentre + Vector3(0.0f, 0.100f, -0.025f))
               hoopYZ 18 0.105f 0.011f Metal ringCentre
               // A second, slightly offset hoop reads as a real split ring.
               hoopYZ 18 0.099f 0.006f Metal (ringCentre + Vector3(0.008f, 0.0f, 0.0f)) |]
        |> MeshGen.scale (Vector3(0.82f, 0.82f, 0.82f))

    /// A visibly curved shinogi-zukuri blade, small tsuba and circular wrapped
    /// tsuka. The point remains local -Z like every firearm muzzle, so the
    /// ordinary preview and viewmodel transforms keep working.
    let private katana =
        let bladeSegments =
            [| for index in 0..7 do
                   let t0 = float32 index / 8.0f
                   let t1 = float32 (index + 1) / 8.0f
                   // Start inside the tsuba and carry the blade shoulder all
                   // the way to it; the earlier decorative gap made the sword
                   // read as two unrelated procedural meshes.
                   let point t = Vector3(0.10f * t * t, 0.035f * t, -0.20f - 1.48f * t)
                   let a, b = point t0, point t1
                   let delta = b - a
                   yield
                       MeshGen.box (Vector3(0.060f - t0 * 0.018f, 0.020f, delta.Length() + 0.015f)) Metal
                       |> MeshGen.transform (Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ delta) * Matrix4x4.CreateTranslation((a + b) * 0.5f)) |]
        MeshGen.union
            [| yield! bladeSegments
               // Bright cutting edge and wedge-like kissaki.
               limb 0.010f Plaster (Vector3(-0.040f, -0.005f, -0.20f)) (Vector3(0.055f, 0.025f, -1.64f))
               MeshGen.wedge (Vector3(0.075f, 0.025f, 0.18f)) Metal |> placed (Vector3(0.10f, 0.04f, -1.72f))
               MeshGen.cylinder 18 0.13f 0.025f Metal |> placed (Vector3(0.0f, 0.0f, -0.20f))
               // Circular ray-skin grip with alternating wrap bands.
               MeshGen.cylinder 14 0.060f 0.54f ToolBlack |> placed (Vector3(0.0f, 0.0f, 0.09f))
               for index in 0..7 do
                   MeshGen.cylinder 10 0.066f 0.026f (if index % 2 = 0 then Plaster else ToolBlack)
                   |> placed (Vector3(0.0f, 0.0f, -0.14f + float32 index * 0.065f))
               MeshGen.cylinder 14 0.075f 0.045f Metal |> placed (Vector3(0.0f, 0.0f, 0.38f)) |]

    /// Small world meshes reused by the renderer for physical special ammo.
    let paintballMesh color =
        MeshGen.lathe 10
            [| Vector2(0.0f, -0.040f); Vector2(0.036f, -0.022f); Vector2(0.042f, 0.0f)
               Vector2(0.036f, 0.022f); Vector2(0.0f, 0.040f) |]
            color

    /// Tip points toward -Z; the shaft extends back toward +Z.
    let dartMesh =
        MeshGen.union
            [| MeshGen.cylinder 10 0.018f 0.18f FoamBlue |> placed (Vector3(0.0f, 0.0f, 0.07f))
               MeshGen.lathe 10
                   [| Vector2(0.032f, -0.025f); Vector2(0.028f, -0.012f); Vector2(0.014f, 0.018f) |]
                   FoamOrange
               |> placed (Vector3(0.0f, 0.0f, -0.045f)) |]

    let rocketMesh =
        MeshGen.union
            [| MeshGen.cylinder 12 0.045f 0.34f UniformOlive
               MeshGen.lathe 12 [| Vector2(0.0f, -0.11f); Vector2(0.065f, -0.02f); Vector2(0.045f, 0.09f) |] UniformOlive
               |> placed (Vector3(0.0f, 0.0f, -0.22f))
               MeshGen.box (Vector3(0.14f, 0.015f, 0.14f)) Metal |> placed (Vector3(0.0f, 0.0f, 0.17f)) |]

    let waterDropletMesh =
        MeshGen.lathe 8
            [| Vector2(0.0f, -0.032f); Vector2(0.020f, -0.015f); Vector2(0.024f, 0.010f); Vector2(0.0f, 0.038f) |]
            WaterBlue

    /// Point is -Z, broad head is +Z.
    let nailMesh =
        MeshGen.union
            [| MeshGen.lathe 8 [| Vector2(0.0f, -0.11f); Vector2(0.008f, -0.08f); Vector2(0.008f, 0.09f) |] Metal
               MeshGen.cylinder 8 0.025f 0.012f Metal |> placed (Vector3(0.0f, 0.0f, 0.096f)) |]

    /// The point begins at local Z=0 and the 1.8 m shaft trails along +Z.
    /// Renderers rotate +Z opposite the flight direction, putting the point at
    /// the projectile's simulated position.
    let harpoonMesh =
        MeshGen.union
            [| MeshGen.lathe 14
                   [| Vector2(0.0f, -0.20f); Vector2(0.105f, -0.015f); Vector2(0.070f, 0.18f) |]
                   Metal
               |> placed (Vector3(0.0f, 0.0f, 0.20f))
               MeshGen.cylinder 10 0.026f 1.56f Metal |> placed (Vector3(0.0f, 0.0f, 1.02f))
               MeshGen.box (Vector3(0.22f, 0.025f, 0.22f)) Metal |> MeshGen.rotateY 0.48f |> placed (Vector3(0.0f, 0.0f, 0.34f))
               MeshGen.box (Vector3(0.025f, 0.22f, 0.22f)) Metal |> MeshGen.rotateX -0.48f |> placed (Vector3(0.0f, 0.0f, 0.34f))
               MeshGen.box (Vector3(0.12f, 0.014f, 0.20f)) Metal |> placed (Vector3(0.0f, 0.0f, 1.77f)) |]

    /// Point is -Z and the fletching trails toward +Z.
    let arrowMesh =
        MeshGen.union
            [| MeshGen.cylinder 8 0.0075f 1.05f Wood |> placed (Vector3(0.0f, 0.0f, 0.46f))
               broadhead Metal |> placed (Vector3(0.0f, 0.0f, -0.205f))
               MeshGen.box (Vector3(0.052f, 0.008f, 0.14f)) PaintRed |> MeshGen.rotateY 0.30f |> placed (Vector3(0.0f, 0.0f, 0.95f))
               MeshGen.box (Vector3(0.008f, 0.052f, 0.14f)) PaintRed |> MeshGen.rotateX -0.30f |> placed (Vector3(0.0f, 0.0f, 0.95f)) |]

    let splatMesh color = MeshGen.cylinder 12 0.17f 0.012f color

    /// Every weapon by name, so callers can iterate the set without repeating
    /// the list. `forWeapon` falls back to the Kar98k for anything unlisted.
    let names =
        [| "Thompson"; "M1911"; "Luger P08"; "M1897 Trench Gun"; "Kar98k"; "Kar98k Sniper"
           "M1 Garand"; "STG-44"; "MP40"; "Lee-Enfield"; "FG42"; "BAR"
           "Paintball Marker"; "Nerf Blaster"; "Bazooka"; "Flamethrower"; "Super Soaker"; "Nailgun"; "Harpoon Gun"; "Bow"; "Laser Pointer"; "Katana" |]

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
        | "Paintball Marker" -> paintballMarker
        | "Nerf Blaster" -> nerfBlaster
        | "Bazooka" -> bazooka
        | "Flamethrower" -> flamethrower
        | "Super Soaker" -> superSoaker
        | "Nailgun" -> nailgun
        | "Harpoon Gun" -> harpoonGun
        | "Bow" -> bow
        | "Laser Pointer" -> laserPointer
        | "Katana" -> katana
        | _ -> kar98k

    /// Gun-only pose variant used by previews and geometry inspection.
    let meshForPose name pose =
        match name with
        | "Harpoon Gun" -> harpoonGunForLoad pose
        | "Bow" -> bowForDraw pose
        | _ -> meshFor name

    let forWeapon name =
        let arms = if name = "M1911" || name = "Luger P08" || name = "Laser Pointer" then pistolArms else rifleArms
        MeshGen.union [| meshFor name; arms |]

    /// Runtime viewmodel variant used for mechanisms with moving geometry.
    let forWeaponPose name pose =
        let arms = if name = "M1911" || name = "Luger P08" || name = "Laser Pointer" then pistolArms else rifleArms
        let gun = meshForPose name pose
        MeshGen.union [| gun; arms |]
