namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module CanalYardMap =
    /// Carentan-inspired canal arena: the east bank sits 1.2 m above the yard
    /// and is only reachable by two stair sets; the west side answers with a
    /// sunken canal lane and ruined houses. High ground versus covered rotate.
    let spec =
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
              // On the raised east bank, said out loud: spawns snap to the
              // ground nearest the height they are written at, so a bank spawn
              // written at y = 0 lands in the yard below it.
              LevelDsl.spawnSquad Axis 1 (Vector3(16.0f, 1.2f, -34.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(20.0f, 1.2f, -32.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-12.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-4.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(4.0f, 0.0f, -35.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(-18.0f, 0.0f, -36.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(10.0f, 0.0f, -36.0f))
              LevelDsl.spawnSquad Axis 1 (Vector3(0.0f, 0.0f, -37.0f))
              LevelDsl.objective "Win the round"
              LevelDsl.trigger (Delay(Units.seconds 0.35f)) (Say("MARSHAL", "Round live. High bank east, canal west. Move.")) ]
        LevelDsl.level "Canal Yard" items
