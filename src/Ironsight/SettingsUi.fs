namespace Ironsight.Shell

open System

[<RequireQualifiedAccess>]
module SettingsUi =
    type RowKind =
        | Header
        | Slider
        | Toggle
        | Cycle
        | Action

    /// One settings row. `Get` renders the current value and `Step` applies a
    /// left (-1) / right (+1) / activate adjustment, returning the new settings.
    /// Adding an option means adding a row here plus a consumer of the field —
    /// the renderer and navigation never change.
    type Row =
        { Label: string
          Kind: RowKind
          Get: GameSettings -> string
          Step: GameSettings -> int -> GameSettings }

    type State = { Settings: GameSettings; Selected: int; FirstVisible: int }

    [<Literal>]
    let MaxVisibleRows = 8

    let RowHeight = 40.0f
    let FirstRowTop = 85.0f

    let panelRect (width: int) (height: int) (visibleCount: int) =
        Rect.centered width height (min 860.0f (float32 width - 48.0f)) (150.0f + RowHeight * float32 visibleCount)

    let rowsRect (panel: Rect) (visibleCount: int) = Rect.rowsIn FirstRowTop RowHeight visibleCount panel

    let private header label =
        { Label = label; Kind = Header; Get = (fun _ -> ""); Step = (fun current _ -> current) }

    /// The same bounds are enforced on load by Settings.clamp — keep in sync.
    let private slider label lo hi step format (get: GameSettings -> float32) set =
        { Label = label
          Kind = Slider
          Get = get >> format
          Step = (fun current direction -> set (Math.Clamp(get current + float32 direction * step, lo, hi)) current) }

    let private toggle label (get: GameSettings -> bool) set =
        { Label = label
          Kind = Toggle
          Get = (fun current -> if get current then "ON" else "OFF")
          Step = (fun current _ -> set (not (get current)) current) }

    let rows: Row array =
        [| header "VIDEO"
           slider "FIELD OF VIEW" 55.0f 95.0f 5.0f (int >> string) (fun c -> c.Fov) (fun v c -> { c with Fov = v })
           slider "CONTRAST" 0.8f 1.3f 0.05f (sprintf "%.2f") (fun c -> c.Contrast) (fun v c -> { c with Contrast = v })
           header "GAMEPLAY"
           slider "MOUSE SENSITIVITY" 0.2f 3.0f 0.1f (sprintf "%.1f") (fun c -> c.MouseSensitivity) (fun v c ->
               { c with MouseSensitivity = v })
           slider "GAMEPAD SENSITIVITY" 0.2f 3.0f 0.1f (sprintf "%.1f") (fun c -> c.GamepadSensitivity) (fun v c ->
               { c with GamepadSensitivity = v })
           toggle "ADS TOGGLE" (fun c -> c.AdsToggle) (fun v c -> { c with AdsToggle = v })
           toggle "CROUCH TOGGLE" (fun c -> c.CrouchToggle) (fun v c -> { c with CrouchToggle = v })
           header "EFFECTS"
           { Label = "BLOOD COLOR"
             Kind = Cycle
             Get = (fun current -> Settings.bloodNames[Settings.bloodColorIndex current.BloodColor])
             Step = Settings.stepBloodColor }
           { Label = "RESET TO DEFAULTS"
             Kind = Action
             Get = (fun _ -> "")
             Step = (fun _ _ -> Settings.defaults) } |]

    let private selectableIndexes =
        rows
        |> Array.indexed
        |> Array.choose (fun (index, row) -> if row.Kind = Header then None else Some index)

    let create (settings: GameSettings) =
        { Settings = settings
          Selected = selectableIndexes[0]
          FirstVisible = 0 }

    type RowVisual =
        { Label: string
          Value: string
          Header: bool
          Adjustable: bool
          Selected: bool }

    /// The row window the HUD draws, with the selected row always in view.
    let visibleRows (state: State) =
        let last = min (rows.Length - 1) (state.FirstVisible + MaxVisibleRows - 1)
        [ for index in state.FirstVisible..last ->
            { Label = rows[index].Label
              Value = rows[index].Get state.Settings
              Header = rows[index].Kind = Header
              Adjustable =
                  match rows[index].Kind with
                  | Slider | Toggle | Cycle -> true
                  | _ -> false
              Selected = index = state.Selected } ]

    let update (width: int) (height: int) (input: MenuInput) (state: State) =
        // Hover hits the drawn window; header rows are labels, not targets.
        let visibleCount = min MaxVisibleRows rows.Length
        let hovered =
            input.Pointer
            |> Option.bind (fun pointer ->
                rowsRect (panelRect width height visibleCount) visibleCount
                |> Rect.slotAt RowHeight visibleCount pointer)
            |> Option.map (fun slot -> min (state.FirstVisible + slot) (rows.Length - 1))
            |> Option.filter (fun index -> rows[index].Kind <> Header)
        let selected = MenuNav.stepSelection selectableIndexes input hovered state.Selected
        let row = rows[selected]
        let direction = if input.Left then -1 elif input.Right then 1 else 0
        let activate = input.Activate || (input.Clicked && hovered.IsSome)
        let stepWhen condition = if condition then row.Step state.Settings direction else state.Settings
        let settings =
            match row.Kind with
            | Header -> state.Settings
            | Slider -> stepWhen (direction <> 0)
            | Toggle | Cycle -> stepWhen (direction <> 0 || activate)
            | Action -> stepWhen activate
        { Settings = settings
          Selected = selected
          FirstVisible = MenuNav.scrollWindow rows.Length MaxVisibleRows selected state.FirstVisible }
