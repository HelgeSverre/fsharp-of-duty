namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module OmahaDrawMap =
    /// Omaha Draw — the D-Day silhouette mirrored across X so both teams hold a
    /// bluff and the beach below is neutral ground.
    ///
    /// The bluff is a single heightfield with three channels carved into it: two
    /// draws at x = ±13 and a deeper ravine up the middle. The spurs between
    /// them are past the slope limit, so they are walls you cannot climb but can
    /// shoot down from, and each channel plays differently — the draws are wide
    /// and overlooked from both sides, the ravine is tight and blind.
    ///
    /// The beach is dunes and shell craters rather than a flat plane, so there
    /// is something to use every few metres of the approach.
    let spec =
        let crest = 6.5f
        let shore = 2.6f
        let channels = [| -13.0f; 0.0f; 13.0f |]
        let mirrored build = [ for sign in [ -1.0f; 1.0f ] -> build sign ]

        // Craters are placed rather than left to noise, because each one is a
        // piece of prone cover on an otherwise open approach.
        // Mirrored pairs, plus one on the centreline.
        let craters =
            [| yield 0.0f, -21.0f
               for x, z in [ 19.0f, -17.0f; 7.0f, -12.0f; 13.0f, -24.0f ] do
                   yield x, z
                   yield -x, z |]
        let craterDip x z =
            craters
            |> Array.sumBy (fun (cx, cz) ->
                let squared = (x - cx) * (x - cx) + (z - cz) * (z - cz)
                -1.1f * MathF.Exp(-squared / 11.0f))

        let beachHeight x z =
            let rise = shore * MathEx.clamp01 ((z + 26.0f) / 23.0f)
            // Sampled on |x| so the two halves are genuinely identical; the
            // map's fairness claim is only as good as its least symmetric part.
            let dunes = (Noise.fbm2 4471 4 (Vector2(abs x * 0.055f, z * 0.055f)) - 0.5f) * 1.5f
            max 0.0f (rise + dunes + craterDip x z)

        let bluffHeight x z =
            let t = MathEx.clamp01 ((z + 3.0f) / 11.0f)
            // A channel climbs at a steady, walkable grade the whole way.
            let walkable = shore + (crest - shore) * t
            // A spur reaches full height within the first third — comfortably
            // past the slope limit — then runs flat along the top.
            let steep = MathEx.clamp01 (t * 5.0f)
            let spur = shore + (crest - shore) * (steep * steep * (3.0f - 2.0f * steep))
            let toChannel = channels |> Array.map (fun centre -> abs (x - centre)) |> Array.min
            let inChannel = 1.0f - MathEx.clamp01 ((toChannel - 3.5f) / 1.2f)
            let ground = spur + (walkable - spur) * inChannel
            // The middle channel is cut deeper still, which is what makes it a
            // ravine rather than a third identical draw.
            let ravine = 2.4f * (1.0f - MathEx.clamp01 (abs x / 4.5f)) * MathEx.clamp01 (t * 3.0f)
            // Keep the noise small; too much of it rounds the channel walls
            // back off into walkable slopes.
            ground - ravine + (Noise.fbm2 9281 4 (Vector2(abs x * 0.05f, z * 0.05f)) - 0.5f) * 0.25f

        let hedgehog x z = LevelDsl.block (Vector3(x, beachHeight x z + 0.6f, z)) (Vector3(1.7f, 1.2f, 1.7f)) Metal

        let items =
            [ LevelDsl.street 60.0f 30.0f Mud

              // Sand below the seawall, bluff above it.
              LevelDsl.heightfield (Vector3(0.0f, 0.0f, -16.5f)) (Vector2(60.0f, 27.0f)) 30 beachHeight Mud
              LevelDsl.heightfield (Vector3(0.0f, 0.0f, 14.5f)) (Vector2(60.0f, 35.0f)) 32 bluffHeight Mud

              // Seawall, blown open at each of the three channels.
              LevelDsl.block (Vector3(-23.5f, shore + 1.0f, -3.0f)) (Vector3(13.0f, 2.0f, 1.2f)) Brick
              LevelDsl.block (Vector3(-6.5f, shore + 1.0f, -3.0f)) (Vector3(5.0f, 2.0f, 1.2f)) Brick
              LevelDsl.block (Vector3(6.5f, shore + 1.0f, -3.0f)) (Vector3(5.0f, 2.0f, 1.2f)) Brick
              LevelDsl.block (Vector3(23.5f, shore + 1.0f, -3.0f)) (Vector3(13.0f, 2.0f, 1.2f)) Brick

              // Trench network along the crest, cut into the terrain itself.
              // The communication trenches run diagonally, which only became
              // possible once line items stopped collapsing to their bounding box.
              LevelDsl.trench (Vector3(-21.0f, crest, 15.0f)) (Vector3(21.0f, crest, 15.0f)) 3.0f
              // Stopping short of z = 24 keeps the spawn fan clear of the cut.
              yield! mirrored (fun sign -> LevelDsl.trench (Vector3(sign * 13.0f, crest, 15.0f)) (Vector3(sign * 21.0f, crest, 21.0f)) 2.6f)

              // Bunkers on the flanks, embrasure facing the sand: a low front
              // wall and a high one with the firing slit left between them.
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 22.0f, crest + 0.45f, 21.0f)) (Vector3(9.0f, 0.9f, 1.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 22.0f, crest + 2.3f, 21.0f)) (Vector3(9.0f, 1.2f, 1.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 22.0f - 4.0f, crest + 1.45f, 23.0f)) (Vector3(1.0f, 2.9f, 5.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 22.0f + 4.0f, crest + 1.45f, 23.0f)) (Vector3(1.0f, 2.9f, 5.0f)) Brick)

              // Firing positions on the spur tops. Each overlooks two channels
              // and is only reachable from the crest, so taking one costs time.
              yield! mirrored (fun sign -> LevelDsl.sandbags (Vector3(sign * 6.5f, crest, 7.0f)) (Vector3(sign * 6.5f, crest, 11.0f)) None)
              yield! mirrored (fun sign -> LevelDsl.sandbags (Vector3(sign * 19.5f, crest, 6.0f)) (Vector3(sign * 19.5f, crest, 10.0f)) None)

              // Cover at each channel mouth, angled so it breaks the sightline
              // without walling the lane off.
              yield! mirrored (fun sign -> LevelDsl.sandbags (Vector3(sign * 10.0f, shore, 0.0f)) (Vector3(sign * 15.5f, shore, 3.5f)) None)
              LevelDsl.sandbags (Vector3(-3.5f, shore, 1.0f)) (Vector3(3.5f, shore, 1.0f)) None

              // Beach obstacles. The wire runs diagonally so it channels the
              // advance instead of being a straight line to walk around.
              hedgehog -22.0f -20.0f
              hedgehog -15.0f -14.0f
              hedgehog -9.0f -22.0f
              hedgehog 0.0f -16.0f
              hedgehog 9.0f -22.0f
              hedgehog 15.0f -14.0f
              hedgehog 22.0f -20.0f
              LevelDsl.fence (Vector3(-26.0f, 0.0f, -11.0f)) (Vector3(-12.0f, 0.0f, -7.0f))
              LevelDsl.fence (Vector3(12.0f, 0.0f, -7.0f)) (Vector3(26.0f, 0.0f, -11.0f))

              // Wrecked landing craft: the only hard cover in the open middle.
              yield! mirrored (fun sign ->
                  LevelDsl.block (Vector3(sign * 8.0f, beachHeight (sign * 8.0f) -19.0f + 0.9f, -19.0f)) (Vector3(9.0f, 1.8f, 3.4f)) Metal)
              yield! mirrored (fun sign ->
                  LevelDsl.block (Vector3(sign * 12.0f, beachHeight (sign * 8.0f) -19.0f + 1.7f, -19.0f)) (Vector3(1.2f, 3.4f, 3.4f)) Metal)
              yield! mirrored (fun sign ->
                  LevelDsl.block (Vector3(sign * 21.0f, beachHeight (sign * 21.0f) -25.0f + 0.8f, -25.0f)) (Vector3(7.0f, 1.6f, 3.0f)) Metal)

              LevelDsl.spawnSquad Allies 8 (Vector3(-20.0f, 0.0f, 26.0f))
              LevelDsl.spawnSquad Axis 8 (Vector3(20.0f, 0.0f, 26.0f))
              LevelDsl.objective "Hold the draws" ]

        LevelDsl.level "Omaha Draw" items
