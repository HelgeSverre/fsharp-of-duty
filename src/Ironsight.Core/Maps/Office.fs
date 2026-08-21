namespace Ironsight.ProcGen

open Ironsight

/// cs_office loaded directly from its original Counter-Strike BSP30 file.
[<RequireQualifiedAccess>]
module OfficeMap =
    let private source = lazy (GoldSrcBsp.load "maps/cs_office.bsp")

    let spec =
        let map = source.Value
        let items =
            [ yield LevelDsl.texturedStaticWorld map.WorldMesh map.WorldBounds map.Atlas
              for item in map.Breakables do
                  yield LevelDsl.breakableWorld item.Id item.Mesh item.Bounds
              for struct (team, position) in map.PlayerSpawns do
                  yield LevelDsl.spawnSquad team 1 position
              for ladder in map.Climbables do
                  yield LevelDsl.ladder ladder.LadderFoot ladder.LadderHeight ladder.LadderFacing
              yield LevelDsl.objective "Eliminate the opposing team"
              yield LevelDsl.trigger
                  (Delay(Units.seconds 0.35f))
                  (Say("MARSHAL", "Office. The original BSP is live.")) ]
        LevelDsl.level "Office" items
