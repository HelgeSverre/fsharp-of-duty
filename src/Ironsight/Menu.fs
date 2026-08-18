namespace Ironsight.Shell

open System
open System.Numerics
open Ironsight

/// Shared geometry for the start-menu panel, used both to draw it (Hud) and
/// to hit-test mouse hover against it (here). The two offsets below are not
/// currently equal (106 vs 99), which is a pre-existing few-pixel mismatch
/// between the drawn rows and the hover hitbox; kept as-is rather than
/// "fixed" as part of a behavior-preserving refactor.
[<RequireQualifiedAccess>]
module MenuLayout =
    let RowHeight = 54.0f
    let panelWidth (width: int) = min 840.0f (float32 width - 48.0f)
    let panelHeight (rowCount: int) = 156.0f + RowHeight * float32 rowCount
    /// Offset from panel-vertical-center to the first row's y, as used when
    /// hit-testing mouse hover in hoveredIndex below.
    let HitTestFirstRowOffset = 99.0f
    /// Offset from panel-vertical-center to the first row's y, as used when
    /// drawing the menu in Hud.fs.
    let DrawFirstRowOffset = 106.0f

type MenuPage = Main | NameEntry | OfflineMaps | ServerList | OnlineLoadout

type StartMenuState =
    { Page: MenuPage
      Selected: int
      PlayerName: string
      /// Room chosen on the server list; the mode sent in the online hello.
      OnlineMode: GameMode
      /// Server chosen on the server list; where the online hello connects.
      OnlineServer: Uri
      /// None until the directory probe answers; one row per server room.
      ServerRows: ServerRow array option }

type MenuInput =
    { Up: bool
      Down: bool
      Left: bool
      Right: bool
      Activate: bool
      Back: bool
      Backspace: bool
      TextInput: string
      Pointer: Vector2 option
      Clicked: bool }

type StartMenuAction =
    | StartOffline of map: string
    | StartOnline of weaponName: string * mode: GameMode * server: Uri
    | OpenSettings
    | ExitGame

/// In-game loadout picker (B key): every weapon, no restrictions, no economy.
/// Offline it swaps the carried slot instantly; online it sends a loadout
/// request that the server arms on the next spawn.
[<RequireQualifiedAccess>]
module LoadoutMenu =
    type Choice =
        | Browsing
        | Closed
        | Chosen of weaponName: string

    let update (input: MenuInput) (selected: int) =
        let count = Tuning.onlineWeapons.Length
        let next =
            if input.Up then (selected + count - 1) % count
            elif input.Down then (selected + 1) % count
            else selected
        if input.Back then struct (next, Closed)
        elif input.Activate then struct (next, Chosen Tuning.onlineWeapons[next].Name)
        else struct (next, Browsing)

