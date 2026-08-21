namespace Ironsight.ProcGen

open System
open System.Collections.Generic
open System.IO
open System.Numerics
open System.Text
open System.Text.RegularExpressions
open Ironsight

type GoldSrcLadder =
    { LadderFoot: Vector3
      LadderHeight: float32
      LadderFacing: float32 }

type GoldSrcBreakable =
    { Id: int
      Mesh: ProceduralMesh
      Bounds: Aabb }

type GoldSrcMap =
    { WorldMesh: ProceduralMesh
      WorldBounds: Aabb
      PlayerSpawns: struct (Team * Vector3) array
      Climbables: GoldSrcLadder array
      Breakables: GoldSrcBreakable array
      Atlas: LevelTextureAtlas
      TextureNames: string array }

/// Native reader for the BSP30 format used by Half-Life and Counter-Strike
/// 1.x. Geometry, entities, UV projection, embedded WAD3 palettes, and optional
/// external WAD files are consumed directly from the shipped map.
[<RequireQualifiedAccess>]
module GoldSrcBsp =
    [<Struct>]
    type private Lump = { Offset: int; Length: int }

    type private MipTexture =
        { Name: string
          Width: int
          Height: int
          Indices: byte array option
          Palette: byte array option }

    [<Struct>]
    type private TexInfo =
        { S: Vector4
          T: Vector4
          Texture: int }

    [<Struct>]
    type private Face =
        { Plane: int
          PlaneSide: bool
          FirstEdge: int
          EdgeCount: int
          TexInfo: int }

    [<Struct>]
    type private BspModel =
        { Min: Vector3
          Max: Vector3
          FirstFace: int
          FaceCount: int }

    let private unitsPerMetre = 40.0f
    let private atlasTileSize = 128

    let private invalid message = raise (InvalidDataException message)

    let private fixedString (reader: BinaryReader) length =
        reader.ReadBytes length
        |> fun bytes ->
            match Array.tryFindIndex ((=) 0uy) bytes with
            | Some ending -> Encoding.Latin1.GetString(bytes, 0, ending)
            | None -> Encoding.Latin1.GetString bytes

    let private vector3 (reader: BinaryReader) =
        Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())

    let private vector4 (reader: BinaryReader) =
        Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle())

    let private resolvePath path =
        let candidates =
            [| Path.GetFullPath path
               Path.Combine(AppContext.BaseDirectory, path)
               Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", path) |> Path.GetFullPath |]
            |> Array.distinct
        candidates
        |> Array.tryFind File.Exists
        |> Option.defaultWith (fun () ->
            let tried = String.Join(", ", candidates)
            invalid $"GoldSrc map '{path}' was not found (tried {tried})")

    let private checkedLump fileLength index (offset: int) (length: int) =
        if offset < 0 || length < 0 || int64 offset + int64 length > int64 fileLength then
            invalid $"BSP lump {index} points outside the file ({offset}+{length})"
        { Offset = offset; Length = length }

    let private countOf name itemSize maximum (lump: Lump) =
        if lump.Length % itemSize <> 0 then invalid $"{name} lump is not aligned to {itemSize} bytes"
        let count = lump.Length / itemSize
        if count > maximum then invalid $"{name} lump has an unreasonable item count ({count})"
        count

    let private readMipTexture (reader: BinaryReader) baseOffset limit =
        if baseOffset < 0 || int64 baseOffset + 40L > int64 limit then invalid "mip texture header is outside its container"
        reader.BaseStream.Position <- int64 baseOffset
        let name = fixedString reader 16
        let width, height = reader.ReadInt32(), reader.ReadInt32()
        let offsets = Array.init 4 (fun _ -> reader.ReadInt32())
        if width <= 0 || height <= 0 || width > 4096 || height > 4096 then
            invalid $"texture '{name}' has invalid dimensions {width}x{height}"
        if offsets[0] = 0 then
            { Name = name; Width = width; Height = height; Indices = None; Palette = None }
        else
            let pixelCount64 = int64 width * int64 height
            if pixelCount64 > int64 Int32.MaxValue then invalid $"texture '{name}' is too large"
            let pixelCount = int pixelCount64
            let pixelOffset64 = int64 baseOffset + int64 offsets[0]
            let paletteOffset64 = pixelOffset64 + pixelCount64 + pixelCount64 / 4L + pixelCount64 / 16L + pixelCount64 / 64L
            if pixelOffset64 < int64 baseOffset || paletteOffset64 + 2L > int64 limit then
                invalid $"texture '{name}' pixels point outside their container"
            let pixelOffset, paletteOffset = int pixelOffset64, int paletteOffset64
            reader.BaseStream.Position <- int64 pixelOffset
            let indices = reader.ReadBytes pixelCount
            if indices.Length <> pixelCount then invalid $"texture '{name}' has truncated pixels"
            reader.BaseStream.Position <- int64 paletteOffset
            let colourCount = int (reader.ReadUInt16())
            if colourCount <= 0 || colourCount > 256 || paletteOffset + 2 + colourCount * 3 > limit then
                invalid $"texture '{name}' has an invalid palette"
            let supplied = reader.ReadBytes(colourCount * 3)
            let palette = Array.zeroCreate<byte> (256 * 3)
            Array.Copy(supplied, palette, supplied.Length)
            { Name = name; Width = width; Height = height; Indices = Some indices; Palette = Some palette }

    let private readBspTextures (reader: BinaryReader) (lump: Lump) =
        if lump.Length < 4 then invalid "texture lump is truncated"
        reader.BaseStream.Position <- int64 lump.Offset
        let count = reader.ReadInt32()
        if count <= 0 || count > 999 || 4 + count * 4 > lump.Length then
            invalid $"texture lump has an invalid item count ({count})"
        let offsets = Array.init count (fun _ -> reader.ReadInt32())
        offsets
        |> Array.mapi (fun index relative ->
            if relative < 0 then
                { Name = $"missing_{index}"; Width = 64; Height = 64; Indices = None; Palette = None }
            else
                let absolute = int64 lump.Offset + int64 relative
                if absolute < int64 lump.Offset || absolute > int64 (lump.Offset + lump.Length) then
                    invalid $"texture {index} points outside the texture lump"
                readMipTexture reader (int absolute) (lump.Offset + lump.Length))

    let private tryReadWad path =
        try
            use stream = File.OpenRead path
            use reader = new BinaryReader(stream)
            let magic = Encoding.ASCII.GetString(reader.ReadBytes 4)
            if magic <> "WAD3" && magic <> "WAD2" then None
            else
                let count, directoryOffset = reader.ReadInt32(), reader.ReadInt32()
                if count < 0 || count > 4096 || directoryOffset < 12 || int64 directoryOffset + int64 count * 32L > stream.Length then None
                else
                    let entries = ResizeArray<string * int * byte>()
                    reader.BaseStream.Position <- int64 directoryOffset
                    for _ in 1..count do
                        let position = reader.ReadInt32()
                        reader.ReadInt32() |> ignore // compressed size
                        reader.ReadInt32() |> ignore // uncompressed size
                        let kind = reader.ReadByte()
                        let compression = reader.ReadByte()
                        reader.ReadUInt16() |> ignore
                        let name = fixedString reader 16
                        entries.Add(name, position, if compression = 0uy then kind else 0uy)
                    entries
                    |> Seq.choose (fun (name, position, kind) ->
                        if kind <> 0x43uy || position < 0 || int64 position + 40L > stream.Length then None
                        else
                            try Some(name.ToLowerInvariant(), readMipTexture reader position (int stream.Length))
                            with :? InvalidDataException -> None)
                    |> Map.ofSeq
                    |> Some
        with
        | :? IOException
        | :? UnauthorizedAccessException -> None

    let private wadTextures (mapPath: string) (entities: Map<string, string> array) =
        let mapDirectory = Path.GetDirectoryName mapPath
        let declared =
            entities
            |> Array.tryFind (fun entity -> Map.tryFind "classname" entity = Some "worldspawn")
            |> Option.bind (Map.tryFind "wad")
            |> Option.defaultValue ""
            |> fun value -> value.Split(';', StringSplitOptions.RemoveEmptyEntries ||| StringSplitOptions.TrimEntries)
            |> Array.map (fun value -> Path.GetFileName(value.Replace('\\', Path.DirectorySeparatorChar)))
        let local =
            if Directory.Exists mapDirectory then Directory.GetFiles(mapDirectory, "*.wad") else [||]
        Array.append (declared |> Array.map (fun name -> Path.Combine(mapDirectory, name))) local
        |> Array.distinct
        |> Array.choose (fun path -> tryReadWad path)
        |> Array.fold (fun combined wad -> Map.fold (fun state key value -> Map.add key value state) combined wad) Map.empty

    let private parseEntities (bytes: byte array) (lump: Lump) =
        let text = Encoding.Latin1.GetString(bytes, lump.Offset, lump.Length).TrimEnd('\000')
        Regex.Matches(text, @"\{([^}]*)\}", RegexOptions.Singleline)
        |> Seq.cast<Match>
        |> Seq.map (fun block ->
            Regex.Matches(block.Groups[1].Value, "\"([^\"]+)\"\\s+\"([^\"]*)\"")
            |> Seq.cast<Match>
            |> Seq.map (fun pair -> pair.Groups[1].Value.ToLowerInvariant(), pair.Groups[2].Value)
            |> Map.ofSeq)
        |> Seq.toArray

    let private parseOrigin (entity: Map<string, string>) =
        match Map.tryFind "origin" entity with
        | None -> Vector3.Zero
        | Some value ->
            let fields = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            if fields.Length <> 3 then Vector3.Zero
            else
                match Single.TryParse fields[0], Single.TryParse fields[1], Single.TryParse fields[2] with
                | (true, x), (true, y), (true, z) -> Vector3(x, y, z)
                | _ -> Vector3.Zero

    let private collisionMaterial (name: string) =
        let lower = name.ToLowerInvariant()
        if lower.Contains "water" then Water
        elif lower.Contains "wood" || lower.Contains "crate" || lower.Contains "card" || lower.Contains "door" then Wood
        elif lower.Contains "metal" || lower.Contains "steel" || lower.Contains "rail" || lower.Contains "vent" || lower.Contains "locker" then Metal
        elif lower.Contains "brick" then Brick
        elif lower.Contains "snow" then Snow
        elif lower.Contains "sand" then Sand
        elif lower.Contains "plaster" || lower.Contains "wall" then Plaster
        else Concrete

    let private invisibleTexture (name: string) =
        let normalized = if name.Length > 2 && name[0] = '+' then name.Substring 2 else name
        match normalized.ToLowerInvariant() with
        | "sky" | "aaatrigger" | "origin" | "clip" | "hint" | "skip" | "null" -> true
        | _ -> false

    let private placeholderColour (name: string) =
        let lower = name.ToLowerInvariant()
        let baseColour =
            if lower.Contains "carpet" then Vector3(0.35f, 0.30f, 0.25f)
            elif lower.Contains "wood" || lower.Contains "crate" || lower.Contains "card" then Vector3(0.45f, 0.29f, 0.16f)
            elif lower.Contains "brick" then Vector3(0.48f, 0.20f, 0.14f)
            elif lower.Contains "metal" || lower.Contains "elev" || lower.Contains "rail" then Vector3(0.42f, 0.45f, 0.48f)
            elif lower.Contains "light" then Vector3(0.88f, 0.84f, 0.62f)
            elif lower.Contains "floor" || lower.Contains "crete" || lower.Contains "cement" then Vector3(0.46f, 0.45f, 0.42f)
            else
                let hash = name |> Seq.fold (fun state character -> (state * 33 + int character) &&& 0xFFFFFF) 5381
                Vector3(0.28f + float32 (hash &&& 31) / 180.0f, 0.28f + float32 ((hash >>> 5) &&& 31) / 180.0f, 0.28f + float32 ((hash >>> 10) &&& 31) / 180.0f)
        baseColour

    let private buildAtlas (textures: MipTexture array) (externalTextures: Map<string, MipTexture>) =
        let resolved =
            textures
            |> Array.map (fun texture ->
                match texture.Indices with
                | Some _ -> texture
                | None -> Map.tryFind (texture.Name.ToLowerInvariant()) externalTextures |> Option.defaultValue texture)
        let columns = int (Math.Ceiling(Math.Sqrt(float resolved.Length)))
        let rows = (resolved.Length + columns - 1) / columns
        let width, height = columns * atlasTileSize, rows * atlasTileSize
        let pixels = Array.zeroCreate<byte> (width * height * 4)
        for layer in 0..resolved.Length - 1 do
            let texture = resolved[layer]
            let tileX, tileY = (layer % columns) * atlasTileSize, (layer / columns) * atlasTileSize
            let fallback = placeholderColour texture.Name
            for y in 0..atlasTileSize - 1 do
                for x in 0..atlasTileSize - 1 do
                    let destination = ((tileY + y) * width + tileX + x) * 4
                    match texture.Indices, texture.Palette with
                    | Some indices, Some palette ->
                        let sourceX = x * texture.Width / atlasTileSize
                        let sourceY = y * texture.Height / atlasTileSize
                        let colour = int indices[sourceY * texture.Width + sourceX]
                        pixels[destination] <- palette[colour * 3]
                        pixels[destination + 1] <- palette[colour * 3 + 1]
                        pixels[destination + 2] <- palette[colour * 3 + 2]
                        pixels[destination + 3] <- if texture.Name.StartsWith "{" && colour = 255 then 0uy else 255uy
                    | _ ->
                        let shade = if (x / 16 + y / 16) % 2 = 0 then 0.88f else 1.08f
                        pixels[destination] <- byte (Math.Clamp(fallback.X * shade, 0.0f, 1.0f) * 255.0f)
                        pixels[destination + 1] <- byte (Math.Clamp(fallback.Y * shade, 0.0f, 1.0f) * 255.0f)
                        pixels[destination + 2] <- byte (Math.Clamp(fallback.Z * shade, 0.0f, 1.0f) * 255.0f)
                        pixels[destination + 3] <- 255uy
        RgbaAtlas(pixels, width, height, columns, rows, atlasTileSize)

    let load path =
        let resolvedPath = resolvePath path
        let bytes = File.ReadAllBytes resolvedPath
        use stream = new MemoryStream(bytes, false)
        use reader = new BinaryReader(stream)
        if reader.ReadInt32() <> 30 then invalid $"'{resolvedPath}' is not a GoldSrc BSP30 map"
        let lumps =
            Array.init 15 (fun index ->
                checkedLump bytes.Length index (reader.ReadInt32()) (reader.ReadInt32()))
        let entities = parseEntities bytes lumps[0]
        let textures = readBspTextures reader lumps[2]
        let externalTextures = wadTextures resolvedPath entities

        let planes =
            let lump = lumps[1]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "plane" 20 65_535 lump) (fun _ ->
                let normal = vector3 reader
                reader.ReadSingle() |> ignore
                reader.ReadInt32() |> ignore
                normal)
        let vertices =
            let lump = lumps[3]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "vertex" 12 65_535 lump) (fun _ -> vector3 reader)
        let texInfos =
            let lump = lumps[6]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "texinfo" 40 16_384 lump) (fun _ ->
                let s, t = vector4 reader, vector4 reader
                let texture = reader.ReadInt32()
                reader.ReadInt32() |> ignore
                if texture < 0 || texture >= textures.Length then invalid $"texinfo references unknown texture {texture}"
                { S = s; T = t; Texture = texture })
        let faces =
            let lump = lumps[7]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "face" 20 65_535 lump) (fun _ ->
                let plane = int (reader.ReadUInt16())
                let planeSide = reader.ReadUInt16() <> 0us
                let firstEdge = reader.ReadInt32()
                let edgeCount = int (reader.ReadUInt16())
                let texInfo = int (reader.ReadUInt16())
                reader.ReadBytes 4 |> ignore
                reader.ReadInt32() |> ignore
                if plane < 0 || plane >= planes.Length then invalid $"face references unknown plane {plane}"
                if texInfo < 0 || texInfo >= texInfos.Length then invalid $"face references unknown texinfo {texInfo}"
                { Plane = plane; PlaneSide = planeSide; FirstEdge = firstEdge; EdgeCount = edgeCount; TexInfo = texInfo })
        let edges =
            let lump = lumps[12]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "edge" 4 256_000 lump) (fun _ -> struct (int (reader.ReadUInt16()), int (reader.ReadUInt16())))
        let surfEdges =
            let lump = lumps[13]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "surfedge" 4 512_000 lump) (fun _ -> reader.ReadInt32())
        let models =
            let lump = lumps[14]
            reader.BaseStream.Position <- int64 lump.Offset
            Array.init (countOf "model" 64 4096 lump) (fun _ ->
                let minPoint, maxPoint = vector3 reader, vector3 reader
                vector3 reader |> ignore
                for _ in 1..5 do reader.ReadInt32() |> ignore
                { Min = minPoint; Max = maxPoint; FirstFace = reader.ReadInt32(); FaceCount = reader.ReadInt32() })
        if models.Length = 0 then invalid "BSP contains no world model"

        let centreX = (models[0].Min.X + models[0].Max.X) * 0.5f
        let centreY = (models[0].Min.Y + models[0].Max.Y) * 0.5f
        let world (translation: Vector3) (point: Vector3) =
            let point = point + translation
            Vector3((centreX - point.X) / unitsPerMetre, point.Z / unitsPerMetre, (point.Y - centreY) / unitsPerMetre)

        let solidClasses =
            set [ "func_wall"; "func_breakable"; "func_button"; "func_door"; "func_door_rotating"
                  "func_illusionary"; "func_water" ]
        let modelEntities =
            entities
            |> Array.choose (fun entity ->
                match Map.tryFind "classname" entity, Map.tryFind "model" entity with
                | Some className, Some model when Set.contains className solidClasses && model.StartsWith "*" ->
                    match Int32.TryParse(model.AsSpan 1) with
                    | true, index when index > 0 && index < models.Length -> Some(index, parseOrigin entity, entity)
                    | _ -> None
                | _ -> None)
        let isGlass (_, _, entity: Map<string, string>) =
            Map.tryFind "classname" entity = Some "func_breakable"
            && Map.tryFind "rendermode" entity = Some "2"
            && (Map.tryFind "material" entity |> Option.defaultValue "0" = "0")
        let glassModels = modelEntities |> Array.filter isGlass
        let solidModels = modelEntities |> Array.filter (isGlass >> not)
        let meshForInstances (instances: (int * Vector3 * Material option) array) =
            let meshVertices = ResizeArray<MeshVertex>()
            let addTriangle material textureIndex expectedNormal a uvA b uvB c uvC =
                let face = Vector3.Cross(b - a, c - a)
                if face.LengthSquared() > 1e-10f then
                    let b, uvB, c, uvC, face =
                        if Vector3.Dot(face, expectedNormal) < 0.0f then c, uvC, b, uvB, -face
                        else b, uvB, c, uvC, face
                    let normal = Vector3.Normalize face
                    let materialId = Materials.importedId material textureIndex
                    for position, uv in [| a, uvA; b, uvB; c, uvC |] do
                        meshVertices.Add { Position = position; Normal = normal; TexCoord = uv; MaterialId = materialId }
            for modelIndex, translation, forcedMaterial in instances do
                let model = models[modelIndex]
                if model.FirstFace < 0 || model.FaceCount < 0 || int64 model.FirstFace + int64 model.FaceCount > int64 faces.Length then
                    invalid $"model {modelIndex} references faces outside the face lump"
                for faceIndex in model.FirstFace..model.FirstFace + model.FaceCount - 1 do
                    let face = faces[faceIndex]
                    if face.EdgeCount >= 3 && face.FirstEdge >= 0 && int64 face.FirstEdge + int64 face.EdgeCount <= int64 surfEdges.Length then
                        let texInfo = texInfos[face.TexInfo]
                        let texture = textures[texInfo.Texture]
                        if not (invisibleTexture texture.Name) then
                            let sourceNormal = planes[face.Plane] * (if face.PlaneSide then -1.0f else 1.0f)
                            let expectedNormal = Vector3(-sourceNormal.X, sourceNormal.Z, sourceNormal.Y)
                            let corners =
                                Array.init face.EdgeCount (fun offset ->
                                    let surfEdge = surfEdges[face.FirstEdge + offset]
                                    if surfEdge = Int32.MinValue then invalid $"face {faceIndex} has an invalid surfedge"
                                    let edgeIndex = abs surfEdge
                                    if edgeIndex >= edges.Length then invalid $"face {faceIndex} references unknown edge {edgeIndex}"
                                    let struct (first, second) = edges[edgeIndex]
                                    let vertexIndex = if surfEdge >= 0 then first else second
                                    if vertexIndex >= vertices.Length then invalid $"edge {edgeIndex} references unknown vertex {vertexIndex}"
                                    let source = vertices[vertexIndex]
                                    let uv =
                                        Vector2(
                                            (source.X * texInfo.S.X + source.Y * texInfo.S.Y + source.Z * texInfo.S.Z + texInfo.S.W) / float32 texture.Width,
                                            (source.X * texInfo.T.X + source.Y * texInfo.T.Y + source.Z * texInfo.T.Z + texInfo.T.W) / float32 texture.Height)
                                    world translation source, uv)
                            let material = forcedMaterial |> Option.defaultValue (collisionMaterial texture.Name)
                            let a, uvA = corners[0]
                            for index in 1..corners.Length - 2 do
                                let b, uvB = corners[index]
                                let c, uvC = corners[index + 1]
                                addTriangle material texInfo.Texture expectedNormal a uvA b uvB c uvC
            let values = meshVertices.ToArray()
            { Vertices = values; Indices = Array.init values.Length uint32 }
        let mesh =
            Array.append [| 0, Vector3.Zero, None |] (solidModels |> Array.map (fun (index, translation, _) -> index, translation, None))
            |> meshForInstances
        if mesh.Vertices.Length = 0 then invalid "BSP produced no renderable world geometry"
        let meshBounds (value: ProceduralMesh) =
            value.Vertices
            |> Array.fold (fun bounds vertex ->
                { Min = Vector3.Min(bounds.Min, vertex.Position)
                  Max = Vector3.Max(bounds.Max, vertex.Position) })
                { Min = Vector3(Single.PositiveInfinity); Max = Vector3(Single.NegativeInfinity) }
        let breakables =
            glassModels
            |> Array.map (fun (modelIndex, translation, _) ->
                let value = meshForInstances [| modelIndex, translation, Some Glass |]
                { Id = modelIndex; Mesh = value; Bounds = meshBounds value })
        let bounds =
            breakables
            |> Array.fold (fun current item ->
                { Min = Vector3.Min(current.Min, item.Bounds.Min)
                  Max = Vector3.Max(current.Max, item.Bounds.Max) }) (meshBounds mesh)
        let spawns =
            entities
            |> Array.choose (fun entity ->
                match Map.tryFind "classname" entity with
                | Some "info_player_start" -> Some(struct (Allies, world Vector3.Zero (parseOrigin entity)))
                | Some "info_player_deathmatch" -> Some(struct (Axis, world Vector3.Zero (parseOrigin entity)))
                | _ -> None)
        let ladders =
            entities
            |> Array.choose (fun entity ->
                match Map.tryFind "classname" entity, Map.tryFind "model" entity with
                | Some "func_ladder", Some model when model.StartsWith "*" ->
                    match Int32.TryParse(model.AsSpan 1) with
                    | true, index when index > 0 && index < models.Length ->
                        let source = models[index]
                        let translation = parseOrigin entity
                        let corners =
                            [| for x in [ source.Min.X; source.Max.X ] do
                                   for y in [ source.Min.Y; source.Max.Y ] do
                                       for z in [ source.Min.Z; source.Max.Z ] do
                                           yield world translation (Vector3(x, y, z)) |]
                        let minPoint = corners |> Array.reduce (fun a b -> Vector3.Min(a, b))
                        let maxPoint = corners |> Array.reduce (fun a b -> Vector3.Max(a, b))
                        let centre = (minPoint + maxPoint) * 0.5f
                        let facing = if maxPoint.X - minPoint.X > maxPoint.Z - minPoint.Z then 0.0f else MathF.PI * 0.5f
                        let outward = MathEx.yawForward facing
                        Some { LadderFoot = Vector3(centre.X, minPoint.Y, centre.Z) - outward * 0.3f
                               LadderHeight = maxPoint.Y - minPoint.Y
                               LadderFacing = facing }
                    | _ -> None
                | _ -> None)
        { WorldMesh = mesh
          WorldBounds = bounds
          PlayerSpawns = spawns
          Climbables = ladders
          Breakables = breakables
          Atlas = buildAtlas textures externalTextures
          TextureNames = textures |> Array.map _.Name }
