namespace Ironsight.Shell

open System.Numerics
open Ironsight

type MenuPage = Main | NameEntry | OfflineMaps | ServerList | OnlineLoadout

type StartMenuState =
    { Page: MenuPage
      Selected: int
      PlayerName: string }

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
    | StartOnline of weaponName: string
    | OpenSettings
    | ExitGame

[<RequireQualifiedAccess>]
module StartMenu =
    let create playerName =
        let sanitized = Multiplayer.sanitizeName playerName
        { Page = Main
          Selected = 0
          PlayerName = if System.String.IsNullOrWhiteSpace sanitized then "Soldier" else sanitized }

    let initial = create "Soldier"

    let items state =
        match state.Page with
        | Main -> [| $"CALLSIGN  {state.PlayerName}"; "QUICK PLAY"; "OFFLINE PLAY / MAP SELECT"; "JOIN ONLINE"; "SETTINGS"; "QUIT" |]
        | NameEntry -> [| $"> {state.PlayerName}_" |]
        | OfflineMaps -> [| "PAINTBALL KILLHOUSE"; "TRAINING YARD"; "STALINGRAD STREET"; "NORMANDY BATTLEFIELD"; "BACK" |]
        | ServerList -> [| "FLY.IO  -  FSHARP-OF-DUTY.FLY.DEV"; "BACK" |]
        | OnlineLoadout ->
            Array.append (Tuning.onlineWeapons |> Array.map (fun weapon -> weapon.Name.ToUpperInvariant())) [| "BACK" |]

    let subtitle state =
        match state.Page with
        | Main -> "SELECT A DEPLOYMENT"
        | NameEntry -> "TYPE YOUR CALLSIGN, THEN PRESS ENTER"
        | OfflineMaps -> "OFFLINE MAP SELECT"
        | ServerList -> "SERVER LIST"
        | OnlineLoadout -> "SELECT ONLINE LOADOUT"

    let private hoveredIndex (width: int) (height: int) (count: int) (pointer: Vector2) =
        let rowHeight = 54.0f
        let panelWidth = min 840.0f (float32 width - 48.0f)
        let left = float32 width * 0.5f - panelWidth * 0.5f
        let top = float32 height * 0.5f - (156.0f + float32 count * rowHeight) * 0.5f + 99.0f
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
            | OfflineMaps, 1 -> struct(next, Some(StartOffline "training"))
            | OfflineMaps, 2 -> struct(next, Some(StartOffline "stalingrad"))
            | OfflineMaps, 3 -> struct(next, Some(StartOffline "battlefield"))
            | OfflineMaps, _ -> struct({ next with Page = Main; Selected = 0 }, None)
            | ServerList, 0 -> struct({ next with Page = OnlineLoadout; Selected = 0 }, None)
            | ServerList, _ -> struct({ next with Page = Main; Selected = 0 }, None)
            | OnlineLoadout, index when index < Tuning.onlineWeapons.Length ->
                struct(next, Some(StartOnline Tuning.onlineWeapons[index].Name))
            | OnlineLoadout, _ -> struct({ next with Page = ServerList; Selected = 0 }, None)
