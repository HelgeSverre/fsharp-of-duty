namespace Ironsight.Shell

open System.Numerics
open Ironsight
open Silk.NET.Input

[<RequireQualifiedAccess>]
module InputTuning =
    /// Keep the right stick precise through a sight without changing the
    /// player's configured hip-fire sensitivity (or mouse sensitivity).
    [<Literal>]
    let GamepadAdsMultiplier = 0.5f

    /// ADS itself eases in, so the matching sensitivity change eases in too
    /// instead of snapping the instant the left trigger is pulled.
    let gamepadAdsScale ads =
        let amount = System.Math.Clamp(ads, 0.0f, 1.0f)
        1.0f + (GamepadAdsMultiplier - 1.0f) * amount

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

    /// Gamepad held-button bindings (DualSense/Xbox layout via GLFW's mapping
    /// database). Fire/ADS ride the triggers and crouch latches, so they are
    /// handled separately like their mouse/keyboard counterparts.
    static let heldPadButtons =
        [ ButtonName.A, InputButtons.Jump
          ButtonName.LeftStick, InputButtons.Sprint
          ButtonName.RightBumper, InputButtons.Grenade
          ButtonName.DPadUp, InputButtons.Weapon1
          ButtonName.DPadRight, InputButtons.Weapon2
          ButtonName.DPadDown, InputButtons.Weapon3
          ButtonName.DPadLeft, InputButtons.Weapon4 ]

    // The knobs real hardware will want tuned, in one place.
    static let StickDeadzone = 0.15f
    /// Against a 0..1-normalized trigger position.
    static let TriggerThreshold = 0.5f
    /// Radians/second of look at full right-stick deflection.
    static let StickTurnRate = 2.8f

    let keyboard = context.Keyboards |> Seq.tryHead
    let mouse = context.Mice |> Seq.tryHead
    let mutable gamepad = context.Gamepads |> Seq.tryHead
    let attachedPads = System.Collections.Generic.HashSet<int>()
    let mutable sequence = 0L
    let mutable lookDelta = Vector2.Zero
    let mutable lastPosition = Vector2.Zero
    let mutable firstMouseSample = true
    let mutable fireLatched = false
    let mutable reloadLatched = false
    let mutable escapeLatched = false
    let mutable loadoutLatched = false
    let mutable chatLatched = false
    let mutable menuActive = false
    let mutable pending = MenuInput.empty
    let mutable mousePosition = Vector2.Zero
    let mutable lastMenuPointer = Vector2(System.Single.NaN, System.Single.NaN)
    let mutable lookSensitivity = 1.0f
    let mutable gamepadSensitivity = 1.0f
    // Scroll notches waiting to be turned into weapon steps. Accumulated on
    // the mouse callback and drained one per sampled frame, so a fast flick
    // walks the inventory instead of collapsing into a single step.
    let mutable scrollPending = 0
    let mutable adsToggleEnabled = false
    let mutable adsLatched = false
    let mutable adsPrevHeld = false
    let mutable crouchToggleEnabled = false
    let mutable crouchLatched = false
    let mutable crouchPrevHeld = false
    let mutable debugLatched = false

    // Pad buttons/triggers are level polls, so a button held while a menu
    // closes (equipping with A, backing out with a trigger pulled) would leak
    // straight into the sim as jump/crouch/fire. SetMenuActive snapshots what
    // is held at that moment and those inputs stay dead until re-edged.
    let mutable suppressedPadButtons: Set<ButtonName> = Set.empty
    let mutable suppressedTriggers: Set<int> = Set.empty

    // Silk's ButtonName order matches GLFW's gamepad button indices, so the
    // list indexes directly — no per-poll scan or enumerator allocation.
    let padPressedRaw (name: ButtonName) =
        gamepad
        |> Option.exists (fun pad ->
            pad.IsConnected && int name < pad.Buttons.Count && pad.Buttons[int name].Pressed)

    /// Trigger position normalized to 0 (released) .. 1 (fully pulled). GLFW
    /// gamepad trigger axes rest at -1, not 0.
    let padTriggerRaw index =
        gamepad
        |> Option.filter (fun pad -> pad.IsConnected && index < pad.Triggers.Count)
        |> Option.map (fun pad -> (pad.Triggers[index].Position + 1.0f) * 0.5f)
        |> Option.defaultValue 0.0f

    let padPressed name =
        if suppressedPadButtons.Contains name then false else padPressedRaw name

    let padTrigger index =
        if suppressedTriggers.Contains index then 0.0f else padTriggerRaw index

    let padStick index =
        gamepad
        |> Option.filter (fun pad -> pad.IsConnected && index < pad.Thumbsticks.Count)
        |> Option.map (fun pad -> Vector2(pad.Thumbsticks[index].X, pad.Thumbsticks[index].Y))
        |> Option.defaultValue Vector2.Zero

    // Menu navigation and one-shot gameplay actions come in as edges, same as
    // KeyDown. Silk reuses the same pad objects across replug, so subscribe
    // once per slot; the handler gates on the active pad so a second
    // controller can't drive reload/menu while the first one aims.
    let attachGamepad (pad: IGamepad) =
        // Traditional zeroes the flat center; AdaptiveGradient is an
        // anti-deadzone in Silk and would make every stick drift at 15%.
        pad.Deadzone <- Deadzone(StickDeadzone, DeadzoneMethod.Traditional)
        if attachedPads.Add pad.Index then
            pad.add_ButtonDown(fun sender button ->
                if gamepad |> Option.exists (fun active -> active.Index = sender.Index) then
                    if menuActive then
                        match button.Name with
                        | ButtonName.DPadUp -> pending <- { pending with Up = true }
                        | ButtonName.DPadDown -> pending <- { pending with Down = true }
                        | ButtonName.DPadLeft -> pending <- { pending with Left = true }
                        | ButtonName.DPadRight -> pending <- { pending with Right = true }
                        | ButtonName.A -> pending <- { pending with Activate = true }
                        | ButtonName.B | ButtonName.Start -> pending <- { pending with Back = true }
                        // Y toggles the loadout picker closed again (see the
                        // Screen.Loadout branch in Program).
                        | ButtonName.Y -> loadoutLatched <- true
                        | _ -> ()
                    else
                        match button.Name with
                        | ButtonName.Start -> escapeLatched <- true
                        | ButtonName.X -> reloadLatched <- true
                        | ButtonName.Y -> loadoutLatched <- true
                        | _ -> ())

    do
        keyboard
        |> Option.iter (fun device ->
            device.add_KeyDown(fun _ key _ ->
                if menuActive then
                    match key with
                    | Key.Up -> pending <- { pending with Up = true }
                    | Key.Down -> pending <- { pending with Down = true }
                    | Key.Left -> pending <- { pending with Left = true }
                    | Key.Right -> pending <- { pending with Right = true }
                    | Key.Enter -> pending <- { pending with Activate = true }
                    | Key.Escape -> pending <- { pending with Back = true }
                    | Key.Backspace -> pending <- { pending with Backspace = true }
                    | _ -> ()
                else
                    match key with
                    | Key.Escape -> escapeLatched <- true
                    | Key.R -> reloadLatched <- true
                    | Key.B -> loadoutLatched <- true
                    | Key.Y -> chatLatched <- true
                    | Key.F3 -> debugLatched <- true
                    | _ -> ())
            device.add_KeyChar(fun _ character ->
                if menuActive && character >= ' ' && character <= '~' then
                    pending <- { pending with TextInput = pending.TextInput + string character }))
        mouse
        |> Option.iter (fun device ->
            device.Cursor.CursorMode <- CursorMode.Raw
            device.add_MouseDown(fun _ button ->
                if button = MouseButton.Left then
                    if menuActive then pending <- { pending with Clicked = true } else fireLatched <- true)
            device.add_Scroll(fun _ wheel ->
                if not menuActive then
                    let notches = int (round wheel.Y)
                    // A trackpad reports fractional deltas; anything non-zero
                    // still counts as one notch so it is not silently lost.
                    let step = if notches <> 0 then notches elif wheel.Y > 0.0f then 1 elif wheel.Y < 0.0f then -1 else 0
                    scrollPending <- System.Math.Clamp(scrollPending + step, -8, 8))
            device.add_MouseMove(fun _ position ->
                mousePosition <- position
                if firstMouseSample then firstMouseSample <- false
                elif not menuActive then
                    let delta = position - lastPosition
                    if delta.LengthSquared() < 40000.0f then
                        lookDelta <- lookDelta + delta * Vector2(0.0022f * lookSensitivity, -0.0022f * lookSensitivity)
                lastPosition <- position))
        gamepad |> Option.iter attachGamepad
        context.add_ConnectionChanged(fun device connected ->
            match device with
            | :? IGamepad as pad ->
                if connected then
                    // Adopt only when there is no live active pad — a wheel or
                    // second controller plugged in mid-match must not steal
                    // input from the one in use.
                    if gamepad |> Option.forall (fun active -> not active.IsConnected) then gamepad <- Some pad
                    attachGamepad pad
                else
                    gamepad <- context.Gamepads |> Seq.filter (fun p -> p.IsConnected) |> Seq.tryHead
                    gamepad |> Option.iter attachGamepad
            | _ -> ())

    member _.Sample(adsAmount: float32) =
        sequence <- sequence + 1L
        let pressed key = keyboard |> Option.exists (fun device -> device.IsKeyPressed key)
        let mousePressed button = mouse |> Option.exists (fun device -> device.IsButtonPressed button)
        // A suppressed pad input comes back only once it has re-edged.
        suppressedPadButtons <- suppressedPadButtons |> Set.filter padPressedRaw
        suppressedTriggers <- suppressedTriggers |> Set.filter (fun index -> padTriggerRaw index > TriggerThreshold)
        // Left stick and WASD stack; GLFW sticks are down-positive, hence -Y.
        let moveStick = padStick 0
        let x =
            System.Math.Clamp(
                (if pressed Key.D then 1.0f else 0.0f) - (if pressed Key.A then 1.0f else 0.0f) + moveStick.X,
                -1.0f, 1.0f)
        let y =
            System.Math.Clamp(
                (if pressed Key.W then 1.0f else 0.0f) - (if pressed Key.S then 1.0f else 0.0f) - moveStick.Y,
                -1.0f, 1.0f)
        if not menuActive then
            let lookStick = padStick 1
            // Sample() runs once per fixed tick, so the tick duration is the
            // exact frame delta — a wall clock would smear pauses into a snap.
            // ponytail: linear response; add a curve if aiming feels twitchy.
            let scale =
                StickTurnRate
                * gamepadSensitivity
                * InputTuning.gamepadAdsScale adsAmount
                / float32 Tuning.TickRate
            lookDelta <- lookDelta + Vector2(lookStick.X * scale, -lookStick.Y * scale)
        let mutable buttons = InputButtons.None
        if fireLatched || mousePressed MouseButton.Left || padTrigger 1 > TriggerThreshold then
            buttons <- buttons ||| InputButtons.Fire
        let adsHeld = mousePressed MouseButton.Right || padTrigger 0 > TriggerThreshold
        let ads =
            if adsToggleEnabled then
                if adsHeld && not adsPrevHeld then adsLatched <- not adsLatched
                adsPrevHeld <- adsHeld
                adsLatched
            else adsHeld
        if ads then buttons <- buttons ||| InputButtons.Ads
        if reloadLatched then buttons <- buttons ||| InputButtons.Reload
        // One notch per sampled frame. The sim ignores further requests while a
        // switch is in flight, so the leftovers queue rather than being eaten.
        if scrollPending > 0 then
            buttons <- buttons ||| InputButtons.WeaponNext
            scrollPending <- scrollPending - 1
        elif scrollPending < 0 then
            buttons <- buttons ||| InputButtons.WeaponPrev
            scrollPending <- scrollPending + 1
        // Hold-to-crouch is the default; toggle mode latches here in the input
        // layer and simply keeps the button held, so the simulation only ever
        // sees hold semantics.
        let crouchHeld = pressed Key.ControlLeft || padPressed ButtonName.B
        let crouch =
            if crouchToggleEnabled then
                if crouchHeld && not crouchPrevHeld then crouchLatched <- not crouchLatched
                crouchPrevHeld <- crouchHeld
                crouchLatched
            else crouchHeld
        if crouch then buttons <- buttons ||| InputButtons.Crouch
        for key, button in heldKeyButtons do
            if pressed key then buttons <- buttons ||| button
        for name, button in heldPadButtons do
            if padPressed name then buttons <- buttons ||| button
        let sampledLook = Vector2(System.Math.Clamp(lookDelta.X, -0.2f, 0.2f), System.Math.Clamp(lookDelta.Y, -0.2f, 0.2f))
        lookDelta <- Vector2.Zero
        fireLatched <- false
        reloadLatched <- false
        { Sequence = sequence; Move = Vector2(x, y); Look = sampledLook; Buttons = buttons }

    /// Menu-style key routing: text/backspace/Enter/Esc collect into `pending`
    /// and look deltas stop reaching the sim. `releasePointer` un-grabs the
    /// cursor, which the real menus want and chat must not do.
    member private _.SetMode(value: bool, releasePointer: bool) =
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
        // a stale Esc would instantly close the menu it just opened, and a B
        // typed into the callsign field must not open the loadout on resume.
        escapeLatched <- false
        loadoutLatched <- false
        chatLatched <- false
        // Notches spun while a menu was open must not fire on resume.
        scrollPending <- 0
        pending <- { MenuInput.empty with TextInput = pending.TextInput }
        if value then
            suppressedPadButtons <- Set.empty
            suppressedTriggers <- Set.empty
        else
            // Everything held at the moment a menu closes stays dead until
            // released, so equip-with-A doesn't jump and a pulled trigger
            // doesn't fire on the first play frame.
            suppressedPadButtons <-
                (heldPadButtons |> List.map fst) @ [ ButtonName.B; ButtonName.Back ]
                |> List.filter padPressedRaw
                |> Set.ofList
            suppressedTriggers <-
                [ 0; 1 ] |> List.filter (fun index -> padTriggerRaw index > TriggerThreshold) |> Set.ofList
        mouse
        |> Option.iter (fun device -> device.Cursor.CursorMode <- if releasePointer then CursorMode.Normal else CursorMode.Raw)

    member this.SetMenuActive(value: bool) = this.SetMode(value, value)

    /// Chat typing: menu key routing with the pointer still grabbed.
    member this.SetTextCapture(value: bool) = this.SetMode(value, false)

    member _.ApplySettings(value: GameSettings) =
        lookSensitivity <- value.MouseSensitivity
        gamepadSensitivity <- value.GamepadSensitivity
        adsToggleEnabled <- value.AdsToggle
        crouchToggleEnabled <- value.CrouchToggle
        adsLatched <- false
        adsPrevHeld <- false
        crouchLatched <- false
        crouchPrevHeld <- false

    member _.ConsumeMenuInput() =
        // Report the pointer only when the mouse moved (or clicked). A cursor
        // resting over the option list must not pin the hover selection every
        // frame, or arrow-key navigation can never move off the hovered row.
        let pointerMoved = (mousePosition - lastMenuPointer).LengthSquared() > 4.0f
        if pointerMoved then lastMenuPointer <- mousePosition
        let value =
            { pending with Pointer = if pointerMoved || pending.Clicked then Some mousePosition else None }
        pending <- MenuInput.empty
        value

    member _.ConsumeEscape() =
        let value = escapeLatched
        escapeLatched <- false
        value

    member _.ConsumeLoadoutToggle() =
        let value = loadoutLatched
        loadoutLatched <- false
        value

    member _.ConsumeChatToggle() =
        let value = chatLatched
        chatLatched <- false
        value

    member _.ConsumeDebugToggle() =
        let value = debugLatched
        debugLatched <- false
        value

    member _.ScoreboardHeld =
        keyboard |> Option.exists (fun device -> device.IsKeyPressed Key.Tab)
        || padPressed ButtonName.Back
