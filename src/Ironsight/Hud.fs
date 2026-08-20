namespace Ironsight.Shell

#nowarn "9"

open System
open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Ironsight.ProcGen
open Silk.NET.OpenGL

type HudInfo =
    { Online: OnlineSnapshot option
      LocalPlayerId: int option
      ShowScoreboard: bool
      DamageDirection: Vector3 option
      /// 1.0 on the frame of the hit, decaying to 0.0; drives the marker's
      /// pop-and-fade animation. 0.0 means no marker.
      HitMarker: float32
      HitMarkerLethal: bool
      Subtitle: string option
      /// Kill-feed rows, newest first: pre-formatted text and a headshot flag
      /// that only picks the colour.
      Feed: (string * bool) list
      /// Chat rows, newest first; the flag marks a server line.
      Chat: (string * bool) list
      /// The line being typed, while the chat input is open.
      ChatDraft: string option
      /// Show the weapon category list (recent switch activity).
      ShowInventory: bool
      /// Wireframe + line-of-sight overlay (F3). Deliberately a wallhack —
      /// this is an experiment sandbox, not a secured competitive game.
      DebugView: bool
      /// Whether the local player is holding a grenade ready to throw, which
      /// drives the trajectory preview. Decided by the caller rather than read
      /// from World.Player.Grenade: online the client never advances the hand
      /// state, so the world would always report idle.
      GrenadeCooking: bool
      /// In-game loadout picker: Some selectedIndex while open.
      LoadoutScreen: LoadoutMenu.State option
      Menu: StartMenuState option
      Settings: GameSettings
      SettingsScreen: SettingsUi.State option }

[<RequireQualifiedAccess>]
module HudLayout =
    /// Ratio of framebuffer pixels to logical window units. High-DPI displays
    /// (retina, Windows scaling) report a framebuffer larger than the window.
    /// The HUD itself needs no scaling — its logical coordinates map to the
    /// framebuffer through NDC — but the ratio is logged at startup to make
    /// display-density problems diagnosable.
    let uiScale (framebufferWidth: int) (logicalWidth: int) =
        if framebufferWidth <= 0 || logicalWidth <= 0 then 1.0f
        else Math.Clamp(float32 framebufferWidth / float32 logicalWidth, 0.5f, 4.0f)

