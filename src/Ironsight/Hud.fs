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
      HitMarker: bool
      HitMarkerLethal: bool
      Subtitle: string option
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

    let compile shaderType source =
        let shader = gl.CreateShader(shaderType: ShaderType)
        gl.ShaderSource(shader, source)
        gl.CompileShader shader
        let mutable status = 0
        gl.GetShader(shader, ShaderParameterName.CompileStatus, &status)
        if status <> int GLEnum.True then invalidOp (gl.GetShaderInfoLog shader)
        shader

    let createProgram () =
        let vertex = compile ShaderType.VertexShader Shaders.hudVertex
        let fragment = compile ShaderType.FragmentShader Shaders.hudFragment
        let value = gl.CreateProgram()
        gl.AttachShader(value, vertex)
        gl.AttachShader(value, fragment)
        gl.LinkProgram value
        let mutable status = 0
        gl.GetProgram(value, ProgramPropertyARB.LinkStatus, &status)
        gl.DeleteShader vertex
        gl.DeleteShader fragment
        if status <> int GLEnum.True then invalidOp (gl.GetProgramInfoLog value)
        value

    let program = createProgram ()

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
        let u = 0.5f / float32 font.Width
        let v = 0.5f / float32 font.Height
        addQuad x y width height u v u v color

    let gradientQuad x y width height (topLeft: Vector4) (topRight: Vector4) (bottomRight: Vector4) (bottomLeft: Vector4) =
        let u = 0.5f / float32 font.Width
        let v = 0.5f / float32 font.Height
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
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, int TextureMinFilter.Linear)
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, int TextureMagFilter.Linear)
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, int TextureWrapMode.ClampToEdge)
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, int TextureWrapMode.ClampToEdge)

    member _.Render(width: int, height: int, world: World, info: HudInfo) =
        vertices.Clear()
        let white = Vector4(0.92f, 0.94f, 0.89f, 0.95f)
        let shadow = Vector4(0.0f, 0.0f, 0.0f, 0.7f)
        let centerX, centerY = float32 width * 0.5f, float32 height * 0.5f
        let weapon = world.Player.Slots[world.Player.Active]
        let scoped = weapon.Class.Name = "Kar98k Sniper" && world.Player.Ads >= 0.72f
        if scoped then
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
        else
            if world.Player.Health > Units.health 0.0f then
                let spread = 7.0f + (weapon.Class.HipSpread + (weapon.Class.AdsSpread - weapon.Class.HipSpread) * world.Player.Ads) * 220.0f
                solid (centerX - spread - 8.0f) (centerY - 1.0f) 8.0f 2.0f white
                solid (centerX + spread) (centerY - 1.0f) 8.0f 2.0f white
                solid (centerX - 1.0f) (centerY - spread - 8.0f) 2.0f 8.0f white
                solid (centerX - 1.0f) (centerY + spread) 2.0f 8.0f white
        if info.HitMarker then
            let hit = if info.HitMarkerLethal then Vector4(0.95f, 0.12f, 0.08f, 1.0f) else Vector4(1.0f, 0.86f, 0.30f, 0.95f)
            let reach = if info.HitMarkerLethal then 14.0f else 11.0f
            solid (centerX - reach) (centerY - reach + 3.0f) 8.0f 2.0f hit
            solid (centerX + reach - 8.0f) (centerY - reach + 3.0f) 8.0f 2.0f hit
            solid (centerX - reach) (centerY + reach - 5.0f) 8.0f 2.0f hit
            solid (centerX + reach - 8.0f) (centerY + reach - 5.0f) 8.0f 2.0f hit
        let ammoText = $"{weapon.InMag} / {weapon.Reserve}"
        addText (float32 width - float32 ammoText.Length * 16.0f - 26.0f) (float32 height - 56.0f) 2.0f shadow ammoText
        addText (float32 width - float32 ammoText.Length * 16.0f - 28.0f) (float32 height - 58.0f) 2.0f white ammoText
        match weapon.State with
        | Reloading remaining ->
            let progress = MathEx.clamp01 (1.0f - Units.raw remaining / max 0.01f (Units.raw weapon.Class.ReloadTime))
            let barWidth = 174.0f
            let barLeft = float32 width - barWidth - 28.0f
            let barTop = float32 height - 82.0f
            let percentage = int (progress * 100.0f)
            let reloadText = sprintf "RELOADING %d%%" percentage
            solid (barLeft - 2.0f) (barTop - 2.0f) (barWidth + 4.0f) 12.0f (Vector4(0.0f, 0.0f, 0.0f, 0.72f))
            solid barLeft barTop barWidth 8.0f (Vector4(0.16f, 0.18f, 0.16f, 0.92f))
            solid barLeft barTop (barWidth * progress) 8.0f (Vector4(0.92f, 0.55f, 0.14f, 1.0f))
            addText (barLeft - float32 reloadText.Length * 8.0f - 12.0f) (barTop - 4.0f) 1.0f shadow reloadText
            addText (barLeft - float32 reloadText.Length * 8.0f - 14.0f) (barTop - 6.0f) 1.0f white reloadText
        | _ -> ()
        let healthText = $"HP {int (Units.raw world.Player.Health)}"
        addText 24.0f (float32 height - 48.0f) 1.5f shadow healthText
        addText 22.0f (float32 height - 50.0f) 1.5f white healthText
        let heading = ((world.Player.Yaw * 180.0f / MathF.PI) % 360.0f + 360.0f) % 360.0f
        let directions = [| "N"; "NE"; "E"; "SE"; "S"; "SW"; "W"; "NW" |]
        let direction = directions[(int (MathF.Round(heading / 45.0f))) % directions.Length]
        let compass = sprintf "%s  %03d" direction (int heading)
        let compassY = if info.Online.IsSome || world.Round.IsSome then 48.0f else 8.0f
        addText (centerX - float32 compass.Length * 4.0f) compassY 0.9f white compass
        if info.Online.IsNone then
            match world.Round with
            | Some round ->
                let score = $"YOU {round.PlayerScore}   BOTS {round.EnemyScore}   ROUND {round.Number}"
                solid (centerX - 162.0f) 10.0f 324.0f 29.0f (Vector4(0.0f, 0.0f, 0.0f, 0.42f))
                addText (centerX - float32 score.Length * 5.0f) 18.0f 1.25f white score
                round.LastResult
                |> Option.iter (fun result -> addText (centerX - float32 result.Length * 8.0f) 70.0f 2.0f white result)
            | None ->
                world.Objectives
                |> Array.tryFind (fun objective -> not objective.Done)
                |> Option.iter (fun objective -> addText 24.0f 28.0f 1.25f white objective.Text)
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
            addText (centerX - float32 score.Length * 5.0f) 22.0f 1.25f white score
        | None when world.Script.Ended -> addText (centerX - 110.0f) 70.0f 1.6f white "MISSION COMPLETE"
        | None -> ()
        match info.Online with
        | Some online when info.ShowScoreboard || online.Phase = Results ->
            let panelWidth, rowHeight = 620.0f, 25.0f
            let sorted =
                online.Players
                |> Array.sortBy (fun player ->
                    match online.Mode with
                    | TeamDeathmatch -> (if player.Team = Allies then 0 else 1), -player.Kills, player.Deaths
                    | _ -> 0, -player.Kills, player.Deaths)
            let panelHeight = 72.0f + rowHeight * float32 sorted.Length
            let left = centerX - panelWidth * 0.5f
            let top = 105.0f
            solid left top panelWidth panelHeight (Vector4(0.015f, 0.02f, 0.018f, 0.88f))
            solid left top panelWidth 4.0f (Vector4(0.62f, 0.14f, 0.08f, 0.95f))
            addText (left + 22.0f) (top + 17.0f) 1.35f white (if online.Mode = FreeForAll then "FREE FOR ALL" else "TEAM DEATHMATCH")
            addText (left + 22.0f) (top + 46.0f) 0.95f white "PLAYER"
            addText (left + 382.0f) (top + 46.0f) 0.95f white "TEAM"
            addText (left + 500.0f) (top + 46.0f) 0.95f white "K   D"
            sorted
            |> Array.iteri (fun index player ->
                let y = top + 70.0f + float32 index * rowHeight
                let isLocal = info.LocalPlayerId = Some player.Id
                if isLocal then solid (left + 10.0f) (y - 3.0f) (panelWidth - 20.0f) 22.0f (Vector4(0.30f, 0.34f, 0.18f, 0.48f))
                let rowColor = if isLocal then Vector4(1.0f, 0.92f, 0.58f, 1.0f) else white
                let team = if online.Mode = FreeForAll then "--" else string player.Team
                addText (left + 22.0f) y 1.0f rowColor player.Name
                addText (left + 382.0f) y 1.0f rowColor team
                addText (left + 500.0f) y 1.0f rowColor $"{player.Kills}   {player.Deaths}")
        | _ -> ()
        info.Subtitle
        |> Option.iter (fun subtitle ->
            solid 0.0f (float32 height - 118.0f) (float32 width) 46.0f (Vector4(0.0f,0.0f,0.0f,0.55f))
            addText (centerX - float32 subtitle.Length * 5.0f) (float32 height - 106.0f) 1.25f white subtitle)
        let hurt = MathEx.clamp01 (1.0f - Units.raw world.Player.Health / 100.0f)
        if hurt > 0.01f then
            let blood = Settings.bloodRgb info.Settings.BloodColor
            let red = Vector4(blood.X, blood.Y, blood.Z, hurt * 0.55f)
            let clearRed = Vector4(blood.X, blood.Y, blood.Z, 0.0f)
            let thickness = 50.0f + hurt * 90.0f
            gradientQuad 0.0f 0.0f (float32 width) thickness red red clearRed clearRed
            gradientQuad 0.0f (float32 height - thickness) (float32 width) thickness clearRed clearRed red red
            gradientQuad 0.0f thickness thickness (float32 height - thickness * 2.0f) red clearRed clearRed red
            gradientQuad (float32 width - thickness) thickness thickness (float32 height - thickness * 2.0f) clearRed red red clearRed
        info.DamageDirection
        |> Option.iter (fun direction ->
            let forward = MathEx.yawForward world.Player.Yaw
            let right = MathEx.yawRight world.Player.Yaw
            let horizontal = MathEx.horizontal direction |> MathEx.normalizedOrZero
            let x = centerX + Vector3.Dot(horizontal, right) * 125.0f
            let y = centerY - Vector3.Dot(horizontal, forward) * 82.0f
            solid (x - 18.0f) (y - 3.0f) 36.0f 6.0f (Vector4(0.85f, 0.03f, 0.02f, 0.88f)))
        if world.Player.Health <= Units.health 0.0f && info.Online.IsNone then
            solid (centerX - 250.0f) (centerY - 45.0f) 500.0f 90.0f (Vector4(0.0f, 0.0f, 0.0f, 0.72f))
            addText (centerX - 92.0f) (centerY - 24.0f) 1.6f white "YOU WERE KILLED"
            let restart = if world.Round.IsSome then "NEXT ROUND..." else "PRESS R TO RESTART"
            addText (centerX - float32 restart.Length * 5.0f) (centerY + 10.0f) 1.0f white restart
        info.Menu
        |> Option.iter (fun menu ->
            let options = StartMenu.items menu
            let rowHeight = 54.0f
            let panelWidth = min 840.0f (float32 width - 48.0f)
            let panelHeight = 156.0f + rowHeight * float32 options.Length
            let panelLeft = centerX - panelWidth * 0.5f
            let panelTop = centerY - panelHeight * 0.5f
            solid 0.0f 0.0f (float32 width) (float32 height) (Vector4(0.005f, 0.009f, 0.008f, 0.63f))
            solid panelLeft panelTop panelWidth panelHeight (Vector4(0.025f, 0.040f, 0.034f, 0.94f))
            solid panelLeft panelTop panelWidth 5.0f (Vector4(0.82f, 0.22f, 0.08f, 1.0f))
            let title = "IRONSIGHT"
            addText (centerX - float32 title.Length * 12.0f) (panelTop + 24.0f) 3.0f white title
            let subtitle = StartMenu.subtitle menu
            addText (centerX - float32 subtitle.Length * 5.0f) (panelTop + 68.0f) 1.0f (Vector4(0.68f, 0.72f, 0.67f, 1.0f)) subtitle
            let firstRow = panelTop + 106.0f
            options
            |> Array.iteri (fun index label ->
                let y = firstRow + float32 index * rowHeight
                let selected = index = menu.Selected
                if selected then
                    solid (panelLeft + 18.0f) (y - 7.0f) (panelWidth - 36.0f) 39.0f (Vector4(0.47f, 0.17f, 0.07f, 0.88f))
                    solid (panelLeft + 18.0f) (y - 7.0f) 5.0f 39.0f (Vector4(1.0f, 0.74f, 0.30f, 1.0f))
                let color = if selected then Vector4(1.0f, 0.91f, 0.64f, 1.0f) else white
                addText (panelLeft + 42.0f) y 1.3f color label)
            let prompt = "UP/DOWN OR MOUSE   ENTER/CLICK TO SELECT   ESC TO BACK"
            addText (centerX - float32 prompt.Length * 3.6f) (panelTop + panelHeight - 30.0f) 0.9f (Vector4(0.64f, 0.67f, 0.61f, 1.0f)) prompt)
        info.SettingsScreen
        |> Option.iter (fun screen ->
            let rowHeight = 40.0f
            let rows = SettingsUi.visibleRows screen
            let panelWidth = min 860.0f (float32 width - 48.0f)
            let panelHeight = 150.0f + rowHeight * float32 rows.Length
            let panelLeft = centerX - panelWidth * 0.5f
            let panelTop = centerY - panelHeight * 0.5f
            solid 0.0f 0.0f (float32 width) (float32 height) (Vector4(0.005f, 0.009f, 0.008f, 0.63f))
            solid panelLeft panelTop panelWidth panelHeight (Vector4(0.025f, 0.040f, 0.034f, 0.94f))
            solid panelLeft panelTop panelWidth 5.0f (Vector4(0.82f, 0.22f, 0.08f, 1.0f))
            let title = "SETTINGS"
            addText (centerX - float32 title.Length * 12.0f) (panelTop + 26.0f) 2.4f white title
            rows
            |> List.iteri (fun index row ->
                let y = panelTop + 92.0f + float32 index * rowHeight
                if row.Selected then
                    solid (panelLeft + 18.0f) (y - 7.0f) (panelWidth - 36.0f) 31.0f (Vector4(0.47f, 0.17f, 0.07f, 0.88f))
                    solid (panelLeft + 18.0f) (y - 7.0f) 5.0f 31.0f (Vector4(1.0f, 0.74f, 0.30f, 1.0f))
                let labelColor =
                    if row.Header then Vector4(0.62f, 0.67f, 0.60f, 0.85f)
                    elif row.Selected then Vector4(1.0f, 0.91f, 0.64f, 1.0f)
                    else white
                addText (panelLeft + 42.0f) y 1.25f labelColor row.Label
                let value =
                    if row.Adjustable then $"< {row.Value} >"
                    elif row.Value = "" then ""
                    else row.Value
                if value <> "" then
                    addText (panelLeft + panelWidth - 70.0f - float32 value.Length * 8.0f) y 1.25f labelColor value)
            let prompt = "UP/DOWN SELECT   LEFT/RIGHT ADJUST   ENTER CONFIRM   ESC BACK"
            addText (centerX - float32 prompt.Length * 3.6f) (panelTop + panelHeight - 30.0f) 0.9f (Vector4(0.64f, 0.67f, 0.61f, 1.0f)) prompt)
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
            use pointer = fixed data
            gl.BufferData(BufferTargetARB.ArrayBuffer, unativeint (data.Length * sizeof<float32>), NativePtr.toVoidPtr pointer, BufferUsageARB.DynamicDraw)
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
