namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module TrainingYardMap =
    let spec =
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

    /// Omaha Draw — the D-Day silhouette mirrored across X so both teams hold a
    /// bluff and the beach below is neutral ground. The shape runs surf, open
    /// sand, shingle and seawall, a draw cut up the bluff, then a trench line
    /// and a bunker. Three planes: beach at 0, seawall at 2.4, plateau at 5.6.
    ///
    /// Lanes are the two draws plus the bluff top between them; the beach links
    /// them at the bottom and the trench at the top, so there are two loops
    /// rather than a single head-on push.
