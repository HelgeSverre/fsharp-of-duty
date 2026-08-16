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
      Gravity: float32
      Remaining: float32
      Lifetime: float32 }

type Particles(gl: GL) =
    let mutable lines: EffectLine list = []
    let mutable puffs: EffectPuff list = []
    let vao = gl.GenVertexArray()
    let buffer = gl.GenBuffer()

    let compile shaderType source =
        let shader = gl.CreateShader(shaderType: ShaderType)
        gl.ShaderSource(shader, source)
        gl.CompileShader shader
        let mutable status = 0
        gl.GetShader(shader, ShaderParameterName.CompileStatus, &status)
        if status <> int GLEnum.True then invalidOp (gl.GetShaderInfoLog shader)
        shader

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
    let program =
        let vertex, fragment = compile ShaderType.VertexShader vertexSource, compile ShaderType.FragmentShader fragmentSource
        let value = gl.CreateProgram()
        gl.AttachShader(value, vertex); gl.AttachShader(value, fragment); gl.LinkProgram value
        gl.DeleteShader vertex; gl.DeleteShader fragment
        let mutable status = 0
        gl.GetProgram(value, ProgramPropertyARB.LinkStatus, &status)
        if status <> int GLEnum.True then invalidOp (gl.GetProgramInfoLog value)
        value

    let addLine from' to' color lifetime =
        lines <- { From = from'; To = to'; Color = color; Remaining = lifetime; Lifetime = lifetime } :: lines

    let addPuff position velocity color size lifetime gravity =
        puffs <-
            { Position = position
              Velocity = velocity
              Color = color
              Size = size
              Gravity = gravity
              Remaining = lifetime
              Lifetime = lifetime }
            :: puffs

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

    member _.Handle(events: GameEvent list) =
        for event in events do
            match event with
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
            | BloodImpact(position, direction, headshot) ->
                let forward = MathEx.normalizedOrZero direction
                let tangent = Vector3.Cross(forward, if abs forward.Y < 0.8f then Vector3.UnitY else Vector3.UnitX) |> MathEx.normalizedOrZero
                let bitangent = Vector3.Cross(forward, tangent) |> MathEx.normalizedOrZero
                let count = if headshot then 12 else 7
                for index in 0..count - 1 do
                    let angle = float32 index * 2.39996f
                    let radial = tangent * MathF.Cos(angle) + bitangent * MathF.Sin(angle)
                    let velocity = forward * (1.5f + float32 (index % 4) * 0.42f) + radial * (0.35f + float32 (index % 3) * 0.18f)
                    let color = if index % 3 = 0 then Vector4(0.24f, 0.005f, 0.008f, 0.95f) else Vector4(0.58f, 0.015f, 0.018f, 0.90f)
                    addPuff position velocity color (if headshot then 15.0f else 10.0f) (0.42f + float32 (index % 3) * 0.08f) -7.5f
                addLine position (position + forward * (if headshot then 0.75f else 0.42f)) (Vector4(0.72f, 0.01f, 0.015f, 0.92f)) 0.18f
            | HeadGib(position, direction) ->
                let forward = MathEx.normalizedOrZero direction
                for index in 0..7 do
                    let angle = float32 index * MathF.Tau / 8.0f
                    let spray = Vector3(MathF.Cos angle, 0.35f + float32 (index % 3) * 0.22f, MathF.Sin angle)
                    let velocity = forward * 1.8f + spray * (1.2f + float32 (index % 2) * 0.55f)
                    let color = if index % 2 = 0 then Vector4(0.32f, 0.006f, 0.008f, 1.0f) else Vector4(0.68f, 0.20f, 0.12f, 1.0f)
                    addPuff position velocity color (20.0f + float32 (index % 3) * 5.0f) 0.72f -8.5f
            | Explosion(position, radius) ->
                let size = min 1.8f (radius * 0.22f)
                for axis in [| Vector3.UnitX; Vector3.UnitY; Vector3.UnitZ |] do
                    addLine (position - axis * size) (position + axis * size) (Vector4(1.0f, 0.34f, 0.05f, 0.95f)) 0.35f
                for index in 0..15 do
                    let angle = float32 index * 2.39996f
                    let rise = 0.35f + float32 (index % 5) * 0.16f
                    let direction = Vector3(MathF.Cos angle, rise, MathF.Sin angle) |> MathEx.normalizedOrZero
                    let color = if index < 5 then Vector4(1.0f, 0.30f, 0.04f, 0.84f) else Vector4(0.20f, 0.19f, 0.17f, 0.58f)
                    addPuff position (direction * (1.4f + float32 (index % 4) * 0.35f)) color (26.0f + float32 (index % 4) * 8.0f) (if index < 5 then 0.38f else 1.15f) 0.28f
            | _ -> ()
        // A synchronized firefight can emit hundreds of cosmetic events in one
        // tick. Keep presentation bounded; simulation and hit results are not
        // affected by dropping the oldest tracers and smoke puffs.
        lines <- lines |> List.truncate 256
        puffs <- puffs |> List.truncate 384

    member _.Step(dt: float32) =
        lines <- lines |> List.choose (fun line -> let remaining = line.Remaining - dt in if remaining > 0.0f then Some { line with Remaining = remaining } else None)
        puffs <-
            puffs
            |> List.choose (fun puff ->
                let remaining = puff.Remaining - dt
                if remaining <= 0.0f then None
                else
                    let velocity = puff.Velocity + Vector3(0.0f, puff.Gravity * dt, 0.0f)
                    Some { puff with Position = puff.Position + velocity * dt; Velocity = velocity; Remaining = remaining; Size = puff.Size + dt * 12.0f })

    member _.Render(viewProjection: Matrix4x4) =
        if not lines.IsEmpty || not puffs.IsEmpty then
            let lineData =
                lines
                |> List.collect (fun line ->
                    let alpha = line.Color.W * MathEx.clamp01 (line.Remaining / line.Lifetime)
                    let color = Vector4(line.Color.X, line.Color.Y, line.Color.Z, alpha)
                    [ line.From; line.To ]
                    |> List.collect (fun position -> [ position.X; position.Y; position.Z; color.X; color.Y; color.Z; color.W; 1.0f ]))
                |> List.toArray
            let puffData =
                puffs
                |> List.collect (fun puff ->
                    let alpha = puff.Color.W * MathEx.clamp01 (puff.Remaining / puff.Lifetime)
                    [ puff.Position.X; puff.Position.Y; puff.Position.Z; puff.Color.X; puff.Color.Y; puff.Color.Z; alpha; puff.Size ])
                |> List.toArray
            let matrix =
                [| viewProjection.M11; viewProjection.M12; viewProjection.M13; viewProjection.M14
                   viewProjection.M21; viewProjection.M22; viewProjection.M23; viewProjection.M24
                   viewProjection.M31; viewProjection.M32; viewProjection.M33; viewProjection.M34
                   viewProjection.M41; viewProjection.M42; viewProjection.M43; viewProjection.M44 |]
            gl.UseProgram program
            use matrixPointer = fixed matrix
            gl.UniformMatrix4(gl.GetUniformLocation(program, "uViewProjection"), 1u, false, matrixPointer)
            gl.BindVertexArray vao
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, buffer)
            gl.Enable EnableCap.Blend
            gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.One)
            if lineData.Length > 0 then
                use linePointer = fixed lineData
                gl.BufferData(BufferTargetARB.ArrayBuffer, unativeint (lineData.Length * sizeof<float32>), NativePtr.toVoidPtr linePointer, BufferUsageARB.DynamicDraw)
                gl.Uniform1(gl.GetUniformLocation(program, "uPointPass"), 0)
                gl.LineWidth 2.0f
                gl.DrawArrays(PrimitiveType.Lines, 0, uint32 (lineData.Length / 8))
            if puffData.Length > 0 then
                gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha)
                use puffPointer = fixed puffData
                gl.BufferData(BufferTargetARB.ArrayBuffer, unativeint (puffData.Length * sizeof<float32>), NativePtr.toVoidPtr puffPointer, BufferUsageARB.DynamicDraw)
                gl.Uniform1(gl.GetUniformLocation(program, "uPointPass"), 1)
                gl.DrawArrays(PrimitiveType.Points, 0, uint32 (puffData.Length / 8))
            gl.Disable EnableCap.Blend
            gl.BindVertexArray 0u

    interface IDisposable with
        member _.Dispose() =
            gl.DeleteBuffer buffer
            gl.DeleteVertexArray vao
            gl.DeleteProgram program
