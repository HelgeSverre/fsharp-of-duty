namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module OmahaDrawMap =
    /// Omaha Draw — Dog Green sector and the D-1 Vierville draw, asymmetric.
    ///
    /// Modelled on the real ground: a tidal flat under the sea at the south, a
    /// short stretch of open sand up to a shingle bank and seawall, one bluff
    /// with a single vehicle draw cut through it, and a casemate at the draw
    /// mouth whose embrasure fires ALONG the beach rather than out to sea —
    /// WN72's 88 was sited for enfilade. Attackers spawn wading in the surf;
    /// defenders hold the crest. The fight lives between the waterline and the
    /// wall.
    ///
    /// Routes up, west to east: the draw (wide, watched by the casemate), a
    /// bluff scramble at x ≈ 14 (steep but just inside the slope limit, slow
    /// and exposed), and a narrow gully at the east edge (fast, blind, tight).
    let spec =
        let seaLevel = 0.0f
        let shingle = 2.2f
        let crestWest = 7.2f
        let crestEast = 5.4f
        let drawX = -10.0f
        let gullyX = 25.0f
        let scrambleX = 14.0f

        // Shell craters are prone cover on the open sand; placed, not random.
        let craters = [| -14.0f, -12.0f; -4.0f, -16.0f; 6.0f, -11.0f; 16.0f, -15.0f; -22.0f, -14.0f |]
        let craterDip x z =
            craters
            |> Array.sumBy (fun (cx, cz) ->
                let squared = (x - cx) * (x - cx) + (z - cz) * (z - cz)
                -1.0f * MathF.Exp(-squared / 9.0f))

        // Seabed at -1.6 rising through the waterline near z = -23 up to the
        // shingle at the wall. Attackers spawn on the wet side of that line.
        let beachHeight x z =
            let rise = -1.6f + (shingle + 1.6f) * MathEx.clamp01 ((z + 32.0f) / 27.0f)
            let dunes = (Noise.fbm2 4471 4 (Vector2(x * 0.06f, z * 0.06f)) - 0.5f) * 0.9f
            // Keep the surf itself smooth so wading depth reads predictably.
            let surfFade = MathEx.clamp01 ((z + 24.0f) / 6.0f)
            rise + (dunes + craterDip x z) * surfFade

        let bluffHeight x z =
            let t = MathEx.clamp01 ((z + 4.0f) / 12.0f)
            // The crest tilts down west-to-east; the casemate side is higher.
            let crest = crestWest + (crestEast - crestWest) * MathEx.clamp01 ((x + 20.0f) / 40.0f)
            // Bluff face: full height in the first third — a wall by slope.
            let steep = MathEx.clamp01 (t * 5.0f)
            let face = shingle + (crest - shingle) * (steep * steep * (3.0f - 2.0f * steep))
            // The draw climbs at a steady walkable grade the whole way.
            let walkable = shingle + (crest - shingle) * t
            let inDraw = 1.0f - MathEx.clamp01 ((abs (x - drawX) - 3.5f) / 1.2f)
            // The gully is narrower and steeper: walkable, but only just.
            let gullyGrade = shingle + (crest - shingle) * MathEx.clamp01 (t * 1.8f)
            let inGully = 1.0f - MathEx.clamp01 ((abs (x - gullyX) - 1.6f) / 1.0f)
            // The scramble is a softened stretch of face, right at the limit.
            let scramble = shingle + (crest - shingle) * MathEx.clamp01 (t * 1.35f)
            let inScramble = 1.0f - MathEx.clamp01 ((abs (x - scrambleX) - 2.0f) / 1.4f)
            let ground =
                face
                |> fun g -> g + (walkable - g) * inDraw
                |> fun g -> g + (gullyGrade - g) * inGully
                |> fun g -> g + (scramble - g) * inScramble
            ground + (Noise.fbm2 9281 4 (Vector2(x * 0.05f, z * 0.05f)) - 0.5f) * 0.2f

        let hedgehog x z = LevelDsl.block (Vector3(x, beachHeight x z + 0.6f, z)) (Vector3(1.7f, 1.2f, 1.7f)) Metal
        let onSand x z (height: float32) = Vector3(x, beachHeight x z + height * 0.5f, z)

        let items =
            [ LevelDsl.street 64.0f 32.0f Mud
              LevelDsl.water seaLevel

              LevelDsl.heightfield (Vector3(0.0f, 0.0f, -18.0f)) (Vector2(64.0f, 28.0f)) 32 beachHeight Mud
              LevelDsl.heightfield (Vector3(0.0f, 0.0f, 14.0f)) (Vector2(64.0f, 36.0f)) 32 bluffHeight Mud

              // Seawall along the promenade, open at the draw and crumbled low
              // near the east end where the gully starts.
              LevelDsl.block (Vector3(-24.0f, shingle + 0.8f, -4.0f)) (Vector3(16.0f, 1.6f, 1.2f)) Brick
              LevelDsl.block (Vector3(6.0f, shingle + 0.8f, -4.0f)) (Vector3(24.0f, 1.6f, 1.2f)) Brick
              LevelDsl.block (Vector3(23.0f, shingle + 0.35f, -4.0f)) (Vector3(8.0f, 0.7f, 1.2f)) Brick

              // WN72: the casemate at the draw mouth. Embrasure faces east to
              // enfilade the sand — solid walls seaward and west, the firing
              // slit left between a low and a high wall on the east face.
              LevelDsl.block (Vector3(-5.5f, shingle + 1.6f, 1.0f)) (Vector3(1.0f, 3.2f, 7.0f)) Brick // west wall
              LevelDsl.block (Vector3(-3.0f, shingle + 1.6f, -2.0f)) (Vector3(6.0f, 3.2f, 1.0f)) Brick // seaward wall
              LevelDsl.block (Vector3(-3.0f, shingle + 3.5f, 1.0f)) (Vector3(6.0f, 0.6f, 7.0f)) Brick // roof slab
              LevelDsl.block (Vector3(-0.5f, shingle + 0.45f, 1.0f)) (Vector3(1.0f, 0.9f, 7.0f)) Brick // east low wall
              LevelDsl.block (Vector3(-0.5f, shingle + 2.6f, 1.0f)) (Vector3(1.0f, 1.2f, 7.0f)) Brick // east high wall
              // Anti-tank wall across the draw behind the casemate, breached at
              // its west end so infantry can squeeze past.
              LevelDsl.block (Vector3(-7.0f, shingle + 1.0f, 4.5f)) (Vector3(5.0f, 2.0f, 1.0f)) Brick

              // WN71 trenches along the crest, with a diagonal spur toward the
              // gully head so defenders rotate without cresting the skyline.
              LevelDsl.trench (Vector3(-18.0f, crestWest - 0.4f, 17.0f)) (Vector3(4.0f, crestWest - 1.0f, 17.0f)) 2.8f
              LevelDsl.trench (Vector3(4.0f, crestWest - 1.0f, 17.0f)) (Vector3(18.0f, crestEast, 23.0f)) 2.4f

              // Crest firing positions over the sand.
              LevelDsl.sandbags (Vector3(-17.0f, crestWest, 11.0f)) (Vector3(-11.0f, crestWest, 11.0f)) (Some Axis)
              LevelDsl.sandbags (Vector3(2.0f, crestWest - 0.9f, 12.0f)) (Vector3(8.0f, crestWest - 1.1f, 12.0f)) (Some Axis)
              LevelDsl.sandbags (Vector3(20.0f, crestEast, 14.0f)) (Vector3(25.0f, crestEast, 14.0f)) (Some Axis)

              // Shingle-side cover for the push over the bank.
              LevelDsl.sandbags (Vector3(-20.0f, shingle, -6.0f)) (Vector3(-14.0f, shingle, -6.0f)) None
              LevelDsl.sandbags (Vector3(9.0f, shingle, -6.0f)) (Vector3(15.0f, shingle, -6.0f)) None

              // Beach obstacles: hedgehogs in the surf and on the sand, wire
              // running diagonally to channel the advance toward the craters.
              hedgehog -18.0f -22.0f
              hedgehog -8.0f -25.0f
              hedgehog 2.0f -21.0f
              hedgehog 12.0f -24.0f
              hedgehog 22.0f -21.0f
              hedgehog -17.0f -17.0f
              hedgehog 8.0f -16.0f
              LevelDsl.fence (Vector3(-28.0f, 0.0f, -10.0f)) (Vector3(-16.0f, 0.0f, -8.0f))
              LevelDsl.fence (Vector3(0.0f, 0.0f, -9.0f)) (Vector3(14.0f, 0.0f, -11.0f))

              // Wrecked landing craft at the waterline: the hard cover that
              // anchors the first bound out of the surf.
              LevelDsl.block (onSand -2.0f -20.0f 1.8f) (Vector3(9.0f, 1.8f, 3.4f)) Metal
              LevelDsl.block (onSand -6.0f -20.0f 3.4f) (Vector3(1.2f, 3.4f, 3.4f)) Metal
              LevelDsl.block (onSand 16.0f -19.0f 1.6f) (Vector3(7.0f, 1.6f, 3.0f)) Metal

              // Attackers in the surf; defenders behind the trench line.
              LevelDsl.spawnSquad Allies 8 (Vector3(-6.0f, 0.0f, -27.0f))
              LevelDsl.spawnSquad Axis 8 (Vector3(-4.0f, 0.0f, 25.0f))
              LevelDsl.objective "Take the draw" ]

        LevelDsl.level "Omaha Draw" items
