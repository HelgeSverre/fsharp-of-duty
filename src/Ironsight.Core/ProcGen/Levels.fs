namespace Ironsight.ProcGen

/// Registry over the built-in maps. Each map's spec lives in its own file
/// under Maps/; this module compiles them once and exposes lookups by name.
[<RequireQualifiedAccess>]
module Levels =
    let paintballArena = LevelCompile.compile PaintballMap.spec
    let scrapDepot = LevelCompile.compile ScrapDepotMap.spec
    let canalYard = LevelCompile.compile CanalYardMap.spec
    let trainingYard = LevelCompile.compile TrainingYardMap.spec
    let omahaDraw = LevelCompile.compile OmahaDrawMap.spec
    let rust = LevelCompile.compile RustMap.spec
    let killhouse = LevelCompile.compile KillhouseMap.spec
    let dust2 = LevelCompile.compile Dust2Map.spec
    let poolDay = LevelCompile.compile PoolDayMap.spec
    let office = LevelCompile.compile OfficeMap.spec
    let aimMap = LevelCompile.compile ClassicBspMaps.aimMap
    let awpIndia = LevelCompile.compile ClassicBspMaps.awpIndia
    let rats2 = LevelCompile.compile ClassicBspMaps.rats2
    let iceworld = LevelCompile.compile ClassicBspMaps.iceworld
    let snow = LevelCompile.compile ClassicBspMaps.snow

    /// Uncompiled specs, in the same order as `all` — the source material for
    /// MapFile encoding (map downloads, exports, identity hashes).
    let specs =
        [| PaintballMap.spec; ScrapDepotMap.spec; CanalYardMap.spec; TrainingYardMap.spec
           OmahaDrawMap.spec; RustMap.spec; KillhouseMap.spec; Dust2Map.spec; PoolDayMap.spec; OfficeMap.spec
           ClassicBspMaps.aimMap; ClassicBspMaps.awpIndia; ClassicBspMaps.rats2; ClassicBspMaps.iceworld
           ClassicBspMaps.snow |]

    /// Short aliases shared by the client's map menu / argv and the server's
    /// IRONSIGHT_LEVEL — the one table both sides resolve builtin maps from.
    let specByAlias (alias: string) =
        match (if isNull alias then "" else alias.ToLowerInvariant()) with
        | "paintball" -> Some PaintballMap.spec
        | "depot" -> Some ScrapDepotMap.spec
        | "canal" -> Some CanalYardMap.spec
        | "omaha" -> Some OmahaDrawMap.spec
        | "rust" -> Some RustMap.spec
        | "killhouse" -> Some KillhouseMap.spec
        | "dust2" | "dust" -> Some Dust2Map.spec
        | "poolday" | "pool" | "fy_pool_day" -> Some PoolDayMap.spec
        | "office" | "cs_office" -> Some OfficeMap.spec
        | "aim" | "aimmap" | "aim_map" -> Some ClassicBspMaps.aimMap
        | "awpindia" | "india" | "awp_india" -> Some ClassicBspMaps.awpIndia
        | "rats" | "rats2" | "de_rats2" -> Some ClassicBspMaps.rats2
        | "ice" | "iceworld" | "fy_iceworld" -> Some ClassicBspMaps.iceworld
        | "snow" | "fy_snow" -> Some ClassicBspMaps.snow
        // Training Yard stays as the dev/test fixture but is not on the menu.
        | "training" -> Some TrainingYardMap.spec
        | _ -> None

    /// The aliases offered on the offline map menu, in menu order.
    let offlineAliases =
        [| "paintball"; "depot"; "canal"; "omaha"; "rust"; "killhouse"; "dust2"; "poolday"; "office"
           "iceworld"; "snow"; "aim"; "awpindia"; "rats2" |]

    let all =
        [| paintballArena; scrapDepot; canalYard; trainingYard; omahaDraw; rust; killhouse; dust2; poolDay; office
           aimMap; awpIndia; rats2; iceworld; snow |]

    let byName name = all |> Array.tryFind (fun level -> level.Name = name)

    /// The compiled level behind a menu/argv alias. Goes through specByAlias so
    /// there is exactly one alias table in the codebase.
    let byAlias alias = specByAlias alias |> Option.bind (fun spec -> byName spec.Name)
