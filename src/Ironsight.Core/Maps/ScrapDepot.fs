namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module ScrapDepotMap =
    /// Rust-inspired scrap arena: a climbable central mound owns the middle,
    /// a blown-out warehouse anchors the north-east, and a railcar wall splits
    /// the west flank. Deliberately asymmetric — no lane mirrors another.
    let spec =
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
        LevelDsl.level "Scrap Depot" items

    /// Carentan-inspired canal arena: the east bank sits 1.2 m above the yard
    /// and is only reachable by two stair sets; the west side answers with a
    /// sunken canal lane and ruined houses. High ground versus covered rotate.
