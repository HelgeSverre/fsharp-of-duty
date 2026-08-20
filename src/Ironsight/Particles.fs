namespace Ironsight.Shell

#nowarn "9"

open System
open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Silk.NET.OpenGL

type private EffectLine =
    { From: Vector3
      To: Vector3
      Color: Vector4
      Remaining: float32
      Lifetime: float32 }

type private EffectPuff =
    { Position: Vector3
      Velocity: Vector3
      Color: Vector4
      Size: float32
      /// Pixels per second added to Size. Smoke billows, embers stay tight.
      Growth: float32
      Gravity: float32
      Remaining: float32
      Lifetime: float32 }

type Particles(gl: GL) =
    let mutable lines: EffectLine list = []
    let mutable puffs: EffectPuff list = []
    /// Transient aiming geometry, replaced wholesale every frame rather than
    /// aged like particles. Currently the grenade trajectory preview.
    let mutable preview: Vector3 array = [||]
    // One-frame debug overlay lines (LOS view); cleared after every Render.
    let debugLines = ResizeArray<struct (Vector3 * Vector3 * Vector4)>()
    let vao = gl.GenVertexArray()
    let buffer = gl.GenBuffer()

    let vertexSource = """
#version 410 core
layout(location=0) in vec3 aPosition;
layout(location=1) in vec4 aColor;
layout(location=2) in float aSize;
uniform mat4 uViewProjection;
out vec4 vColor;
void main() { gl_Position = uViewProjection * vec4(aPosition, 1.0); gl_PointSize = aSize; vColor = aColor; }
"""
    let fragmentSource = """
#version 410 core
in vec4 vColor;
uniform int uPointPass;
out vec4 outColor;
void main() {
    float alpha = vColor.a;
    if (uPointPass == 1) {
        float radius = length(gl_PointCoord - vec2(0.5)) * 2.0;
        alpha *= 1.0 - smoothstep(0.25, 1.0, radius);
        if (alpha < 0.01) discard;
    }
    outColor = vec4(vColor.rgb, alpha);
}
"""
    let program = GlUtil.createProgram gl vertexSource fragmentSource

    /// One stride-8 vertex: pos3 / color4 / point size.
    let vtx (position: Vector3) (color: Vector4) (size: float32) =
        [| position.X; position.Y; position.Z; color.X; color.Y; color.Z; color.W; size |]

    let addLine from' to' color lifetime =
        lines <- { From = from'; To = to'; Color = color; Remaining = lifetime; Lifetime = lifetime } :: lines

    let addGrowingPuff position velocity color size lifetime gravity growth =
        puffs <-
            { Position = position
              Velocity = velocity
              Color = color
              Size = size
              Growth = growth
              Gravity = gravity
              Remaining = lifetime
              Lifetime = lifetime }
            :: puffs

    /// The long-standing default growth rate, kept for every effect that was
    /// tuned against it.
    let addPuff position velocity color size lifetime gravity =
        addGrowingPuff position velocity color size lifetime gravity 12.0f

    do
        gl.Enable EnableCap.ProgramPointSize
        gl.BindVertexArray vao
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer)
        let stride = uint32 (8 * sizeof<float32>)
        gl.EnableVertexAttribArray 0u
        gl.VertexAttribPointer(0u, 3, VertexAttribPointerType.Float, false, stride, nativeint 0)
        gl.EnableVertexAttribArray 1u
        gl.VertexAttribPointer(1u, 4, VertexAttribPointerType.Float, false, stride, nativeint (3 * sizeof<float32>))
        gl.EnableVertexAttribArray 2u
        gl.VertexAttribPointer(2u, 1, VertexAttribPointerType.Float, false, stride, nativeint (7 * sizeof<float32>))
        gl.BindVertexArray 0u

    member _.Handle(events: GameEvent list) (blood: Vector3) =
        for event in events do
            match event with
            | ShotFired(_, origin, direction, "Paintball Marker") ->
                addPuff (origin + direction * 0.25f) (direction * 0.6f) (Vector4(0.72f, 0.78f, 0.82f, 0.42f)) 9.0f 0.16f 0.1f
            | ShotFired(_, origin, direction, "Nerf Blaster") ->
                addPuff (origin + direction * 0.20f) (direction * 0.35f) (Vector4(1.0f, 0.40f, 0.05f, 0.48f)) 8.0f 0.12f 0.0f
            | ShotFired(_, origin, direction, "Bazooka") ->
                for index in 0..8 do
                    let angle = float32 index * 2.39996f
                    let side = Vector3(MathF.Cos angle, MathF.Sin angle, 0.0f) * (0.25f + float32 (index % 3) * 0.10f)
                    addGrowingPuff (origin + direction * 0.3f) (direction * (1.8f + float32 index * 0.10f) + side) (Vector4(0.62f, 0.60f, 0.54f, 0.58f)) 18.0f 0.8f 0.2f 34.0f
            | ShotFired(_, origin, direction, "Flamethrower") ->
                addPuff (origin + direction * 0.18f) (direction * 1.8f) (Vector4(1.0f, 0.74f, 0.18f, 0.92f)) 18.0f 0.16f 1.2f
            | ShotFired(_, origin, direction, "Super Soaker") ->
                addPuff (origin + direction * 0.16f) (direction * 1.1f) (Vector4(0.30f, 0.72f, 1.0f, 0.55f)) 9.0f 0.18f -1.5f
            | ShotFired(_, origin, direction, "Nailgun") ->
                addPuff (origin + direction * 0.12f) (direction * 0.35f) (Vector4(0.68f, 0.70f, 0.72f, 0.42f)) 8.0f 0.10f 0.0f
            | ShotFired(_, origin, direction, "Harpoon Gun") ->
                addLine origin (origin + direction * 1.4f) (Vector4(0.92f, 0.68f, 0.24f, 0.92f)) 0.18f
                for index in 0..7 do
                    let angle = float32 index * MathF.Tau / 8.0f
                    let side = Vector3(MathF.Cos angle, MathF.Sin angle, 0.0f) * 0.32f
                    addGrowingPuff (origin + direction * 0.18f) (direction * 1.6f + side) (Vector4(0.68f, 0.70f, 0.72f, 0.48f)) 15.0f 0.55f 0.0f 22.0f
            | ShotFired(_, origin, direction, "Bow") ->
                addLine origin (origin + direction * 2.2f) (Vector4(0.72f, 0.48f, 0.22f, 0.72f)) 0.10f
            | ShotFired(_, origin, direction, _) ->
                addLine (origin + direction * 0.35f) (origin + direction * 32.0f) (Vector4(1.0f, 0.78f, 0.28f, 0.8f)) 0.08f
                addPuff (origin + direction * 0.45f) (direction * 0.4f) (Vector4(1.0f, 0.55f, 0.10f, 0.9f)) 18.0f 0.12f 0.28f
            | Impact(position, normal, _) ->
                let tangent = Vector3.Cross(normal, if abs normal.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(normal, tangent) |> MathEx.normalizedOrZero
                addLine position (position + normal * 0.28f) (Vector4(1.0f, 0.58f, 0.18f, 0.9f)) 0.16f
                addLine (position - tangent * 0.12f) (position + tangent * 0.12f) (Vector4(0.75f, 0.48f, 0.18f, 0.7f)) 0.16f
                addLine (position - bitangent * 0.12f) (position + bitangent * 0.12f) (Vector4(0.75f, 0.48f, 0.18f, 0.7f)) 0.16f
                for index in 0..3 do
                    let angle = float32 index * MathF.Tau / 4.0f
                    let velocity = normal * (0.35f + float32 index * 0.08f) + tangent * MathF.Cos(angle) * 0.3f + bitangent * MathF.Sin(angle) * 0.3f
                    addPuff position velocity (Vector4(0.55f, 0.42f, 0.25f, 0.56f)) (10.0f + float32 index * 2.0f) 0.42f -1.8f
            | ArrowImpact(position, normal, stuck) ->
                let color = if stuck then Vector4(0.64f, 0.43f, 0.20f, 0.88f) else Vector4(0.78f, 0.72f, 0.60f, 0.72f)
                addLine position (position + normal * 0.32f) color 0.20f
            | BloodImpact(position, direction, headshot) ->
                let dark = Vector4(blood.X * 0.45f, blood.Y * 0.45f, blood.Z * 0.45f, 0.95f)
                let light = Vector4(blood.X, blood.Y, blood.Z, 0.90f)
                let lineColor = Vector4(min 1.0f (blood.X * 1.25f), min 1.0f (blood.Y * 1.25f), min 1.0f (blood.Z * 1.25f), 0.92f)
                let forward = MathEx.normalizedOrZero direction
                let tangent = Vector3.Cross(forward, if abs forward.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(forward, tangent) |> MathEx.normalizedOrZero
                // Fine exit mist.
                let count = if headshot then 22 else 13
                for index in 0..count - 1 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)
                    let velocity = forward * (1.6f + float32 (index % 4) * 0.5f) + radial * (0.4f + float32 (index % 3) * 0.24f)
                    let color = if index % 3 = 0 then dark else light
                    addPuff position velocity color (if headshot then 17.0f else 12.0f) (0.48f + float32 (index % 3) * 0.10f) -7.5f
                // Heavy droplets that arc down and hang around a moment longer.
                let drops = if headshot then 8 else 5
                for index in 0..drops - 1 do
                    let angle = float32 index * 2.39996f + 0.9f
                    let radial = tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)
                    let velocity = forward * (0.9f + float32 (index % 3) * 0.4f) + radial * 0.7f + Vector3.UnitY * (0.8f + float32 (index % 2) * 0.5f)
                    addPuff position velocity dark (8.0f + float32 (index % 3) * 3.0f) (0.75f + float32 (index % 3) * 0.12f) -12.0f
                // Fanned spurt streaks along the exit direction.
                for index in 0..2 do
                    let spread = float32 (index - 1) * 0.22f
                    let streak = MathEx.normalizedOrZero (forward + tangent * spread + bitangent * spread * 0.6f)
                    addLine position (position + streak * ((if headshot then 0.85f else 0.55f) - abs spread * 0.4f)) lineColor 0.22f
            | HeadGib(position, direction) ->
                let dark = Vector4(blood.X * 0.55f, blood.Y * 0.55f, blood.Z * 0.55f, 1.0f)
                let light = Vector4(min 1.0f (blood.X * 1.15f), min 1.0f (blood.Y * 1.15f), min 1.0f (blood.Z * 1.15f), 1.0f)
                let forward = MathEx.normalizedOrZero direction
                for index in 0..11 do
                    let angle = float32 index * MathF.Tau / 12.0f
                    let spray = Vector3(MathF.Cos angle, 0.35f + float32 (index % 3) * 0.22f, MathF.Sin angle)
                    let velocity = forward * 1.8f + spray * (1.2f + float32 (index % 2) * 0.55f)
                    let color = if index % 2 = 0 then dark else light
                    addPuff position velocity color (22.0f + float32 (index % 3) * 6.0f) 0.85f -8.5f
            | Explosion(position, radius) ->
                // Four layers, front to back in time: a shockwave star, a fast
                // white-hot core, embers thrown outward under gravity, and a
                // smoke column that outlives all of it.
                let size = min 2.4f (radius * 0.3f)
                for axis in [| Vector3.UnitX; Vector3.UnitY; Vector3.UnitZ |] do
                    addLine (position - axis * size) (position + axis * size) (Vector4(1.0f, 0.52f, 0.12f, 0.95f)) 0.22f
                    addLine (position - axis * size * 0.6f) (position + axis * size * 0.6f) (Vector4(1.0f, 0.90f, 0.55f, 0.9f)) 0.14f
                // Fireball: bright, brief, and expanding hard so the first frames
                // read as a flash rather than a cluster of dots.
                for index in 0..9 do
                    let angle = float32 index * 2.39996f
                    let rise = 0.3f + float32 (index % 4) * 0.2f
                    let direction = Vector3(MathF.Cos angle, rise, MathF.Sin angle) |> MathEx.normalizedOrZero
                    let heat = if index % 3 = 0 then Vector4(1.0f, 0.86f, 0.52f, 0.92f) else Vector4(1.0f, 0.42f, 0.06f, 0.88f)
                    addGrowingPuff position (direction * (2.2f + float32 (index % 4) * 0.6f)) heat (30.0f + float32 (index % 3) * 10.0f) (0.26f + float32 (index % 3) * 0.07f) 0.6f 130.0f
                // Embers: small, fast, and falling, to give the blast a sense of
                // throwing material rather than just glowing.
                for index in 0..11 do
                    let angle = float32 index * 2.39996f + 1.1f
                    let rise = 0.55f + float32 (index % 5) * 0.3f
                    let direction = Vector3(MathF.Cos angle, rise, MathF.Sin angle) |> MathEx.normalizedOrZero
                    addGrowingPuff position (direction * (4.5f + float32 (index % 5) * 1.6f)) (Vector4(1.0f, 0.62f, 0.20f, 0.85f)) (7.0f + float32 (index % 3) * 3.0f) (0.5f + float32 (index % 4) * 0.18f) -6.5f 0.0f
                // Smoke: slow, wide, and long-lived. This is the layer that makes
                // a blast still visible a few seconds later.
                for index in 0..23 do
                    let angle = float32 index * 2.39996f + 0.5f
                    let rise = 0.2f + float32 (index % 6) * 0.13f
                    let direction = Vector3(MathF.Cos angle, rise, MathF.Sin angle) |> MathEx.normalizedOrZero
                    let shade = 0.16f + float32 (index % 4) * 0.05f
                    addGrowingPuff
                        (position + direction * 0.3f)
                        (direction * (0.9f + float32 (index % 4) * 0.45f))
                        (Vector4(shade, shade * 0.96f, shade * 0.9f, 0.42f))
                        (24.0f + float32 (index % 5) * 9.0f)
                        (2.6f + float32 (index % 4) * 0.3f)
                        0.42f
                        26.0f
            | PaintImpact(position, normal, color) ->
                let rgb =
                    match color with
                    | PaintRed -> Vector3(1.0f, 0.06f, 0.08f)
                    | PaintBlue -> Vector3(0.04f, 0.32f, 1.0f)
                    | PaintGreen -> Vector3(0.08f, 0.96f, 0.22f)
                    | PaintYellow -> Vector3(1.0f, 0.84f, 0.04f)
                    | PaintPurple -> Vector3(0.72f, 0.08f, 0.98f)
                    | _ -> Vector3(1.0f, 0.30f, 0.03f)
                let tangent = Vector3.Cross(normal, if abs normal.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(normal, tangent) |> MathEx.normalizedOrZero
                for index in 0..13 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
                    let velocity = normal * (0.4f + float32 (index % 4) * 0.22f) + radial * (0.45f + float32 (index % 3) * 0.18f)
                    addPuff position velocity (Vector4(rgb, 0.92f)) (10.0f + float32 (index % 3) * 3.0f) 0.55f -8.0f
            | DartImpact(position, normal, stuck) ->
                let color = if stuck then Vector4(1.0f, 0.42f, 0.06f, 0.80f) else Vector4(0.20f, 0.42f, 1.0f, 0.72f)
                addLine position (position + normal * (if stuck then 0.22f else 0.42f)) color 0.24f
                for index in 0..3 do addPuff position (normal * (0.2f + float32 index * 0.1f)) color (7.0f + float32 index) 0.25f -2.0f
            | RocketDud(position, normal) ->
                addLine position (position + normal * 0.45f) (Vector4(1.0f, 0.48f, 0.10f, 0.82f)) 0.22f
                for index in 0..9 do
                    let shade = 0.24f + float32 (index % 3) * 0.06f
                    addGrowingPuff position (normal * (0.3f + float32 index * 0.08f) + Vector3.UnitY * 0.2f) (Vector4(shade, shade, shade, 0.50f)) 18.0f 1.2f 0.25f 22.0f
            | Backblast(position, direction) ->
                for index in 0..15 do
                    let angle = float32 index * 2.39996f
                    let radial = Vector3(MathF.Cos angle, MathF.Sin angle, 0.0f) * (0.4f + float32 (index % 4) * 0.12f)
                    addGrowingPuff position (direction * (2.2f + float32 (index % 5) * 0.5f) + radial) (Vector4(0.66f, 0.63f, 0.56f, 0.48f)) 22.0f 1.4f 0.3f 30.0f
            | FlameStream(origin, endpoint) ->
                let delta = endpoint - origin
                let direction = MathEx.normalizedOrZero delta
                let reference = if abs direction.Y < 0.9f then Vector3.UnitY else Vector3.UnitX
                let tangent = Vector3.Cross(direction, reference) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(direction, tangent) |> MathEx.normalizedOrZero
                addLine (origin + direction * 0.12f) endpoint (Vector4(1.0f, 0.70f, 0.10f, 0.94f)) 0.14f
                addLine (origin + direction * 0.18f + tangent * 0.025f) (endpoint + tangent * 0.08f) (Vector4(1.0f, 0.24f, 0.03f, 0.72f)) 0.16f
                for index in 1..14 do
                    let t = float32 index / 15.0f
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
                    let position = Vector3.Lerp(origin, endpoint, t) + radial * (0.03f + t * 0.20f)
                    let color = if index % 3 = 0 then Vector4(1.0f, 0.82f, 0.20f, 0.86f) else Vector4(1.0f, 0.25f, 0.02f, 0.78f)
                    addGrowingPuff position (direction * (1.2f + t * 2.0f) + Vector3.UnitY * (0.4f + t)) color (11.0f + t * 22.0f) 0.22f 1.8f 18.0f
            | FlameImpact(position, normal) ->
                let tangent = Vector3.Cross(normal, if abs normal.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(normal, tangent) |> MathEx.normalizedOrZero
                for index in 0..11 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
                    let color = if index % 3 = 0 then Vector4(1.0f, 0.86f, 0.20f, 0.92f) else Vector4(1.0f, 0.22f, 0.015f, 0.82f)
                    addGrowingPuff (position + radial * 0.05f) (normal * (0.35f + float32 (index % 4) * 0.16f) + radial * 0.42f + Vector3.UnitY * 0.55f) color (14.0f + float32 (index % 4) * 4.0f) 0.55f 1.5f 20.0f
                for index in 0..3 do
                    addGrowingPuff position (normal * 0.2f + Vector3.UnitY * (0.3f + float32 index * 0.12f)) (Vector4(0.22f, 0.20f, 0.18f, 0.32f)) 18.0f 0.9f 0.7f 28.0f
            | WaterImpact(position, normal) ->
                addPuff position (normal * 0.6f) (Vector4(0.18f, 0.62f, 1.0f, 0.65f)) 12.0f 0.25f -5.0f
                let tangent = Vector3.Cross(normal, if abs normal.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                for index in 0..5 do
                    let angle = float32 index * MathF.Tau / 6.0f
                    let radial = tangent * MathF.Cos angle + Vector3.Cross(normal, tangent) * MathF.Sin angle
                    addLine position (position + normal * 0.08f + radial * 0.20f) (Vector4(0.15f, 0.58f, 1.0f, 0.72f)) 0.18f
            | NailImpact(position, normal, stuck) ->
                let color = if stuck then Vector4(0.82f, 0.84f, 0.88f, 0.85f) else Vector4(1.0f, 0.60f, 0.16f, 0.92f)
                for index in 0..3 do
                    let side = Vector3(MathF.Cos(float32 index * MathF.PI * 0.5f), 0.2f, MathF.Sin(float32 index * MathF.PI * 0.5f))
                    addLine position (position + normal * 0.15f + side * 0.10f) color 0.14f
            | HarpoonSkewer(position, direction, _) ->
                let forward = MathEx.normalizedOrZero direction
                let tangent = Vector3.Cross(forward, if abs forward.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(forward, tangent) |> MathEx.normalizedOrZero
                let dark = Vector4(blood.X * 0.55f, blood.Y * 0.55f, blood.Z * 0.55f, 0.95f)
                for index in 0..13 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
                    addPuff position (forward * (1.2f + float32 (index % 4) * 0.35f) + radial * 0.75f) dark (12.0f + float32 (index % 3) * 4.0f) 0.62f -8.0f
                addLine position (position + forward * 0.85f) (Vector4(blood, 0.90f)) 0.22f
            | HarpoonEmbedded(position, normal) ->
                let tangent = Vector3.Cross(normal, if abs normal.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(normal, tangent) |> MathEx.normalizedOrZero
                for index in 0..11 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos angle + bitangent * MathF.Sin angle
                    addLine position (position + normal * 0.18f + radial * (0.18f + float32 (index % 3) * 0.08f)) (Vector4(1.0f, 0.63f, 0.12f, 0.92f)) 0.20f
                for index in 0..5 do
                    addPuff position (normal * 0.35f + Vector3.UnitY * (0.18f + float32 index * 0.06f)) (Vector4(0.32f, 0.30f, 0.27f, 0.42f)) 12.0f 0.55f -0.8f
            | Ignited(_, position) ->
                for index in 0..10 do
                    let angle = float32 index * 2.39996f
                    let side = Vector3(MathF.Cos angle, 0.4f, MathF.Sin angle)
                    addGrowingPuff position (side * 0.7f + Vector3.UnitY * 0.8f) (Vector4(1.0f, 0.36f, 0.04f, 0.86f)) 16.0f 0.45f 1.6f 16.0f
            | Burning(_, position) ->
                let phase = float32 puffs.Length * 2.39996f
                let side = Vector3(MathF.Cos phase, 0.0f, MathF.Sin phase) * 0.18f
                addGrowingPuff (position + side) (Vector3.UnitY * 1.2f) (Vector4(1.0f, 0.32f, 0.025f, 0.72f)) 14.0f 0.28f 1.4f 12.0f
            | Extinguished(_, position) ->
                for index in 0..7 do
                    addGrowingPuff position (Vector3.UnitY * (0.4f + float32 index * 0.08f)) (Vector4(0.72f, 0.82f, 0.86f, 0.34f)) 16.0f 0.65f 0.5f 24.0f
            | _ -> ()
        // A synchronized firefight can emit hundreds of cosmetic events in one
        // tick. Keep presentation bounded; simulation and hit results are not
        // affected by dropping the oldest tracers and smoke puffs.
        lines <- lines |> List.truncate 256
        // Smoke lives for seconds rather than fractions of one, so the budget has
        // to cover several overlapping blasts. The truncate keeps the newest, so
        // too low a cap silently eats tracers and blood mid-firefight.
        puffs <- puffs |> List.truncate 1024

    /// Sets the grenade trajectory preview for this frame. Pass an empty array
    /// to clear it. Drawn as points rather than a line strip because macOS core
    /// profile clamps glLineWidth to 1.0, which would leave a hairline arc.
    member _.SetPreview(points: Vector3 array) = preview <- points

    member _.SubmitDebugLine(fromPoint: Vector3, toPoint: Vector3, color: Vector4) =
        debugLines.Add(struct (fromPoint, toPoint, color))

    member _.Step(dt: float32) =
        lines <- lines |> List.choose (fun line -> let remaining = line.Remaining - dt in if remaining > 0.0f then Some { line with Remaining = remaining } else None)
        puffs <-
            puffs
            |> List.choose (fun puff ->
                let remaining = puff.Remaining - dt
                if remaining <= 0.0f then None
                else
                    let velocity = puff.Velocity + Vector3(0.0f, puff.Gravity * dt, 0.0f)
                    Some { puff with Position = puff.Position + velocity * dt; Velocity = velocity; Remaining = remaining; Size = puff.Size + dt * puff.Growth })

    member _.Render(viewProjection: Matrix4x4) =
        if not lines.IsEmpty || not puffs.IsEmpty || preview.Length > 0 || debugLines.Count > 0 then
            let lineData =
                Seq.append
                    (lines
                     |> Seq.collect (fun line ->
                         let alpha = line.Color.W * MathEx.clamp01 (line.Remaining / line.Lifetime)
                         let color = Vector4(line.Color.X, line.Color.Y, line.Color.Z, alpha)
                         Array.append (vtx line.From color 1.0f) (vtx line.To color 1.0f)))
                    (debugLines
                     |> Seq.collect (fun struct (fromPoint, toPoint, color) ->
                         Array.append (vtx fromPoint color 1.0f) (vtx toPoint color 1.0f)))
                |> Seq.toArray
            let puffData =
                puffs
                |> Seq.collect (fun puff ->
                    let alpha = puff.Color.W * MathEx.clamp01 (puff.Remaining / puff.Lifetime)
                    vtx puff.Position (Vector4(puff.Color.X, puff.Color.Y, puff.Color.Z, alpha)) puff.Size)
                |> Seq.toArray
            // The arc tapers away from the hand and ends in a bright landing
            // marker, so the eye is drawn to where the grenade actually stops.
            let previewData =
                preview
                |> Array.mapi (fun index point ->
                    let progress = if preview.Length < 2 then 1.0f else float32 index / float32 (preview.Length - 1)
                    let landing = index = preview.Length - 1
                    let color = if landing then Vector4(1.0f, 0.85f, 0.45f, 0.95f) else Vector4(0.95f, 0.72f, 0.30f, 0.30f + progress * 0.45f)
                    let size = if landing then 22.0f else 4.0f + progress * 4.0f
                    vtx point color size)
                |> Array.concat
            gl.UseProgram program
            GlUtil.setMatrix gl program "uViewProjection" viewProjection
            gl.BindVertexArray vao
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer)
            gl.Enable EnableCap.Blend
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One)
            if lineData.Length > 0 then
                GlUtil.upload gl BufferTargetARB.ArrayBuffer lineData BufferUsageARB.DynamicDraw
                gl.Uniform1(gl.GetUniformLocation(program, "uPointPass"), 0)
                gl.LineWidth 2.0f
                gl.DrawArrays(PrimitiveType.Lines, 0, uint32 (lineData.Length / 8))
            if puffData.Length > 0 || previewData.Length > 0 then
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
                gl.Uniform1(gl.GetUniformLocation(program, "uPointPass"), 1)
                // Overlapping soft particles must not write depth, or each puff
                // punches a hole in the ones drawn after it. Barely visible with
                // brief sparks; obvious once smoke is large and long-lived.
                gl.DepthMask false
                if puffData.Length > 0 then
                    GlUtil.upload gl BufferTargetARB.ArrayBuffer puffData BufferUsageARB.DynamicDraw
                    gl.DrawArrays(PrimitiveType.Points, 0, uint32 (puffData.Length / 8))
                if previewData.Length > 0 then
                    GlUtil.upload gl BufferTargetARB.ArrayBuffer previewData BufferUsageARB.DynamicDraw
                    gl.DrawArrays(PrimitiveType.Points, 0, uint32 (previewData.Length / 8))
                gl.DepthMask true
            gl.Disable EnableCap.Blend
            debugLines.Clear()
            gl.BindVertexArray 0u

    interface IDisposable with
        member _.Dispose() =
            gl.DeleteBuffer buffer
            gl.DeleteVertexArray vao
            gl.DeleteProgram program
