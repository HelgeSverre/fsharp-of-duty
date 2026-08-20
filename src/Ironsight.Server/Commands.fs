namespace Ironsight.Server

open System
open Ironsight
open Ironsight.ProcGen

type CommandLevel =
    | Everyone
    | Op

type CommandContext =
    { PlayerId: EntityId
      Host: MatchHost
      /// Whispers back to the caller. Command output never reaches anyone
      /// else — /op would otherwise leak the key into every chat log.
      Reply: string -> unit
      /// Exactly the commands this caller may run.
      Visible: Command list }

and Command =
    { Verb: string
      Level: CommandLevel
      Usage: string
      Run: CommandContext -> string list -> unit }

type ServerExtension =
    { Name: string
      Commands: Command list
      /// Every replicated event, after the tick that produced it. Runs outside
      /// the room gate, so a hook may call back into the host.
      OnEvent: (MatchHost -> ReplicatedEvent -> unit) option
      /// End of tick, once per room. Runs inside the tick loop's fault
      /// isolation, so a throwing hook logs rather than killing the host.
      OnTick: (MatchHost -> MatchState -> unit) option }

[<RequireQualifiedAccess>]
module ServerExtension =
    /// Name only; add the hooks you need with `{ empty with OnTick = ... }`.
    let empty name =
        { Name = name; Commands = []; OnEvent = None; OnTick = None }

/// Address-based bans, the only durable identity this server has: names are
/// free to choose and session tokens only resume a slot. Server-wide rather
/// than per-room, since a ban the player dodges by switching rooms is no ban.
///
/// A shared or carrier-NAT address bans everyone behind it, and a residential
/// lease moves — so this stops a specific nuisance now, not a determined one
/// forever. That is the same bargain every ban list of this kind makes.
[<RequireQualifiedAccess>]
module Bans =
    let private gate = obj ()
    let private path = Environment.GetEnvironmentVariable "IRONSIGHT_BAN_LIST"

    /// Live players' addresses, so /ban can turn a name into something to ban.
    /// Populated as connections arrive and dropped when they end.
    let mutable private addresses: Map<EntityId, string> = Map.empty

    let private load () =
        if String.IsNullOrWhiteSpace path || not (IO.File.Exists path) then Set.empty
        else
            try IO.File.ReadAllLines path |> Seq.map (fun line -> line.Trim()) |> Seq.filter (fun line -> line <> "") |> Set.ofSeq
            with ex ->
                eprintfn $"[bans] could not read '{path}': {ex.Message}"
                Set.empty

    let mutable private banned = load ()

    /// Behind Fly the socket's peer is the proxy, so banning RemoteIpAddress
    /// would ban every player at once. Fly-Client-IP is set by the edge and is
    /// the only header here that a client cannot forge. X-Forwarded-For is
    /// accepted only as a fallback for other proxies, taking the *last* hop:
    /// proxies append themselves left to right, so the rightmost entry is the
    /// one your own proxy observed. The first hop is whatever the client
    /// typed — trusting it would let a socket both dodge a ban and frame
    /// someone else's address into one.
    let clientAddress (flyClientIp: string) (forwardedFor: string) (remote: string) =
        if not (String.IsNullOrWhiteSpace flyClientIp) then flyClientIp.Trim()
        elif not (String.IsNullOrWhiteSpace forwardedFor) then
            forwardedFor.Split(',') |> Array.last |> fun value -> value.Trim()
        else remote

    let isBanned address =
        not (String.IsNullOrWhiteSpace address) && lock gate (fun () -> Set.contains address banned)

    let remember id address = lock gate (fun () -> addresses <- Map.add id address addresses)
    let forget id = lock gate (fun () -> addresses <- Map.remove id addresses)
    let addressOf id = lock gate (fun () -> Map.tryFind id addresses)

    let ban address =
        lock gate (fun () ->
            if Set.contains address banned then false
            else
                banned <- Set.add address banned
                if not (String.IsNullOrWhiteSpace path) then
                    try IO.File.AppendAllLines(path, [ address ])
                    with ex -> eprintfn $"[bans] append to '{path}' failed: {ex.Message}"
                true)

    let count () = lock gate (fun () -> Set.count banned)

