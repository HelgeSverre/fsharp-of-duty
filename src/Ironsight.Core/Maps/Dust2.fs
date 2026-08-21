namespace Ironsight.ProcGen

open System
open System.IO
open System.Numerics
open System.Reflection
open Ironsight

/// The supplied de_dust2 BSP export, retained at its original player-relative
/// scale. This is the actual reference geometry rather than an interpretation:
/// 9,932 triangles preserve every route, tunnel, staircase, ledge, doorway,
/// crate and building silhouette present in the OBJ.
[<RequireQualifiedAccess>]
module Dust2Map =
    let private readVector3 (reader: BinaryReader) =
        Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())

    let private readCorner (reader: BinaryReader) =
        let position = readVector3 reader
        position, Vector2(reader.ReadSingle(), reader.ReadSingle())

    let private loadWorld () =
        let assembly = Assembly.GetExecutingAssembly()
        use stream = assembly.GetManifestResourceStream("Ironsight.Maps.dust2.mesh")
        if isNull stream then failwith "embedded Dust II geometry is missing"
        use reader = new BinaryReader(stream)
        let magic = Text.Encoding.ASCII.GetString(reader.ReadBytes 4)
        if magic <> "D2M2" then failwith "embedded Dust II geometry has an invalid header"
        let triangleCount = reader.ReadInt32()
        if triangleCount <= 0 || triangleCount > 100_000 then
            failwith $"embedded Dust II geometry has an invalid triangle count ({triangleCount})"
        let vertices = Array.zeroCreate<MeshVertex> (triangleCount * 3)
        for triangle in 0..triangleCount - 1 do
            let materialId = int (reader.ReadUInt16())
            let collisionMaterial =
                if Materials.isImported materialId then Materials.importedCollisionId materialId else materialId
            if collisionMaterial < 0 || collisionMaterial >= Materials.all.Length then
                failwith $"embedded Dust II geometry uses unknown material {materialId}"
            let (a, uvA), (b, uvB), (c, uvC) = readCorner reader, readCorner reader, readCorner reader
            let face = Vector3.Cross(b - a, c - a)
            let normal = if face.LengthSquared() < 1e-12f then Vector3.UnitY else Vector3.Normalize face
            for offset, position, texCoord in [| 0, a, uvA; 1, b, uvB; 2, c, uvC |] do
                vertices[triangle * 3 + offset] <-
                    { Position = position
                      Normal = normal
                      TexCoord = texCoord
                      MaterialId = materialId }
        if stream.Position <> stream.Length then failwith "embedded Dust II geometry has trailing data"
        { Vertices = vertices
          Indices = Array.init vertices.Length uint32 }

    let private world = lazy (loadWorld ())

    let spec =
        let bounds =
            { Min = Vector3(-56.0f, -4.8f, -66.4f)
              Max = Vector3(56.0f, 10.4f, 66.4f) }
        let items =
            [ yield LevelDsl.texturedStaticWorld
                        world.Value
                        bounds
                        (EmbeddedPngAtlas("Ironsight.Assets.dust2-materials.png", 6, 6, 256))

              // The OBJ is world geometry only and carries no entity lump.
              // Spawn yards are placed in the original map's open T and CT
              // starts; compile-time ground probes settle each onto the exact
              // BSP floor beneath it.
              for offset in -3..3 do
                  yield LevelDsl.spawnSquad Axis 1 (Vector3(float32 offset * 1.8f + 30.0f, 0.0f, -48.0f))
                  yield LevelDsl.spawnSquad Allies 1 (Vector3(float32 offset * 1.8f + 35.0f, 0.0f, 34.0f))
              yield LevelDsl.spawnSquad Axis 1 (Vector3(30.0f, 0.0f, -45.0f))
              yield LevelDsl.spawnSquad Allies 1 (Vector3(38.0f, 0.0f, 37.0f))

              yield LevelDsl.objective "Win the round"
              yield LevelDsl.trigger
                  (Delay(Units.seconds 0.35f))
                  (Say("MARSHAL", "Dust II. Long, short, mid and tunnels are open.")) ]
        LevelDsl.level "Dust II" items
