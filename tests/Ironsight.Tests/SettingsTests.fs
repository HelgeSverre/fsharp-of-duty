namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.Shell
open Xunit

module SettingsTests =
    let idleInput = TestKit.idleMenuInput

    let private selectRow label (state: SettingsUi.State) =
        let index = SettingsUi.rows |> Array.findIndex (fun row -> row.Label = label)
        { state with Selected = index }

    [<Fact>]
    let ``settings json round trip preserves every field`` () =
        let custom =
            { Fov = 85.0f
              Contrast = 1.15f
              MouseSensitivity = 1.6f
              GamepadSensitivity = 2.2f
              AdsToggle = true
              CrouchToggle = true
              ScrollWeaponSwitchOff = true
              BloodColor = Green
              Fullscreen = true
              WindowWidth = 2560
              WindowHeight = 1440 }
        let restored = Settings.deserialize (Settings.serialize custom)
        Assert.Equal(85.0f, restored.Fov)
        Assert.Equal(1.15f, restored.Contrast)
        Assert.Equal(1.6f, restored.MouseSensitivity)
        Assert.Equal(2.2f, restored.GamepadSensitivity)
        Assert.True(restored.AdsToggle)
        Assert.True(restored.CrouchToggle)
        Assert.True(restored.ScrollWeaponSwitchOff)
        Assert.Equal(Green, restored.BloodColor)
        Assert.True(restored.Fullscreen)
        Assert.Equal(2560, restored.WindowWidth)
        Assert.Equal(1440, restored.WindowHeight)

    [<Fact>]
    let ``a settings file written before scroll switching existed keeps it on`` () =
        // A bool missing from the JSON deserialises to false, so the field is
        // stored as the negative: absent means "not off", which is on. Name it
        // the other way round and every existing player silently loses scroll
        // weapon switching on upgrade.
        let old =
            """{"fov":75,"contrast":1.1,"mouseSensitivity":1.2,"gamepadSensitivity":1,
                 "adsToggle":false,"crouchToggle":false,"bloodColor":"Crimson",
                 "fullscreen":false,"windowWidth":1280,"windowHeight":720}"""
        let restored = Settings.deserialize old
        Assert.False(restored.ScrollWeaponSwitchOff)
        Assert.Equal(75.0f, restored.Fov)

    [<Fact>]
    let ``settings are clamped to supported ranges after load`` () =
        let wild =
            { Fov = 200.0f
              Contrast = 0.0f
              MouseSensitivity = 99.0f
              GamepadSensitivity = 99.0f
              AdsToggle = true
              CrouchToggle = false
              ScrollWeaponSwitchOff = false
              BloodColor = Black
              Fullscreen = false
              WindowWidth = 16
              WindowHeight = 9 }
        let restored = Settings.deserialize (Settings.serialize wild)
        Assert.Equal(95.0f, restored.Fov)
        Assert.Equal(0.8f, restored.Contrast)
        Assert.Equal(3.0f, restored.MouseSensitivity)
        Assert.Equal(3.0f, restored.GamepadSensitivity)
        // Floored rather than reset: a hand-edited file cannot produce a
        // window too small to interact with.
        Assert.Equal(640, restored.WindowWidth)
        Assert.Equal(360, restored.WindowHeight)

    [<Fact>]
    let ``old settings files without gamepad sensitivity fall back to the default`` () =
        let restored = Settings.deserialize """{"fov":75,"contrast":1.0,"mouseSensitivity":1.0,"adsToggle":false,"crouchToggle":false,"bloodColor":"Crimson"}"""
        Assert.Equal(1.0f, restored.GamepadSensitivity)

    [<Fact>]
    let ``old settings files without display fields fall back to a usable window`` () =
        let restored = Settings.deserialize """{"fov":75,"contrast":1.0,"mouseSensitivity":1.0,"adsToggle":false,"crouchToggle":false,"bloodColor":"Crimson"}"""
        Assert.Equal(1280, restored.WindowWidth)
        Assert.Equal(720, restored.WindowHeight)
        Assert.False(restored.Fullscreen)

    // These exercise the built-in Settings.resolutions fallback, which is what
    // a headless test run sees — Program replaces it at window load with the
    // modes the real monitor reports.
    [<Fact>]
    let ``resolution steps through the offered sizes and wraps at both ends`` () =
        let first = Settings.resolutions[0]
        let last = Settings.resolutions[Settings.resolutions.Length - 1]
        let atFirst = { Settings.defaults with WindowWidth = fst first; WindowHeight = snd first }
        Assert.Equal(Settings.resolutions[1], (let n = Settings.stepResolution atFirst 1 in n.WindowWidth, n.WindowHeight))
        Assert.Equal(last, (let n = Settings.stepResolution atFirst -1 in n.WindowWidth, n.WindowHeight))
        let atLast = { Settings.defaults with WindowWidth = fst last; WindowHeight = snd last }
        Assert.Equal(first, (let n = Settings.stepResolution atLast 1 in n.WindowWidth, n.WindowHeight))

    [<Fact>]
    let ``a stored size that is no longer offered steps from the nearest entry`` () =
        // 1288x724 is nobody's mode — a monitor swap or a hand-edited file.
        let odd = { Settings.defaults with WindowWidth = 1288; WindowHeight = 724 }
        Assert.Equal(0, Settings.resolutionIndex odd)
        let next = Settings.stepResolution odd 1
        Assert.Equal(Settings.resolutions[1], (next.WindowWidth, next.WindowHeight))

    [<Fact>]
    let ``fullscreen toggle flips with enter and left right`` () =
        let selected = selectRow "FULLSCREEN" (SettingsUi.create Settings.defaults)
        let on = SettingsUi.update 1280 720 { idleInput with Activate = true } selected
        Assert.True(on.Settings.Fullscreen)
        let off = SettingsUi.update 1280 720 { idleInput with Left = true } on
        Assert.False(off.Settings.Fullscreen)

    [<Fact>]
    let ``blood palette covers every color with a distinct rgb`` () =
        let rgbs = Settings.bloodColors |> Array.map Settings.bloodRgb |> Array.distinct
        Assert.Equal(Settings.bloodColors.Length, rgbs.Length)
        Assert.Equal(Vector3(0.58f, 0.015f, 0.018f), Settings.bloodRgb Crimson)

    [<Fact>]
    let ``settings navigation skips headers and wraps`` () =
        let state = SettingsUi.create Settings.defaults
        let selectable =
            SettingsUi.rows
            |> Array.filter (fun row -> row.Kind <> SettingsUi.Header)
            |> Array.length
        let mutable walked = state
        for _ in 1..selectable do walked <- SettingsUi.update 1280 720 { idleInput with Down = true } walked
        // Walking down never lands on a header and eventually returns home.
        Assert.Equal(state.Selected, walked.Selected)
        let down = SettingsUi.update 1280 720 { idleInput with Down = true } state
        let selectedRow = SettingsUi.rows[down.Selected]
        Assert.NotEqual(SettingsUi.Header, selectedRow.Kind)

    [<Fact>]
    let ``fov slider steps and clamps at its bounds`` () =
        let atFov = selectRow "FIELD OF VIEW" (SettingsUi.create Settings.defaults)
        let raised = SettingsUi.update 1280 720 { idleInput with Right = true } atFov
        Assert.Equal(70.0f, raised.Settings.Fov)
        let lowered = SettingsUi.update 1280 720 { idleInput with Left = true } atFov
        Assert.Equal(60.0f, lowered.Settings.Fov)
        let atMax = selectRow "FIELD OF VIEW" (SettingsUi.create { Settings.defaults with Fov = 95.0f })
        let capped = SettingsUi.update 1280 720 { idleInput with Right = true } atMax
        Assert.Equal(95.0f, capped.Settings.Fov)

    [<Fact>]
    let ``ads toggle flips with enter and left right`` () =
        let selected = selectRow "ADS TOGGLE" (SettingsUi.create Settings.defaults)
        let toggled = SettingsUi.update 1280 720 { idleInput with Activate = true } selected
        Assert.True(toggled.Settings.AdsToggle)
        let toggledBack = SettingsUi.update 1280 720 { idleInput with Left = true } toggled
        Assert.False(toggledBack.Settings.AdsToggle)

    [<Fact>]
    let ``blood color cycles through the palette`` () =
        let selected = selectRow "BLOOD COLOR" (SettingsUi.create Settings.defaults)
        let next = SettingsUi.update 1280 720 { idleInput with Right = true } selected
        Assert.Equal(Blue, next.Settings.BloodColor)
        let wrapped = SettingsUi.update 1280 720 { idleInput with Left = true } selected
        Assert.Equal(Pink, wrapped.Settings.BloodColor)

    [<Fact>]
    let ``reset action restores defaults`` () =
        let changed = SettingsUi.create { Settings.defaults with Fov = 90.0f; BloodColor = Green }
        let reset = SettingsUi.update 1280 720 { idleInput with Activate = true } (selectRow "RESET TO DEFAULTS" changed)
        Assert.Equal(Settings.defaults.Fov, reset.Settings.Fov)
        Assert.Equal(Settings.defaults.BloodColor, reset.Settings.BloodColor)

    [<Fact>]
    let ``mouse hover selects settings rows and a click activates them`` () =
        let state = SettingsUi.create Settings.defaults
        let all = SettingsUi.rows
        let visibleCount = min SettingsUi.MaxVisibleRows all.Length
        let rowsRect = SettingsUi.rowsRect (SettingsUi.panelRect 1280 720 visibleCount) visibleCount
        let middleOf index = TestKit.rowMiddle SettingsUi.RowHeight index rowsRect
        // Hovering a header row is ignored; selection stays where it was.
        let overHeader = SettingsUi.update 1280 720 { idleInput with Pointer = Some(middleOf 0) } state
        Assert.Equal(state.Selected, overHeader.Selected)
        // Hovering an adjustable row selects it; clicking a toggle flips it.
        // Must be a toggle inside the initial row window — rows past
        // MaxVisibleRows are not drawn, so they are not hoverable either.
        let toggleIndex = all |> Array.findIndex (fun row -> row.Label = "FULLSCREEN")
        Assert.True(toggleIndex < visibleCount)
        let clicked =
            SettingsUi.update 1280 720 { idleInput with Pointer = Some(middleOf toggleIndex); Clicked = true } state
        Assert.Equal(toggleIndex, clicked.Selected)
        Assert.True(clicked.Settings.Fullscreen)

    [<Fact>]
    let ``scroll window keeps the selected row visible`` () =
        let state = SettingsUi.create Settings.defaults
        Assert.True((SettingsUi.rows).Length > SettingsUi.MaxVisibleRows)
        let mutable walked = state
        // Walk to the last selectable row (RESET TO DEFAULTS), which sits
        // beyond the initial window and must pull the scroll into view.
        let selectable =
            SettingsUi.rows
            |> Array.filter (fun row -> row.Kind <> SettingsUi.Header)
            |> Array.length
        for _ in 1..selectable - 1 do walked <- SettingsUi.update 1280 720 { idleInput with Down = true } walked
        Assert.True(walked.FirstVisible > 0)
        let visible = SettingsUi.visibleRows walked
        Assert.Contains(visible, fun row -> row.Selected)
        Assert.True(visible.Length <= SettingsUi.MaxVisibleRows)
