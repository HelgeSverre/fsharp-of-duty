namespace Ironsight.Shell

open System
open System.Numerics
open Ironsight

/// Shared geometry for the start-menu panel, used both to draw it (Hud) and
/// to hit-test mouse hover against it (here). A row is a RowHeight-tall slot
/// measured from FirstRowTop; the highlight bar, the text, and the hover
/// hitbox all derive from the same slot so they can never drift apart.
[<RequireQualifiedAccess>]
module MenuLayout =
    let RowHeight = 54.0f
    let panelWidth (width: int) = min 840.0f (float32 width - 48.0f)
    let panelHeight (rowCount: int) = 156.0f + RowHeight * float32 rowCount
    /// Top of the first row slot, measured from the panel top.
    let FirstRowTop = 99.0f
    /// Text y that vertically centers a glyph line of `scale` (12 logical
    /// pixels tall at scale 1) in a row slot, so cells drawn at different
    /// scales share the slot's midline.
    let rowTextY (slotTop: float32) (slotHeight: float32) (scale: float32) =
        slotTop + (slotHeight - 12.0f * scale) * 0.5f

type MenuPage = Main | NameEntry | OfflineMaps | ServerList | OnlineLoadout

type StartMenuState =
    { Page: MenuPage
      Selected: int
      /// First row of the scroll window when a page has more rows than fit.
      FirstVisible: int
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
          FirstVisible = 0
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
            // Half-Life-style table: one row per room. The labels here only
            // drive selection count and keyboard flow; the HUD draws server
            // rows from serverCells below.
            match state.ServerRows with
            | Some rows -> Array.append (rows |> Array.map (fun row -> row.Server.Url.Host.ToUpperInvariant())) [| "BACK" |]
            | None -> [| "CONTACTING SERVERS..."; "BACK" |]
        | OnlineLoadout ->
            Array.append (Tuning.onlineWeapons |> Array.map (fun weapon -> weapon.Name.ToUpperInvariant())) [| "BACK" |]

    /// Column x-offsets from the row's left edge, shared by the header and rows.
    let serverColumns = [| 0.0f, "SERVER"; 300.0f, "MODE"; 500.0f, "PLAYERS"; 590.0f, "PHASE"; 700.0f, "PING" |]

    /// Server-table rows as (xOffset, text) cells drawn at fixed columns by
    /// the HUD; None on pages that draw plain labels. The trailing BACK row is
    /// not included — the HUD falls back to its label.
    let serverCells state =
        match state.Page, state.ServerRows with
        | ServerList, Some rows ->
            rows
            |> Array.map (fun row ->
                let host =
                    let value = row.Server.Url.Host
                    if value.Length > 26 then value.Substring(0, 26) else value
                if row.Online then
                    [| 0.0f, host
                       300.0f, (if row.Mode = FreeForAll then "FREE FOR ALL" else "TEAM DEATHMATCH")
                       500.0f, $"{row.Players}/{row.Capacity}"
                       590.0f, row.Phase.ToUpperInvariant()
                       700.0f, $"{row.PingMs}MS" |]
                else [| 0.0f, host; 300.0f, "OFFLINE" |])
            |> Some
        | _ -> None

    let subtitle state =
        match state.Page with
        | Main -> "SELECT A DEPLOYMENT"
        | NameEntry -> "TYPE YOUR CALLSIGN, THEN PRESS ENTER"
        | OfflineMaps -> "OFFLINE MAP SELECT"
        | ServerList ->
            match state.ServerRows with
            | Some rows ->
                let servers = rows |> Array.distinctBy (fun row -> row.Server.Url) |> Array.length
                $"{servers} SERVERS"
            | None -> "SERVER LIST"
        | OnlineLoadout -> "SELECT ONLINE LOADOUT"

    /// Rows that fit before the panel scrolls; a settings-style window keeps
    /// the selected row in view (SettingsUi.scroll is the same shape).
    let MaxVisibleRows = 10

    let private scrollOffset total selected firstVisible =
        if total <= MaxVisibleRows then 0
        else Math.Clamp(Math.Clamp(firstVisible, selected - MaxVisibleRows + 1, selected), 0, total - MaxVisibleRows)

    /// First visible row index and visible row count for the current page,
    /// clamped on the fly so the HUD can never draw a stale window.
    let visibleRange state =
        let total = (items state).Length
        scrollOffset total state.Selected state.FirstVisible, min MaxVisibleRows total

    let private hoveredIndex (width: int) (height: int) (count: int) (pointer: Vector2) =
        let rowHeight = MenuLayout.RowHeight
        let panelWidth = MenuLayout.panelWidth width
        let left = float32 width * 0.5f - panelWidth * 0.5f
        let top = float32 height * 0.5f - MenuLayout.panelHeight count * 0.5f + MenuLayout.FirstRowTop
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
        // Hover hits the drawn window, so a hit maps back through FirstVisible.
        let firstVisible, visibleCount = visibleRange state
        let fromPointer =
            input.Pointer
            |> Option.bind (hoveredIndex width height visibleCount)
            |> Option.map (fun slot -> min (firstVisible + slot) (options.Length - 1))
        let selected =
            match fromPointer with
            | Some index -> index
            | None when input.Up -> (state.Selected + options.Length - 1) % options.Length
            | None when input.Down -> (state.Selected + 1) % options.Length
            | None -> min state.Selected (options.Length - 1)
        let next = { state with Selected = selected; FirstVisible = scrollOffset options.Length selected firstVisible }
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
