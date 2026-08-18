namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module PaintballMap =
    let spec =
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
        LevelDsl.level "Paintball Killhouse" arenaItems

    /// Rust-inspired scrap arena: a climbable central mound owns the middle,
    /// a blown-out warehouse anchors the north-east, and a railcar wall splits
    /// the west flank. Deliberately asymmetric — no lane mirrors another.
