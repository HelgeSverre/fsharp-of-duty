namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

/// A shoothouse inside a warehouse: waist-and-shoulder plywood partitions laid
/// out around a central tower, two breaching rooms at the ends, and a lid over
/// all of it. Close, fast and entirely indoors — the first map here that is.
///
/// Proportions come from a reference model of the Call of Duty 4 map, dumped
/// with tools/fbx-layout.py. Layout only; every surface is our own brushes.
/// Reference coordinates were shifted (+0.8 on X, -0.2 on Z) to centre the hall
/// and dropped so its floor is y = 0.
[<RequireQualifiedAccess>]
module KillhouseMap =
    /// Hall footprint and the height of its lid.
    let private halfWidth = 18.4f
    let private halfDepth = 9.8f
    let private ceiling = 7.0f

    /// A partition: thin, and low enough to shoot over but not to see past
    /// while crouched. The reference builds the whole maze out of these.
    let private wall (x: float32) (z: float32) (spanX: float32) (spanZ: float32) height =
        LevelDsl.block (Vector3(x, height * 0.5f, z)) (Vector3(spanX, height, spanZ)) Wood

    /// One of the two end rooms: four walls, a doorway punched through the face
    /// given by `doorSide`, and a lintel over it so it reads as a door rather
    /// than a missing wall.
    let private room (centre: Vector3) (size: Vector3) (doorSide: float32) =
        let halfX, halfZ = size.X * 0.5f, size.Z * 0.5f
        let thickness = 0.22f
        let height = size.Y
        let doorHalf = 0.75f
        let post offset span =
            LevelDsl.block
                (Vector3(centre.X + offset, height * 0.5f, centre.Z + doorSide * halfZ))
                (Vector3(span, height, thickness))
                Plaster
        [ // Side walls and the blind rear wall.
          LevelDsl.block (Vector3(centre.X - halfX, height * 0.5f, centre.Z)) (Vector3(thickness, height, size.Z)) Plaster
          LevelDsl.block (Vector3(centre.X + halfX, height * 0.5f, centre.Z)) (Vector3(thickness, height, size.Z)) Plaster
          LevelDsl.block
              (Vector3(centre.X, height * 0.5f, centre.Z - doorSide * halfZ))
              (Vector3(size.X, height, thickness))
              Plaster
          // The door face, split around the opening.
          post (-(halfX + doorHalf) * 0.5f) (halfX - doorHalf)
          post ((halfX + doorHalf) * 0.5f) (halfX - doorHalf)
          LevelDsl.block
              (Vector3(centre.X, height - 0.25f, centre.Z + doorSide * halfZ))
              (Vector3(doorHalf * 2.0f, 0.5f, thickness))
              Plaster ]

    /// The tower in the middle of the hall: four posts to the roof, a platform
    /// five metres up with a rail around it, and one ladder to reach it. It is
    /// the only high ground on the map and the only thing worth crossing it for.
    let private tower =
        let x, z = 0.3f, 1.3f
        let half = 1.5f
        [ yield! [ for cornerX in [ -half; half ] do
                     for cornerZ in [ -half; half ] ->
                       LevelDsl.block
                           (Vector3(x + cornerX, ceiling * 0.5f, z + cornerZ))
                           (Vector3(0.22f, ceiling, 0.22f))
                           RustedMetal ]
          // Deck. The hall's own lid roofs it: a second roof of its own left
          // 1.75 m of headroom, which is not enough to stand in and cost the
          // tower its navmesh entirely.
          yield LevelDsl.block (Vector3(x, 4.75f, z)) (Vector3(3.2f, 0.3f, 3.2f)) RustedMetal
          // Waist rails on three sides; the fourth is where the ladder lands.
          yield LevelDsl.block (Vector3(x, 5.35f, z + half)) (Vector3(3.2f, 0.9f, 0.1f)) RustedMetal
          yield LevelDsl.block (Vector3(x - half, 5.35f, z)) (Vector3(0.1f, 0.9f, 3.2f)) RustedMetal
          yield LevelDsl.block (Vector3(x + half, 5.35f, z)) (Vector3(0.1f, 0.9f, 3.2f)) RustedMetal
          // Ends a little above the deck, so the top of the climb is a step
          // forward rather than a jump.
          yield LevelDsl.ladder (Vector3(x, 0.0f, z - half - 0.35f)) 5.3f MathF.PI ]

    let spec =
        let items =
            [ yield LevelDsl.street (halfDepth * 2.0f) halfWidth Concrete

              // ---- The shell: four walls and a lid. No sky on this one. ----
              for struct (cx, cz, sx, sz) in
                  [ struct (0.0f, -halfDepth, halfWidth * 2.0f, 0.4f)
                    struct (0.0f, halfDepth, halfWidth * 2.0f, 0.4f)
                    struct (-halfWidth, 0.0f, 0.4f, halfDepth * 2.0f)
                    struct (halfWidth, 0.0f, 0.4f, halfDepth * 2.0f) ] do
                  yield LevelDsl.block (Vector3(cx, ceiling * 0.5f, cz)) (Vector3(sx, ceiling, sz)) Concrete
              yield LevelDsl.block (Vector3(0.0f, ceiling + 0.2f, 0.0f)) (Vector3(halfWidth * 2.0f, 0.4f, halfDepth * 2.0f)) Concrete

              yield! tower

              // ---- The two end rooms, doors facing the middle ----
              yield! room (Vector3(-11.0f, 0.0f, 2.6f)) (Vector3(3.8f, 2.6f, 7.7f)) 1.0f
              yield! room (Vector3(10.7f, 0.0f, -1.4f)) (Vector3(3.8f, 2.6f, 6.7f)) -1.0f

              // ---- Plywood maze, straight off the reference ----
              yield wall -14.2f -6.7f 0.3f 5.9f 1.8f
              yield wall -14.2f 7.5f 0.25f 4.1f 1.9f
              yield wall -9.3f 8.7f 0.25f 1.7f 1.9f
              yield wall -5.4f 8.5f 3.0f 0.2f 1.7f
              yield wall -5.6f 6.9f 0.25f 3.0f 1.7f
              yield wall -5.9f 1.5f 0.25f 3.9f 1.7f
              yield wall -6.5f 1.0f 1.0f 0.25f 1.7f
              yield wall -10.3f -8.2f 0.4f 2.8f 1.7f
              yield wall -9.9f -6.9f 3.5f 0.4f 1.8f
              yield wall -9.8f -3.4f 0.25f 4.2f 0.9f
              yield wall 3.4f 7.9f 4.1f 0.25f 1.6f
              yield wall 7.1f -7.9f 0.25f 3.4f 2.0f
              yield wall 8.6f 6.6f 0.25f 5.8f 1.8f
              yield wall 12.5f -7.1f 0.25f 4.9f 2.0f
              yield wall 12.9f 7.5f 0.25f 4.1f 2.0f

              // ---- Hard cover: the block, the containers, the bin ----
              yield LevelDsl.block (Vector3(-0.4f, 1.3f, -7.1f)) (Vector3(9.6f, 2.6f, 4.5f)) Concrete
              yield LevelDsl.block (Vector3(-1.9f, 0.9f, 7.1f)) (Vector3(1.7f, 1.8f, 4.7f)) RustedMetal
              yield LevelDsl.block (Vector3(2.6f, 0.9f, 4.9f)) (Vector3(4.6f, 1.8f, 4.5f)) RustedMetal
              yield LevelDsl.prop (MeshGen.box (Vector3(4.1f, 4.0f, 3.6f)) RustedMetal) (Vector3(-12.1f, 2.0f, -7.9f)) 0.0f
              yield LevelDsl.block (Vector3(-12.1f, 2.0f, -7.9f)) (Vector3(3.6f, 4.0f, 3.2f)) RustedMetal
              // Ramp onto the big block, so it is a firing position and not a wall.
              yield LevelDsl.ramp (Vector3(4.8f, 0.0f, -7.1f)) (Vector3(4.4f, 2.6f, -7.1f)) 2.2f Concrete

              // ---- Clutter, at the reference's positions ----
              for struct (x, z, stacked) in
                  [ struct (-15.6f, -8.6f, true); struct (-11.6f, -8.6f, true)
                    struct (-4.4f, -5.4f, true); struct (-0.4f, -4.3f, true)
                    struct (2.7f, -8.8f, true); struct (16.8f, -7.3f, true)
                    struct (16.5f, 9.0f, true); struct (-14.9f, -7.1f, false)
                    struct (-14.9f, -6.3f, false); struct (-10.0f, 3.7f, false)
                    struct (-1.4f, -4.3f, false); struct (13.6f, 7.5f, false)
                    struct (13.6f, 6.7f, false) ] do
                  yield LevelDsl.block (Vector3(x, 0.3f, z)) (Vector3(0.8f, 0.6f, 0.8f)) Wood
                  if stacked then
                      yield LevelDsl.block (Vector3(x + 0.3f, 0.85f, z)) (Vector3(0.7f, 0.55f, 0.7f)) Wood
              // Oil drums and a stack of tyres.
              for struct (x, z) in [ struct (4.2f, 6.9f); struct (13.3f, 8.6f); struct (-5.1f, 0.8f) ] do
                  yield LevelDsl.prop
                            (MeshGen.cylinder 12 0.3f 0.9f RustedMetal |> MeshGen.rotateX (MathF.PI * 0.5f))
                            (Vector3(x, 0.45f, z)) 0.0f

              // ---- Spawns at the two ends, as the reference has them ----
              for offset in -3 .. 3 do
                  yield LevelDsl.spawnSquad Allies 1 (Vector3(-16.4f, 0.0f, float32 offset * 2.2f))
                  yield LevelDsl.spawnSquad Axis 1 (Vector3(15.6f, 0.0f, float32 offset * 2.2f))
              yield LevelDsl.spawnSquad Allies 1 (Vector3(-17.3f, 0.0f, 0.0f))
              yield LevelDsl.spawnSquad Axis 1 (Vector3(16.6f, 0.0f, 0.0f))

              yield LevelDsl.objective "Win the round"
              yield LevelDsl.trigger
                        (Delay(Units.seconds 0.35f))
                        (Say("MARSHAL", "Shoothouse is hot. Watch the tower.")) ]
        LevelDsl.level "Killhouse" items
