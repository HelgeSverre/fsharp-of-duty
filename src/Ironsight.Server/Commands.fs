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
      Commands: Command list }

[<RequireQualifiedAccess>]
module Commands =
    let private command verb level usage run =
        { Verb = verb; Level = level; Usage = usage; Run = run }

    let private help =
        command "help" Everyone "/help - list the commands you can run" (fun context _ ->
            for entry in context.Visible do context.Reply entry.Usage)

    let private op =
        command "op" Everyone "/op <key> - elevate with the server's op key" (fun context arguments ->
            match arguments with
            | [ key ] when context.Host.TryElevate(context.PlayerId, key) -> context.Reply "You are now an op."
            | _ -> context.Reply "Rejected.")

    let private say =
        // Sender None renders as a highlighted system line client-side.
        command "say" Op "/say <text> - broadcast a server announcement" (fun context arguments ->
            match Multiplayer.sanitizeText 120 (String.concat " " arguments) with
            | "" -> context.Reply "Usage: /say <text>"
            | line -> context.Host.Enqueue(Chat(None, "", line)))

    let private kick =
        command "kick" Op "/kick <name> - drop a player (he can rejoin; there is no ban list)" (fun context arguments ->
            match context.Host.Kick(String.concat " " arguments) with
            | Some name -> context.Reply $"Kicked {name}."
            | None -> context.Reply "No connected player by that name.")

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

    let builtins = { Name = "builtin"; Commands = [ help; op; say; kick; map; restart ] }

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
