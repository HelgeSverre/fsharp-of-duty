// Renders a weapon's procedural mesh to a PNG contact sheet: side, top and
// front orthographic views, shaded by material. Guns are data, so this needs
// no window and no GPU — which is the point. Spotting a part that floats away
// from the body by launching the game, spawning the weapon and squinting at it
// in first person is far slower than looking at a side elevation.
//
//   just gun-preview "Kar98k"
//   just gun-preview all
//   dotnet fsi tools/GunPreview.fsx Bow 1.0   # inspect full draw
//
// Views share one scale so parts can be compared across them. A faint grid
// every 0.1 world units gives a ruler for measuring gaps, and the muzzle
// points left in the side and top views.
//
// ponytail: flat-shaded painter's z-buffer, no anti-aliasing. Enough to see
// where geometry is; not trying to be the renderer.

#r "../src/Ironsight.Core/bin/Debug/net10.0/Ironsight.Core.dll"

open System
open System.IO
open System.IO.Compression
open System.Numerics
open Ironsight
open Ironsight.ProcGen

// ---------------------------------------------------------------- PNG output

// CRC32 and a zlib-wrapped IDAT are all a valid PNG needs. Writing those two
// by hand is cheaper than taking an image dependency for a debug tool.
let private crcTable =
    Array.init 256 (fun n ->
        let mutable c = uint32 n
        for _ in 0..7 do c <- if c &&& 1u <> 0u then 0xEDB88320u ^^^ (c >>> 1) else c >>> 1
        c)

let private crc32 (bytes: byte array) =
    let mutable c = 0xFFFFFFFFu
    for b in bytes do c <- crcTable[int ((c ^^^ uint32 b) &&& 0xFFu)] ^^^ (c >>> 8)
    c ^^^ 0xFFFFFFFFu

let private beBytes (value: uint32) =
    [| byte (value >>> 24); byte (value >>> 16); byte (value >>> 8); byte value |]

let writePng (path: string) (width: int) (height: int) (rgb: byte array) =
    let chunk (kind: string) (data: byte array) =
        let tagged = Array.append (Text.Encoding.ASCII.GetBytes kind) data
        Array.concat [ beBytes (uint32 data.Length); tagged; beBytes (crc32 tagged) ]
    // Each scanline is prefixed with filter byte 0 (None).
    let raw =
        [| for y in 0 .. height - 1 do
             yield 0uy
             yield! rgb[y * width * 3 .. (y + 1) * width * 3 - 1] |]
    use compressed = new MemoryStream()
    (use z = new ZLibStream(compressed, CompressionLevel.Optimal, true)
     z.Write(raw, 0, raw.Length))
    let ihdr = Array.concat [ beBytes (uint32 width); beBytes (uint32 height); [| 8uy; 2uy; 0uy; 0uy; 0uy |] ]
    File.WriteAllBytes(
        path,
        Array.concat
            [ [| 0x89uy; 0x50uy; 0x4Euy; 0x47uy; 0x0Duy; 0x0Auy; 0x1Auy; 0x0Auy |]
              chunk "IHDR" ihdr
              chunk "IDAT" (compressed.ToArray())
              chunk "IEND" [||] ])

// -------------------------------------------------------------- Canvas + draw

type Canvas =
    { Width: int; Height: int; Pixels: byte array; Depth: float32 array }

let canvas width height (bg: Vector3) =
    let pixels = Array.zeroCreate<byte> (width * height * 3)
    for index in 0 .. width * height - 1 do
        pixels[index * 3] <- byte (bg.X * 255.0f)
        pixels[index * 3 + 1] <- byte (bg.Y * 255.0f)
        pixels[index * 3 + 2] <- byte (bg.Z * 255.0f)
    { Width = width; Height = height; Pixels = pixels; Depth = Array.create (width * height) Single.NegativeInfinity }

let plot (c: Canvas) x y (colour: Vector3) =
    if x >= 0 && x < c.Width && y >= 0 && y < c.Height then
        let index = (y * c.Width + x) * 3
        c.Pixels[index] <- byte (Math.Clamp(colour.X, 0.0f, 1.0f) * 255.0f)
        c.Pixels[index + 1] <- byte (Math.Clamp(colour.Y, 0.0f, 1.0f) * 255.0f)
        c.Pixels[index + 2] <- byte (Math.Clamp(colour.Z, 0.0f, 1.0f) * 255.0f)

