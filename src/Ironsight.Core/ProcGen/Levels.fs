namespace Ironsight.ProcGen

open System.Collections.Generic
open Ironsight

/// One row of the built-in map registry: what the map is called, the aliases
/// that reach it, whether the offline menu lists it, and how to load its spec.
type BuiltinMap =
    { /// Equal to the loaded spec's Name (the registry guard test asserts it),
      /// so lookups and menu labels never load a map to learn its name.
      Title: string
      /// Every alias that resolves to this map; the first one is what the
      /// offline menu and /map help print.
      Aliases: string array
      OnMenu: bool
      /// Loading is deferred behind the thunk: touching one entry's spec reads
      /// one map's files, not all of them.
      LoadSpec: unit -> LevelSpec }

/// Registry over the built-in maps. Each map's spec lives in its own file
/// under Maps/; this registry loads and compiles a map on first use and
/// caches it.
///
/// A static class rather than a module on purpose: module bindings evaluate
/// at type initialization, which would compile every map the moment any one
/// is touched. Static members keep each accessor lazy.
[<Sealed; AbstractClass>]
type Levels private () =
    static let cache = Dictionary<string, Level>()

    /// The one table. Menu order; off-menu fixtures at the end.
    static member builtins: BuiltinMap array =
        [| { Title = "Paintball Killhouse"; Aliases = [| "paintball" |]; OnMenu = true; LoadSpec = fun () -> PaintballMap.spec }
           { Title = "Scrap Depot"; Aliases = [| "depot" |]; OnMenu = true; LoadSpec = fun () -> ScrapDepotMap.spec }
           { Title = "Canal Yard"; Aliases = [| "canal" |]; OnMenu = true; LoadSpec = fun () -> CanalYardMap.spec }
           { Title = "Omaha Draw"; Aliases = [| "omaha" |]; OnMenu = true; LoadSpec = fun () -> OmahaDrawMap.spec }
           { Title = "Rust"; Aliases = [| "rust" |]; OnMenu = true; LoadSpec = fun () -> RustMap.spec }
           { Title = "Killhouse"; Aliases = [| "killhouse" |]; OnMenu = true; LoadSpec = fun () -> KillhouseMap.spec }
           { Title = "Dust II"; Aliases = [| "dust2"; "dust" |]; OnMenu = true; LoadSpec = fun () -> Dust2Map.spec }
           { Title = "Pool Day"; Aliases = [| "poolday"; "pool"; "fy_pool_day" |]; OnMenu = true; LoadSpec = fun () -> PoolDayMap.spec }
           { Title = "Office"; Aliases = [| "office"; "cs_office" |]; OnMenu = true; LoadSpec = fun () -> OfficeMap.spec }
           { Title = "Iceworld"; Aliases = [| "ice"; "iceworld"; "fy_iceworld" |]; OnMenu = true; LoadSpec = fun () -> ClassicBspMaps.iceworld }
           { Title = "Snow"; Aliases = [| "snow"; "fy_snow" |]; OnMenu = true; LoadSpec = fun () -> ClassicBspMaps.snow }
           { Title = "Aim Map"; Aliases = [| "aim"; "aimmap"; "aim_map" |]; OnMenu = true; LoadSpec = fun () -> ClassicBspMaps.aimMap }
           { Title = "AWP India"; Aliases = [| "awpindia"; "india"; "awp_india" |]; OnMenu = true; LoadSpec = fun () -> ClassicBspMaps.awpIndia }
           { Title = "Rats 2"; Aliases = [| "rats"; "rats2"; "de_rats2" |]; OnMenu = true; LoadSpec = fun () -> ClassicBspMaps.rats2 }
           // Training Yard stays as the dev/test fixture but is not on the menu.
           { Title = "Training Yard"; Aliases = [| "training" |]; OnMenu = false; LoadSpec = fun () -> TrainingYardMap.spec } |]

    /// The compiled level for a spec, compiling on first request. The cache is
    /// keyed by name and shared with every accessor below, so a map is only
    /// ever compiled once per process however it was reached.
    // ponytail: one lock around compilation, so two rooms racing for different
    // maps compile serially. Boot-time only; per-name locks if it ever matters.
    static member ofSpec(spec: LevelSpec) =
        lock cache (fun () ->
            match cache.TryGetValue spec.Name with
            | true, level -> level
            | _ ->
                let level = LevelCompile.compile spec
                cache[spec.Name] <- level
                level)

    /// Uncompiled specs — the source material for MapFile encoding (map
    /// downloads, exports, identity hashes). Touching this loads every map
    /// file; compiled levels stay lazy.
    static member specs = Levels.builtins |> Array.map (fun entry -> entry.LoadSpec())

    static member specByAlias(alias: string) =
        let wanted = if isNull alias then "" else alias.ToLowerInvariant()
        Levels.builtins
        |> Array.tryFind (fun entry -> Array.contains wanted entry.Aliases)
        |> Option.map (fun entry -> entry.LoadSpec())

    /// The aliases offered on the offline map menu, in menu order.
    static member offlineAliases =
        Levels.builtins
        |> Array.filter (fun entry -> entry.OnMenu)
        |> Array.map (fun entry -> entry.Aliases[0])

    static member paintballArena = Levels.ofSpec PaintballMap.spec
    static member scrapDepot = Levels.ofSpec ScrapDepotMap.spec
    static member canalYard = Levels.ofSpec CanalYardMap.spec
    static member trainingYard = Levels.ofSpec TrainingYardMap.spec
    static member omahaDraw = Levels.ofSpec OmahaDrawMap.spec
    static member rust = Levels.ofSpec RustMap.spec
    static member killhouse = Levels.ofSpec KillhouseMap.spec
    static member dust2 = Levels.ofSpec Dust2Map.spec
    static member poolDay = Levels.ofSpec PoolDayMap.spec
    static member office = Levels.ofSpec OfficeMap.spec
    static member aimMap = Levels.ofSpec ClassicBspMaps.aimMap
    static member awpIndia = Levels.ofSpec ClassicBspMaps.awpIndia
    static member rats2 = Levels.ofSpec ClassicBspMaps.rats2
    static member iceworld = Levels.ofSpec ClassicBspMaps.iceworld
    static member snow = Levels.ofSpec ClassicBspMaps.snow

    /// The compiled builtin with this name, compiling it on first request.
    /// Registry titles equal spec names by construction, so the search itself
    /// loads nothing.
    static member byName name =
        Levels.builtins
        |> Array.tryFind (fun entry -> entry.Title = name)
        |> Option.map (fun entry -> Levels.ofSpec (entry.LoadSpec()))

    /// The compiled level behind a menu/argv alias.
    static member byAlias alias =
        Levels.specByAlias alias |> Option.map Levels.ofSpec
