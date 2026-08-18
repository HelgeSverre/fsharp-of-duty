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

    /// Uncompiled specs, in the same order as `all` — the source material for
    /// MapFile encoding (map downloads, exports, identity hashes).
    let specs = [| PaintballMap.spec; ScrapDepotMap.spec; CanalYardMap.spec; TrainingYardMap.spec; OmahaDrawMap.spec |]

    let specByName name = specs |> Array.tryFind (fun spec -> spec.Name = name)

    // Training Yard stays as the dev/test fixture but is not offered on the map menu.
    let all = [| paintballArena; scrapDepot; canalYard; trainingYard; omahaDraw |]

    let byName name = all |> Array.tryFind (fun level -> level.Name = name)
