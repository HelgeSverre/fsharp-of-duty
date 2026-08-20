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

    /// Uncompiled specs, in the same order as `all` — the source material for
    /// MapFile encoding (map downloads, exports, identity hashes).
    let specs =
        [| PaintballMap.spec; ScrapDepotMap.spec; CanalYardMap.spec; TrainingYardMap.spec
           OmahaDrawMap.spec; RustMap.spec; KillhouseMap.spec |]

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
        // Training Yard stays as the dev/test fixture but is not on the menu.
        | "training" -> Some TrainingYardMap.spec
        | _ -> None

    /// The aliases offered on the offline map menu, in menu order.
    let offlineAliases = [| "paintball"; "depot"; "canal"; "omaha"; "rust"; "killhouse" |]

    let all = [| paintballArena; scrapDepot; canalYard; trainingYard; omahaDraw; rust; killhouse |]

    let byName name = all |> Array.tryFind (fun level -> level.Name = name)

    /// The compiled level behind a menu/argv alias. Goes through specByAlias so
    /// there is exactly one alias table in the codebase.
    let byAlias alias = specByAlias alias |> Option.bind (fun spec -> byName spec.Name)
