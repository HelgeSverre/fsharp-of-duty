namespace Ironsight.Shell

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Ironsight

/// One connectable game server, as listed in servers.json.
type ServerEntry = { Name: string; Url: Uri }

/// One row of the Half-Life-style server browser: a room on a server.
type ServerRow =
    { Server: ServerEntry
      Mode: GameMode
      Phase: string
      Players: int
      Capacity: int
      PingMs: int
      Online: bool }

/// The master server list, GameSpy/QuakeWorld style but with no infrastructure:
/// a servers.json packaged with the game, a user-editable copy in appdata, and
/// a canonical community copy in the repo fetched over raw GitHub (cached for
/// offline runs). Adding a server is editing a JSON file — or PRing the repo.
[<RequireQualifiedAccess>]
module ServerDirectory =
    [<Literal>]
    let DefaultServer = "wss://fsharp-of-duty.fly.dev/play"

    [<Literal>]
    let MasterListUrl = "https://raw.githubusercontent.com/HelgeSverre/fsharp-of-duty/main/servers.json"

    /// Tolerant parse: junk entries are skipped, junk documents yield [].
    let parse (json: string) =
        try
            use document = JsonDocument.Parse json
            document.RootElement.GetProperty("servers").EnumerateArray()
            |> Seq.choose (fun entry ->
                try
                    let name = entry.GetProperty("name").GetString()
                    let url = Uri(entry.GetProperty("url").GetString())
                    if (url.Scheme = "ws" || url.Scheme = "wss") && not (String.IsNullOrWhiteSpace name) then
                        Some { Name = name.Trim(); Url = url }
                    else None
                with _ -> None)
            |> Seq.toList
        with _ -> []

    /// First occurrence wins, so list order encodes precedence.
    let merge (lists: ServerEntry list list) =
        List.concat lists
        |> List.distinctBy (fun entry -> entry.Url.AbsoluteUri.ToLowerInvariant())

    let private readFileEntries path =
        try
            if File.Exists path then parse (File.ReadAllText path) else []
        with _ -> []

    let userListPath () = Path.Combine(Settings.appDataDir (), "servers.json")
    let private remoteCachePath () = Path.Combine(Settings.appDataDir (), "servers-remote.json")
    let private packagedListPath () = Path.Combine(AppContext.BaseDirectory, "servers.json")

    /// The community list from the repo; cached so offline runs keep it.
    let private remoteEntries () =
        try
            use http = new HttpClient(Timeout = TimeSpan.FromSeconds 3.0)
            let json = http.GetStringAsync(Uri MasterListUrl).GetAwaiter().GetResult()
            let entries = parse json
            if not entries.IsEmpty then
                try
                    Directory.CreateDirectory(Settings.appDataDir ()) |> ignore
                    File.WriteAllText(remoteCachePath (), json)
                with _ -> ()
            entries
        with _ -> readFileEntries (remoteCachePath ())

    /// Precedence: IRONSIGHT_SERVER env override, the user's own appdata list,
    /// the community master list, the packaged list, the compiled-in default.
    /// Blocking (network); call off the main thread.
    let load () =
        let envEntry =
            match Environment.GetEnvironmentVariable "IRONSIGHT_SERVER" with
            | value when not (String.IsNullOrWhiteSpace value) ->
                try [ { Name = "Env Override"; Url = Uri value } ] with _ -> []
            | _ -> []
        merge
            [ envEntry
              readFileEntries (userListPath ())
              remoteEntries ()
              readFileEntries (packagedListPath ())
              [ { Name = "Official Fly.io"; Url = Uri DefaultServer } ] ]

    /// Probe every server in parallel: one leaderboard GET per server yields
    /// both the ping and the per-room player counts. Unreachable servers still
    /// get a row so the table shows them as offline.
    let probeAll (servers: ServerEntry list) : Task<ServerRow list> =
        task {
            let probeOne (server: ServerEntry) =
                Task.Run(fun () ->
                    try
                        let scheme = if server.Url.Scheme = "wss" then "https" else "http"
                        let target = UriBuilder(server.Url, Scheme = scheme, Path = "/api/leaderboard").Uri
                        use http = new HttpClient(Timeout = TimeSpan.FromSeconds 4.0)
                        let clock = Diagnostics.Stopwatch.StartNew()
                        let json = http.GetStringAsync(target).GetAwaiter().GetResult()
                        let ping = int clock.ElapsedMilliseconds
                        use document = JsonDocument.Parse json
                        let root = document.RootElement
                        let capacity =
                            match root.TryGetProperty "capacityPerRoom" with
                            | true, value -> value.GetInt32()
                            | _ -> 16
                        root.GetProperty("rooms").EnumerateArray()
                        |> Seq.map (fun room ->
                            { Server = server
                              Mode = (if room.GetProperty("mode").GetString() = "FreeForAll" then FreeForAll else TeamDeathmatch)
                              Phase = room.GetProperty("phase").GetString()
                              Players = room.GetProperty("connectedPlayers").GetInt32()
                              Capacity = capacity
                              PingMs = ping
                              Online = true })
                        |> Seq.toList
                    with _ ->
                        [ { Server = server; Mode = TeamDeathmatch; Phase = ""; Players = 0; Capacity = 0; PingMs = 0; Online = false } ])
            let! results = servers |> List.map probeOne |> Task.WhenAll
            return results |> Array.toList |> List.concat
        }
