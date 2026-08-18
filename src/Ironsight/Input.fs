namespace Ironsight.Shell

open System.Numerics
open Ironsight
open Silk.NET.Input

type InputSampler(context: IInputContext) =
    /// Plain held-key bindings. Fire/ADS/crouch/reload are handled separately
    /// because they latch or toggle.
    static let heldKeyButtons =
        [ Key.ShiftLeft, InputButtons.Sprint
          Key.Z, InputButtons.Prone
          Key.G, InputButtons.Grenade
          Key.Space, InputButtons.Jump
          Key.Number1, InputButtons.Weapon1
          Key.Number2, InputButtons.Weapon2
          Key.Number3, InputButtons.Weapon3
          Key.Number4, InputButtons.Weapon4
          Key.Number5, InputButtons.Weapon5 ]

    let keyboard = context.Keyboards |> Seq.tryHead
    let mouse = context.Mice |> Seq.tryHead
    let mutable sequence = 0L
    let mutable lookDelta = Vector2.Zero
    let mutable lastPosition = Vector2.Zero
    let mutable firstMouseSample = true
    let mutable fireLatched = false
    let mutable reloadLatched = false
    let mutable escapeLatched = false
    let mutable loadoutLatched = false
    let mutable menuActive = false
    let mutable menuUp = false
    let mutable menuDown = false
    let mutable menuLeft = false
    let mutable menuRight = false
    let mutable menuActivate = false
    let mutable menuBack = false
    let mutable menuBackspace = false
    let mutable menuText = ""
    let mutable menuClick = false
    let mutable mousePosition = Vector2.Zero
    let mutable lastMenuPointer = Vector2(System.Single.NaN, System.Single.NaN)
    let mutable lookSensitivity = 1.0f
    let mutable adsToggleEnabled = false
    let mutable adsLatched = false
    let mutable adsPrevHeld = false
    let mutable crouchToggleEnabled = false
    let mutable crouchLatched = false
    let mutable crouchPrevHeld = false
    let mutable debugLatched = false

    do
        keyboard
        |> Option.iter (fun device ->
            device.add_KeyDown(fun _ key _ ->
                if menuActive then
                    match key with
                    | Key.Up -> menuUp <- true
                    | Key.Down -> menuDown <- true
                    | Key.Left -> menuLeft <- true
                    | Key.Right -> menuRight <- true
                    | Key.Enter -> menuActivate <- true
                    | Key.Escape -> menuBack <- true
                    | Key.Backspace -> menuBackspace <- true
                    | _ -> ()
                else
                    match key with
                    | Key.Escape -> escapeLatched <- true
                    | Key.R -> reloadLatched <- true
                    | Key.B -> loadoutLatched <- true
                    | Key.F3 -> debugLatched <- true
                    | _ -> ())
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
                        lookDelta <- lookDelta + delta * Vector2(0.0022f * lookSensitivity, -0.0022f * lookSensitivity)
                lastPosition <- position))

    member _.Sample() =
        sequence <- sequence + 1L
        let pressed key = keyboard |> Option.exists (fun device -> device.IsKeyPressed key)
        let mousePressed button = mouse |> Option.exists (fun device -> device.IsButtonPressed button)
        let x = (if pressed Key.D then 1.0f else 0.0f) - (if pressed Key.A then 1.0f else 0.0f)
        let y = (if pressed Key.W then 1.0f else 0.0f) - (if pressed Key.S then 1.0f else 0.0f)
        let mutable buttons = InputButtons.None
        if fireLatched || mousePressed MouseButton.Left then buttons <- buttons ||| InputButtons.Fire
        let adsHeld = mousePressed MouseButton.Right
        let ads =
            if adsToggleEnabled then
                if adsHeld && not adsPrevHeld then adsLatched <- not adsLatched
                adsPrevHeld <- adsHeld
                adsLatched
            else adsHeld
        if ads then buttons <- buttons ||| InputButtons.Ads
        if reloadLatched then buttons <- buttons ||| InputButtons.Reload
        // Hold-to-crouch is the default; toggle mode latches here in the input
        // layer and simply keeps the button held, so the simulation only ever
        // sees hold semantics.
        let crouchHeld = pressed Key.ControlLeft
        let crouch =
            if crouchToggleEnabled then
                if crouchHeld && not crouchPrevHeld then crouchLatched <- not crouchLatched
                crouchPrevHeld <- crouchHeld
                crouchLatched
            else crouchHeld
        if crouch then buttons <- buttons ||| InputButtons.Crouch
        for key, button in heldKeyButtons do
            if pressed key then buttons <- buttons ||| button
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
        adsLatched <- false
        adsPrevHeld <- false
        crouchLatched <- false
        crouchPrevHeld <- false
        // A press latched under the old mode must not fire under the new one —
        // a stale Esc would instantly close the menu it just opened.
        escapeLatched <- false
        menuBack <- false
        mouse
        |> Option.iter (fun device -> device.Cursor.CursorMode <- if value then CursorMode.Normal else CursorMode.Raw)

    member _.SetSensitivity(value: float32) = lookSensitivity <- value

    member _.SetAdsToggle(value: bool) =
        adsToggleEnabled <- value
        adsLatched <- false
        adsPrevHeld <- false

    member _.SetCrouchToggle(value: bool) =
        crouchToggleEnabled <- value
        crouchLatched <- false
        crouchPrevHeld <- false

    member _.ConsumeMenuInput() =
        // Report the pointer only when the mouse moved (or clicked). A cursor
        // resting over the option list must not pin the hover selection every
        // frame, or arrow-key navigation can never move off the hovered row.
        let pointerMoved = (mousePosition - lastMenuPointer).LengthSquared() > 4.0f
        if pointerMoved then lastMenuPointer <- mousePosition
        let value =
            { Up = menuUp
              Down = menuDown
              Left = menuLeft
              Right = menuRight
              Activate = menuActivate
              Back = menuBack
              Backspace = menuBackspace
              TextInput = menuText
              Pointer = if pointerMoved || menuClick then Some mousePosition else None
              Clicked = menuClick }
        menuUp <- false
        menuDown <- false
        menuLeft <- false
        menuRight <- false
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

    member _.ConsumeLoadoutToggle() =
        let value = loadoutLatched
        loadoutLatched <- false
        value

    member _.ConsumeDebugToggle() =
        let value = debugLatched
        debugLatched <- false
        value
    member _.ScoreboardHeld = keyboard |> Option.exists (fun device -> device.IsKeyPressed Key.Tab)
