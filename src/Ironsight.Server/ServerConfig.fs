namespace Ironsight.Server

open System
open System.Text.Json
open Ironsight
open Ironsight.ProcGen

/// One room as written in server.json. Every field but `id` and `mode` is
/// optional; omitted ones fall back to what the server ran before rooms were
/// configurable. CLIMutable because System.Text.Json needs settable members,
/// and the ints are nullable so "absent" is distinguishable from "0".
[<CLIMutable>]
type RoomConfigFile =
    { id: string
      name: string
      mode: string
      level: string
      scoreLimit: Nullable<int>
      timeLimit: Nullable<float32>
      maxPlayers: Nullable<int> }

[<CLIMutable>]
type ServerConfigFile =
    { name: string
      motd: string
      rooms: RoomConfigFile array }

/// Server identity, alongside the rooms it hosts. The name is what the server
/// browser lists; the MOTD is whispered to each joiner.
type ServerIdentity = { Name: string; Motd: string }

/// A validated room: every default already applied, so nothing downstream
/// deals in options or has to know what the fallbacks were.
type RoomConfig =
    { Id: string
      Name: string
      Mode: GameMode
      Level: Level
      ScoreLimit: int
      TimeLimit: float32<s>
      MaxPlayers: int }

[<RequireQualifiedAccess>]
module ServerConfig =
    /// The cap every room ran under when it was a literal in MatchHost.
    let DefaultMaxPlayers = 16

    let private jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let private parseMode (value: string) =
        match value with
        | value when String.Equals(value, "FreeForAll", StringComparison.OrdinalIgnoreCase) -> FreeForAll
        | value when String.Equals(value, "TeamDeathmatch", StringComparison.OrdinalIgnoreCase) -> TeamDeathmatch
        | other -> failwith $"server config: room mode '{other}' is not TeamDeathmatch or FreeForAll."

    /// Builtin aliases only. byAlias compiles just this room's map and caches
    /// it, so two rooms on one map share the instance — and the client
    /// hot-swaps a builtin by name, with no map download.
    let private parseLevel (alias: string) =
        Levels.byAlias alias
        |> Option.defaultWith (fun () ->
            let known = String.concat ", " Levels.offlineAliases
            failwith $"server config: level '{alias}' is not a builtin ({known}).")

    /// Blank name means the browser keeps showing whatever the player called
    /// this server in their own bookmarks; blank MOTD means no greeting.
    let defaultIdentity = { Name = ""; Motd = "" }

    /// The two rooms the server hosted before it read a config at all. Used
    /// verbatim when there is no config file, so an existing deployment keeps
    /// working after this change.
    let defaultRooms (level: Level) =
        [| { Id = "tdm"
             Name = "Team Deathmatch"
             Mode = TeamDeathmatch
             Level = level
             ScoreLimit = Multiplayer.scoreLimit TeamDeathmatch
             TimeLimit = Multiplayer.defaultTimeLimit
             MaxPlayers = DefaultMaxPlayers }
           { Id = "ffa"
             Name = "Free For All"
             Mode = FreeForAll
             Level = level
             ScoreLimit = Multiplayer.scoreLimit FreeForAll
             TimeLimit = Multiplayer.defaultTimeLimit
             MaxPlayers = DefaultMaxPlayers } |]

    let private validate (rooms: RoomConfig array) =
        if Array.isEmpty rooms then failwith "server config: 'rooms' is empty; remove the file to get the defaults."
        let duplicate =
            rooms
            |> Array.countBy (fun room -> room.Id.ToLowerInvariant())
            |> Array.tryFind (fun (_, count) -> count > 1)
        match duplicate with
        | Some(id, _) -> failwith $"server config: duplicate room id '{id}'."
        | None -> rooms

    /// Fails loudly rather than falling back to defaults: a server config that
    /// is silently ignored is an operator trap. The client's Settings.load
    /// swallows errors on purpose, but that is a player's preferences file.
    let parse (fallbackLevel: Level) (json: string) =
        let file =
            try JsonSerializer.Deserialize<ServerConfigFile>(json, jsonOptions)
            with :? JsonException as ex -> failwith $"server config: not valid JSON — {ex.Message}"
        if isNull (box file.rooms) then failwith "server config: no 'rooms' array."
        let identity =
            { Name = Multiplayer.sanitizeText 32 file.name
              // Same filter every other player-visible string goes through: the
              // MOTD lands in a chat log, so it cannot carry control scalars.
              Motd = Multiplayer.sanitizeText 120 file.motd }
        let rooms =
            file.rooms
            |> Array.mapi (fun index room ->
                let id = if isNull room.id then "" else room.id.Trim()
                if id = "" then failwith $"server config: room {index} has no id."
                let mode = parseMode (if isNull room.mode then "" else room.mode)
                { Id = id
                  Name = if String.IsNullOrWhiteSpace room.name then id else room.name.Trim()
                  Mode = mode
                  Level = if String.IsNullOrWhiteSpace room.level then fallbackLevel else parseLevel room.level
                  ScoreLimit =
                    if room.scoreLimit.HasValue && room.scoreLimit.Value > 0 then room.scoreLimit.Value
                    else Multiplayer.scoreLimit mode
                  TimeLimit =
                    if room.timeLimit.HasValue && room.timeLimit.Value > 0.0f then Units.seconds room.timeLimit.Value
                    else Multiplayer.defaultTimeLimit
                  MaxPlayers =
                    if room.maxPlayers.HasValue && room.maxPlayers.Value > 0 then room.maxPlayers.Value
                    else DefaultMaxPlayers })
            |> validate
        identity, rooms

    /// IRONSIGHT_CONFIG, else ./server.json. A missing file is not an error —
    /// it is the single-map, two-room server everyone has today.
    let load (fallbackLevel: Level) =
        let path =
            match Environment.GetEnvironmentVariable "IRONSIGHT_CONFIG" with
            | value when not (String.IsNullOrWhiteSpace value) -> value
            | _ -> "server.json"
        if IO.File.Exists path then parse fallbackLevel (IO.File.ReadAllText path)
        else defaultIdentity, defaultRooms fallbackLevel
