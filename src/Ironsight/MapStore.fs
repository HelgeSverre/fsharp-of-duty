namespace Ironsight.Shell

open System
open System.IO
open System.Net.Http
open System.Threading
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
    /// Shared across every HTTP call this process makes (map downloads, the
    /// server directory's master list and probes): one HttpClient per process
    /// is the documented pattern, since a fresh instance per call exhausts
    /// sockets under churn (ServerDirectory's probe alone fires once per
    /// server every 5s). No Timeout here — each call supplies its own via a
    /// CancellationTokenSource so one slow server can't cap every other call.
    let http = new HttpClient()

    /// The server's ws(s) URI translated to http(s) at the given path — the
    /// game protocol runs on a WebSocket, but map sync and the directory
    /// probe are plain HTTP requests to the same host.
    let httpUri (serverUri: Uri) (path: string) : Uri =
        let scheme = if serverUri.Scheme = "wss" then "https" else "http"
        UriBuilder(serverUri, Scheme = scheme, Path = path).Uri

    /// Built-in maps by the hash of their encoded spec.
    let private builtins =
        lazy
            (Levels.specs
             // A prop-bearing builtin has no encoding, so it can never be
             // hash-addressed; it is found by name instead.
             |> Array.filter MapFile.encodable
             |> Array.map (fun spec ->
                 let bytes = MapFile.encode spec
                 MapFile.hash bytes, LevelCompile.compile spec)
             |> Map.ofArray)

    let cacheDir () = Path.Combine(Settings.appDataDir (), "maps")

    let private cachePath hash = Path.Combine(cacheDir (), hash + MapFile.Extension)

    /// A content hash is exactly 64 lowercase hex chars (SHA-256). This is what makes it
    /// safe to use directly as a filename (no path traversal) and as a URL path segment.
    let private validHash (hash: string) =
        hash.Length = 64 && hash |> Seq.forall (fun c -> Char.IsAsciiDigit c || (c >= 'a' && c <= 'f'))

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
        let uri = httpUri serverUri $"/maps/{hash}"
        try
            use timeout = new CancellationTokenSource(TimeSpan.FromSeconds 15.0)
            let bytes = http.GetByteArrayAsync(uri, timeout.Token).GetAwaiter().GetResult()
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
        if not (validHash hash) then
            Error "server sent a malformed map hash"
        else
            match Map.tryFind hash builtins.Value with
            | Some level -> Ok level
            | None ->
                match fromCache hash with
                | Some level -> Ok level
                | None -> download serverUri hash
