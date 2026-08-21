namespace Ironsight.Shell

#nowarn "9"

open System.Numerics
open Microsoft.FSharp.NativeInterop
open Ironsight
open Silk.NET.OpenGL

/// Small GL helpers shared by Hud, Renderer, and Particles: shader/program
/// linking, the stride-7 mesh vertex layout, and matrix flattening for
/// UniformMatrix4 uploads.
[<RequireQualifiedAccess>]
module GlUtil =
    let private compileShader (gl: GL) (shaderType: ShaderType) (source: string) =
        let shader = gl.CreateShader shaderType
        gl.ShaderSource(shader, source)
        gl.CompileShader shader
        let mutable status = 0
        gl.GetShader(shader, ShaderParameterName.CompileStatus, &status)
        if status <> int GLEnum.True then
            let message = gl.GetShaderInfoLog shader
            gl.DeleteShader shader
            invalidOp $"Shader compilation failed: {message}"
        shader

    /// Compiles and links a vertex+fragment program, deleting the intermediate
    /// shader objects either way. Raises with the driver's info log on failure.
    let createProgram (gl: GL) (vertexSource: string) (fragmentSource: string) : uint32 =
        let vertex = compileShader gl ShaderType.VertexShader vertexSource
        let fragment = compileShader gl ShaderType.FragmentShader fragmentSource
        let value = gl.CreateProgram()
        gl.AttachShader(value, vertex)
        gl.AttachShader(value, fragment)
        gl.LinkProgram value
        let mutable status = 0
        gl.GetProgram(value, ProgramPropertyARB.LinkStatus, &status)
        gl.DeleteShader vertex
        gl.DeleteShader fragment
        if status <> int GLEnum.True then invalidOp $"Shader linking failed: {gl.GetProgramInfoLog value}"
        value

    /// Flattens level/actor/gun mesh vertices into the interleaved
    /// pos3/normal3/material1/uv2 layout the level shaders expect.
    let flattenVertices (vertices: MeshVertex array) : float32[] =
        vertices
        |> Array.collect (fun vertex ->
            [| vertex.Position.X; vertex.Position.Y; vertex.Position.Z
               vertex.Normal.X; vertex.Normal.Y; vertex.Normal.Z; float32 vertex.MaterialId
               vertex.TexCoord.X; vertex.TexCoord.Y |])

    /// Creates a VAO/VBO/IBO trio and binds the stride-9 pos3/normal3/material1/uv2
    /// vertex-attribute layout shared by the level, soldier, and gun meshes.
    /// Leaves the VAO unbound; buffer data upload is the caller's job.
    let createMeshBuffers (gl: GL) : struct (uint32 * uint32 * uint32) =
        let vao = gl.GenVertexArray()
        let vertexBuffer = gl.GenBuffer()
        let indexBuffer = gl.GenBuffer()
        gl.BindVertexArray vao
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertexBuffer)
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indexBuffer)
        let stride = uint32 (9 * sizeof<float32>)
        gl.EnableVertexAttribArray 0u
        gl.VertexAttribPointer(0u, 3, VertexAttribPointerType.Float, false, stride, nativeint 0)
        gl.EnableVertexAttribArray 1u
        gl.VertexAttribPointer(1u, 3, VertexAttribPointerType.Float, false, stride, nativeint (3 * sizeof<float32>))
        gl.EnableVertexAttribArray 2u
        gl.VertexAttribPointer(2u, 1, VertexAttribPointerType.Float, false, stride, nativeint (6 * sizeof<float32>))
        gl.EnableVertexAttribArray 3u
        gl.VertexAttribPointer(3u, 2, VertexAttribPointerType.Float, false, stride, nativeint (7 * sizeof<float32>))
        gl.BindVertexArray 0u
        struct (vao, vertexBuffer, indexBuffer)

    /// Clamp-to-edge, linear-filtered 2D texture parameters shared by the HUD
    /// font atlas and the shadow map.
    let clampLinearTexture (gl: GL) (target: TextureTarget) =
        gl.TexParameter(target, TextureParameterName.TextureMinFilter, int TextureMinFilter.Linear)
        gl.TexParameter(target, TextureParameterName.TextureMagFilter, int TextureMagFilter.Linear)
        gl.TexParameter(target, TextureParameterName.TextureWrapS, int TextureWrapMode.ClampToEdge)
        gl.TexParameter(target, TextureParameterName.TextureWrapT, int TextureWrapMode.ClampToEdge)

    /// Flattens a row-major System.Numerics matrix into the 16-float layout
    /// UniformMatrix4's fixed-pointer upload expects.
    let matrixArray (matrix: Matrix4x4) : float32[] =
        [| matrix.M11; matrix.M12; matrix.M13; matrix.M14
           matrix.M21; matrix.M22; matrix.M23; matrix.M24
           matrix.M31; matrix.M32; matrix.M33; matrix.M34
           matrix.M41; matrix.M42; matrix.M43; matrix.M44 |]

    /// Uploads an array to the given buffer target in one call; the pointer
    /// only needs to live for the duration of BufferData.
    let inline upload (gl: GL) (target: BufferTargetARB) (data: ^a[]) (usage: BufferUsageARB) =
        use pointer = fixed data
        gl.BufferData(target, unativeint (data.Length * sizeof< ^a>), NativePtr.toVoidPtr pointer, usage)

    /// Sets a mat4 uniform from a System.Numerics matrix (flatten + fixed +
    /// UniformMatrix4 in one call).
    let setMatrix (gl: GL) (program: uint32) (name: string) (matrix: Matrix4x4) =
        let values = matrixArray matrix
        use pointer = fixed values
        gl.UniformMatrix4(gl.GetUniformLocation(program, name), 1u, false, pointer)
