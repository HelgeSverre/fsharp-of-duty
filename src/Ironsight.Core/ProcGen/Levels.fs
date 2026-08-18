namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module Levels =
    let paintballArena =
        let arenaItems =
            [ LevelDsl.street 52.0f 26.0f Mud
              // A deliberately compact three-lane killhouse. The staggered
              // dividers leave cross-lane doors instead of creating corridors
              // that trap the navmesh or the player.
              LevelDsl.fence (Vector3(-24.0f, 0.0f, -24.0f)) (Vector3(-24.0f, 0.0f, 24.0f))
              LevelDsl.fence (Vector3(24.0f, 0.0f, -24.0f)) (Vector3(24.0f, 0.0f, 24.0f))
              LevelDsl.fence (Vector3(-24.0f, 0.0f, -24.0f)) (Vector3(24.0f, 0.0f, -24.0f))
              LevelDsl.fence (Vector3(-24.0f, 0.0f, 24.0f)) (Vector3(24.0f, 0.0f, 24.0f))

              LevelDsl.block (Vector3(-8.0f, 1.45f, 13.0f)) (Vector3(1.0f, 2.9f, 13.0f)) Wood
              LevelDsl.block (Vector3(-8.0f, 1.45f, -11.0f)) (Vector3(1.0f, 2.9f, 15.0f)) Wood
              LevelDsl.block (Vector3(8.0f, 1.45f, 11.0f)) (Vector3(1.0f, 2.9f, 15.0f)) Metal
              LevelDsl.block (Vector3(8.0f, 1.45f, -13.0f)) (Vector3(1.0f, 2.9f, 13.0f)) Metal

              LevelDsl.block (Vector3(-17.0f, 1.0f, 10.0f)) (Vector3(4.0f, 2.0f, 2.2f)) Wood
              LevelDsl.block (Vector3(-16.0f, 0.75f, -8.0f)) (Vector3(3.2f, 1.5f, 2.0f)) Metal
              LevelDsl.block (Vector3(17.0f, 1.0f, -10.0f)) (Vector3(4.0f, 2.0f, 2.2f)) Wood
              LevelDsl.block (Vector3(16.0f, 0.75f, 8.0f)) (Vector3(3.2f, 1.5f, 2.0f)) Metal
              LevelDsl.block (Vector3(0.0f, 1.15f, 0.0f)) (Vector3(4.5f, 2.3f, 4.5f)) Plaster

              LevelDsl.sandbags (Vector3(-22.0f, 0.0f, 2.0f)) (Vector3(-12.0f, 0.0f, 2.0f)) None
              LevelDsl.sandbags (Vector3(-4.5f, 0.0f, 8.0f)) (Vector3(4.5f, 0.0f, 8.0f)) (Some Allies)
              LevelDsl.sandbags (Vector3(-4.5f, 0.0f, -8.0f)) (Vector3(4.5f, 0.0f, -8.0f)) (Some Axis)
              LevelDsl.sandbags (Vector3(12.0f, 0.0f, -2.0f)) (Vector3(22.0f, 0.0f, -2.0f)) None

              // The offline slice consumes one Allied and the first four Axis
              // markers. The remaining markers keep the same level safe for
              // the authoritative online 8v8 mode.
              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 21.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-18.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-12.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-6.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(6.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(12.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(18.0f, 0.0f, 20.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 18.5f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-18.0f, 0.0f, -20.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-5.5f, 0.0f, -20.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(5.5f, 0.0f, -20.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(18.0f, 0.0f, -20.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-12.0f, 0.0f, -20.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-10.0f, 0.0f, -18.5f))
              LevelDsl.spawnSquad Axis 1 (Vector3(10.0f, 0.0f, -18.5f))
              LevelDsl.spawnSquad Axis 1 (Vector3(12.0f, 0.0f, -20.0f))
              LevelDsl.objective "Win the round"
              LevelDsl.trigger (Delay(Units.seconds 0.35f)) (Say("MARSHAL", "Round live. Four hostiles, three lanes.")) ]
        LevelDsl.level "Paintball Killhouse" arenaItems |> LevelCompile.compile

    /// Rust-inspired scrap arena: a climbable central mound owns the middle,
    /// a blown-out warehouse anchors the north-east, and a railcar wall splits
    /// the west flank. Deliberately asymmetric — no lane mirrors another.
    let scrapDepot =
        let items =
            [ LevelDsl.street 72.0f 28.0f Mud
              // Central scrap mound: three 0.4 m tiers so both the player and
              // the navmesh can climb to the king-of-the-hill platform.
              LevelDsl.block (Vector3(0.0f, 0.2f, -2.0f)) (Vector3(12.0f, 0.4f, 12.0f)) Metal
              LevelDsl.block (Vector3(0.0f, 0.4f, -2.0f)) (Vector3(8.0f, 0.8f, 8.0f)) Metal
              LevelDsl.block (Vector3(0.0f, 0.6f, -2.0f)) (Vector3(4.0f, 1.2f, 4.0f)) Metal
              LevelDsl.block (Vector3(0.0f, 1.5f, -2.0f)) (Vector3(1.2f, 0.6f, 1.2f)) Wood

              // North-east warehouse, door opening toward the Axis approach.
              LevelDsl.ruin (Vector3(16.0f, 0.0f, -22.0f)) (Vector2(13.0f, 9.0f)) 6.2f Brick BlownOut
              // Derailed railcar wall on the west flank with a crawl gap at
              // each end — a hard sightline blocker that only covers one side.
              LevelDsl.block (Vector3(-17.0f, 1.3f, -4.0f)) (Vector3(3.0f, 2.6f, 11.0f)) Metal
              LevelDsl.block (Vector3(-17.0f, 1.3f, 12.0f)) (Vector3(3.0f, 2.6f, 7.0f)) Metal

              // Scattered junk: pipes, crate stacks, a tipped cart.
              LevelDsl.block (Vector3(9.0f, 0.35f, 10.0f)) (Vector3(6.5f, 0.7f, 1.4f)) Metal
              LevelDsl.block (Vector3(12.0f, 0.75f, 12.5f)) (Vector3(1.6f, 1.5f, 1.6f)) Wood
              LevelDsl.block (Vector3(-6.0f, 0.6f, 20.0f)) (Vector3(2.4f, 1.2f, 2.4f)) Wood
              LevelDsl.block (Vector3(-4.0f, 0.45f, 22.0f)) (Vector3(1.6f, 0.9f, 1.6f)) Wood
              LevelDsl.block (Vector3(20.0f, 0.55f, 2.0f)) (Vector3(2.0f, 1.1f, 3.4f)) Wood
              LevelDsl.block (Vector3(-9.0f, 0.5f, -20.0f)) (Vector3(4.5f, 1.0f, 1.6f)) Metal
              LevelDsl.block (Vector3(-22.0f, 0.7f, -24.0f)) (Vector3(2.6f, 1.4f, 2.6f)) Wood
              LevelDsl.fence (Vector3(-26.0f, 0.0f, 26.0f)) (Vector3(-14.0f, 0.0f, 30.0f))
              LevelDsl.fence (Vector3(14.0f, 0.0f, -30.0f)) (Vector3(26.0f, 0.0f, -26.0f))

              // Cover lines: neutral mid bags plus one owned line per approach.
              LevelDsl.sandbags (Vector3(-8.0f, 0.0f, 6.0f)) (Vector3(-2.0f, 0.0f, 6.0f)) None
              LevelDsl.sandbags (Vector3(4.0f, 0.0f, -12.0f)) (Vector3(10.0f, 0.0f, -12.0f)) None
              LevelDsl.sandbags (Vector3(-14.0f, 0.0f, 24.0f)) (Vector3(-6.0f, 0.0f, 24.0f)) (Some Allies)
              LevelDsl.sandbags (Vector3(-4.0f, 0.0f, -26.0f)) (Vector3(4.0f, 0.0f, -26.0f)) (Some Axis)

              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 31.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-16.0f, 0.0f, 30.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-8.0f, 0.0f, 30.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(8.0f, 0.0f, 30.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(16.0f, 0.0f, 30.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-22.0f, 0.0f, 31.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(22.0f, 0.0f, 31.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 33.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-20.0f, 0.0f, -31.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-10.0f, 0.0f, -30.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -31.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(10.0f, 0.0f, -30.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(20.0f, 0.0f, -31.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-5.0f, 0.0f, -33.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(5.0f, 0.0f, -33.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -34.0f))
              LevelDsl.objective "Win the round"
              LevelDsl.trigger (Delay(Units.seconds 0.35f)) (Say("MARSHAL", "Round live. Take the mound or flank the railcar.")) ]
        LevelDsl.level "Scrap Depot" items |> LevelCompile.compile

    /// Carentan-inspired canal arena: the east bank sits 1.2 m above the yard
    /// and is only reachable by two stair sets; the west side answers with a
    /// sunken canal lane and ruined houses. High ground versus covered rotate.
    let canalYard =
        let items =
            [ LevelDsl.street 80.0f 24.0f Snow
              // Raised east bank with 0.4 m stair steps at both ends.
              LevelDsl.block (Vector3(18.0f, 0.6f, 0.0f)) (Vector3(12.0f, 1.2f, 76.0f)) Snow
              LevelDsl.block (Vector3(10.0f, 0.2f, -22.0f)) (Vector3(4.0f, 0.4f, 4.0f)) Wood
              LevelDsl.block (Vector3(11.0f, 0.4f, -22.0f)) (Vector3(2.0f, 0.8f, 4.0f)) Wood
              LevelDsl.block (Vector3(10.0f, 0.2f, 24.0f)) (Vector3(4.0f, 0.4f, 4.0f)) Wood
              LevelDsl.block (Vector3(11.0f, 0.4f, 24.0f)) (Vector3(2.0f, 0.8f, 4.0f)) Wood
              // Bank-edge lip gives the high ground crouchable cover.
              LevelDsl.block (Vector3(12.6f, 1.5f, -8.0f)) (Vector3(0.9f, 0.6f, 10.0f)) Sandbag
              LevelDsl.block (Vector3(12.6f, 1.5f, 12.0f)) (Vector3(0.9f, 0.6f, 8.0f)) Sandbag

              // Sunken canal lane on the west-mid, broken by two crossings.
              LevelDsl.trench (Vector3(-6.0f, 0.0f, -32.0f)) (Vector3(-6.0f, 0.0f, -12.0f)) 3.0f
              LevelDsl.trench (Vector3(-6.0f, 0.0f, -6.0f)) (Vector3(-6.0f, 0.0f, 14.0f)) 3.0f
              LevelDsl.trench (Vector3(-6.0f, 0.0f, 20.0f)) (Vector3(-6.0f, 0.0f, 34.0f)) 3.0f

              // Ruined houses on the low west side, doors facing north.
              LevelDsl.ruin (Vector3(-17.0f, 0.0f, -16.0f)) (Vector2(8.5f, 7.0f)) 5.6f Plaster BlownOut
              LevelDsl.ruin (Vector3(-17.5f, 0.0f, 8.0f)) (Vector2(7.5f, 6.5f)) 4.8f Brick Intact
              LevelDsl.ruin (Vector3(-16.0f, 0.0f, 28.0f)) (Vector2(7.0f, 6.0f)) 5.2f Brick BlownOut

              // Yard clutter between the canal and the bank.
              LevelDsl.block (Vector3(2.0f, 0.55f, -6.0f)) (Vector3(2.6f, 1.1f, 1.8f)) Wood
              LevelDsl.block (Vector3(5.0f, 0.4f, 4.0f)) (Vector3(1.8f, 0.8f, 2.6f)) Wood
              LevelDsl.block (Vector3(0.0f, 0.6f, 14.0f)) (Vector3(2.2f, 1.2f, 2.2f)) Metal
              LevelDsl.block (Vector3(4.0f, 0.5f, -18.0f)) (Vector3(3.2f, 1.0f, 1.5f)) Metal
              LevelDsl.sandbags (Vector3(-2.0f, 0.0f, -26.0f)) (Vector3(6.0f, 0.0f, -26.0f)) (Some Axis)
              LevelDsl.sandbags (Vector3(-4.0f, 0.0f, 26.0f)) (Vector3(4.0f, 0.0f, 26.0f)) (Some Allies)
              LevelDsl.sandbags (Vector3(-11.0f, 0.0f, 0.0f)) (Vector3(-3.0f, 0.0f, 0.0f)) None

              LevelDsl.spawnSquad Allies 1 (Vector3(-4.0f, 0.0f, 35.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-14.0f, 0.0f, 35.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-20.0f, 0.0f, 36.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(4.0f, 0.0f, 36.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(16.0f, 0.0f, 35.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(-9.0f, 0.0f, 37.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(9.0f, 0.0f, 37.0f))
              LevelDsl.spawnSquad Allies 1 (Vector3(0.0f, 0.0f, 37.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(16.0f, 0.0f, -34.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(20.0f, 0.0f, -32.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-12.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-4.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(4.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-18.0f, 0.0f, -36.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(10.0f, 0.0f, -36.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -37.0f))
              LevelDsl.objective "Win the round"
              LevelDsl.trigger (Delay(Units.seconds 0.35f)) (Say("MARSHAL", "Round live. High bank east, canal west. Move.")) ]
        LevelDsl.level "Canal Yard" items |> LevelCompile.compile

    let trainingYard =
        LevelDsl.level "Training Yard"
            [ LevelDsl.street 40.0f 20.0f Mud
              LevelDsl.sandbags (Vector3(-2.0f, 0.0f, -2.0f)) (Vector3(2.0f, 0.0f, -2.0f)) None
              LevelDsl.ruin (Vector3(7.0f, 0.0f, -7.0f)) (Vector2(5.0f, 5.0f)) 3.5f Brick Intact
              LevelDsl.ruin (Vector3(-10.0f, 0.0f, -7.5f)) (Vector2(6.5f, 6.0f)) 5.4f Plaster BlownOut
              LevelDsl.block (Vector3(-5.5f, 0.45f, -5.0f)) (Vector3(1.6f, 0.9f, 1.2f)) Wood
              LevelDsl.block (Vector3(-3.9f, 0.28f, -6.1f)) (Vector3(1.2f, 0.56f, 1.7f)) Brick
              LevelDsl.block (Vector3(4.1f, 0.35f, -1.2f)) (Vector3(1.4f, 0.7f, 1.0f)) Sandbag
              LevelDsl.block (Vector3(6.2f, 0.20f, -4.1f)) (Vector3(1.8f, 0.4f, 1.5f)) Plaster
              LevelDsl.spawnSquad Allies 4 (Vector3(-8.0f, 0.0f, 8.0f))
              LevelDsl.spawnSquad Axis 6 (Vector3(8.0f, 0.0f, -10.0f))
              LevelDsl.objective "Clear the training yard"
              LevelDsl.trigger (Delay(Units.seconds 0.6f)) (Say("Sgt. Evans", "Stay low. Clear the far end of the yard."))
              LevelDsl.trigger
                  (EnterVolume { Min = Vector3(-4.0f, 0.0f, -4.0f); Max = Vector3(4.0f, 3.0f, 4.0f) })
                  (Say("Sgt. Evans", "Contact front!"))
              LevelDsl.trigger
                  (EnterVolume { Min = Vector3(-4.0f, 0.0f, -4.0f); Max = Vector3(4.0f, 3.0f, 4.0f) })
                  (SpawnWave(Axis, 4, Vector3(0.0f, 0.0f, -16.0f)))
              LevelDsl.trigger (ObjectiveDone 0) EndMission ]
        |> LevelCompile.compile

    /// Omaha Draw — the D-Day silhouette mirrored across X so both teams hold a
    /// bluff and the beach below is neutral ground. The shape runs surf, open
    /// sand, shingle and seawall, a draw cut up the bluff, then a trench line
    /// and a bunker. Three planes: beach at 0, seawall at 2.4, plateau at 5.6.
    ///
    /// Lanes are the two draws plus the bluff top between them; the beach links
    /// them at the bottom and the trench at the top, so there are two loops
    /// rather than a single head-on push.
    let omahaDraw =
        // The sand ramps up from the waterline; anything standing on it has to
        // sit at the right height, so the profile is shared rather than guessed.
        let sandTop = 1.2f
        let sandStart, sandEnd = -22.0f, -8.0f
        let sandY z = sandTop * MathEx.clamp01 ((z - sandStart) / (sandEnd - sandStart))
        let shingleTop = 2.4f
        let plateau = 5.6f
        let drawX = 18.5f
        // Bunkers sit outboard of the draws so they overlook the beach without
        // standing between a spawn and its route down.
        let bunkerX = 29.0f
        // A hedgehog: chest-high obstacle, so it reads as cover to the AI too.
        let hedgehog x z =
            LevelDsl.block (Vector3(x, sandY z + 0.6f, z)) (Vector3(1.6f, 1.2f, 1.6f)) Metal
        // Mirrored pairs keep the two halves honestly identical.
        let mirrored build = [ for sign in [ -1.0f; 1.0f ] -> build sign ]
        let items =
            [ LevelDsl.street 64.0f 34.0f Mud

              // Beach: flat wet sand at the waterline, then the long open rise.
              LevelDsl.ramp (Vector3(0.0f, 0.0f, sandStart)) (Vector3(0.0f, sandTop, sandEnd)) 68.0f Mud
              // The shingle runs right up to the draw mouths; stopping it short
              // left a 2.4 m step the player would have had to climb.
              LevelDsl.ramp (Vector3(0.0f, sandTop, sandEnd)) (Vector3(0.0f, shingleTop, -4.0f)) 68.0f Sandbag

              // Seawall along the shingle, broken by one gap per draw.
              LevelDsl.block (Vector3(0.0f, shingleTop + 0.6f, -4.5f)) (Vector3(26.0f, 1.6f, 1.2f)) Brick
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 28.5f, shingleTop + 0.6f, -4.5f)) (Vector3(11.0f, 1.6f, 1.2f)) Brick)

              // The bluff. Vertical faces, so it is a wall by slope rather than
              // by an invisible box, and play is funnelled into the draws.
              LevelDsl.block (Vector3(0.0f, plateau * 0.5f, 3.0f)) (Vector3(26.0f, plateau, 14.0f)) Mud
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * 29.0f, plateau * 0.5f, 3.0f)) (Vector3(10.0f, plateau, 14.0f)) Mud)

              // The draws: the only walkable way up, about 13 degrees.
              yield! mirrored (fun sign ->
                  LevelDsl.ramp (Vector3(sign * drawX, shingleTop, -4.0f)) (Vector3(sign * drawX, plateau, 10.0f)) 10.0f Mud)

              // Plateau behind the crest, carrying the trench and the bunkers.
              LevelDsl.block (Vector3(0.0f, plateau * 0.5f, 21.0f)) (Vector3(68.0f, plateau, 22.0f)) Mud
              // Trench walls: a channel at plateau level between two parapets.
              // Both are broken behind each draw, so the route runs beach, draw,
              // trench, plateau without ever being a dead end.
              LevelDsl.block (Vector3(0.0f, plateau + 0.7f, 11.0f)) (Vector3(27.0f, 1.4f, 2.0f)) Sandbag
              LevelDsl.block (Vector3(0.0f, plateau + 0.7f, 18.0f)) (Vector3(27.0f, 1.4f, 2.0f)) Sandbag

              // Bunkers overlooking the beach, with a real embrasure: a gap left
              // between a low front wall and a high one.
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * bunkerX, plateau + 0.45f, 20.5f)) (Vector3(9.0f, 0.9f, 1.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * bunkerX, plateau + 2.3f, 20.5f)) (Vector3(9.0f, 1.2f, 1.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * bunkerX - 4.0f, plateau + 1.45f, 22.5f)) (Vector3(1.0f, 2.9f, 5.0f)) Brick)
              yield! mirrored (fun sign -> LevelDsl.block (Vector3(sign * bunkerX + 4.0f, plateau + 1.45f, 22.5f)) (Vector3(1.0f, 2.9f, 5.0f)) Brick)

              // Focal point on the bluff top, the one landmark visible from the sand.
              LevelDsl.sandbags (Vector3(-5.0f, plateau, 6.0f)) (Vector3(5.0f, plateau, 6.0f)) None
              LevelDsl.block (Vector3(0.0f, plateau + 0.5f, 8.0f)) (Vector3(3.0f, 1.0f, 2.0f)) Metal

              // Beach clutter: hedgehogs staggered so no single line of them
              // gives a clean lane down the sand.
              hedgehog -24.0f -18.0f
              hedgehog -14.0f -14.0f
              hedgehog -6.0f -19.0f
              hedgehog 6.0f -19.0f
              hedgehog 14.0f -14.0f
              hedgehog 24.0f -18.0f
              // A wrecked landing craft: the only hard cover in the open middle.
              LevelDsl.block (Vector3(0.0f, sandY -12.0f + 0.9f, -12.0f)) (Vector3(9.0f, 1.8f, 3.4f)) Metal
              LevelDsl.block (Vector3(-4.0f, sandY -12.0f + 1.6f, -12.0f)) (Vector3(1.2f, 3.2f, 3.4f)) Metal

              // Sandbagged firing steps flanking each draw mouth.
              yield! mirrored (fun sign ->
                  LevelDsl.sandbags (Vector3(sign * 13.5f, shingleTop, -3.0f)) (Vector3(sign * 13.5f, shingleTop, 2.0f)) None)

              LevelDsl.spawnSquad Allies 8 (Vector3(-drawX, 0.0f, 28.0f))
              LevelDsl.spawnSquad Axis 8 (Vector3(drawX, 0.0f, 28.0f))
              LevelDsl.objective "Hold the draws" ]
        LevelDsl.level "Omaha Draw" items |> LevelCompile.compile

    // Training Yard stays as the dev/test fixture but is not offered on the map menu.
    let all = [| paintballArena; scrapDepot; canalYard; trainingYard; omahaDraw |]

    let byName name = all |> Array.tryFind (fun level -> level.Name = name)
