// Exports every built-in map spec to ./maps/<slug>.ironmap. The files are
// reference material for custom-map authors and real inputs for the server's
// IRONSIGHT_LEVEL=<path>.ironmap and the client's --map flag.
//
//   dotnet fsi tools/map-export.fsx [outputDir]

#r "../src/Ironsight.Core/bin/Debug/net10.0/Ironsight.Core.dll"

open System
open System.IO
open Ironsight.ProcGen

let outputDir =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.tryHead with
    | Some path -> path
    | None -> "maps"

Directory.CreateDirectory outputDir |> ignore

for spec in Levels.specs do
    let slug = spec.Name.ToLowerInvariant().Replace(" ", "-")
    let bytes = MapFile.encode spec
    let path = Path.Combine(outputDir, slug + MapFile.Extension)
    File.WriteAllBytes(path, bytes)
    printfn $"{path}  {bytes.Length} bytes  sha256:{MapFile.hash bytes}"