/// Flat-shaded triangle with a z-buffer. Depth is "towards the viewer", so a
/// larger value wins.
let triangle (c: Canvas) (a: Vector3) (b: Vector3) (d: Vector3) (colour: Vector3) =
    let minX = max 0 (int (floor (min a.X (min b.X d.X))))
    let maxX = min (c.Width - 1) (int (ceil (max a.X (max b.X d.X))))
    let minY = max 0 (int (floor (min a.Y (min b.Y d.Y))))
    let maxY = min (c.Height - 1) (int (ceil (max a.Y (max b.Y d.Y))))
    let area = (b.X - a.X) * (d.Y - a.Y) - (b.Y - a.Y) * (d.X - a.X)
    if abs area > 1e-6f then
        for y in minY..maxY do
            for x in minX..maxX do
                let px, py = float32 x + 0.5f, float32 y + 0.5f
                let w0 = ((b.X - a.X) * (py - a.Y) - (b.Y - a.Y) * (px - a.X)) / area
                let w1 = ((d.X - b.X) * (py - b.Y) - (d.Y - b.Y) * (px - b.X)) / area
                let w2 = ((a.X - d.X) * (py - d.Y) - (a.Y - d.Y) * (px - d.X)) / area
                // One inclusive test per winding, so triangles of either
                // orientation fill (the mesh has both once mirrored).
                if (w0 >= 0.0f && w1 >= 0.0f && w2 >= 0.0f) || (w0 <= 0.0f && w1 <= 0.0f && w2 <= 0.0f) then
                    let depth = a.Z * w1 + b.Z * w2 + d.Z * w0
                    let index = y * c.Width + x
                    if depth > c.Depth[index] then
                        c.Depth[index] <- depth
                        plot c x y colour

// --------------------------------------------------------------- Scene set-up

let materialColour id =
    match Materials.all[id] with
    | Wood -> Vector3(0.55f, 0.34f, 0.16f)
    | Plaster -> Vector3(0.82f, 0.84f, 0.86f)
    | Metal -> Vector3(0.62f, 0.65f, 0.70f)
    | Sandbag -> Vector3(0.58f, 0.48f, 0.30f)
    | Skin -> Vector3(0.85f, 0.66f, 0.52f)
    | UniformOlive -> Vector3(0.34f, 0.38f, 0.22f)
    | PaintRed -> Vector3(0.95f, 0.06f, 0.08f)
    | PaintBlue -> Vector3(0.04f, 0.30f, 0.98f)
    | PaintGreen -> Vector3(0.08f, 0.90f, 0.22f)
    | PaintYellow -> Vector3(1.0f, 0.82f, 0.04f)
    | PaintPurple -> Vector3(0.68f, 0.08f, 0.92f)
    | PaintOrange | FoamOrange -> Vector3(1.0f, 0.30f, 0.03f)
    | FoamBlue -> Vector3(0.04f, 0.22f, 0.72f)
    | ToolBlack -> Vector3(0.035f, 0.04f, 0.045f)
    | WaterBlue -> Vector3(0.05f, 0.52f, 0.88f)
    | WetDark -> Vector3(0.075f, 0.10f, 0.12f)
    | _ -> Vector3(0.7f, 0.4f, 0.7f)

/// name, world -> (screen right, screen up, towards-viewer depth)
let views: (string * (Vector3 -> Vector3)) array =
    // Muzzle is -Z and up is +Y. Side and top put the muzzle on the left.
    [| "SIDE (from right, muzzle left)", fun (p: Vector3) -> Vector3(p.Z, p.Y, p.X)
       "TOP (from above, muzzle left)", fun p -> Vector3(p.Z, p.X, p.Y)
       "FRONT (down the barrel)", fun p -> Vector3(p.X, p.Y, -p.Z) |]

let pad = 26
let gridStep = 0.1f