type Hud(gl: GL) =
    let font = FontGen.create ()
    let vertices = ResizeArray<float32>()
    let vao = gl.GenVertexArray()
    let buffer = gl.GenBuffer()
    let texture = gl.GenTexture()

    let program = GlUtil.createProgram gl Shaders.hudVertex Shaders.hudFragment

    let addVertex x y u v (color: Vector4) =
        vertices.Add x; vertices.Add y; vertices.Add u; vertices.Add v
        vertices.Add color.X; vertices.Add color.Y; vertices.Add color.Z; vertices.Add color.W

    let addQuad x y width height u0 v0 u1 v1 color =
        addVertex x y u0 v0 color
        addVertex (x + width) y u1 v0 color
        addVertex (x + width) (y + height) u1 v1 color
        addVertex x y u0 v0 color
        addVertex (x + width) (y + height) u1 v1 color
        addVertex x (y + height) u0 v1 color

    let solid x y width height color =
        let u = (float32 font.Width - 0.5f) / float32 font.Width
        let v = (float32 font.Height - 0.5f) / float32 font.Height
        addQuad x y width height u v u v color

    let gradientQuad x y width height (topLeft: Vector4) (topRight: Vector4) (bottomRight: Vector4) (bottomLeft: Vector4) =
        let u = (float32 font.Width - 0.5f) / float32 font.Width
        let v = (float32 font.Height - 0.5f) / float32 font.Height
        addVertex x y u v topLeft
        addVertex (x + width) y u v topRight
        addVertex (x + width) (y + height) u v bottomRight
        addVertex x y u v topLeft
        addVertex (x + width) (y + height) u v bottomRight
        addVertex x (y + height) u v bottomLeft

    let addText x y scale (color: Vector4) (text: string) =
        let logicalWidth, logicalHeight = 8.0f, 12.0f
        let mutable cursor = x
        for character in text.ToUpperInvariant() do
            let code = Math.Clamp(int character, 32, 127)
            let index = code - 32
            let column, row = index % 16, index / 16
            let u0 = float32 (column * font.CellWidth) / float32 font.Width
            let v0 = float32 (row * font.CellHeight) / float32 font.Height
            let u1 = float32 ((column + 1) * font.CellWidth) / float32 font.Width
            let v1 = float32 ((row + 1) * font.CellHeight) / float32 font.Height
            addQuad cursor y (logicalWidth * scale) (logicalHeight * scale) u0 v0 u1 v1 color
            cursor <- cursor + logicalWidth * scale

    /// Monospace advance: every glyph is 8 logical pixels wide at scale 1.
    let textWidth scale (text: string) = 8.0f * scale * float32 text.Length
    let addTextCentered centerX y scale color text = addText (centerX - textWidth scale text * 0.5f) y scale color text
    let addTextRight rightX y scale color text = addText (rightX - textWidth scale text) y scale color text

    do
        gl.BindVertexArray vao
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer)
        let stride = uint32 (8 * sizeof<float32>)
        gl.EnableVertexAttribArray 0u
        gl.VertexAttribPointer(0u, 2, VertexAttribPointerType.Float, false, stride, nativeint 0)
        gl.EnableVertexAttribArray 1u
        gl.VertexAttribPointer(1u, 2, VertexAttribPointerType.Float, false, stride, nativeint (2 * sizeof<float32>))
        gl.EnableVertexAttribArray 2u
        gl.VertexAttribPointer(2u, 4, VertexAttribPointerType.Float, false, stride, nativeint (4 * sizeof<float32>))
        gl.BindTexture(TextureTarget.Texture2D, texture)
        use pixels = fixed font.Pixels
        gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.R8, uint32 font.Width, uint32 font.Height, 0, PixelFormat.Red, PixelType.UnsignedByte, NativePtr.toVoidPtr pixels)
        GlUtil.clampLinearTexture gl TextureTarget.Texture2D

    member _.Render(width: int, height: int, world: World, info: HudInfo) =
        vertices.Clear()
        let white = Vector4(0.92f, 0.94f, 0.89f, 0.95f)
        let shadow = Vector4(0.0f, 0.0f, 0.0f, 0.7f)
        let centerX, centerY = float32 width * 0.5f, float32 height * 0.5f
        // ---- Tiny layout DSL. Every panel, overlay screen, and row list
        // routes through these, so accent strips, highlight insets, column
        // offsets, and text centering have exactly one source each. Row
        // geometry lives in Rect (UiLayout.fs), shared with hover hit-testing.
        let panel (r: Rect) (back: Vector4) (accentHeight: float32) (accent: Vector4) =
            solid r.X r.Y r.W r.H back
            solid r.X r.Y r.W accentHeight accent
        /// Centered overlay screen: optional fullscreen dim, panel chrome, and
        /// the prompt footer. Returns the panel rect the caller lays out in.
        let overlay dim (accentAlpha: float32) (r: Rect) (prompt: string) =
            if dim then solid 0.0f 0.0f (float32 width) (float32 height) (Vector4(0.005f, 0.009f, 0.008f, 0.63f))
            panel r (Vector4(0.025f, 0.040f, 0.034f, 0.94f)) 5.0f (Vector4(0.82f, 0.22f, 0.08f, accentAlpha))
            addTextCentered centerX (r.Y + r.H - 30.0f) 0.9f (Vector4(0.64f, 0.67f, 0.61f, 1.0f)) prompt
            r
        /// Progress meter: background track plus a left-anchored fill.
        let meter (r: Rect) (ratio: float32) (back: Vector4) (fill: Vector4) =
            solid r.X r.Y r.W r.H back
            solid r.X r.Y (r.W * MathEx.clamp01 ratio) r.H fill
        /// Horizontal pad from a rows rect's left edge to its text origin.
        let rowTextPad = 24.0f
        /// Column header labels at the same offsets the data rows use.
        let tableHeader (rows: Rect) (y: float32) scale color (columns: (float32 * string) array) =
            for offset, label in columns do
                addText (rows.X + rowTextPad + offset) y scale color label
        /// One data row: optional inset-centered selection bar, then cells as
        /// (xOffset, scale, text) — offsets shared with tableHeader, every
        /// scale centered on the slot midline.
        /// Faint fill marking the row under the pointer, drawn under the
        /// selection bar so a hovered-and-selected row reads as one, not two.
        let hoverRow (rows: Rect) (rowHeight: float32) slotIndex =
            let slot = Rect.inset 0.0f (rowHeight / 9.0f) (Rect.slot rowHeight slotIndex rows)
            solid slot.X slot.Y slot.W slot.H (Vector4(1.0f, 0.92f, 0.66f, 0.13f))
        let tableRow (rows: Rect) (rowHeight: float32) slotIndex selected (color: Vector4) (cells: (float32 * float32 * string) list) =
            let slot = Rect.slot rowHeight slotIndex rows
            if selected then
                let bar = Rect.inset 0.0f (slot.H / 9.0f) slot
                solid bar.X bar.Y bar.W bar.H (Vector4(0.47f, 0.17f, 0.07f, 0.88f))
                solid bar.X bar.Y 5.0f bar.H (Vector4(1.0f, 0.74f, 0.30f, 1.0f))
            for offset, scale, text in cells do
                addText (rows.X + rowTextPad + offset) (Rect.textY scale slot) scale color text
        let weapon = world.Player.Slots[world.Player.Active]
        let scoped = weapon.Class.Kind = SniperRifle && world.Player.Ads >= 0.72f
        let panelBack = Vector4(0.015f, 0.022f, 0.018f, 0.66f)
        let accent = Vector4(0.82f, 0.22f, 0.08f, 0.92f)
        let label = Vector4(0.70f, 0.74f, 0.67f, 0.95f)
        let weaponPanelWidth, weaponPanelHeight = 226.0f, 66.0f
        let weaponPanelX = float32 width - weaponPanelWidth - 20.0f
        let weaponPanelY = float32 height - weaponPanelHeight - 18.0f
        // ---- Draw blocks. Defined here, invoked in order at the bottom. ----
        let drawScopeMask () =
            let radius = min (float32 width * 0.46f) (float32 height * 0.47f)
            let left, right = centerX - radius, centerX + radius
            let black = Vector4(0.0f, 0.0f, 0.0f, 1.0f)
            solid 0.0f 0.0f (max 0.0f left) (float32 height) black
            solid right 0.0f (max 0.0f (float32 width - right)) (float32 height) black
            let bands = 160
            let bandWidth = radius * 2.0f / float32 bands
            for band in 0..bands - 1 do
                let x = left + float32 band * bandWidth
                let dx = x + bandWidth * 0.5f - centerX
                let halfHeight = MathF.Sqrt(max 0.0f (radius * radius - dx * dx))
                solid x 0.0f (bandWidth + 0.75f) (max 0.0f (centerY - halfHeight)) black
                let lower = centerY + halfHeight
                solid x lower (bandWidth + 0.75f) (max 0.0f (float32 height - lower)) black
            let reticle = Vector4(0.02f, 0.025f, 0.02f, 0.96f)
            let reticleEdge = Vector4(0.85f, 0.87f, 0.80f, 0.30f)
            solid (centerX - radius) (centerY - 1.5f) (radius * 2.0f) 3.0f reticleEdge
            solid (centerX - radius) (centerY - 0.5f) (radius * 2.0f) 1.0f reticle
            solid (centerX - 1.5f) (centerY - radius) 3.0f (radius * 2.0f) reticleEdge
            solid (centerX - 0.5f) (centerY - radius) 1.0f (radius * 2.0f) reticle
            solid (centerX - 2.0f) (centerY - 2.0f) 4.0f 4.0f reticle
        let drawCrosshair () =
            if world.Player.IsAlive then
                let spread = 7.0f + (weapon.Class.HipSpread + (weapon.Class.AdsSpread - weapon.Class.HipSpread) * world.Player.Ads) * 220.0f
                solid (centerX - spread - 8.0f) (centerY - 1.0f) 8.0f 2.0f white
                solid (centerX + spread) (centerY - 1.0f) 8.0f 2.0f white
                solid (centerX - 1.0f) (centerY - spread - 8.0f) 2.0f 8.0f white
                solid (centerX - 1.0f) (centerY + spread) 2.0f 8.0f white
        // Corner brackets that snap in tight and expand outward while
        // fading — the pop reads as impact instead of four static dashes.
        let drawHitMarker () =
            let strength = MathEx.clamp01 info.HitMarker
            let pop = 1.0f + (1.0f - strength) * 0.6f
            let alpha = 0.30f + 0.70f * strength
            let hit = if info.HitMarkerLethal then Vector4(0.95f, 0.12f, 0.08f, alpha) else Vector4(1.0f, 0.86f, 0.30f, alpha)
            let reach = (if info.HitMarkerLethal then 16.0f else 12.0f) * pop
            let thick = if info.HitMarkerLethal then 3.0f else 2.0f
            let arm = if info.HitMarkerLethal then 10.0f else 8.0f
            solid (centerX - reach) (centerY - reach) arm thick hit
            solid (centerX - reach) (centerY - reach) thick arm hit
            solid (centerX + reach - arm) (centerY - reach) arm thick hit
            solid (centerX + reach - thick) (centerY - reach) thick arm hit
            solid (centerX - reach) (centerY + reach - thick) arm thick hit
            solid (centerX - reach) (centerY + reach - arm) thick arm hit
            solid (centerX + reach - arm) (centerY + reach - thick) arm thick hit
            solid (centerX + reach - thick) (centerY + reach - arm) thick arm hit
        // ---- Bottom-right weapon panel: name, magazine, reserve, grenades ----
        let drawWeaponPanel () =
            panel { X = weaponPanelX; Y = weaponPanelY; W = weaponPanelWidth; H = weaponPanelHeight } panelBack 3.0f accent
            let weaponName = weapon.Class.Name.ToUpperInvariant()
            addText (weaponPanelX + 14.0f) (weaponPanelY + 10.0f) 1.0f label weaponName
            let magText = $"{weapon.InMag}"
            addText (weaponPanelX + 14.0f) (weaponPanelY + 28.0f) 2.2f white magText
            addText (weaponPanelX + 18.0f + textWidth 2.2f magText) (weaponPanelY + 40.0f) 1.1f label $"/ {weapon.Reserve}"
            let grenadeCount = match world.Player.Grenade with GrenadeIdle count -> count | Cooking(_, count) -> count
            addText (weaponPanelX + weaponPanelWidth - 74.0f) (weaponPanelY + 40.0f) 1.1f label $"GREN {grenadeCount}"
        // Half-Life-style category list, shown briefly after switch activity.
        let drawInventory () =
            let dim = Vector4(0.62f, 0.66f, 0.60f, 0.62f)
            let highlight = Vector4(1.0f, 0.86f, 0.30f, 0.98f)
            // Derived from the carried slots, so it lists a two-weapon online
            // kit and the twelve-slot offline sandbox alike, and skips keys
            // that hold nothing.
            [| 0 .. 4 |]
            |> Array.map (fun category -> category, Tuning.categorySlots world.Player.Slots category)
            |> Array.filter (fun (_, members) -> members.Length > 0)
            |> Array.iteri (fun line (category, members) ->
                let active = members |> Array.contains world.Player.Active
                let shown = if active then world.Player.Active else members[0]
                let extras = if members.Length > 1 then " " + String.replicate (members.Length - 1) "." else ""
                let row = $"{category + 1}  {world.Player.Slots[shown].Class.Name.ToUpperInvariant()}{extras}"
                let y = float32 height - 224.0f + float32 line * 20.0f
                let x = float32 width - textWidth 1.0f row - 28.0f
                solid (x - 6.0f) (y - 3.0f) (textWidth 1.0f row + 12.0f) 18.0f (Vector4(0.0f, 0.0f, 0.0f, if active then 0.55f else 0.34f))
                addText x y 1.0f (if active then highlight else dim) row)
        let drawReloadBar () =
            match weapon.State with
            | Reloading remaining ->
                let progress = MathEx.clamp01 (1.0f - Units.raw remaining / max 0.01f (Units.raw weapon.Class.ReloadTime))
                let barWidth = weaponPanelWidth
                let barLeft = weaponPanelX
                let barTop = weaponPanelY - 16.0f
                let reloadText = sprintf "RELOADING %d%%" (int (progress * 100.0f))
                solid (barLeft - 2.0f) (barTop - 2.0f) (barWidth + 4.0f) 12.0f (Vector4(0.0f, 0.0f, 0.0f, 0.72f))
                meter { X = barLeft; Y = barTop; W = barWidth; H = 8.0f } progress
                    (Vector4(0.16f, 0.18f, 0.16f, 0.92f)) (Vector4(0.92f, 0.55f, 0.14f, 1.0f))
                addTextRight (barLeft - 14.0f) (barTop - 3.0f) 1.0f white reloadText
            | _ -> ()
        // ---- Bottom-left health panel with a color-shifting bar ----
        let drawHealthPanel () =
            let healthPanelWidth, healthPanelHeight = 196.0f, 48.0f
            let healthPanelX, healthPanelY = 20.0f, float32 height - 48.0f - 18.0f
            panel { X = healthPanelX; Y = healthPanelY; W = healthPanelWidth; H = healthPanelHeight } panelBack 3.0f accent
            let healthRatio = MathEx.clamp01 (Units.raw world.Player.Health / 100.0f)
            let healthFill = Vector4(0.85f - 0.42f * healthRatio, 0.16f + 0.52f * healthRatio, 0.12f + 0.20f * healthRatio, 0.95f)
            addText (healthPanelX + 14.0f) (healthPanelY + 10.0f) 1.0f label "HEALTH"
            addTextRight (healthPanelX + healthPanelWidth - 14.0f) (healthPanelY + 8.0f) 1.4f white $"{int (Units.raw world.Player.Health)}"
            meter { X = healthPanelX + 14.0f; Y = healthPanelY + 30.0f; W = healthPanelWidth - 28.0f; H = 9.0f } healthRatio
                (Vector4(0.10f, 0.12f, 0.10f, 0.9f)) healthFill
        let drawDebugLabel () =
            addText 22.0f 8.0f 1.0f (Vector4(1.0f, 0.74f, 0.30f, 0.95f)) "DEBUG VIEW [F3]  GREEN: CLEAR LOS  RED: BLOCKED"
        let drawCompass () =
            let heading = ((world.Player.Yaw * 180.0f / MathF.PI) % 360.0f + 360.0f) % 360.0f
            let directions = [| "N"; "NE"; "E"; "SE"; "S"; "SW"; "W"; "NW" |]
            let direction = directions[(int (MathF.Round(heading / 45.0f))) % directions.Length]
            let compass = sprintf "%s  %03d" direction (int heading)
            let compassY = if info.Online.IsSome || world.Round.IsSome then 48.0f else 8.0f
            addTextCentered centerX compassY 0.9f white compass
        let drawOfflineScore () =
            match world.Round with
            | Some round ->
                let score = $"YOU {round.PlayerScore}   BOTS {round.EnemyScore}   ROUND {round.Number}"
                solid (centerX - 162.0f) 10.0f 324.0f 29.0f (Vector4(0.0f, 0.0f, 0.0f, 0.42f))
                addTextCentered centerX 18.0f 1.25f white score
                round.LastResult
                |> Option.iter (fun result -> addTextCentered centerX 70.0f 2.0f white result)
            | None ->
                world.Objectives
                |> Array.tryFind (fun objective -> not objective.Done)
                |> Option.iter (fun objective -> addText 24.0f 28.0f 1.25f white objective.Text)
        let drawOnlineScore () =
            match info.Online with
            | Some online ->
                let score =
                    match online.Mode with
                    | FreeForAll ->
                        let local = info.LocalPlayerId |> Option.bind (fun id -> online.Players |> Array.tryFind (fun player -> player.Id = id))
                        let leader = online.Players |> Array.sortByDescending (fun player -> player.Kills) |> Array.tryHead
                        let own = local |> Option.map (fun player -> $"YOU {player.Kills}/{player.Deaths}") |> Option.defaultValue "YOU 0/0"
                        let leading = leader |> Option.map (fun player -> $"LEADER {player.Name} {player.Kills}") |> Option.defaultValue "LEADER --"
                        $"{own}   {online.Phase}   {leading}"
                    | _ -> $"ALLIES {online.AlliesScore}   {online.Phase}   AXIS {online.AxisScore}"
                addTextCentered centerX 22.0f 1.25f white score
            | None when world.Script.Ended -> addText (centerX - 110.0f) 70.0f 1.6f white "MISSION COMPLETE"
            | None -> ()
        let drawScoreboard () =
            match info.Online with
            | Some online when info.ShowScoreboard || online.Phase = Results ->
                let panelWidth, rowHeight = 620.0f, 25.0f
                let sorted =
                    online.Players
                    |> Array.sortBy (fun player ->
                        match online.Mode with
                        | TeamDeathmatch -> (if player.Team = Allies then 0 else 1), -player.Kills, player.Deaths
                        | _ -> 0, -player.Kills, player.Deaths)
                let summary = online.Phase = Results
                let panelRect =
                    { X = centerX - panelWidth * 0.5f
                      Y = 105.0f
                      W = panelWidth
                      H = 72.0f + rowHeight * float32 sorted.Length + (if summary then 76.0f else 0.0f) }
                panel panelRect (Vector4(0.015f, 0.02f, 0.018f, 0.88f)) 4.0f (Vector4(0.62f, 0.14f, 0.08f, 0.95f))
                let rows = { X = panelRect.X + 18.0f; Y = panelRect.Y + 64.0f; W = panelRect.W - 36.0f; H = rowHeight * float32 sorted.Length }
                let columns = [| 0.0f, "PLAYER"; 340.0f, "TEAM"; 460.0f, "K   D" |]
                addText (rows.X + rowTextPad) (panelRect.Y + 17.0f) 1.35f white (if online.Mode = FreeForAll then "FREE FOR ALL" else "TEAM DEATHMATCH")
                tableHeader rows (panelRect.Y + 46.0f) 0.95f white columns
                sorted
                |> Array.iteri (fun index player ->
                    let isLocal = info.LocalPlayerId = Some player.Id
                    if isLocal then
                        // "You" tint, not the amber selection bar: the scoreboard
                        // has no cursor, it marks the local player's own row.
                        let bar = Rect.inset 0.0f 3.0f (Rect.slot rowHeight index rows)
                        solid bar.X bar.Y bar.W bar.H (Vector4(0.30f, 0.34f, 0.18f, 0.48f))
                    let rowColor = if isLocal then Vector4(1.0f, 0.92f, 0.58f, 1.0f) else white
                    let team = if online.Mode = FreeForAll then "--" else string player.Team
                    tableRow rows rowHeight index false rowColor
                        [ fst columns[0], 1.0f, player.Name
                          fst columns[1], 1.0f, team
                          fst columns[2], 1.0f, $"{player.Kills}   {player.Deaths}" ])
                if summary then
                    let winner =
                        match online.Mode with
                        | TeamDeathmatch when online.AlliesScore > online.AxisScore -> "ALLIES WIN"
                        | TeamDeathmatch when online.AxisScore > online.AlliesScore -> "AXIS WIN"
                        | TeamDeathmatch -> "DRAW"
                        // Sorted by kills already, so the head is the winner.
                        | _ -> sorted |> Array.tryHead |> Option.map (fun p -> $"{p.Name} WINS") |> Option.defaultValue "DRAW"
                    let topFragger =
                        online.Players
                        |> Array.sortByDescending (fun player -> player.Kills)
                        |> Array.tryHead
                        |> Option.map (fun player -> $"TOP FRAGGER  {player.Name}  {player.Kills}")
                        |> Option.defaultValue ""
                    let bestStreak =
                        online.Players
                        |> Array.sortByDescending (fun player -> player.BestStreak)
                        |> Array.tryHead
                        |> Option.map (fun player -> $"BEST STREAK  {player.Name}  {player.BestStreak}")
                        |> Option.defaultValue ""
                    addTextCentered centerX (rows.Y + rows.H + 10.0f) 1.35f (Vector4(1.0f, 0.92f, 0.58f, 1.0f)) winner
                    addTextCentered centerX (rows.Y + rows.H + 34.0f) 0.95f label topFragger
                    addTextCentered centerX (rows.Y + rows.H + 58.0f) 0.95f label bestStreak
            | _ -> ()
        let drawKillFeed () =
            info.Feed
            |> List.iteri (fun index (text, headshot) ->
                addTextRight (float32 width - 20.0f) (100.0f + 18.0f * float32 index) 1.0f (if headshot then accent else white) text)
        // Bottom-left, stacking upward. The subtitle band owns the full width
        // at height-118 and the health panel sits at height-66, so everything
        // here stays above height-124.
        let drawChat () =
            let draftY = float32 height - 142.0f
            // Each row carries its own backing plate, the way Counter-Strike's
            // does: bare text over a sunlit wall or a muzzle flash is
            // unreadable exactly when someone is calling a position.
            info.Chat
            |> List.iteri (fun index (text, system) ->
                let y = draftY - 20.0f * float32 (index + 1)
                solid 14.0f (y - 4.0f) (max 240.0f (textWidth 1.0f text + 14.0f)) 20.0f (Vector4(0.0f, 0.0f, 0.0f, 0.45f))
                addText 20.0f y 1.0f (if system then accent else white) text)
            info.ChatDraft
            |> Option.iter (fun draft ->
                let line = $"SAY: {draft}_"
                solid 14.0f (draftY - 4.0f) (max 240.0f (textWidth 1.0f line + 14.0f)) 22.0f (Vector4(0.0f, 0.0f, 0.0f, 0.62f))
                addText 20.0f draftY 1.0f white line)
        let drawSubtitle (subtitle: string) =
            solid 0.0f (float32 height - 118.0f) (float32 width) 46.0f (Vector4(0.0f,0.0f,0.0f,0.55f))
            addTextCentered centerX (float32 height - 106.0f) 1.25f white subtitle
        let hurt = MathEx.clamp01 (1.0f - Units.raw world.Player.Health / 100.0f)
        let drawDamageVignette () =
            let blood = Settings.bloodRgb info.Settings.BloodColor
            let red = Vector4(blood.X, blood.Y, blood.Z, hurt * 0.55f)
            let clearRed = Vector4(blood.X, blood.Y, blood.Z, 0.0f)
            let thickness = 50.0f + hurt * 90.0f
            gradientQuad 0.0f 0.0f (float32 width) thickness red red clearRed clearRed
            gradientQuad 0.0f (float32 height - thickness) (float32 width) thickness clearRed clearRed red red
            gradientQuad 0.0f thickness thickness (float32 height - thickness * 2.0f) red clearRed clearRed red
            gradientQuad (float32 width - thickness) thickness thickness (float32 height - thickness * 2.0f) clearRed red red clearRed
        let drawDamageDirection (direction: Vector3) =
            let forward = MathEx.yawForward world.Player.Yaw
            let right = MathEx.yawRight world.Player.Yaw
            let horizontal = MathEx.horizontal direction |> MathEx.normalizedOrZero
            let x = centerX + Vector3.Dot(horizontal, right) * 125.0f
            let y = centerY - Vector3.Dot(horizontal, forward) * 82.0f
            solid (x - 18.0f) (y - 3.0f) 36.0f 6.0f (Vector4(0.85f, 0.03f, 0.02f, 0.88f))
        let drawDeathOverlay () =
            solid (centerX - 250.0f) (centerY - 45.0f) 500.0f 90.0f (Vector4(0.0f, 0.0f, 0.0f, 0.72f))
            addText (centerX - 92.0f) (centerY - 24.0f) 1.6f white "YOU WERE KILLED"
            let restart = if world.Round.IsSome then "NEXT ROUND..." else "PRESS R TO RESTART"
            addTextCentered centerX (centerY + 10.0f) 1.0f white restart
        let drawLoadoutScreen (picker: LoadoutMenu.State) =
            let rowHeight = LoadoutMenu.RowHeight
            let panelRect =
                overlay false accent.W (LoadoutMenu.panelRect width height)
                    "UP/DOWN, MOUSE   1-5 JUMP   ENTER/CLICK EQUIP   ESC CLOSE"
            addText (panelRect.X + 26.0f) (panelRect.Y + 20.0f) 2.0f white "LOADOUT"
            let hint = if info.Online.IsSome then "ARMS ON YOUR NEXT SPAWN" else "SWAPS IMMEDIATELY"
            addTextRight (panelRect.X + panelRect.W - 26.0f) (panelRect.Y + 30.0f) 1.0f label hint
            let rows = LoadoutMenu.rowsRect panelRect
            // The list keeps the left two thirds; the stat block sits in the
            // remainder, so a name and its numbers are read without a second
            // screen.
            let listWidth = rows.W * 0.58f
            let statsX = rows.X + listWidth + 24.0f
            let columns = [| 0.0f, "WEAPON"; 250.0f, "DMG"; 320.0f, "RPM" |]
            tableHeader rows (panelRect.Y + 62.0f) 0.95f label columns
            let firstVisible, visible = LoadoutMenu.visibleRows picker
            visible
            |> List.iteri (fun slot (index, row) ->
                match row with
                | LoadoutMenu.Header category ->
                    // A heading names the number key that reaches this group.
                    let y = Rect.textY 0.95f (Rect.slot rowHeight slot rows)
                    addText (rows.X + rowTextPad) y 0.95f accent $"{category + 1}  {Tuning.categoryName category}"
                | LoadoutMenu.Weapon weaponIndex ->
                    let weaponClass = Tuning.onlineWeapons[weaponIndex]
                    if picker.Hovered = Some index then hoverRow rows rowHeight slot
                    let isSelected = index = picker.Selected
                    let isCurrent = weaponClass.Name = weapon.Class.Name
                    let color =
                        if isSelected then Vector4(1.0f, 0.91f, 0.64f, 1.0f)
                        elif isCurrent then Vector4(1.0f, 0.86f, 0.30f, 0.95f)
                        else white
                    let pellets = if weaponClass.Pellets > 1 then $"x{weaponClass.Pellets}" else ""
                    let cells =
                        [ fst columns[0] + 14.0f, 1.15f, weaponClass.Name.ToUpperInvariant()
                          fst columns[1], 1.0f, $"{int (Units.raw weaponClass.Damage)}{pellets}"
                          fst columns[2], 1.0f, $"{int weaponClass.RoundsPerMin}" ]
                    let cells = if isCurrent then cells @ [ fst columns[2] + 60.0f, 1.0f, "<" ] else cells
                    tableRow rows rowHeight slot isSelected color cells)
            // More rows than fit: show which part of the list is on screen.
            if LoadoutMenu.rows.Length > LoadoutMenu.MaxVisibleRows then
                let span = float32 LoadoutMenu.MaxVisibleRows / float32 LoadoutMenu.rows.Length
                let track = { X = rows.X + listWidth + 4.0f; Y = rows.Y; W = 3.0f; H = rows.H }
                solid track.X track.Y track.W track.H (Vector4(1.0f, 1.0f, 1.0f, 0.10f))
                let thumb = track.H * span
                let offset = track.H * (float32 firstVisible / float32 LoadoutMenu.rows.Length)
                solid track.X (track.Y + offset) track.W thumb (Vector4(1.0f, 0.86f, 0.30f, 0.55f))
            // Full stats for the highlighted weapon, from the same derivation
            // the website's arsenal renders.
            LoadoutMenu.weaponAt picker.Selected
            |> Option.iter (fun weaponClass ->
                let stats = WeaponStats.of' weaponClass
                let falloff =
                    if stats.FalloffEndMetres <= 0.0f then "NONE"
                    else $"{int stats.FalloffStartMetres}-{int stats.FalloffEndMetres}M  MIN {int stats.MinimumDamagePerProjectile}"
                let ttk = if stats.TimeToKillSeconds <= 0.0f then "ONE SHOT" else $"{stats.ShotsToKill} SHOTS  %.2f{stats.TimeToKillSeconds}S"
                let lines =
                    [ $"{Tuning.categoryName (Tuning.categoryOf weaponClass)}  {weaponClass.Mode}".ToUpperInvariant(), accent
                      $"KILL      {ttk}", white
                      $"DAMAGE    {int stats.DamagePerProjectile}" + (if weaponClass.Pellets > 1 then $" x{weaponClass.Pellets} = {int stats.MaximumDamagePerShot}" else ""), white
                      $"HEADSHOT  x%.1f{weaponClass.HeadshotMultiplier}", white
                      $"RPM       {int weaponClass.RoundsPerMin}", white
                      $"MAGAZINE  {weaponClass.MagSize}", white
                      $"RELOAD    %.2f{Units.raw weaponClass.ReloadTime}S", white
                      $"ADS       %.2f{Units.raw weaponClass.AdsTime}S", white
                      $"SPREAD    %.3f{weaponClass.HipSpread} HIP  %.3f{weaponClass.AdsSpread} ADS", label
                      $"PIERCE    %.0f{weaponClass.Penetration}", label
                      $"FALLOFF   {falloff}", label ]
                lines
                |> List.iteri (fun index (text, color) ->
                    addText statsX (rows.Y + 6.0f + float32 index * 22.0f) 0.95f color text))
        let drawStartMenu (menu: StartMenuState) =
            let options = StartMenu.items menu
            let firstVisible, visibleCount = StartMenu.visibleRange menu
            let panelRect =
                overlay true 1.0f (MenuLayout.panelRect width height visibleCount)
                    "UP/DOWN OR MOUSE   ENTER/CLICK TO SELECT   ESC TO BACK"
            // The exact rect StartMenu hit-tests hover against: pixels and
            // hitboxes cannot disagree.
            let rows = MenuLayout.rowsRect panelRect visibleCount
            addTextCentered centerX (panelRect.Y + 24.0f) 3.0f white "IRONSIGHT"
            let subtitleColor = Vector4(0.68f, 0.72f, 0.67f, 1.0f)
            let cells = StartMenu.serverCells menu
            match cells with
            | Some _ ->
                // The server table gets column headers aligned with the rows;
                // the room count sits where the subtitle would be.
                tableHeader rows (panelRect.Y + 68.0f) 1.0f subtitleColor StartMenu.serverColumns
                addTextRight (panelRect.X + panelRect.W - 42.0f) (panelRect.Y + 68.0f) 1.0f subtitleColor (StartMenu.subtitle menu)
            | None -> addTextCentered centerX (panelRect.Y + 68.0f) 1.0f subtitleColor (StartMenu.subtitle menu)
            if options.Length > visibleCount then
                addTextRight (panelRect.X + panelRect.W - 42.0f) (panelRect.Y + 46.0f) 0.9f subtitleColor
                    $"{firstVisible + 1}-{firstVisible + visibleCount} OF {options.Length}"
            for slot in 0 .. visibleCount - 1 do
                let index = firstVisible + slot
                let selected = index = menu.Selected
                let color = if selected then Vector4(1.0f, 0.91f, 0.64f, 1.0f) else white
                let rowCells =
                    match cells with
                    | Some serverRows when index < serverRows.Length ->
                        [ for offset, text in serverRows[index] -> offset, 1.3f, text ]
                    | _ -> [ 0.0f, 1.3f, options[index] ]
                tableRow rows MenuLayout.RowHeight slot selected color rowCells
        let drawSettingsScreen (screen: SettingsUi.State) =
            let rowHeight = SettingsUi.RowHeight
            let visible = SettingsUi.visibleRows screen
            let panelRect =
                overlay true 1.0f (SettingsUi.panelRect width height visible.Length)
                    "UP/DOWN, MOUSE   LEFT/RIGHT ADJUST   ENTER/CLICK CONFIRM   ESC BACK"
            addTextCentered centerX (panelRect.Y + 26.0f) 2.4f white "SETTINGS"
            let rows = SettingsUi.rowsRect panelRect visible.Length
            visible
            |> List.iteri (fun index row ->
                let labelColor =
                    if row.Header then Vector4(0.62f, 0.67f, 0.60f, 0.85f)
                    elif row.Selected then Vector4(1.0f, 0.91f, 0.64f, 1.0f)
                    else white
                tableRow rows rowHeight index row.Selected labelColor [ 0.0f, 1.25f, row.Label ]
                let value =
                    if row.Adjustable then $"< {row.Value} >"
                    elif row.Value = "" then ""
                    else row.Value
                if value <> "" then
                    let slot = Rect.slot rowHeight index rows
                    addTextRight (panelRect.X + panelRect.W - 70.0f) (Rect.textY 1.25f slot) 1.25f labelColor value)
        // ---- Draw order: later blocks paint over earlier ones. ----
        if scoped then drawScopeMask () else drawCrosshair ()
        if info.HitMarker > 0.0f then drawHitMarker ()
        drawWeaponPanel ()
        if info.ShowInventory && world.Player.Slots.Length > 1 then drawInventory ()
        drawReloadBar ()
        drawHealthPanel ()
        if info.DebugView then drawDebugLabel ()
        drawCompass ()
        if info.Online.IsNone then drawOfflineScore ()
        drawOnlineScore ()
        drawKillFeed ()
        drawChat ()
        drawScoreboard ()
        info.Subtitle |> Option.iter drawSubtitle
        if hurt > 0.01f then drawDamageVignette ()
        info.DamageDirection |> Option.iter drawDamageDirection
        if world.Player.IsDead && info.Online.IsNone then drawDeathOverlay ()
        info.LoadoutScreen |> Option.iter drawLoadoutScreen
        info.Menu |> Option.iter drawStartMenu
        info.SettingsScreen |> Option.iter drawSettingsScreen
        let data = vertices.ToArray()
        if data.Length > 0 then
            gl.Disable EnableCap.DepthTest
            gl.Disable EnableCap.CullFace
            gl.Enable EnableCap.Blend
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
            gl.UseProgram program
            gl.Uniform2(gl.GetUniformLocation(program, "uResolution"), float32 width, float32 height)
            gl.ActiveTexture TextureUnit.Texture0
            gl.BindTexture(TextureTarget.Texture2D, texture)
            gl.Uniform1(gl.GetUniformLocation(program, "uFont"), 0)
            gl.Uniform2(gl.GetUniformLocation(program, "uFontTexel"), 1.0f / float32 font.Width, 1.0f / float32 font.Height)
            gl.BindVertexArray vao
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer)
            GlUtil.upload gl BufferTargetARB.ArrayBuffer data BufferUsageARB.DynamicDraw
            gl.DrawArrays(PrimitiveType.Triangles, 0, uint32 (data.Length / 8))
            gl.Disable EnableCap.Blend
            gl.Enable EnableCap.CullFace
            gl.Enable EnableCap.DepthTest

    interface IDisposable with
        member _.Dispose() =
            gl.DeleteTexture texture
            gl.DeleteBuffer buffer
            gl.DeleteVertexArray vao
            gl.DeleteProgram program