[<RequireQualifiedAccess>]
module Commands =
    let private command verb level usage run =
        { Verb = verb; Level = level; Usage = usage; Run = run }

    let private help =
        command "help" Everyone "/help - list the commands you can run" (fun context _ ->
            for entry in context.Visible do context.Reply entry.Usage)

    let private op =
        command "op" Everyone "/op <key> - elevate with the server's op key" (fun context arguments ->
            // The address rides along so the guess throttle survives a
            // reconnect; see MatchHost.TryElevate.
            match arguments with
            | [ key ] when context.Host.TryElevate(context.PlayerId, Bans.addressOf context.PlayerId, key) -> context.Reply "You are now an op."
            | _ -> context.Reply "Rejected.")

    let private say =
        // Sender None renders as a highlighted system line client-side.
        command "say" Op "/say <text> - broadcast a server announcement" (fun context arguments ->
            match Multiplayer.sanitizeText 120 (String.concat " " arguments) with
            | "" -> context.Reply "Usage: /say <text>"
            | line -> context.Host.Enqueue(Chat(None, "", line)))

    let private kick =
        // Kick stays deliberately soft: drop him now, let him come back.
        command "kick" Op "/kick <name> - drop a player now (he may rejoin; see /ban)" (fun context arguments ->
            match context.Host.Kick(String.concat " " arguments) with
            | Some name -> context.Reply $"Kicked {name}."
            | None -> context.Reply "No connected player by that name.")

    let private ban =
        command "ban" Op "/ban <name> - kick a player and refuse his address from now on" (fun context arguments ->
            let name = String.concat " " arguments
            // Resolve the address before the kick: the disconnect drops him
            // from the address table, and then there is nothing left to ban.
            match context.Host.FindConnected name with
            | None -> context.Reply "No connected player by that name."
            | Some(id, matched) ->
                match Bans.addressOf id with
                | None -> context.Reply $"No address on record for {matched}; kicked only."
                | Some address ->
                    let added = Bans.ban address
                    context.Host.Kick matched |> ignore
                    if added then context.Reply $"Banned {matched} ({address})."
                    else context.Reply $"{matched} ({address}) was already banned; kicked again.")

    let private map =
        command "map" Op $"""/map <alias> - queue a builtin map for the next round ({String.concat ", " Levels.offlineAliases})""" (fun context arguments ->
            // Builtin aliases only: the client hot-swaps a level by name.
            let requested = Levels.specByAlias (String.concat " " arguments) |> Option.bind (fun spec -> Levels.byName spec.Name)
            match requested with
            | Some level ->
                context.Host.RequestLevel level
                context.Reply $"{level.Name} starts after this round."
            | None -> context.Reply "Unknown map. Builtins only.")

    let private restart =
        command "restart" Op "/restart - end the round now and reset scores" (fun context _ ->
            context.Host.Restart()
            context.Reply "Round restarted.")

    let builtins =
        { ServerExtension.empty "builtin" with Commands = [ help; op; say; kick; ban; map; restart ] }

    let private run (extensions: ServerExtension list) (host: MatchHost) (playerId: EntityId) (line: string) =
        let isOp = host.IsOp playerId
        let visible =
            extensions
            |> List.collect (fun extension -> extension.Commands)
            |> List.filter (fun entry -> entry.Level = Everyone || isOp)
        let reply text = host.Enqueue(Chat(None, "", text), recipient = playerId)
        let words =
            (Multiplayer.sanitizeText 120 line).TrimStart('/').Split(' ', StringSplitOptions.RemoveEmptyEntries)
            |> List.ofArray
        match words with
        | [] -> ()
        | verb :: arguments ->
            match visible |> List.tryFind (fun entry -> String.Equals(entry.Verb, verb, StringComparison.OrdinalIgnoreCase)) with
            | Some entry -> entry.Run { PlayerId = playerId; Host = host; Reply = reply; Visible = visible } arguments
            | None -> reply $"Unknown command '/{verb}'. Try /help."

    /// The one entry point for a client chat line. A leading slash makes it a
    /// command, answered to the caller alone and never broadcast.
    ///
    /// Sanitized *before* the routing decision: TrimStart drops whitespace but
    /// not C0 control scalars, so "/op <key>" used to miss the slash test
    /// and then get republished as broadcast chat with the key in it.
    let handleChat extensions (host: MatchHost) playerId (text: string) =
        let line = Multiplayer.sanitizeText 120 text
        if line.StartsWith '/' then
            // Commands pay the same one-line-per-second budget as chat: each
            // reply is an O(n) append to the event list every client serializes.
            if host.TryChatCredit playerId then run extensions host playerId line
        else host.Chat(playerId, line)

/// Transcript of everything said in a room, so an operator reading the server
/// afterwards can see what happened. Stdout is the sink that survives on Fly,
/// where the filesystem is ephemeral without a volume; IRONSIGHT_CHAT_LOG adds
/// a file for hosts that have somewhere durable to put one.
[<RequireQualifiedAccess>]
module ChatLog =
    let private path = Environment.GetEnvironmentVariable "IRONSIGHT_CHAT_LOG"

    /// Serialises appends: the tick loop drives both rooms from one thread
    /// today, but a second writer must not interleave half-written lines.
    let private fileGate = obj ()

    let format (timestamp: DateTimeOffset) (mode: GameMode) (name: string) (line: string) =
        let who = if String.IsNullOrEmpty name then "*" else name
        $"""{timestamp.ToString "yyyy-MM-ddTHH:mm:ssZ"} [{mode}] {who}: {line}"""

    let private write (entry: string) =
        printfn $"{entry}"
        if not (String.IsNullOrWhiteSpace path) then
            // A broken log path must not take the room down with it; the
            // stdout copy above is already the durable one.
            try lock fileGate (fun () -> IO.File.AppendAllLines(path, [ entry ]))
            with ex -> eprintfn $"[chatlog] append to '{path}' failed: {ex.Message}"

    /// Records chat only. Command lines never reach here — they are whispered
    /// back to the caller, and /op carries the key.
    let extension =
        { ServerExtension.empty "chatlog" with
            OnEvent =
                Some(fun host event ->
                    match event.Event with
                    | Chat(Some _, name, line) -> write (format DateTimeOffset.UtcNow (host.Snapshot().Mode) name line)
                    | _ -> ()) }
