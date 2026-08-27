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
      /// Id of the room chosen on the server list, blank against a server that
      /// does not name its rooms — then the mode above is what picks one.
      OnlineRoom: string
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
      /// Wheel notches this frame: positive scrolls the list down. Only long
      /// lists read it; the rest of the menus ignore it.
      Scroll: int
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
          Scroll = 0
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
    | StartOnline of weaponName: string * mode: GameMode * room: string * server: Uri
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

    /// A group heading or a weapon. Headings are labels, never targets — the
    /// same split SettingsUi uses for its section headers.
    type Row =
        | Header of category: int
        | Weapon of index: int

    type State =
        { Selected: int
          FirstVisible: int
          /// Row under the pointer. Distinct from Selected so a parked cursor
          /// reads as "this is what a click takes" even while the window is
          /// scrolled away from the keyboard cursor.
          Hovered: int option }

    let RowHeight = 30.0f
    let FirstRowTop = 82.0f

    /// Enough rows that the twelve-weapon arsenal shows whole, while leaving a
    /// twenty-weapon one inside a 720p window once the panel chrome is added.
    [<Literal>]
    let MaxVisibleRows = 14

    /// Weapons grouped under their own number key, so the picker and the 1-5
    /// keys agree by construction rather than by a second hand-kept table.
    let rows: Row array =
        Tuning.categories
        |> Array.collect (fun category ->
            let members =
                Tuning.onlineWeapons
                |> Array.indexed
                |> Array.filter (fun (_, weapon) -> Tuning.categoryOf weapon = category)
            if Array.isEmpty members then [||]
            else Array.append [| Header category |] (members |> Array.map (fst >> Weapon)))

    let private selectableIndexes =
        rows
        |> Array.indexed
        |> Array.choose (fun (index, row) -> match row with Weapon _ -> Some index | Header _ -> None)

    let private visibleCount = min MaxVisibleRows rows.Length

    /// Weapon indices in the order the picker lists them — grouped by number
    /// key. The pre-join menu shares this so both pickers present the arsenal
    /// the same way round, without it needing the header rows.
    let weaponOrder =
        rows |> Array.choose (function Weapon index -> Some index | Header _ -> None)

    /// "3  M1911": the key that reaches a weapon, then its name.
    let label (index: int) =
        let weapon = Tuning.onlineWeapons[index]
        $"{Tuning.categoryOf weapon + 1}  {weapon.Name}".ToUpperInvariant()

    let create () =
        { Selected = selectableIndexes[0]; FirstVisible = 0; Hovered = None }

    /// The weapon a row index names, if it names one.
    let weaponAt index =
        if index < 0 || index >= rows.Length then None
        else match rows[index] with Weapon weapon -> Some Tuning.onlineWeapons[weapon] | Header _ -> None

    let panelRect (width: int) (height: int) =
        Rect.centered width height (min 900.0f (float32 width - 48.0f)) (150.0f + RowHeight * float32 visibleCount)

    let rowsRect (panel: Rect) = Rect.rowsIn FirstRowTop RowHeight visibleCount panel

    let private maxFirstVisible = max 0 (rows.Length - MaxVisibleRows)

    /// The window the HUD draws. `update` already keeps the selection inside it,
    /// so this only clamps to the list — re-centring on the selection here would
    /// undo wheel scrolling on the very next frame.
    let visibleRows (state: State) =
        let first = Math.Clamp(state.FirstVisible, 0, maxFirstVisible)
        let last = min (rows.Length - 1) (first + MaxVisibleRows - 1)
        first, [ for index in first..last -> index, rows[index] ]

    /// Typing 1-5 jumps to that key's first weapon. Menu-mode keystrokes already
    /// arrive as TextInput, so this needs nothing from the input sampler.
    let private categoryJump (input: MenuInput) =
        input.TextInput
        |> Seq.tryPick (fun character ->
            if character >= '1' && character <= '5' then
                let category = int character - int '1'
                rows |> Array.tryFindIndex (function Weapon index -> Tuning.categoryOf Tuning.onlineWeapons[index] = category | Header _ -> false)
            else None)

    let update (width: int) (height: int) (input: MenuInput) (state: State) =
        // The wheel scrolls the window without moving the cursor, so a mouse
        // can reach rows the keyboard selection has not walked to.
        let first = Math.Clamp(state.FirstVisible + input.Scroll, 0, maxFirstVisible)
        // Hover hits the same rect the HUD draws, offset by the scroll position;
        // headings are skipped so the cursor cannot land on one.
        let hovered =
            input.Pointer
            |> Option.bind (fun pointer -> Rect.slotAt RowHeight visibleCount pointer (rowsRect (panelRect width height)))
            |> Option.map (fun slot -> min (first + slot) (rows.Length - 1))
            |> Option.filter (fun index -> match rows[index] with Weapon _ -> true | Header _ -> false)
        let stepped = MenuNav.stepSelection selectableIndexes input hovered state.Selected
        let selected = categoryJump input |> Option.defaultValue stepped
        // A click lands on the row under the cursor even if a dpad step
        // arrived in the same frame.
        let activateIndex = if input.Clicked && hovered.IsSome then hovered.Value else selected
        let activate = input.Activate || (input.Clicked && hovered.IsSome)
        // Wheel scrolling alone must not drag the selection with it: keep the
        // scrolled window unless the selection itself moved, and only then pull
        // the window back to wherever the cursor went.
        let window =
            if selected = state.Selected then first
            else MenuNav.scrollWindow rows.Length MaxVisibleRows selected first
        let next = { Selected = selected; FirstVisible = window; Hovered = hovered }
        if input.Back then struct (next, Closed)
        else
            match (if activate then weaponAt activateIndex else None) with
            | Some weapon -> struct (next, Chosen weapon.Name)
            | None -> struct (next, Browsing)

