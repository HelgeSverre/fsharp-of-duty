namespace Ironsight.Tests

open System.Numerics
open Ironsight
open Ironsight.Shell
open Xunit

module SettingsTests =
    let idleInput = TestKit.idleMenuInput

    let private selectRow label (state: SettingsUi.State) =
        let index = SettingsUi.rows state.Settings |> Array.findIndex (fun row -> row.Label = label)
        { state with Selected = index }

    [<Fact>]
    let ``settings json round trip preserves every field`` () =
        let custom =
            { Fov = 85.0f
              Contrast = 1.15f
              MouseSensitivity = 1.6f
              AdsToggle = true
              CrouchToggle = true
              BloodColor = Green }
        let restored = Settings.deserialize (Settings.serialize custom)
        Assert.Equal(85.0f, restored.Fov)
        Assert.Equal(1.15f, restored.Contrast)
        Assert.Equal(1.6f, restored.MouseSensitivity)
        Assert.True(restored.AdsToggle)
        Assert.True(restored.CrouchToggle)
        Assert.Equal(Green, restored.BloodColor)

    [<Fact>]
    let ``settings are clamped to supported ranges after load`` () =
        let wild =
            { Fov = 200.0f
              Contrast = 0.0f
              MouseSensitivity = 99.0f
              AdsToggle = true
              CrouchToggle = false
              BloodColor = Black }
        let restored = Settings.deserialize (Settings.serialize wild)
        Assert.Equal(95.0f, restored.Fov)
        Assert.Equal(0.8f, restored.Contrast)
        Assert.Equal(3.0f, restored.MouseSensitivity)

    [<Fact>]
    let ``blood palette covers every color with a distinct rgb`` () =
        let rgbs = Settings.bloodColors |> Array.map Settings.bloodRgb |> Array.distinct
        Assert.Equal(Settings.bloodColors.Length, rgbs.Length)
        Assert.Equal(Vector3(0.58f, 0.015f, 0.018f), Settings.bloodRgb Crimson)

    [<Fact>]
    let ``settings navigation skips headers and wraps`` () =
        let state = SettingsUi.create Settings.defaults
        let selectable =
            SettingsUi.rows Settings.defaults
            |> Array.filter (fun row -> row.Kind <> SettingsUi.Header)
            |> Array.length
        let mutable walked = state
        for _ in 1..selectable do walked <- SettingsUi.update 1280 720 { idleInput with Down = true } walked
        // Walking down never lands on a header and eventually returns home.
        Assert.Equal(state.Selected, walked.Selected)
        let down = SettingsUi.update 1280 720 { idleInput with Down = true } state
        let selectedRow = (SettingsUi.rows down.Settings)[down.Selected]
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
        let all = SettingsUi.rows Settings.defaults
        let visibleCount = min SettingsUi.MaxVisibleRows all.Length
        let rowsRect = SettingsUi.rowsRect (SettingsUi.panelRect 1280 720 visibleCount) visibleCount
        let middleOf index =
            let slot = Rect.slot SettingsUi.RowHeight index rowsRect
            Vector2(slot.X + slot.W * 0.5f, slot.Y + slot.H * 0.5f)
        // Hovering a header row is ignored; selection stays where it was.
        let overHeader = SettingsUi.update 1280 720 { idleInput with Pointer = Some(middleOf 0) } state
        Assert.Equal(state.Selected, overHeader.Selected)
        // Hovering an adjustable row selects it; clicking a toggle flips it.
        let toggleIndex = all |> Array.findIndex (fun row -> row.Label = "ADS TOGGLE")
        let clicked =
            SettingsUi.update 1280 720 { idleInput with Pointer = Some(middleOf toggleIndex); Clicked = true } state
        Assert.Equal(toggleIndex, clicked.Selected)
        Assert.True(clicked.Settings.AdsToggle)

    [<Fact>]
    let ``scroll window keeps the selected row visible`` () =
        let state = SettingsUi.create Settings.defaults
        Assert.True((SettingsUi.rows state.Settings).Length > SettingsUi.MaxVisibleRows)
        let mutable walked = state
        // Walk to the last selectable row (RESET TO DEFAULTS), which sits
        // beyond the initial window and must pull the scroll into view.
        let selectable =
            SettingsUi.rows Settings.defaults
            |> Array.filter (fun row -> row.Kind <> SettingsUi.Header)
            |> Array.length
        for _ in 1..selectable - 1 do walked <- SettingsUi.update 1280 720 { idleInput with Down = true } walked
        Assert.True(walked.FirstVisible > 0)
        let visible = SettingsUi.visibleRows walked
        Assert.Contains(visible, fun row -> row.Selected)
        Assert.True(visible.Length <= SettingsUi.MaxVisibleRows)
