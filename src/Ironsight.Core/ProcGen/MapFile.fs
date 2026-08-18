namespace Ironsight.ProcGen

open System
open System.IO
open System.Numerics
open System.Security.Cryptography
open System.Text
open Ironsight

/// Binary .ironmap codec. Encodes the map *spec* (DSL items), never compiled
/// geometry, so engine changes (slopes, triangle collision, ...) cannot
/// invalidate map files — new DSL items only add a tag and bump the version.
/// A map's identity is the SHA-256 of its encoded bytes; caches key on that
/// hash, which is what makes GoldSrc's "map differs from server" impossible.
[<RequireQualifiedAccess>]
module MapFile =
    [<Literal>]
    let Extension = ".ironmap"

    [<Literal>]
    let Version = 1uy

    let private magic = "IRNM"B

    /// Absolute ceiling on accepted files; real maps are a few KB.
    [<Literal>]
    let MaxBytes = 4 * 1024 * 1024

    // Derived from Materials.all, the single source of truth for the tag
    // order; tag numbers are a serialization format, so Materials.all only
    // ever grows at the end.
    let private materialTag material = byte (Materials.id material)

    let private materialOf tag =
        let index = int tag
        if index >= 0 && index < Materials.all.Length then Materials.all[index]
        else failwith $"unknown material tag {tag}"

    let private teamTag = function Allies -> 0uy | Axis -> 1uy
    let private teamOf = function 0uy -> Allies | 1uy -> Axis | tag -> failwith $"unknown team tag {tag}"

    let private writeVector3 (writer: BinaryWriter) (value: Vector3) =
        writer.Write value.X
        writer.Write value.Y
        writer.Write value.Z

    let private readVector3 (reader: BinaryReader) =
        let x = reader.ReadSingle()
        let y = reader.ReadSingle()
        Vector3(x, y, reader.ReadSingle())

    let private writeVector2 (writer: BinaryWriter) (value: Vector2) =
        writer.Write value.X
        writer.Write value.Y

    let private readVector2 (reader: BinaryReader) =
        let x = reader.ReadSingle()
        Vector2(x, reader.ReadSingle())

    let private writeCondition (writer: BinaryWriter) condition =
        match condition with
        | EnterVolume bounds ->
            writer.Write 0uy
            writeVector3 writer bounds.Min
            writeVector3 writer bounds.Max
        | SquadDead squad ->
            writer.Write 1uy
            writer.Write squad
        | ObjectiveDone index ->
            writer.Write 2uy
            writer.Write index
        | Delay seconds ->
            writer.Write 3uy
            writer.Write(Units.raw seconds)

    let private readCondition (reader: BinaryReader) =
        match reader.ReadByte() with
        | 0uy ->
            let minimum = readVector3 reader
            EnterVolume { Min = minimum; Max = readVector3 reader }
        | 1uy -> SquadDead(reader.ReadInt32())
        | 2uy -> ObjectiveDone(reader.ReadInt32())
        | 3uy -> Delay(Units.seconds (reader.ReadSingle()))
        | tag -> failwith $"unknown trigger condition tag {tag}"

    let private writeAction (writer: BinaryWriter) action =
        match action with
        | SpawnWave(team, count, position) ->
            writer.Write 0uy
            writer.Write(teamTag team)
            writer.Write count
            writeVector3 writer position
        | SetObjective(index, completed) ->
            writer.Write 1uy
            writer.Write index
            writer.Write completed
        | Say(speaker, line) ->
            writer.Write 2uy
            writer.Write speaker
            writer.Write line
        | OpenPath brushIndex ->
            writer.Write 3uy
            writer.Write brushIndex
        | EndMission -> writer.Write 4uy

    let private readAction (reader: BinaryReader) =
        match reader.ReadByte() with
        | 0uy ->
            let team = teamOf (reader.ReadByte())
            let count = reader.ReadInt32()
            SpawnWave(team, count, readVector3 reader)
        | 1uy ->
            let index = reader.ReadInt32()
            SetObjective(index, reader.ReadBoolean())
        | 2uy ->
            let speaker = reader.ReadString()
            Say(speaker, reader.ReadString())
        | 3uy -> OpenPath(reader.ReadInt32())
        | 4uy -> EndMission
        | tag -> failwith $"unknown script action tag {tag}"

    // Heightfields carry a height *function*, which cannot travel. It is
    // sampled at exactly the grid corners LevelCompile evaluates, and decoding
    // rebuilds a bilinear lookup over that grid — identical geometry, and any
    // off-grid query still interpolates sensibly.
    let private heightSamples (center: Vector3) (size: Vector2) (cells: int) (height: float32 -> float32 -> float32) =
        let cells = max 1 cells
        let stepX, stepZ = size.X / float32 cells, size.Y / float32 cells
        Array.init ((cells + 1) * (cells + 1)) (fun index ->
            let ix, iz = index % (cells + 1), index / (cells + 1)
            height (center.X - size.X * 0.5f + float32 ix * stepX) (center.Z - size.Y * 0.5f + float32 iz * stepZ))

    let private heightLookup (center: Vector3) (size: Vector2) (cells: int) (samples: float32 array) =
        let cells = max 1 cells
        let stepX, stepZ = size.X / float32 cells, size.Y / float32 cells
        // Queries at grid corners must return the stored sample exactly —
        // float arithmetic puts (corner - origin) / step a ULP off an integer,
        // which would bleed a neighbour sample in and break re-encode identity.
        let snap (value: float32) =
            let nearest = MathF.Round value
            if MathF.Abs(value - nearest) < 1e-4f then nearest else value
        fun (x: float32) (z: float32) ->
            let fx = Math.Clamp(snap ((x - (center.X - size.X * 0.5f)) / stepX), 0.0f, float32 cells)
            let fz = Math.Clamp(snap ((z - (center.Z - size.Y * 0.5f)) / stepZ), 0.0f, float32 cells)
            let ix, iz = min (cells - 1) (int fx), min (cells - 1) (int fz)
            let tx, tz = fx - float32 ix, fz - float32 iz
            let at gx gz = samples[gz * (cells + 1) + gx]
            // Return the endpoint itself rather than a + (b - a) * 1.0, which is
            // a ULP off b and shifts the last row and column on re-encode.
            let lerp (a: float32) (b: float32) t = if t <= 0.0f then a elif t >= 1.0f then b else a + (b - a) * t
            let low = lerp (at ix iz) (at (ix + 1) iz) tx
            let high = lerp (at ix (iz + 1)) (at (ix + 1) (iz + 1)) tx
            lerp low high tz

    let private writeItem (writer: BinaryWriter) item =
        match item with
        | Street(length, width, surface) ->
            writer.Write 1uy
            writer.Write length
            writer.Write width
            writer.Write(materialTag surface)
        | Ruin(center, size, height, facade, condition) ->
            writer.Write 2uy
            writeVector3 writer center
            writeVector2 writer size
            writer.Write height
            writer.Write(materialTag facade)
            writer.Write(match condition with Intact -> 0uy | BlownOut -> 1uy)
        | SandbagLine(startPoint, endPoint, owner) ->
            writer.Write 3uy
            writeVector3 writer startPoint
            writeVector3 writer endPoint
            writer.Write(match owner with None -> 255uy | Some team -> teamTag team)
        | FenceLine(startPoint, endPoint) ->
            writer.Write 4uy
            writeVector3 writer startPoint
            writeVector3 writer endPoint
        | Trench(startPoint, endPoint, width) ->
            writer.Write 5uy
            writeVector3 writer startPoint
            writeVector3 writer endPoint
            writer.Write width
        | Mg42(position, facing, owner) ->
            writer.Write 6uy
            writeVector3 writer position
            writer.Write facing
            writer.Write(teamTag owner)
        | Block(center, size, material) ->
            writer.Write 7uy
            writeVector3 writer center
            writeVector3 writer size
            writer.Write(materialTag material)
        | SpawnSquad(team, count, center) ->
            writer.Write 8uy
            writer.Write(teamTag team)
            writer.Write count
            writeVector3 writer center
        | Objective text ->
            writer.Write 9uy
            writer.Write text
        | MissionRule(condition, action) ->
            writer.Write 10uy
            writeCondition writer condition
            writeAction writer action
        | Ramp(startPoint, endPoint, width, material) ->
            writer.Write 11uy
            writeVector3 writer startPoint
            writeVector3 writer endPoint
            writer.Write width
            writer.Write(materialTag material)
        | Heightfield(center, size, cells, height, material) ->
            writer.Write 12uy
            writeVector3 writer center
            writeVector2 writer size
            // Must match readItem's accepted range (1..512) so every encoded file decodes.
            let cells = Math.Clamp(cells, 1, 512)
            writer.Write cells
            writer.Write(materialTag material)
            for sample in heightSamples center size cells height do
                writer.Write sample
        | WaterPlane height ->
            writer.Write 13uy
            writer.Write height

    let private readItem (reader: BinaryReader) =
        match reader.ReadByte() with
        | 1uy ->
            let length = reader.ReadSingle()
            let width = reader.ReadSingle()
            Street(length, width, materialOf (reader.ReadByte()))
        | 2uy ->
            let center = readVector3 reader
            let size = readVector2 reader
            let height = reader.ReadSingle()
            let facade = materialOf (reader.ReadByte())
            let condition = match reader.ReadByte() with 0uy -> Intact | 1uy -> BlownOut | tag -> failwith $"unknown ruin condition tag {tag}"
            Ruin(center, size, height, facade, condition)
        | 3uy ->
            let startPoint = readVector3 reader
            let endPoint = readVector3 reader
            let owner = match reader.ReadByte() with 255uy -> None | tag -> Some(teamOf tag)
            SandbagLine(startPoint, endPoint, owner)
        | 4uy ->
            let startPoint = readVector3 reader
            FenceLine(startPoint, readVector3 reader)
        | 5uy ->
            let startPoint = readVector3 reader
            let endPoint = readVector3 reader
            Trench(startPoint, endPoint, reader.ReadSingle())
        | 6uy ->
            let position = readVector3 reader
            let facing = reader.ReadSingle()
            Mg42(position, facing, teamOf (reader.ReadByte()))
        | 7uy ->
            let center = readVector3 reader
            let size = readVector3 reader
            Block(center, size, materialOf (reader.ReadByte()))
        | 8uy ->
            let team = teamOf (reader.ReadByte())
            let count = reader.ReadInt32()
            SpawnSquad(team, count, readVector3 reader)
        | 9uy -> Objective(reader.ReadString())
        | 10uy ->
            let condition = readCondition reader
            MissionRule(condition, readAction reader)
        | 11uy ->
            let startPoint = readVector3 reader
            let endPoint = readVector3 reader
            let width = reader.ReadSingle()
            Ramp(startPoint, endPoint, width, materialOf (reader.ReadByte()))
        | 12uy ->
            let center = readVector3 reader
            let size = readVector2 reader
            let cells = reader.ReadInt32()
            if cells < 1 || cells > 512 then failwith $"heightfield cell count {cells} out of range"
            let material = materialOf (reader.ReadByte())
            let samples = Array.init ((cells + 1) * (cells + 1)) (fun _ -> reader.ReadSingle())
            Heightfield(center, size, cells, heightLookup center size cells samples, material)
        | 13uy -> WaterPlane(reader.ReadSingle())
        | tag -> failwith $"unknown map item tag {tag} (map made by a newer game version?)"

    let encode (spec: LevelSpec) =
        use stream = new MemoryStream()
        use writer = new BinaryWriter(stream, Encoding.UTF8)
        writer.Write magic
        writer.Write Version
        writer.Write spec.Name
        writer.Write spec.Items.Length
        for item in spec.Items do
            writeItem writer item
        writer.Flush()
        stream.ToArray()

    let decode (bytes: byte array) : Result<LevelSpec, string> =
        if bytes.Length > MaxBytes then Error $"map file is too large ({bytes.Length} bytes)"
        else
            try
                use stream = new MemoryStream(bytes)
                use reader = new BinaryReader(stream, Encoding.UTF8)
                if reader.ReadBytes 4 <> magic then Error "not an ironmap file (bad magic)"
                else
                    let version = reader.ReadByte()
                    if version <> Version then Error $"unsupported map format version {version} (this game reads version {Version})"
                    else
                        let name = reader.ReadString()
                        let count = reader.ReadInt32()
                        if count < 0 || count > 100_000 then Error $"implausible item count {count}"
                        elif String.IsNullOrWhiteSpace name then Error "map has no name"
                        else
                            let items = List.init count (fun _ -> readItem reader)
                            if stream.Position <> stream.Length then Error "trailing bytes after map data"
                            else Ok { Name = name.Trim(); Items = items }
            with
            | :? EndOfStreamException -> Error "truncated map file"
            | Failure message -> Error message
            | error -> Error $"malformed map file: {error.Message}"

    let hash (bytes: byte array) =
        Convert.ToHexString(SHA256.HashData bytes).ToLowerInvariant()
