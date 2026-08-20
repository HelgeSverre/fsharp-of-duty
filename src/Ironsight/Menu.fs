namespace Ironsight.Shell

open System
open System.Numerics
open Ironsight

/// Shared geometry for the start-menu panel, used both to draw it (Hud) and
/// to hit-test mouse hover against it (here). rowsRect is the single source:
/// the highlight bars, the row text, and the hover hitbox all derive from
/// the same rect, so they can never drift apart.
[<RequireQualifiedAccess>]
module MenuLayout =
    let RowHeight = 54.0f
    let panelWidth (width: int) = min 840.0f (float32 width - 48.0f)
    let panelHeight (rowCount: int) = 156.0f + RowHeight * float32 rowCount
    /// Top of the first row slot, measured from the panel top.
    let FirstRowTop = 99.0f

    /// The centered panel rect for a page showing `rowCount` rows.
    let panelRect (width: int) (height: int) (rowCount: int) =
        Rect.centered width height (panelWidth width) (panelHeight rowCount)

    /// The rows' shared rect inside the panel: highlight-bar extent and hover
    /// hitbox; row text sits a fixed pad inside it.
    let rowsRect (panel: Rect) (rowCount: int) = Rect.rowsIn FirstRowTop RowHeight rowCount panel

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

[<RequireQualifiedAccess>]
module MenuInput =
    let empty =
        { Up = false
          Down = false
          Left = false
          Right = false
          Activate = false
          Back = false
          Backspace = false
          TextInput = ""
          Pointer = None
          Clicked = false }

