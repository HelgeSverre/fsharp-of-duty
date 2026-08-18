namespace Ironsight.ProcGen

open System
open System.Numerics
open Ironsight

type ProceduralMesh =
    { Vertices: MeshVertex array
      Indices: uint32 array }

[<RequireQualifiedAccess>]
module MeshGen =
    let empty = { Vertices = [||]; Indices = [||] }

    let private faces =
        [| [| Vector3(-0.5f,-0.5f,-0.5f); Vector3(0.5f,-0.5f,-0.5f); Vector3(0.5f,0.5f,-0.5f); Vector3(-0.5f,0.5f,-0.5f) |], -Vector3.UnitZ
           [| Vector3(0.5f,-0.5f,0.5f); Vector3(-0.5f,-0.5f,0.5f); Vector3(-0.5f,0.5f,0.5f); Vector3(0.5f,0.5f,0.5f) |], Vector3.UnitZ
           [| Vector3(-0.5f,-0.5f,0.5f); Vector3(-0.5f,-0.5f,-0.5f); Vector3(-0.5f,0.5f,-0.5f); Vector3(-0.5f,0.5f,0.5f) |], -Vector3.UnitX
           [| Vector3(0.5f,-0.5f,-0.5f); Vector3(0.5f,-0.5f,0.5f); Vector3(0.5f,0.5f,0.5f); Vector3(0.5f,0.5f,-0.5f) |], Vector3.UnitX
           [| Vector3(-0.5f,0.5f,-0.5f); Vector3(0.5f,0.5f,-0.5f); Vector3(0.5f,0.5f,0.5f); Vector3(-0.5f,0.5f,0.5f) |], Vector3.UnitY
           [| Vector3(-0.5f,-0.5f,0.5f); Vector3(0.5f,-0.5f,0.5f); Vector3(0.5f,-0.5f,-0.5f); Vector3(-0.5f,-0.5f,-0.5f) |], -Vector3.UnitY |]

    let box (size: Vector3) (material: Material) =
        let transform = Matrix4x4.CreateScale size
        let vertices =
            faces
            |> Array.collect (fun (corners, normal) ->
                corners
                |> Array.map (fun corner ->
                    { Position = Vector3.Transform(corner, transform)
                      Normal = normal
                      MaterialId = Materials.id material }))
        let indices =
            Array.init 6 (fun face ->
                let start = uint32 (face * 4)
                [| start; start + 2u; start + 1u; start; start + 3u; start + 2u |])
            |> Array.concat
        { Vertices = vertices; Indices = indices }

    let cylinder (segments: int) (radius: float32) (length: float32) (material: Material) =
        let count = max 3 segments
        let vertices = ResizeArray<MeshVertex>()
        let indices = ResizeArray<uint32>()
        for index = 0 to count - 1 do
            let a0 = float32 index / float32 count * MathF.Tau
            let a1 = float32 (index + 1) / float32 count * MathF.Tau
            let n0, n1 = Vector3(MathF.Cos a0, MathF.Sin a0, 0.0f), Vector3(MathF.Cos a1, MathF.Sin a1, 0.0f)
            let start = uint32 vertices.Count
            for position, normal in
                [| Vector3(n0.X * radius, n0.Y * radius, -length * 0.5f), n0
                   Vector3(n1.X * radius, n1.Y * radius, -length * 0.5f), n1
                   Vector3(n1.X * radius, n1.Y * radius, length * 0.5f), n1
                   Vector3(n0.X * radius, n0.Y * radius, length * 0.5f), n0 |] do
                vertices.Add { Position = position; Normal = normal; MaterialId = Materials.id material }
            indices.Add start; indices.Add(start + 1u); indices.Add(start + 2u)
            indices.Add start; indices.Add(start + 2u); indices.Add(start + 3u)
            // Flat end caps are deliberately duplicated so their normals remain
            // sharp. Without these, scopes and barrels expose hollow interiors
            // whenever back-face culling is enabled.
            let capStart = uint32 vertices.Count
            vertices.Add { Position = Vector3(0.0f, 0.0f, -length * 0.5f); Normal = -Vector3.UnitZ; MaterialId = Materials.id material }
            vertices.Add { Position = Vector3(n1.X * radius, n1.Y * radius, -length * 0.5f); Normal = -Vector3.UnitZ; MaterialId = Materials.id material }
            vertices.Add { Position = Vector3(n0.X * radius, n0.Y * radius, -length * 0.5f); Normal = -Vector3.UnitZ; MaterialId = Materials.id material }
            vertices.Add { Position = Vector3(0.0f, 0.0f, length * 0.5f); Normal = Vector3.UnitZ; MaterialId = Materials.id material }
            vertices.Add { Position = Vector3(n0.X * radius, n0.Y * radius, length * 0.5f); Normal = Vector3.UnitZ; MaterialId = Materials.id material }
            vertices.Add { Position = Vector3(n1.X * radius, n1.Y * radius, length * 0.5f); Normal = Vector3.UnitZ; MaterialId = Materials.id material }
            indices.Add capStart; indices.Add(capStart + 1u); indices.Add(capStart + 2u)
            indices.Add(capStart + 3u); indices.Add(capStart + 4u); indices.Add(capStart + 5u)
        { Vertices = vertices.ToArray(); Indices = indices.ToArray() }

    let lathe (segments: int) (profile: Vector2 array) (material: Material) =
        if profile.Length < 2 then empty
        else
            let count = max 3 segments
            let vertices = ResizeArray<MeshVertex>()
            let indices = ResizeArray<uint32>()
            // Normal at each ring follows the profile slope (the revolved
            // perpendicular of the local segment), not the bare radial. A
            // radial-only normal shades a near-flat dome like a vertical wall,
            // which read as a dark band across helmet crowns.
            let slopeNormal ring =
                let before = profile[max 0 (ring - 1)]
                let after = profile[min (profile.Length - 1) (ring + 1)]
                let delta = after - before
                let n = Vector2(delta.Y, -delta.X)
                if n.LengthSquared() < 0.000001f then Vector2(1.0f, 0.0f) else Vector2.Normalize n
            for ring in 0..profile.Length - 1 do
                let n = slopeNormal ring
                for segment in 0..count - 1 do
                    let angle = float32 segment / float32 count * MathF.Tau
                    let radial = Vector3(MathF.Cos angle, MathF.Sin angle, 0.0f)
                    vertices.Add
                        { Position = Vector3(radial.X * profile[ring].X, radial.Y * profile[ring].X, profile[ring].Y)
                          Normal = MathEx.normalizedOrZero (radial * n.X + Vector3.UnitZ * n.Y)
                          MaterialId = Materials.id material }
            for ring in 0..profile.Length - 2 do
                for segment in 0..count - 1 do
                    let next = (segment + 1) % count
                    let a = uint32 (ring * count + segment)
                    let b = uint32 (ring * count + next)
                    let c = uint32 ((ring + 1) * count + next)
                    let d = uint32 ((ring + 1) * count + segment)
                    indices.Add a; indices.Add b; indices.Add c
                    indices.Add a; indices.Add c; indices.Add d
            // End caps, like cylinder's: without them every lathe part is an
            // open tube whose interior is back-face culled, so helmet crowns
            // and torso tops were literal see-through holes.
            let cap (ringIndex: int) (outward: Vector3) =
                let radius = profile[ringIndex].X
                if radius > 0.001f then
                    let z = profile[ringIndex].Y
                    let centre = uint32 vertices.Count
                    vertices.Add { Position = Vector3(0.0f, 0.0f, z); Normal = outward; MaterialId = Materials.id material }
                    for segment in 0..count - 1 do
                        let angle = float32 segment / float32 count * MathF.Tau
                        vertices.Add
                            { Position = Vector3(MathF.Cos angle * radius, MathF.Sin angle * radius, z)
                              Normal = outward
                              MaterialId = Materials.id material }
                    for segment in 0..count - 1 do
                        let next = (segment + 1) % count
                        indices.Add centre
                        // Wind so the face is CCW seen from along `outward`.
                        if outward.Z > 0.0f then
                            indices.Add(centre + 1u + uint32 segment)
                            indices.Add(centre + 1u + uint32 next)
                        else
                            indices.Add(centre + 1u + uint32 next)
                            indices.Add(centre + 1u + uint32 segment)
            cap 0 -Vector3.UnitZ
            cap (profile.Length - 1) Vector3.UnitZ
            { Vertices = vertices.ToArray(); Indices = indices.ToArray() }

    let tube segments outerRadius length material = cylinder segments outerRadius length material

    let wedge (size: Vector3) material =
        let half = size * 0.5f
        let points =
            [| Vector3(-half.X, -half.Y, -half.Z); Vector3(half.X, -half.Y, -half.Z)
               Vector3(-half.X, -half.Y, half.Z); Vector3(half.X, -half.Y, half.Z)
               Vector3(-half.X, half.Y, half.Z); Vector3(half.X, half.Y, half.Z) |]
        let triangles = [| 0,1,3; 0,3,2; 2,3,5; 2,5,4; 0,2,4; 0,4,1; 1,4,5; 1,5,3 |]
        let vertices = ResizeArray<MeshVertex>()
        let indices = ResizeArray<uint32>()
        for a,b,c in triangles do
            let normal = Vector3.Cross(points[b] - points[a], points[c] - points[a]) |> MathEx.normalizedOrZero
            let start = uint32 vertices.Count
            for index in [| a; b; c |] do vertices.Add { Position = points[index]; Normal = normal; MaterialId = Materials.id material }
            indices.Add start; indices.Add(start + 1u); indices.Add(start + 2u)
        { Vertices = vertices.ToArray(); Indices = indices.ToArray() }

    let transform (matrix: Matrix4x4) (mesh: ProceduralMesh) : ProceduralMesh =
        { mesh with
            Vertices =
                mesh.Vertices
                |> Array.map (fun vertex ->
                    { vertex with
                        Position = Vector3.Transform(vertex.Position, matrix)
                        Normal = Vector3.TransformNormal(vertex.Normal, matrix) |> MathEx.normalizedOrZero }) }

    let translate (offset: Vector3) mesh = transform (Matrix4x4.CreateTranslation offset) mesh
    let rotateX angle mesh = transform (Matrix4x4.CreateRotationX angle) mesh
    let rotateY angle mesh = transform (Matrix4x4.CreateRotationY angle) mesh
    let rotateZ angle mesh = transform (Matrix4x4.CreateRotationZ angle) mesh
    let scale (amount: Vector3) mesh = transform (Matrix4x4.CreateScale amount) mesh
    let paint material (mesh: ProceduralMesh) =
        { mesh with Vertices = mesh.Vertices |> Array.map (fun vertex -> { vertex with MaterialId = Materials.id material }) }

    let union (meshes: ProceduralMesh array) =
        let vertices = ResizeArray<MeshVertex>()
        let indices = ResizeArray<uint32>()
        for mesh in meshes do
            let offset = uint32 vertices.Count
            vertices.AddRange mesh.Vertices
            for index in mesh.Indices do indices.Add(index + offset)
        { Vertices = vertices.ToArray(); Indices = indices.ToArray() }
