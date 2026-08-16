namespace Ironsight.Shell

open System.Numerics
open Ironsight
open Silk.NET.Input

type InputSampler(context: IInputContext) =
    let keyboard = context.Keyboards |> Seq.tryHead
    let mouse = context.Mice |> Seq.tryHead
    let mutable sequence = 0L
    let mutable lookDelta = Vector2.Zero
    let mutable lastPosition = Vector2.Zero
    let mutable firstMouseSample = true
    let mutable fireLatched = false
    let mutable reloadLatched = false
    let mutable escapeLatched = false
    let mutable menuActive = false
    let mutable menuUp = false
    let mutable menuDown = false
    let mutable menuActivate = false
    let mutable menuBack = false
    let mutable menuBackspace = false
    let mutable menuText = ""
    let mutable menuClick = false
    let mutable mousePosition = Vector2.Zero

    do
        keyboard
        |> Option.iter (fun device ->
            device.add_KeyDown(fun _ key _ ->
                if menuActive then
                    if key = Key.Up then menuUp <- true
                    elif key = Key.Down then menuDown <- true
                    elif key = Key.Enter then menuActivate <- true
                    elif key = Key.Escape then menuBack <- true
                    elif key = Key.Backspace then menuBackspace <- true
                else
                    if key = Key.Escape then escapeLatched <- true
                    elif key = Key.R then reloadLatched <- true)
            device.add_KeyChar(fun _ character ->
                if menuActive && character >= ' ' && character <= '~' then
                    menuText <- menuText + string character))
        mouse
        |> Option.iter (fun device ->
            device.Cursor.CursorMode <- CursorMode.Raw
            device.add_MouseDown(fun _ button ->
                if button = MouseButton.Left then
                    if menuActive then menuClick <- true else fireLatched <- true)
            device.add_MouseMove(fun _ position ->
                mousePosition <- position
                if firstMouseSample then firstMouseSample <- false
                elif not menuActive then
                    let delta = position - lastPosition
                    if delta.LengthSquared() < 40000.0f then
                        lookDelta <- lookDelta + delta * Vector2(0.0022f, -0.0022f)
                lastPosition <- position))

    member _.Sample() =
        sequence <- sequence + 1L
        let pressed key = keyboard |> Option.exists (fun device -> device.IsKeyPressed key)
        let mousePressed button = mouse |> Option.exists (fun device -> device.IsButtonPressed button)
        let x = (if pressed Key.D then 1.0f else 0.0f) - (if pressed Key.A then 1.0f else 0.0f)
        let y = (if pressed Key.W then 1.0f else 0.0f) - (if pressed Key.S then 1.0f else 0.0f)
        let mutable buttons = InputButtons.None
        if fireLatched || mousePressed MouseButton.Left then buttons <- buttons ||| InputButtons.Fire
        if mousePressed MouseButton.Right then buttons <- buttons ||| InputButtons.Ads
        if pressed Key.ShiftLeft then buttons <- buttons ||| InputButtons.Sprint
        if reloadLatched then buttons <- buttons ||| InputButtons.Reload
        if pressed Key.ControlLeft then buttons <- buttons ||| InputButtons.Crouch
        if pressed Key.Z then buttons <- buttons ||| InputButtons.Prone
        if pressed Key.G then buttons <- buttons ||| InputButtons.Grenade
        if pressed Key.Space then buttons <- buttons ||| InputButtons.Jump
        if pressed Key.Number1 then buttons <- buttons ||| InputButtons.Weapon1
        if pressed Key.Number2 then buttons <- buttons ||| InputButtons.Weapon2
        if pressed Key.Number3 then buttons <- buttons ||| InputButtons.Weapon3
        if pressed Key.Number4 then buttons <- buttons ||| InputButtons.Weapon4
        if pressed Key.Number5 then buttons <- buttons ||| InputButtons.Weapon5
        let sampledLook = Vector2(System.Math.Clamp(lookDelta.X, -0.2f, 0.2f), System.Math.Clamp(lookDelta.Y, -0.2f, 0.2f))
        lookDelta <- Vector2.Zero
        fireLatched <- false
        reloadLatched <- false
        menuBackspace <- false
        menuText <- ""
        { Sequence = sequence; Move = Vector2(x, y); Look = sampledLook; Buttons = buttons }

    member _.SetMenuActive(value: bool) =
        menuActive <- value
        firstMouseSample <- true
        lookDelta <- Vector2.Zero
        fireLatched <- false
        reloadLatched <- false
        mouse
        |> Option.iter (fun device -> device.Cursor.CursorMode <- if value then CursorMode.Normal else CursorMode.Raw)

    member _.ConsumeMenuInput() =
        let value =
            { Up = menuUp
              Down = menuDown
              Activate = menuActivate
              Back = menuBack
              Backspace = menuBackspace
              TextInput = menuText
              Pointer = Some mousePosition
              Clicked = menuClick }
        menuUp <- false
        menuDown <- false
        menuActivate <- false
        menuBack <- false
        menuBackspace <- false
        menuText <- ""
        menuClick <- false
        value

    member _.ConsumeEscape() =
        let value = escapeLatched
        escapeLatched <- false
        value
    member _.ScoreboardHeld = keyboard |> Option.exists (fun device -> device.IsKeyPressed Key.Tab)
