namespace Ironsight.Shell

open System
open System.IO
open System.Net.Http
open Ironsight
open Ironsight.ProcGen

/// Resolves the map a server announced (name + content hash) into a compiled
/// Level, the GoldSrc slice: use a built-in if its bytes match, then the local
/// hash-keyed cache, then download from the server over HTTP and cache it.
/// Because the cache is keyed by content hash, a stale or renamed file can
/// never produce GoldSrc's "map differs from server" — a different map is
/// simply a different cache entry.
[<RequireQualifiedAccess>]
module MapStore =
    /// Built-in maps by the hash of their encoded spec.
    let private builtins =
        lazy
            (Levels.specs
             |> Array.map (fun spec ->
                 let bytes = MapFile.encode spec
                 MapFile.hash bytes, LevelCompile.compile spec)
             |> Map.ofArray)

    let cacheDir () = Path.Combine(Settings.appDataDir (), "maps")

    let private cachePath hash = Path.Combine(cacheDir (), hash + MapFile.Extension)

    let private compileVerified (expectedHash: string) (bytes: byte array) =
        if MapFile.hash bytes <> expectedHash then Error "map bytes do not match the announced hash"
        else
            MapFile.decode bytes
            |> Result.map LevelCompile.compile

    let private fromCache hash =
        let path = cachePath hash
        if not (File.Exists path) then None
        else
            match compileVerified hash (File.ReadAllBytes path) with
            | Ok level -> Some level
            | Error _ ->
                // Corrupted or tampered cache entry: discard and redownload.
                try File.Delete path with _ -> ()
                None

    let private download (serverUri: Uri) (hash: string) =
        let scheme = if serverUri.Scheme = "wss" then "https" else "http"
        let builder = UriBuilder(serverUri, Scheme = scheme, Path = $"/maps/{hash}")
        try
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds 15.0)
            let bytes = client.GetByteArrayAsync(builder.Uri).GetAwaiter().GetResult()
            if bytes.Length > MapFile.MaxBytes then Error "server sent an oversized map"
            else
                match compileVerified hash bytes with
                | Ok level ->
                    try
                        Directory.CreateDirectory(cacheDir ()) |> ignore
                        File.WriteAllBytes(cachePath hash, bytes)
                    with _ -> () // a failed cache write only costs a redownload
                    Ok level
                | Error message -> Error $"downloaded map is invalid: {message}"
        with error ->
            Error $"map download failed: {error.Message}"

    /// Resolve the announced map. `hash` comes from the server's welcome.
    let resolve (serverUri: Uri) (hash: string) : Result<Level, string> =
        match Map.tryFind hash builtins.Value with
        | Some level -> Ok level
        | None ->
            match fromCache hash with
            | Some level -> Ok level
            | None -> download serverUri hash
