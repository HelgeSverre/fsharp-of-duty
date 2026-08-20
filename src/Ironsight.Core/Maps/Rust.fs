namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

/// A desert oil-pumping station: a derrick over a walled compound, a raised
/// pipeline, a conveyor run down from the tower, and a yard of containers.
///
/// Proportions and placements come from a reference model of the Modern
/// Warfare 2 map, dumped with tools/fbx-layout.py. It is a recreation of the
/// *layout*, built entirely from our own brushes and procedural meshes — the
/// reference contributed numbers, not geometry.
///
/// Reference coordinates were centred (its compound sat at x -0.5, z -7.6) and
/// dropped so the yard floor is y = 0.
[<RequireQualifiedAccess>]
module RustMap =
    /// A strut between two points: how every truss member is built.
    let private strut radius material (a: Vector3) (b: Vector3) =
        let delta = b - a
        MeshGen.cylinder 6 radius (max 0.05f (delta.Length())) material
        |> MeshGen.transform (
            Matrix4x4.CreateFromQuaternion(MathEx.rotationFromZ delta)
            * Matrix4x4.CreateTranslation((a + b) * 0.5f))

    /// The derrick: four legs tapering from `baseSpread` to `topSpread` over
    /// `height`, tied at every level by girts and cross-braced on all four
    /// faces. This silhouette is the whole reason the map is recognisable, so
    /// it is generated rather than hand-placed — the bracing alone is 48
    /// members and nobody should be typing those out.
    let private derrick height baseSpread topSpread =
        let levels = 7
        // Half-width of the tower at a given level, tapering linearly.
        let half level =
            let t = float32 level / float32 levels
            (baseSpread + (topSpread - baseSpread) * t) * 0.5f
        let corners level =
            let h = half level
            let y = height * float32 level / float32 levels
            [| Vector3(-h, y, -h); Vector3(h, y, -h); Vector3(h, y, h); Vector3(-h, y, h) |]
        MeshGen.union
            [| for level in 0 .. levels - 1 do
                 let lower, upper = corners level, corners (level + 1)
                 for corner in 0..3 do
                     let next = (corner + 1) % 4
                     // Leg, then the girt closing this face at the top of the
                     // level, then the diagonal across the face.
                     yield strut 0.09f RustedMetal lower[corner] upper[corner]
                     yield strut 0.05f RustedMetal upper[corner] upper[next]
                     yield strut 0.04f RustedMetal lower[corner] upper[next]
               // Crown block and the mast above it.
               let top = half levels
               yield MeshGen.box (Vector3(top * 2.2f, 0.30f, top * 2.2f)) RustedMetal
                     |> MeshGen.translate (Vector3(0.0f, height, 0.0f))
               yield MeshGen.cylinder 6 0.10f 1.8f RustedMetal
                     |> MeshGen.rotateX (MathF.PI * 0.5f)
                     |> MeshGen.translate (Vector3(0.0f, height + 0.9f, 0.0f)) |]

    /// An upright storage tank: a plain shell with a domed cap, which is what
    /// separates a tank farm from a row of boxes at any distance.
    let private tank radius height =
        MeshGen.union
            [| MeshGen.cylinder 14 radius height RustedMetal
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> MeshGen.translate (Vector3(0.0f, height * 0.5f, 0.0f))
               MeshGen.lathe 14
                   [| Vector2(radius, 0.0f); Vector2(radius * 0.86f, radius * 0.32f)
                      Vector2(radius * 0.5f, radius * 0.52f); Vector2(0.0f, radius * 0.62f) |]
                   RustedMetal
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> MeshGen.translate (Vector3(0.0f, height, 0.0f))
               // Skirt ring at the base, so it does not look pasted onto sand.
               MeshGen.cylinder 14 (radius * 1.08f) 0.35f Concrete
               |> MeshGen.rotateX (MathF.PI * 0.5f)
               |> MeshGen.translate (Vector3(0.0f, 0.18f, 0.0f)) |]

    /// A run of pipe along +X with a trestle under each end.
    let private pipeline length radius =
        MeshGen.union
            [| yield MeshGen.cylinder 12 radius length RustedMetal |> MeshGen.rotateY (MathF.PI * 0.5f)
               for side in [ -1.0f; 1.0f ] do
                   yield MeshGen.box (Vector3(0.22f, 6.0f, 0.22f)) RustedMetal
                         |> MeshGen.translate (Vector3(length * 0.42f * side, -3.0f, 0.0f)) |]

    /// Dune relief outside the compound. Flat inside the wall line so the
    /// fight happens on level ground; rolling beyond it so the map does not
    /// end at a cliff of nothing.
    let private dunes (x: float32) (z: float32) =
        let outside = max 0.0f (max (abs x - 23.0f) (abs z - 30.0f)) / 8.0f
        let roll = MathF.Sin(x * 0.13f) * MathF.Cos(z * 0.11f) + MathF.Sin(z * 0.21f) * 0.5f
        MathF.Min(1.0f, outside) * (1.6f + roll * 1.4f)

    let spec =
        let items =
            [ yield LevelDsl.street 62.0f 24.0f Sand
              yield LevelDsl.heightfield (Vector3(0.0f, 0.0f, 0.0f)) (Vector2(56.0f, 72.0f)) 40 dunes Sand

              // ---- Perimeter: chainlink on a low concrete curb ----
              for struct (a, b) in
                  [ struct (Vector3(-22.0f, 0.0f, -30.0f), Vector3(22.0f, 0.0f, -30.0f))
                    struct (Vector3(-22.0f, 0.0f, 29.0f), Vector3(22.0f, 0.0f, 29.0f))
                    struct (Vector3(-22.0f, 0.0f, -30.0f), Vector3(-22.0f, 0.0f, 29.0f))
                    struct (Vector3(22.0f, 0.0f, -30.0f), Vector3(22.0f, 0.0f, 29.0f)) ] do
                  yield LevelDsl.fence a b

              // ---- The derrick, on its concrete pad ----
              yield LevelDsl.block (Vector3(-11.9f, 1.75f, 11.8f)) (Vector3(7.2f, 3.5f, 6.7f)) Concrete
              yield LevelDsl.prop (derrick 22.4f 5.6f 1.9f) (Vector3(-11.9f, 3.5f, 11.8f)) 0.0f
              // Ramp onto the pad, so the tower base is a real position rather
              // than scenery: 3.5 m over 6 m is inside the slope limit.
              yield LevelDsl.ramp (Vector3(-11.9f, 0.0f, 17.0f)) (Vector3(-11.9f, 3.5f, 14.6f)) 3.0f Concrete
              // Squat block north of the pad, climbable from it.
              yield LevelDsl.block (Vector3(-12.7f, 4.0f, 17.6f)) (Vector3(4.2f, 8.0f, 4.1f)) Concrete

              // ---- Central structure: two catwalk floors in an open frame ----
              yield LevelDsl.block (Vector3(-3.3f, 4.6f, 1.3f)) (Vector3(6.1f, 0.3f, 10.4f)) RustedMetal
              yield LevelDsl.block (Vector3(-4.3f, 9.7f, 2.5f)) (Vector3(4.1f, 0.3f, 7.8f)) RustedMetal
              // Corner posts carrying the floors, and the frame above them.
              for struct (x, z) in
                  [ struct (-6.1f, -3.6f); struct (-0.5f, -3.6f)
                    struct (-6.1f, 6.2f); struct (-0.5f, 6.2f) ] do
                  yield LevelDsl.block (Vector3(x, 6.0f, z)) (Vector3(0.4f, 12.0f, 0.4f)) RustedMetal
              yield LevelDsl.prop (derrick 8.0f 4.6f 3.4f) (Vector3(-2.5f, 9.9f, -0.3f)) 0.0f
              // Exhaust stack off the upper floor.
              yield LevelDsl.prop (tank 1.0f 3.4f) (Vector3(-4.4f, 9.9f, 5.4f)) 0.0f

              // ---- The conveyor: the run down off the tower into the yard ----
              yield LevelDsl.ramp (Vector3(-1.0f, 4.6f, 1.0f)) (Vector3(6.6f, 0.0f, 8.0f)) 2.6f RustedMetal
              // Its side rails, so it reads as a covered run rather than a slab.
              yield LevelDsl.prop
                  (strut 0.12f RustedMetal (Vector3(-1.0f, 5.4f, 2.3f)) (Vector3(6.6f, 0.8f, 9.3f)))
                  Vector3.Zero 0.0f
              yield LevelDsl.prop
                  (strut 0.12f RustedMetal (Vector3(-1.0f, 5.4f, -0.3f)) (Vector3(6.6f, 0.8f, 6.7f)))
                  Vector3.Zero 0.0f

              // ---- Raised pipeline across the west of the yard ----
              yield LevelDsl.prop (pipeline 15.0f 1.5f) (Vector3(-7.9f, 4.6f, -0.4f)) 0.0f
              yield LevelDsl.prop (pipeline 12.0f 1.1f) (Vector3(-15.0f, 3.4f, -12.0f)) 0.35f

              // ---- Tank farm, south-east ----
              yield LevelDsl.prop (tank 1.6f 5.0f) (Vector3(15.2f, 0.0f, -19.8f)) 0.0f
              yield LevelDsl.prop (tank 1.6f 5.0f) (Vector3(15.2f, 0.0f, -17.3f)) 0.0f
              yield LevelDsl.prop (tank 1.6f 4.6f) (Vector3(18.4f, 0.0f, -18.6f)) 0.0f
              yield LevelDsl.prop (tank 1.4f 7.0f) (Vector3(-13.8f, 0.0f, -0.4f)) 0.0f
              yield LevelDsl.prop (tank 1.1f 2.9f) (Vector3(0.5f, 4.6f, -5.9f)) 0.0f
              // Bots path around the farm rather than into it: props are not nav
              // obstructions, so each tank keeps a block inside its shell.
              for struct (x, z, r) in
                  [ struct (15.2f, -19.8f, 1.6f); struct (15.2f, -17.3f, 1.6f)
                    struct (18.4f, -18.6f, 1.6f); struct (-13.8f, -0.4f, 1.4f) ] do
                  yield LevelDsl.block (Vector3(x, 1.6f, z)) (Vector3(r * 1.4f, 3.2f, r * 1.4f)) RustedMetal

              // ---- Yard buildings, east side ----
              yield LevelDsl.ruin (Vector3(16.5f, 0.0f, -3.8f)) (Vector2(6.4f, 11.2f)) 4.2f Concrete Intact
              yield LevelDsl.ruin (Vector3(14.0f, 0.0f, -17.9f)) (Vector2(6.3f, 7.5f)) 4.4f Concrete Intact
              yield LevelDsl.block (Vector3(3.2f, 1.05f, 0.4f)) (Vector3(3.3f, 2.1f, 4.7f)) Plaster
              yield LevelDsl.block (Vector3(-1.8f, 1.05f, 8.2f)) (Vector3(3.9f, 2.1f, 4.1f)) Plaster
              yield LevelDsl.block (Vector3(1.7f, 1.0f, -3.0f)) (Vector3(6.2f, 2.0f, 2.1f)) RustedMetal

              // ---- Containers: the yard's hard cover, and its stairs ----
              yield LevelDsl.block (Vector3(0.4f, 1.15f, -6.8f)) (Vector3(2.0f, 2.3f, 4.0f)) RustedMetal
              yield LevelDsl.block (Vector3(-7.7f, 1.15f, -10.8f)) (Vector3(4.0f, 2.3f, 2.0f)) RustedMetal
              yield LevelDsl.block (Vector3(-7.7f, 1.15f, -13.1f)) (Vector3(4.0f, 2.3f, 2.0f)) RustedMetal
              yield LevelDsl.block (Vector3(-7.7f, 1.15f, -15.6f)) (Vector3(4.0f, 2.3f, 2.0f)) RustedMetal
              yield LevelDsl.block (Vector3(-0.5f, 1.05f, 21.5f)) (Vector3(6.1f, 2.1f, 3.8f)) RustedMetal
              yield LevelDsl.block (Vector3(11.0f, 1.05f, 12.7f)) (Vector3(6.0f, 2.1f, 5.9f)) RustedMetal
              yield LevelDsl.block (Vector3(-5.0f, 1.05f, -23.8f)) (Vector3(5.7f, 2.1f, 2.7f)) RustedMetal
              // Stacked pair reaching the lower catwalk, so the middle is
              // climbable without a ladder.
              yield LevelDsl.block (Vector3(-1.2f, 1.15f, 1.2f)) (Vector3(2.2f, 2.3f, 5.0f)) RustedMetal
              yield LevelDsl.block (Vector3(-1.2f, 3.45f, 1.2f)) (Vector3(2.2f, 2.3f, 5.0f)) RustedMetal
              yield LevelDsl.ramp (Vector3(-1.2f, 0.0f, -2.6f)) (Vector3(-1.2f, 2.3f, -0.2f)) 2.0f RustedMetal

              // ---- Loose junk ----
              for struct (x, z) in
                  [ struct (6.4f, 3.2f); struct (7.1f, 2.4f); struct (-9.4f, -6.0f)
                    struct (-9.9f, -6.8f); struct (4.6f, -12.2f); struct (12.7f, 6.1f)
                    struct (-16.8f, 4.4f); struct (-3.2f, -18.4f); struct (9.8f, -25.1f)
                    struct (-18.2f, -20.6f); struct (17.4f, 8.8f); struct (-6.6f, 25.8f) ] do
                  yield LevelDsl.prop (tank 0.32f 0.86f) (Vector3(x, 0.0f, z)) 0.0f
              for struct (x, z) in [ struct (6.1f, 10.1f); struct (-5.5f, 15.6f); struct (11.0f, -8.4f) ] do
                  yield LevelDsl.block (Vector3(x, 0.5f, z)) (Vector3(1.5f, 1.0f, 1.3f)) Concrete

              // ---- Spawns: the long axis, as the reference has them ----
              for offset in -3 .. 3 do
                  yield LevelDsl.spawnSquad Allies 1 (Vector3(float32 offset * 4.0f, 0.0f, 26.0f))
                  yield LevelDsl.spawnSquad Axis 1 (Vector3(float32 offset * 4.0f, 0.0f, -27.0f))
              yield LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 28.0f))
              yield LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -29.0f))

              yield LevelDsl.objective "Win the round"
              yield LevelDsl.trigger
                  (Delay(Units.seconds 0.35f))
                  (Say("MARSHAL", "Rig is live. Take the derrick or work the containers.")) ]
        LevelDsl.level "Rust" items