/// Row-list navigation shared by the start menu, loadout picker, and settings.
[<RequireQualifiedAccess>]
module MenuNav =
    /// Hover sets the cursor; Up/Down still step from wherever it landed, so a
    /// pointer parked over the list never eats dpad/arrow presses. `indexes`
    /// is the selectable row set — all rows for plain lists, non-headers for
    /// the settings screen.
    let stepSelection (indexes: int array) (input: MenuInput) (hovered: int option) (selected: int) =
        let start = defaultArg hovered selected
        let position = indexes |> Array.tryFindIndex ((=) start) |> Option.defaultValue 0
        let delta = (if input.Up then -1 else 0) + (if input.Down then 1 else 0)
        indexes[(position + delta + indexes.Length) % indexes.Length]

    /// First visible row of a scroll window that always contains `selected`.
    let scrollWindow total maxVisible selected firstVisible =
        if total <= maxVisible then 0
        else Math.Clamp(Math.Clamp(firstVisible, selected - maxVisible + 1, selected), 0, total - maxVisible)

    /// One tick of text editing, shared by the callsign field and the chat
    /// draft: backspace first, then append this tick's printable characters up
    /// to `maxLength`.
    let editText maxLength (input: MenuInput) (text: string) =
        let afterBackspace = if input.Backspace && text.Length > 0 then text.Remove(text.Length - 1) else text
        input.TextInput
        |> Seq.filter (fun character -> character >= ' ' && character <= '~')
        |> Seq.fold (fun (edited: string) character -> if edited.Length < maxLength then edited + string character else edited) afterBackspace

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

    let RowHeight = 30.0f
    let FirstRowTop = 82.0f

    let panelRect (width: int) (height: int) =
        Rect.centered width height (min 640.0f (float32 width - 48.0f)) (150.0f + RowHeight * float32 Tuning.onlineWeapons.Length)

    let rowsRect (panel: Rect) = Rect.rowsIn FirstRowTop RowHeight Tuning.onlineWeapons.Length panel

    let update (width: int) (height: int) (input: MenuInput) (selected: int) =
        let count = Tuning.onlineWeapons.Length
        // Hover hits the same rect the HUD draws the rows in.
        let hovered =
            input.Pointer
            |> Option.bind (fun pointer -> Rect.slotAt RowHeight count pointer (rowsRect (panelRect width height)))
        let next = MenuNav.stepSelection [| 0 .. count - 1 |] input hovered selected
        // A click lands on the row under the cursor even if a dpad step
        // arrived in the same frame.
        let activateIndex = if input.Clicked && hovered.IsSome then hovered.Value else next
        let activate = input.Activate || (input.Clicked && hovered.IsSome)
        if input.Back then struct (next, Closed)
        elif activate then struct (next, Chosen Tuning.onlineWeapons[activateIndex].Name)
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

    /// Main-page rows: one array drives both the label and the activate
    /// behavior in update, so they cannot drift apart.
    type private MainRow = Callsign | QuickPlay | MapSelect | JoinOnline | SettingsRow | QuitRow
    let private mainRows = [| Callsign; QuickPlay; MapSelect; JoinOnline; SettingsRow; QuitRow |]

    let private mainLabel state = function
        | Callsign -> $"CALLSIGN  {state.PlayerName}"
        | QuickPlay -> "QUICK PLAY"
        | MapSelect -> "OFFLINE PLAY / MAP SELECT"
        | JoinOnline -> "JOIN ONLINE"
        | SettingsRow -> "SETTINGS"
        | QuitRow -> "QUIT"

    /// Offline map rows straight from the level registry: (alias, label).
    let private offlineMaps =
        Ironsight.ProcGen.Levels.offlineAliases
        |> Array.map (fun alias ->
            alias, (Ironsight.ProcGen.Levels.specByAlias alias).Value.Name.ToUpperInvariant())

    let items state =
        match state.Page with
        | Main -> mainRows |> Array.map (mainLabel state)
        | NameEntry -> [| $"> {state.PlayerName}_" |]
        | OfflineMaps -> Array.append (offlineMaps |> Array.map snd) [| "BACK" |]
        | ServerList ->
            // Half-Life-style table: one row per room. The labels here only
            // drive selection count and keyboard flow; the HUD draws server
            // rows from serverCells below.
            match state.ServerRows with
            | Some rows -> Array.append (rows |> Array.map (fun row -> row.Server.Name.ToUpperInvariant())) [| "BACK" |]
            | None -> [| "CONTACTING SERVERS..."; "BACK" |]
        | OnlineLoadout ->
            Array.append (Tuning.onlineWeapons |> Array.map (fun weapon -> weapon.Name.ToUpperInvariant())) [| "BACK" |]

    /// Column x-offsets from the row's left edge, shared by the header and rows.
    let serverColumns = [| 0.0f, "SERVER"; 300.0f, "MODE"; 500.0f, "PLAYERS"; 590.0f, "PHASE"; 700.0f, "PING" |]

    let private columnX index = fst serverColumns[index]

    /// Server-table rows as (xOffset, text) cells drawn at fixed columns by
    /// the HUD; None on pages that draw plain labels. The trailing BACK row is
    /// not included — the HUD falls back to its label.
    let serverCells state =
        match state.Page, state.ServerRows with
        | ServerList, Some rows ->
            rows
            |> Array.map (fun row ->
                let host =
                    let value = row.Server.Name
                    if value.Length > 26 then value.Substring(0, 26) else value
                if row.Online then
                    [| columnX 0, host
                       columnX 1, (if row.Mode = FreeForAll then "FREE FOR ALL" else "TEAM DEATHMATCH")
                       columnX 2, $"{row.Players}/{row.Capacity}"
                       columnX 3, row.Phase.ToUpperInvariant()
                       columnX 4, $"{row.PingMs}MS" |]
                else [| columnX 0, host; columnX 1, "OFFLINE" |])
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
    /// the selected row in view.
    let MaxVisibleRows = 10

    let private scrollOffset total selected firstVisible =
        MenuNav.scrollWindow total MaxVisibleRows selected firstVisible

    /// First visible row index and visible row count for the current page,
    /// clamped on the fly so the HUD can never draw a stale window.
    let visibleRange state =
        let total = (items state).Length
        scrollOffset total state.Selected state.FirstVisible, min MaxVisibleRows total

    let private hoveredIndex (width: int) (height: int) (count: int) (pointer: Vector2) =
        MenuLayout.rowsRect (MenuLayout.panelRect width height count) count
        |> Rect.slotAt MenuLayout.RowHeight count pointer

    let update (width: int) (height: int) (input: MenuInput) (state: StartMenuState) =
        let editedName =
            if state.Page <> NameEntry then state.PlayerName
            else MenuNav.editText 24 input state.PlayerName
        let state = { state with PlayerName = editedName }
        let options = items state
        // Hover hits the drawn window, so a hit maps back through FirstVisible.
        let firstVisible, visibleCount = visibleRange state
        let fromPointer =
            input.Pointer
            |> Option.bind (hoveredIndex width height visibleCount)
            |> Option.map (fun slot -> min (firstVisible + slot) (options.Length - 1))
        let selected =
            MenuNav.stepSelection [| 0 .. options.Length - 1 |] input fromPointer
                (min state.Selected (options.Length - 1))
        let next = { state with Selected = selected; FirstVisible = scrollOffset options.Length selected firstVisible }
        // A click lands on the row under the cursor even if a dpad step
        // arrived in the same frame.
        let activateIndex = if input.Clicked && fromPointer.IsSome then fromPointer.Value else selected
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
            match state.Page, activateIndex with
            | Main, index ->
                match mainRows[index] with
                | Callsign -> struct({ next with Page = NameEntry; Selected = 0 }, None)
                | QuickPlay -> struct(next, Some(StartOffline "paintball"))
                | MapSelect -> struct({ next with Page = OfflineMaps; Selected = 0 }, None)
                | JoinOnline -> struct({ next with Page = ServerList; Selected = 0 }, None)
                | SettingsRow -> struct(next, Some OpenSettings)
                | QuitRow -> struct(next, Some ExitGame)
            | NameEntry, _ ->
                let sanitized = Multiplayer.sanitizeName next.PlayerName
                let playerName = if System.String.IsNullOrWhiteSpace sanitized then "Soldier" else sanitized
                struct({ next with Page = Main; Selected = 0; PlayerName = playerName }, None)
            | OfflineMaps, index when index < offlineMaps.Length ->
                struct(next, Some(StartOffline(fst offlineMaps[index])))
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