[<RequireQualifiedAccess>]
module StartMenu =
    let create playerName =
        let sanitized = Multiplayer.sanitizeName playerName
        { Page = Main
          Selected = 0
          FirstVisible = 0
          PlayerName = if System.String.IsNullOrWhiteSpace sanitized then "Soldier" else sanitized
          OnlineMode = TeamDeathmatch
          OnlineRoom = ""
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
    /// Titles come from the registry table, so building the menu loads no map.
    let private offlineMaps =
        Ironsight.ProcGen.Levels.builtins
        |> Array.filter (fun entry -> entry.OnMenu)
        |> Array.map (fun entry -> entry.Aliases[0], entry.Title.ToUpperInvariant())

    /// What a browser row is called. The room name when the server reports one
    /// — otherwise ten rooms on one server would render as ten identical
    /// lines — falling back to the server name for servers without rooms.
    let rowLabel (row: ServerRow) =
        if String.IsNullOrWhiteSpace row.RoomName then row.Server.Name else row.RoomName

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
            | Some rows -> Array.append (rows |> Array.map (fun row -> (rowLabel row).ToUpperInvariant())) [| "BACK" |]
            | None -> [| "CONTACTING SERVERS..."; "BACK" |]
        | OnlineLoadout ->
            // Same order and labelling as the in-game picker; StartMenu's own
            // scroll window already keeps a long list on screen.
            Array.append (LoadoutMenu.weaponOrder |> Array.map LoadoutMenu.label) [| "BACK" |]

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
                    let value = rowLabel row
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
        let stepped =
            MenuNav.stepSelection [| 0 .. options.Length - 1 |] input fromPointer
                (min state.Selected (options.Length - 1))
        // The wheel walks the selection rather than the window: every page here
        // resets Selected but not FirstVisible, so the window has to stay
        // derived from the cursor or a page change would open on a stale row.
        let selected =
            if input.Scroll = 0 then stepped
            else Math.Clamp(stepped + input.Scroll, 0, options.Length - 1)
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
                        struct({ next with Page = OnlineLoadout; Selected = 0; OnlineMode = row.Mode; OnlineRoom = row.RoomId; OnlineServer = row.Server.Url }, None)
                    | _ -> struct(next, None) // offline row: nothing to join
                elif state.ServerRows.IsNone && index = 0 then
                    struct(next, None) // still contacting servers
                else struct({ next with Page = Main; Selected = 0 }, None)
            | OnlineLoadout, index when index < LoadoutMenu.weaponOrder.Length ->
                // The row index is a position in the picker's category order,
                // not an index into onlineWeapons.
                let weapon = Tuning.onlineWeapons[LoadoutMenu.weaponOrder[index]]
                struct(next, Some(StartOnline(weapon.Name, state.OnlineMode, state.OnlineRoom, state.OnlineServer)))
            | OnlineLoadout, _ -> struct({ next with Page = ServerList; Selected = 0 }, None)
