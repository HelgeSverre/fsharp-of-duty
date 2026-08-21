namespace Ironsight.ProcGen

open Ironsight

/// Classic community Counter-Strike maps loaded directly from BSP30 files.
[<RequireQualifiedAccess>]
module ClassicBspMaps =
    let private spec displayName path announcement =
        let map = GoldSrcBsp.load path
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
                  (Say("MARSHAL", announcement)) ]
        LevelDsl.level displayName items

    let aimMap = spec "Aim Map" "maps/aim_map.bsp" "Aim Map. The original BSP is live."

    let awpIndia = spec "AWP India" "maps/awp_india.bsp" "AWP India. Watch the long sightlines."

    let rats2 = spec "Rats 2" "maps/de_rats2.bsp" "Rats 2. Everything is bigger than you."

    let iceworld = spec "Iceworld" "maps/fy_iceworld.bsp" "Iceworld. Grab a weapon and move."

    let snow = spec "Snow" "maps/fy_snow.bsp" "Snow. The fight starts immediately."