[<RequireQualifiedAccess>]
module StartMenu =
    let create playerName =
        let sanitized = Multiplayer.sanitizeName playerName
        { Page = Main
          Selected = 0
          PlayerName = if System.String.IsNullOrWhiteSpace sanitized then "Soldier" else sanitized
          OnlineMode = TeamDeathmatch
          OnlineServer = Uri ServerDirectory.DefaultServer
          ServerRows = None }

    let initial = create "Soldier"

    let items state =
        match state.Page with
        | Main -> [| $"CALLSIGN  {state.PlayerName}"; "QUICK PLAY"; "OFFLINE PLAY / MAP SELECT"; "JOIN ONLINE"; "SETTINGS"; "QUIT" |]
        | NameEntry -> [| $"> {state.PlayerName}_" |]
        | OfflineMaps -> [| "PAINTBALL KILLHOUSE"; "SCRAP DEPOT"; "CANAL YARD"; "OMAHA DRAW"; "BACK" |]
        | ServerList ->
            // Half-Life-style table: one row per room, the server as a column.
            match state.ServerRows with
            | Some rows ->
                let formatted =
                    rows
                    |> Array.map (fun row ->
                        let host =
                            let value = row.Server.Url.Host
                            if value.Length > 26 then value.Substring(0, 26) else value
                        if row.Online then
                            let mode = if row.Mode = FreeForAll then "FREE FOR ALL" else "TEAM DEATHMATCH"
                            sprintf "%-26s  %-15s  %5s  %-7s  %4dMS" host mode $"{row.Players}/{row.Capacity}" (row.Phase.ToUpperInvariant()) row.PingMs
                        else sprintf "%-26s  OFFLINE" host)
                Array.append formatted [| "BACK" |]
            | None -> [| "CONTACTING SERVERS..."; "BACK" |]
        | OnlineLoadout ->
            Array.append (Tuning.onlineWeapons |> Array.map (fun weapon -> weapon.Name.ToUpperInvariant())) [| "BACK" |]

    let subtitle state =
        match state.Page with
        | Main -> "SELECT A DEPLOYMENT"
        | NameEntry -> "TYPE YOUR CALLSIGN, THEN PRESS ENTER"
        | OfflineMaps -> "OFFLINE MAP SELECT"
        | ServerList ->
            match state.ServerRows with
            | Some rows ->
                let servers = rows |> Array.distinctBy (fun row -> row.Server.Url) |> Array.length
                $"SERVER  /  MODE  /  PLAYERS  /  PHASE  /  PING   -   {servers} SERVERS"
            | None -> "SERVER LIST"
        | OnlineLoadout -> "SELECT ONLINE LOADOUT"

    let private hoveredIndex (width: int) (height: int) (count: int) (pointer: Vector2) =
        let rowHeight = MenuLayout.RowHeight
        let panelWidth = MenuLayout.panelWidth width
        let left = float32 width * 0.5f - panelWidth * 0.5f
        let top = float32 height * 0.5f - MenuLayout.panelHeight count * 0.5f + MenuLayout.HitTestFirstRowOffset
        if pointer.X >= left + 18.0f && pointer.X <= left + panelWidth - 18.0f && pointer.Y >= top && pointer.Y < top + rowHeight * float32 count then
            Some(int ((pointer.Y - top) / rowHeight))
        else None

    let update (width: int) (height: int) (input: MenuInput) (state: StartMenuState) =
        let editedName =
            if state.Page <> NameEntry then state.PlayerName
            else
                let afterBackspace =
                    if input.Backspace && state.PlayerName.Length > 0 then state.PlayerName.Remove(state.PlayerName.Length - 1)
                    else state.PlayerName
                input.TextInput
                |> Seq.filter (fun character -> character >= ' ' && character <= '~')
                |> Seq.fold (fun name character -> if name.Length < 24 then name + string character else name) afterBackspace
        let state = { state with PlayerName = editedName }
        let options = items state
        let fromPointer = input.Pointer |> Option.bind (hoveredIndex width height options.Length)
        let selected =
            match fromPointer with
            | Some index -> index
            | None when input.Up -> (state.Selected + options.Length - 1) % options.Length
            | None when input.Down -> (state.Selected + 1) % options.Length
            | None -> min state.Selected (options.Length - 1)
        let next = { state with Selected = selected }
        let activate = input.Activate || (input.Clicked && fromPointer.IsSome)
        if input.Back then
            match state.Page with
            // Escape never quits: quitting is the explicit QUIT item. On the
            // root page there is nothing to back out of, so it does nothing.
            | Main -> struct(next, None)
            | NameEntry -> struct({ next with Page = Main; Selected = 0 }, None)
            | OnlineLoadout -> struct({ next with Page = ServerList; Selected = 0 }, None)
            | _ -> struct({ next with Page = Main; Selected = 0 }, None)
        elif not activate then struct(next, None)
        else
            match state.Page, selected with
            | Main, 0 -> struct({ next with Page = NameEntry; Selected = 0 }, None)
            | Main, 1 -> struct(next, Some(StartOffline "paintball"))
            | Main, 2 -> struct({ next with Page = OfflineMaps; Selected = 0 }, None)
            | Main, 3 -> struct({ next with Page = ServerList; Selected = 0 }, None)
            | Main, 4 -> struct(next, Some OpenSettings)
            | Main, _ -> struct(next, Some ExitGame)
            | NameEntry, _ ->
                let sanitized = Multiplayer.sanitizeName next.PlayerName
                let playerName = if System.String.IsNullOrWhiteSpace sanitized then "Soldier" else sanitized
                struct({ next with Page = Main; Selected = 0; PlayerName = playerName }, None)
            | OfflineMaps, 0 -> struct(next, Some(StartOffline "paintball"))
            | OfflineMaps, 1 -> struct(next, Some(StartOffline "depot"))
            | OfflineMaps, 2 -> struct(next, Some(StartOffline "canal"))
            | OfflineMaps, 3 -> struct(next, Some(StartOffline "omaha"))
            | OfflineMaps, _ -> struct({ next with Page = Main; Selected = 0 }, None)
            | ServerList, index ->
                let rows = state.ServerRows |> Option.defaultValue [||]
                if index < rows.Length then
                    match Array.tryItem index rows with
                    | Some row when row.Online ->
                        struct({ next with Page = OnlineLoadout; Selected = 0; OnlineMode = row.Mode; OnlineServer = row.Server.Url }, None)
                    | _ -> struct(next, None) // offline row: nothing to join
                elif state.ServerRows.IsNone && index = 0 then
                    struct(next, None) // still contacting servers
                else struct({ next with Page = Main; Selected = 0 }, None)
            | OnlineLoadout, index when index < Tuning.onlineWeapons.Length ->
                struct(next, Some(StartOnline(Tuning.onlineWeapons[index].Name, state.OnlineMode, state.OnlineServer)))
            | OnlineLoadout, _ -> struct({ next with Page = ServerList; Selected = 0 }, None)