let renderWeapon (name: string) (mesh: ProceduralMesh) (path: string) =
    let positions = mesh.Vertices |> Array.map (fun v -> v.Position)
    // One shared scale and one shared world origin across all three views, so
    // a part's position can be read off consistently from view to view.
    let lo =
        Vector3(positions |> Array.map (fun p -> p.X) |> Array.min,
                positions |> Array.map (fun p -> p.Y) |> Array.min,
                positions |> Array.map (fun p -> p.Z) |> Array.min)
    let hi =
        Vector3(positions |> Array.map (fun p -> p.X) |> Array.max,
                positions |> Array.map (fun p -> p.Y) |> Array.max,
                positions |> Array.map (fun p -> p.Z) |> Array.max)
    let span = hi - lo
    let scale = 1000.0f / (max span.Z 0.1f)
    let cellW = int (span.Z * scale) + pad * 2
    // Each view's vertical extent differs; size every cell to the tallest so
    // the sheet is a clean stack.
    let cellH = int ((max span.Y span.X) * scale) + pad * 2
    let width = cellW
    let height = cellH * views.Length
    let c = canvas width height (Vector3(0.09f, 0.10f, 0.12f))

    views
    |> Array.iteri (fun viewIndex (_, project) ->
        let originY = viewIndex * cellH
        let pLo, pHi = project lo, project hi
        let minRight = min pLo.X pHi.X
        let minUp = min pLo.Y pHi.Y
        let upSpan = abs (pHi.Y - pLo.Y)
        // Centre this view's own vertical extent inside the cell.
        let yPad = (float32 (cellH - pad * 2) - upSpan * scale) * 0.5f
        let toScreen (p: Vector3) =
            let v = project p
            Vector3(
                (v.X - minRight) * scale + float32 pad,
                float32 originY + float32 pad + yPad + (upSpan - (v.Y - minUp)) * scale,
                v.Z)
        // Grid first, so geometry paints over it.
        let grid = Vector3(0.17f, 0.19f, 0.22f)
        let axis = Vector3(0.30f, 0.26f, 0.24f)
        let mutable g = floor (minRight / gridStep) * gridStep
        while g <= minRight + abs (pHi.X - pLo.X) + gridStep do
            let x = int ((g - minRight) * scale) + pad
            let colour = if abs g < 0.001f then axis else grid
            for y in originY + 2 .. originY + cellH - 3 do plot c x y colour
            g <- g + gridStep
        let mutable u = floor (minUp / gridStep) * gridStep
        while u <= minUp + upSpan + gridStep do
            let y = originY + pad + int (yPad + (upSpan - (u - minUp)) * scale)
            let colour = if abs u < 0.001f then axis else grid
            for x in 2 .. cellW - 3 do plot c x y colour
            u <- u + gridStep

        for i in 0 .. mesh.Indices.Length / 3 - 1 do
            let v0 = mesh.Vertices[int mesh.Indices[i * 3]]
            let v1 = mesh.Vertices[int mesh.Indices[i * 3 + 1]]
            let v2 = mesh.Vertices[int mesh.Indices[i * 3 + 2]]
            // Lambert against a fixed over-the-shoulder key, plus ambient, so
            // adjoining parts at different angles stay distinguishable.
            let light = Vector3.Normalize(Vector3(0.4f, 0.8f, 0.45f))
            let lambert = max 0.0f (Vector3.Dot(Vector3.Normalize v0.Normal, light))
            let shade = 0.35f + 0.65f * lambert
            triangle c (toScreen v0.Position) (toScreen v1.Position) (toScreen v2.Position)
                (materialColour v0.MaterialId * shade)

        // A white tick bar in the top-left of each cell spanning 0.1 units,
        // so the grid pitch is unambiguous in the saved image.
        for x in pad .. pad + int (gridStep * scale) do
            plot c x (originY + 8) (Vector3.One)
            plot c x (originY + 9) (Vector3.One))

    writePng path width height c.Pixels
    printfn "%-18s %4d tris  bounds X[%+.3f %+.3f] Y[%+.3f %+.3f] Z[%+.3f %+.3f]  -> %s"
        name (mesh.Indices.Length / 3) lo.X hi.X lo.Y hi.Y lo.Z hi.Z (Path.GetFileName path)

let outDir = Path.Combine(__SOURCE_DIRECTORY__, "..", "debug", "gun-preview")
Directory.CreateDirectory outDir |> ignore

let requested = fsi.CommandLineArgs |> Array.skip 1 |> Array.tryHead |> Option.defaultValue "Kar98k"
let requestedPose =
    if fsi.CommandLineArgs.Length > 2 then
        Some(Single.Parse(fsi.CommandLineArgs[2], Globalization.CultureInfo.InvariantCulture))
    else None

let targets =
    if requested.Equals("all", StringComparison.OrdinalIgnoreCase) then Guns.names
    else
        match Guns.names |> Array.tryFind (fun n -> n.Equals(requested, StringComparison.OrdinalIgnoreCase)) with
        | Some found -> [| found |]
        | None ->
            match Guns.names |> Array.tryFind (fun n -> n.ToLowerInvariant().Contains(requested.ToLowerInvariant())) with
            | Some found -> [| found |]
            | None ->
                let available = String.Join(", ", Guns.names)
                failwith $"No weapon matching '%s{requested}'. Available: %s{available}"

printfn "Grid %.2f units. Muzzle points -Z (left in SIDE/TOP), up is +Y.\n" gridStep
for name in targets do
    let mesh = requestedPose |> Option.map (Guns.meshForPose name) |> Option.defaultWith (fun () -> Guns.meshFor name)
    let poseSuffix =
        requestedPose
        |> Option.map (fun pose -> "-pose-" + pose.ToString("0.00", Globalization.CultureInfo.InvariantCulture))
        |> Option.defaultValue ""
    renderWeapon name mesh (Path.Combine(outDir, name.Replace(" ", "-").ToLowerInvariant() + poseSuffix + ".png"))
