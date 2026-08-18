namespace Ironsight.Shell

open System

[<RequireQualifiedAccess>]
module SettingsUi =
    type RowKind =
        | Header
        | Slider of minimum: float32 * maximum: float32 * step: float32
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

    let rows (settings: GameSettings) : Row array =
        [| { Label = "VIDEO"
             Kind = Header
             Get = (fun _ -> "")
             Step = (fun current _ -> current) }
           { Label = "FIELD OF VIEW"
             Kind = Slider(55.0f, 95.0f, 5.0f)
             Get = (fun current -> string (int current.Fov))
             Step =
                 (fun current direction ->
                     { current with Fov = Math.Clamp(current.Fov + float32 direction * 5.0f, 55.0f, 95.0f) }) }
           { Label = "CONTRAST"
             Kind = Slider(0.8f, 1.3f, 0.05f)
             Get = (fun current -> $"{current.Contrast:F2}")
             Step =
                 (fun current direction ->
                     { current with Contrast = Math.Clamp(current.Contrast + float32 direction * 0.05f, 0.8f, 1.3f) }) }
           { Label = "GAMEPLAY"
             Kind = Header
             Get = (fun _ -> "")
             Step = (fun current _ -> current) }
           { Label = "MOUSE SENSITIVITY"
             Kind = Slider(0.2f, 3.0f, 0.1f)
             Get = (fun current -> $"{current.MouseSensitivity:F1}")
             Step =
                 (fun current direction ->
                     { current with
                         MouseSensitivity = Math.Clamp(current.MouseSensitivity + float32 direction * 0.1f, 0.2f, 3.0f) }) }
           { Label = "ADS TOGGLE"
             Kind = Toggle
             Get = (fun current -> if current.AdsToggle then "ON" else "OFF")
             Step = (fun current _ -> { current with AdsToggle = not current.AdsToggle }) }
           { Label = "CROUCH TOGGLE"
             Kind = Toggle
             Get = (fun current -> if current.CrouchToggle then "ON" else "OFF")
             Step = (fun current _ -> { current with CrouchToggle = not current.CrouchToggle }) }
           { Label = "EFFECTS"
             Kind = Header
             Get = (fun _ -> "")
             Step = (fun current _ -> current) }
           { Label = "BLOOD COLOR"
             Kind = Cycle
             Get = (fun current -> Settings.bloodNames[Settings.bloodColorIndex current.BloodColor])
             Step = Settings.stepBloodColor }
           { Label = "RESET TO DEFAULTS"
             Kind = Action
             Get = (fun _ -> "")
             Step = (fun _ _ -> Settings.defaults) } |]

    let private selectableIndexes (all: Row array) =
        all
        |> Array.indexed
        |> Array.choose (fun (index, row) -> if row.Kind = Header then None else Some index)

    let create (settings: GameSettings) =
        let indexes = selectableIndexes (rows settings)
        { Settings = settings
          Selected = indexes[0]
          FirstVisible = 0 }

    let private move (all: Row array) direction selected =
        let indexes = selectableIndexes all
        if indexes.Length = 0 then selected
        else
            let position = indexes |> Array.tryFindIndex ((=) selected) |> Option.defaultValue 0
            indexes[(position + direction + indexes.Length) % indexes.Length]

    let private scroll (all: Row array) maxVisible selected firstVisible =
        if all.Length <= maxVisible then 0
        else
            Math.Clamp(Math.Clamp(firstVisible, selected - maxVisible + 1, selected), 0, all.Length - maxVisible)

    type RowVisual =
        { Label: string
          Value: string
          Header: bool
          Adjustable: bool
          Selected: bool }

    /// The row window the HUD draws, with the selected row always in view.
    let visibleRows (state: State) =
        let all = rows state.Settings
        let last = min (all.Length - 1) (state.FirstVisible + MaxVisibleRows - 1)
        [ for index in state.FirstVisible..last ->
            { Label = all[index].Label
              Value = all[index].Get state.Settings
              Header = all[index].Kind = Header
              Adjustable =
                  match all[index].Kind with
                  | Slider _ | Toggle | Cycle -> true
                  | _ -> false
              Selected = index = state.Selected } ]

    let update (width: int) (height: int) (input: MenuInput) (state: State) =
        let all = rows state.Settings
        // Hover hits the drawn window; header rows are labels, not targets.
        let visibleCount = min MaxVisibleRows all.Length
        let hovered =
            input.Pointer
            |> Option.bind (fun pointer ->
                rowsRect (panelRect width height visibleCount) visibleCount
                |> Rect.slotAt RowHeight visibleCount pointer)
            |> Option.map (fun slot -> min (state.FirstVisible + slot) (all.Length - 1))
            |> Option.filter (fun index -> all[index].Kind <> Header)
        let mutable selected = state.Selected
        hovered |> Option.iter (fun index -> selected <- index)
        if input.Up then selected <- move all -1 selected
        if input.Down then selected <- move all 1 selected
        let row = all[selected]
        let direction = if input.Left then -1 elif input.Right then 1 else 0
        let activate = input.Activate || (input.Clicked && hovered.IsSome)
        let stepWhen condition = if condition then row.Step state.Settings direction else state.Settings
        let settings =
            match row.Kind with
            | Header -> state.Settings
            | Slider _ -> stepWhen (direction <> 0)
            | Toggle | Cycle -> stepWhen (direction <> 0 || activate)
            | Action -> stepWhen activate
        { Settings = settings
          Selected = selected
          FirstVisible = scroll all MaxVisibleRows selected state.FirstVisible }
