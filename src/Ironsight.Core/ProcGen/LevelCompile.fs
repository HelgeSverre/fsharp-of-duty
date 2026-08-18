namespace Ironsight.ProcGen

open System
open System.Collections.Generic
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module LevelCompile =
    let private gridCellSize = 4.0f

    let private cellCoordinate value = int (MathF.Floor(value / gridCellSize))

    let private compileBrushGrid (brushes: Brush array) =
        let cells = Dictionary<struct (int * int), ResizeArray<int>>()
        brushes
        |> Array.iteri (fun index item ->
            let minX, maxX = cellCoordinate item.Bounds.Min.X, cellCoordinate item.Bounds.Max.X
            let minZ, maxZ = cellCoordinate item.Bounds.Min.Z, cellCoordinate item.Bounds.Max.Z
            for z in minZ..maxZ do
                for x in minX..maxX do
                    let key = struct (x, z)
                    match cells.TryGetValue key with
                    | true, entries -> entries.Add index
                    | _ ->
                        let entries = ResizeArray<int>()
                        entries.Add index
                        cells[key] <- entries)
        { CellSize = gridCellSize
          Cells = cells |> Seq.map (fun pair -> pair.Key, pair.Value.ToArray()) |> Map.ofSeq }

    let brushesNear (position: Vector3) radius (level: Level) =
        let cell value = int (MathF.Floor(value / level.BrushGrid.CellSize))
        let minX, maxX = cell (position.X - radius), cell (position.X + radius)
        let minZ, maxZ = cell (position.Z - radius), cell (position.Z + radius)
        [| for z in minZ..maxZ do
               for x in minX..maxX do
                   match Map.tryFind (struct (x, z)) level.BrushGrid.Cells with
                   | Some indices -> yield! indices
                   | None -> () |]
        |> Array.distinct
        |> Array.map (fun index -> level.Brushes[index])

    let brushesAlongRay (origin: Vector3) (direction: Vector3) distance (level: Level) =
        let step = level.BrushGrid.CellSize * 0.45f
        let samples = max 1 (int (MathF.Ceiling(distance / step)))
        [| for sample in 0..samples do
               let point = origin + direction * (min distance (float32 sample * step))
               let x = int (MathF.Floor(point.X / level.BrushGrid.CellSize))
               let z = int (MathF.Floor(point.Z / level.BrushGrid.CellSize))
               match Map.tryFind (struct (x, z)) level.BrushGrid.Cells with
               | Some indices -> yield! indices
               | None -> () |]
        |> Array.distinct
        |> Array.map (fun index -> level.Brushes[index])

    /// The twelve triangles of a box, wound counter-clockwise seen from outside
    /// so the stored normal and the winding agree.
    let boxTriangles (bounds: Aabb) (material: Material) =
        let lo, hi = bounds.Min, bounds.Max
        let corner x y z = Vector3((if x = 0 then lo.X else hi.X), (if y = 0 then lo.Y else hi.Y), (if z = 0 then lo.Z else hi.Z))
        // Each face as four corners in winding order, paired with its outward normal.
        let quads =
            [| -Vector3.UnitZ, [| corner 1 0 0; corner 0 0 0; corner 0 1 0; corner 1 1 0 |]
               Vector3.UnitZ, [| corner 0 0 1; corner 1 0 1; corner 1 1 1; corner 0 1 1 |]
               -Vector3.UnitX, [| corner 0 0 0; corner 0 0 1; corner 0 1 1; corner 0 1 0 |]
               Vector3.UnitX, [| corner 1 0 1; corner 1 0 0; corner 1 1 0; corner 1 1 1 |]
               Vector3.UnitY, [| corner 0 1 1; corner 1 1 1; corner 1 1 0; corner 0 1 0 |]
               -Vector3.UnitY, [| corner 0 0 0; corner 1 0 0; corner 1 0 1; corner 0 0 1 |] |]
        quads
        |> Array.collect (fun (normal, points) ->
            [| { A = points[0]; B = points[1]; C = points[2]; Normal = normal; Material = material }
               { A = points[0]; B = points[2]; C = points[3]; Normal = normal; Material = material } |])

    let private triangleBounds (triangle: Tri) =
        { Min = Vector3.Min(triangle.A, Vector3.Min(triangle.B, triangle.C))
          Max = Vector3.Max(triangle.A, Vector3.Max(triangle.B, triangle.C)) }

    let compileCollision (triangles: Tri array) =
        let cells = Dictionary<struct (int * int), ResizeArray<int>>()
        triangles
        |> Array.iteri (fun index triangle ->
            let bounds = triangleBounds triangle
            let minX, maxX = cellCoordinate bounds.Min.X, cellCoordinate bounds.Max.X
            let minZ, maxZ = cellCoordinate bounds.Min.Z, cellCoordinate bounds.Max.Z
            for z in minZ..maxZ do
                for x in minX..maxX do
                    let key = struct (x, z)
                    match cells.TryGetValue key with
                    | true, entries -> entries.Add index
                    | _ ->
                        let entries = ResizeArray<int>()
                        entries.Add index
                        cells[key] <- entries)
        { Triangles = triangles
          CellSize = gridCellSize
          Cells = cells |> Seq.map (fun pair -> pair.Key, pair.Value.ToArray()) |> Map.ofSeq }

    let trianglesNear (position: Vector3) radius (level: Level) =
        let mesh = level.Collision
        let cell value = int (MathF.Floor(value / mesh.CellSize))
        let minX, maxX = cell (position.X - radius), cell (position.X + radius)
        let minZ, maxZ = cell (position.Z - radius), cell (position.Z + radius)
        [| for z in minZ..maxZ do
               for x in minX..maxX do
                   match Map.tryFind (struct (x, z)) mesh.Cells with
                   | Some indices -> yield! indices
                   | None -> () |]
        |> Array.distinct
        |> Array.map (fun index -> mesh.Triangles[index])

    /// Amanatides-Woo 2D traversal. The brush version point-samples every 1.8 m
    /// and can skip a cell the ray only clips, which matters more as maps gain
    /// elevation and long diagonal shots.
    let trianglesAlongRay (origin: Vector3) (direction: Vector3) distance (level: Level) =
        let mesh = level.Collision
        let size = mesh.CellSize
        let cell value = int (MathF.Floor(value / size))
        let visited = HashSet<struct (int * int)>()
        let collected = ResizeArray<int>()
        let mutable x = cell origin.X
        let mutable z = cell origin.Z
        let stepX = if direction.X > 0.0f then 1 elif direction.X < 0.0f then -1 else 0
        let stepZ = if direction.Z > 0.0f then 1 elif direction.Z < 0.0f then -1 else 0
        let inverse value = if MathF.Abs value < 0.000001f then Single.PositiveInfinity else size / MathF.Abs value
        let deltaX = inverse direction.X
        let deltaZ = inverse direction.Z
        let boundary current step axisOrigin axisDirection =
            if step = 0 then Single.PositiveInfinity
            else
                let edge = float32 (if step > 0 then current + 1 else current) * size
                (edge - axisOrigin) / axisDirection
        let mutable nextX = boundary x stepX origin.X direction.X
        let mutable nextZ = boundary z stepZ origin.Z direction.Z
        let mutable travelled = 0.0f
        let mutable guard = 0
        let take () =
            let key = struct (x, z)
            if visited.Add key then
                match Map.tryFind key mesh.Cells with
                | Some indices -> collected.AddRange indices
                | None -> ()
        take ()
        // The guard bounds a ray that runs exactly along a cell boundary.
        while travelled <= distance && guard < 4096 do
            guard <- guard + 1
            if nextX < nextZ then
                travelled <- nextX
                x <- x + stepX
                nextX <- nextX + deltaX
            else
                travelled <- nextZ
                z <- z + stepZ
                nextZ <- nextZ + deltaZ
            if travelled <= distance then take ()
        collected
        |> Seq.distinct
        |> Seq.map (fun index -> mesh.Triangles[index])
        |> Seq.toArray

    /// Highest walkable surface in an XZ column, with its normal. The shared
    /// ground probe for nav nodes, spawn snapping and cover placement, so all
    /// three agree about where the floor is.
    let surfaceColumn (mesh: CollisionMesh) (x: float32) (z: float32) =
        let cell value = int (MathF.Floor(value / mesh.CellSize))
        let indices =
            match Map.tryFind (struct (cell x, cell z)) mesh.Cells with
            | Some found -> found
            | None -> [||]
        // Start above anything the level can contain and cast straight down.
        let origin = Vector3(x, 100000.0f, z)
        let mutable bestHeight = Single.NegativeInfinity
        let mutable bestNormal = Vector3.UnitY
        for index in indices do
            let triangle = mesh.Triangles[index]
            if triangle.Normal.Y >= Tuning.MaxSlopeCosine then
                match MathEx.rayTriangle origin -Vector3.UnitY triangle.A triangle.B triangle.C with
                | ValueSome distance ->
                    let height = origin.Y - distance
                    if height > bestHeight then
                        bestHeight <- height
                        bestNormal <- triangle.Normal
                | ValueNone -> ()
        if Single.IsNegativeInfinity bestHeight then ValueNone else ValueSome(struct (bestHeight, bestNormal))

    let private brush (minimum: Vector3) (maximum: Vector3) (material: Material) : Brush =
        { Bounds = { Min = minimum; Max = maximum }; Material = material }

    let private lineBrush (startPoint: Vector3) (endPoint: Vector3) (height: float32) (width: float32) material =
        let minimum = Vector3.Min(startPoint, endPoint) - Vector3(width * 0.5f, 0.0f, width * 0.5f)
        let maximum = Vector3.Max(startPoint, endPoint) + Vector3(width * 0.5f, height, width * 0.5f)
        brush minimum maximum material

    let private ruinBrushes (center: Vector3) (size: Vector2) (height: float32) (material: Material) condition =
        let halfX, halfZ = size.X * 0.5f, size.Y * 0.5f
        let thickness = 0.3f
        let bottom = center.Y
        let wallHeight = if condition = BlownOut then height * 0.7f else height
        let doorHalf = min 0.8f (size.X * 0.16f)
        let doorTop = bottom + min 2.25f (height * 0.62f)
        let pieces = ResizeArray<Brush>()
        // Side and rear walls establish the ruin volume. The street-facing wall
        // is split around a real doorway rather than emitted as a sealed box.
        pieces.Add(brush (Vector3(center.X - halfX, bottom, center.Z - halfZ)) (Vector3(center.X - halfX + thickness, bottom + wallHeight, center.Z + halfZ)) material)
        pieces.Add(brush (Vector3(center.X + halfX - thickness, bottom, center.Z - halfZ)) (Vector3(center.X + halfX, bottom + height, center.Z + halfZ)) material)
        pieces.Add(brush (Vector3(center.X - halfX, bottom, center.Z + halfZ - thickness)) (Vector3(center.X + halfX, bottom + wallHeight, center.Z + halfZ)) material)
        pieces.Add(brush (Vector3(center.X - halfX, bottom, center.Z - halfZ)) (Vector3(center.X - doorHalf, bottom + height, center.Z - halfZ + thickness)) material)
        pieces.Add(brush (Vector3(center.X + doorHalf, bottom, center.Z - halfZ)) (Vector3(center.X + halfX, bottom + wallHeight, center.Z - halfZ + thickness)) material)
        pieces.Add(brush (Vector3(center.X - doorHalf, doorTop, center.Z - halfZ)) (Vector3(center.X + doorHalf, bottom + height, center.Z - halfZ + thickness)) material)
        // Deep, dark recesses break up otherwise blank procedural facades. They
        // sit inside the wall skin, so they read as windows without adding a
        // separate texture or affecting the usable interior.
        let windowWidth = min 1.15f (size.X * 0.18f)
        let addFrontWindow x y width windowHeight =
            pieces.Add(
                brush
                    (Vector3(x - width * 0.5f, y - windowHeight * 0.5f, center.Z - halfZ - 0.025f))
                    (Vector3(x + width * 0.5f, y + windowHeight * 0.5f, center.Z - halfZ + 0.035f))
                    Metal)
        addFrontWindow (center.X - halfX * 0.52f) (bottom + 1.55f) windowWidth 1.20f
        if condition = Intact then
            addFrontWindow (center.X + halfX * 0.52f) (bottom + 1.55f) windowWidth 1.20f
        if height > 5.2f then
            addFrontWindow (center.X - halfX * 0.48f) (bottom + 4.15f) windowWidth 1.10f
            addFrontWindow (center.X + halfX * 0.48f) (bottom + 4.15f) windowWidth 1.10f
        // A damaged upper floor and lintel give the skyline layered depth.
        if height > 4.5f then
            let floorY = bottom + min 3.0f (height * 0.48f)
            let floorReach = if condition = BlownOut then center.X + halfX * 0.35f else center.X + halfX
            pieces.Add(brush (Vector3(center.X - halfX + thickness, floorY, center.Z - halfZ + thickness)) (Vector3(floorReach, floorY + 0.18f, center.Z + halfZ - thickness)) Wood)
        if condition = Intact then
            pieces.Add(brush (Vector3(center.X - halfX - 0.12f, bottom + height, center.Z - halfZ - 0.12f)) (Vector3(center.X + halfX + 0.12f, bottom + height + 0.24f, center.Z + halfZ + 0.12f)) Wood)
            pieces.Add(brush (Vector3(center.X + halfX * 0.48f, bottom + height + 0.20f, center.Z + halfZ * 0.20f)) (Vector3(center.X + halfX * 0.66f, bottom + height + 1.25f, center.Z + halfZ * 0.42f)) Brick)
        pieces.ToArray()

    let private faces (item: Brush) =
        let min, max = item.Bounds.Min, item.Bounds.Max
        let positions =
            [| Vector3(min.X, min.Y, min.Z); Vector3(max.X, min.Y, min.Z); Vector3(max.X, max.Y, min.Z); Vector3(min.X, max.Y, min.Z)
               Vector3(max.X, min.Y, max.Z); Vector3(min.X, min.Y, max.Z); Vector3(min.X, max.Y, max.Z); Vector3(max.X, max.Y, max.Z)
               Vector3(min.X, min.Y, max.Z); Vector3(min.X, min.Y, min.Z); Vector3(min.X, max.Y, min.Z); Vector3(min.X, max.Y, max.Z)
               Vector3(max.X, min.Y, min.Z); Vector3(max.X, min.Y, max.Z); Vector3(max.X, max.Y, max.Z); Vector3(max.X, max.Y, min.Z)
               Vector3(min.X, max.Y, min.Z); Vector3(max.X, max.Y, min.Z); Vector3(max.X, max.Y, max.Z); Vector3(min.X, max.Y, max.Z)
               Vector3(min.X, min.Y, max.Z); Vector3(max.X, min.Y, max.Z); Vector3(max.X, min.Y, min.Z); Vector3(min.X, min.Y, min.Z) |]
        let normals =
            [| -Vector3.UnitZ; Vector3.UnitZ; -Vector3.UnitX; Vector3.UnitX; Vector3.UnitY; -Vector3.UnitY |]
        let materialId = Materials.id item.Material
        Array.init 24 (fun index -> { Position = positions[index]; Normal = normals[index / 4]; MaterialId = materialId })

    let private compileMesh (brushes: Brush array) =
        let vertices = brushes |> Array.collect faces
        let indices =
            brushes
            |> Array.mapi (fun brushIndex _ ->
                Array.init 6 (fun face ->
                    let start = uint32 (brushIndex * 24 + face * 4)
                    // Match the outward normals and MeshGen.box winding. The
                    // previous reversed order exposed brush interiors whenever
                    // OpenGL back-face culling was enabled.
                    [| start; start + 2u; start + 1u; start; start + 3u; start + 2u |]) |> Array.concat)
            |> Array.concat
        vertices, indices

    let private compileNav (bounds: Aabb) (brushes: Brush array) (brushGrid: BrushGrid) (collision: CollisionMesh) =
        let spacing = 2.0f
        let minX, maxX = int (MathF.Ceiling(bounds.Min.X / spacing)), int (MathF.Floor(bounds.Max.X / spacing))
        let minZ, maxZ = int (MathF.Ceiling(bounds.Min.Z / spacing)), int (MathF.Floor(bounds.Max.Z / spacing))
        let positions = ResizeArray<Vector3>()
        let lookup = Dictionary<struct (int * int), int>()
        for z in minZ..maxZ do
            for x in minX..maxX do
                let flatPosition = Vector3(float32 x * spacing, 0.0f, float32 z * spacing)
                let gridX = int (MathF.Floor(flatPosition.X / brushGrid.CellSize))
                let gridZ = int (MathF.Floor(flatPosition.Z / brushGrid.CellSize))
                let nearby =
                    [| for dz in -1..1 do
                           for dx in -1..1 do
                               match Map.tryFind (struct (gridX + dx, gridZ + dz)) brushGrid.Cells with
                               | Some indices -> yield! indices
                               | None -> () |]
                    |> Array.distinct
                // A downward probe finds the walkable surface whatever height it
                // sits at. The old rule only accepted brushes resting on y = 0
                // and under 3.1 m, so anything stacked was invisible as ground
                // and the navmesh grew a hole across it.
                let groundY =
                    match surfaceColumn collision flatPosition.X flatPosition.Z with
                    | ValueSome(struct (height, _)) -> height
                    | ValueNone -> 0.0f
                let position = Vector3(flatPosition.X, groundY, flatPosition.Z)
                let blocked =
                    nearby
                    |> Array.exists (fun index ->
                        brushes[index].Bounds.Max.Y > groundY + 0.05f
                        && MathEx.capsuleIntersectsAabb Tuning.PlayerRadius Tuning.StandingHeight position brushes[index].Bounds)
                if not blocked then
                    lookup[struct (x, z)] <- positions.Count
                    positions.Add position
        positions
        |> Seq.mapi (fun index position ->
            let x, z = int (MathF.Round(position.X / spacing)), int (MathF.Round(position.Z / spacing))
            let neighbours =
                [| struct (x - 1, z); struct (x + 1, z); struct (x, z - 1); struct (x, z + 1) |]
                |> Array.choose (fun key ->
                    match lookup.TryGetValue key with
                    | true, value ->
                        let other = positions[value]
                        let rise = abs (other.Y - position.Y)
                        // A step is climbable outright. Anything taller is only
                        // linked when the ground between the two nodes actually
                        // ramps: a 2 m rise over 2 m is a 45 degree slope if the
                        // midpoint is halfway up, and a wall if it is not.
                        let climbable =
                            rise <= 0.45f
                            || (rise <= spacing * MathF.Tan(Tuning.MaxSlopeAngle * MathF.PI / 180.0f)
                                && (match surfaceColumn collision ((position.X + other.X) * 0.5f) ((position.Z + other.Z) * 0.5f) with
                                    | ValueSome(struct (midHeight, _)) -> abs (midHeight - (position.Y + other.Y) * 0.5f) <= 0.35f
                                    | ValueNone -> false))
                        if climbable then Some value else None
                    | _ -> None)
            { Position = positions[index]; Neighbours = neighbours })
        |> Seq.toArray

    let compile (spec: LevelSpec) =
        let streets = spec.Items |> List.choose (function Street(length, width, surface) -> Some(length, width, surface) | _ -> None)
        let length, width, surface = streets |> List.tryHead |> Option.defaultValue (40.0f, 16.0f, Mud)
        let bounds = { Min = Vector3(-width, 0.0f, -length * 0.5f); Max = Vector3(width, 8.0f, length * 0.5f) }
        let brushes = ResizeArray<Brush>()
        brushes.Add(brush (Vector3(-width, -0.25f, -length * 0.5f)) (Vector3(width, 0.0f, length * 0.5f)) surface)
        let covers = ResizeArray<CoverPoint>()
        let spawns = ResizeArray<struct (Team option * Vector3)>()
        let mountedGuns = ResizeArray<MountedGun>()
        let coverSideForTeam team midpoint lineNormal =
            let nearestOwnedSpawn =
                spec.Items
                |> List.choose (function SpawnSquad(owner, _, center) when owner = team -> Some center | _ -> None)
                |> List.sortBy (fun center -> Vector3.DistanceSquared(center, midpoint))
                |> List.tryHead
            match nearestOwnedSpawn with
            | Some spawn when Vector3.Dot(lineNormal, spawn - midpoint) < 0.0f -> -lineNormal
            | Some _ -> lineNormal
            // Preserve the established north/south convention for a reusable
            // level fragment that does not declare its own spawn markers.
            | None -> if team = Allies then lineNormal else -lineNormal
        for item in spec.Items do
            match item with
            | Street _ | Objective _ | MissionRule _ -> ()
            | Block(center, size, material) -> brushes.Add(brush (center - size * 0.5f) (center + size * 0.5f) material)
            | Ruin(center, size, height, facade, condition) -> brushes.AddRange(ruinBrushes center size height facade condition)
            | SandbagLine(startPoint, endPoint, owner) ->
                brushes.Add(lineBrush startPoint endPoint 1.15f 0.55f Sandbag)
                let direction = MathEx.normalizedOrZero (endPoint - startPoint)
                let lineNormal = Vector3(-direction.Z, 0.0f, direction.X)
                let midpoint = (startPoint + endPoint) * 0.5f
                let defensiveSides =
                    match owner with
                    | Some team -> [| coverSideForTeam team midpoint lineNormal |]
                    | None -> [| lineNormal; -lineNormal |]
                let length = Vector3.Distance(startPoint, endPoint)
                for index in 0..int (length / 1.5f) do
                    let onLine = Vector3.Lerp(startPoint, endPoint, float32 index / max 1.0f (length / 1.5f))
                    for side in defensiveSides do
                        covers.Add
                            { Pos = onLine + side * 0.72f
                              PeekDir = -side
                              Crouch = true
                              Owner = owner }
            | FenceLine(startPoint, endPoint) ->
                let length = Vector3.Distance(startPoint, endPoint)
                let postCount = max 1 (int (MathF.Ceiling(length / 2.2f)))
                for index in 0..postCount do
                    let position = Vector3.Lerp(startPoint, endPoint, float32 index / float32 postCount)
                    brushes.Add(brush (position - Vector3(0.07f, 0.0f, 0.07f)) (position + Vector3(0.07f, 1.15f, 0.07f)) Wood)
                brushes.Add(lineBrush (startPoint + Vector3.UnitY * 0.36f) (endPoint + Vector3.UnitY * 0.36f) 0.10f 0.10f Wood)
                brushes.Add(lineBrush (startPoint + Vector3.UnitY * 0.83f) (endPoint + Vector3.UnitY * 0.83f) 0.10f 0.10f Wood)
            | Trench(startPoint, endPoint, trenchWidth) ->
                let direction = MathEx.normalizedOrZero (endPoint - startPoint)
                let side = Vector3(-direction.Z, 0.0f, direction.X) * (trenchWidth * 0.5f + 0.25f)
                brushes.Add(lineBrush (startPoint + side) (endPoint + side) 0.8f 0.5f Mud)
                brushes.Add(lineBrush (startPoint - side) (endPoint - side) 0.8f 0.5f Mud)
            | Mg42(position, facing, owner) ->
                let forward = MathEx.yawForward facing
                let side = MathEx.yawRight facing
                let pivot = position + Vector3.UnitY * 1.03f
                // Receiver, long barrel, shield, and splayed tripod are emitted
                // as ordinary brushes so render, collision and shadowing agree.
                brushes.Add(brush (pivot - Vector3(0.20f, 0.13f, 0.24f)) (pivot + Vector3(0.20f, 0.13f, 0.24f)) Metal)
                brushes.Add(lineBrush (pivot + forward * 0.15f) (pivot + forward * 1.48f) 0.09f 0.10f Metal)
                brushes.Add(lineBrush (pivot - forward * 0.08f - side * 0.58f) (pivot - forward * 0.08f + side * 0.58f) 0.52f 0.10f Metal)
                for foot in [ position - forward * 0.52f - side * 0.48f; position - forward * 0.52f + side * 0.48f; position + forward * 0.42f ] do
                    brushes.Add(lineBrush (pivot - Vector3.UnitY * 0.12f) foot 0.08f 0.09f Metal)
                covers.Add { Pos = position - forward * 0.45f; PeekDir = forward; Crouch = true; Owner = Some owner }
                mountedGuns.Add { Position = position - forward * 0.45f; Facing = facing; Team = owner }
            | SpawnSquad(team, count, center) ->
                for index in 0..count - 1 do
                    let offset =
                        if index = 0 then Vector3.Zero
                        else
                            let rank = float32 (index - 1)
                            let angle = rank * 2.3999632f
                            let radius = 2.0f + MathF.Sqrt(rank) * 0.85f
                            Vector3(MathF.Cos(angle) * radius, 0.0f, MathF.Sin(angle) * radius)
                    let flat = center + offset
                    // Snapped to the ground once the collision mesh exists, below.
                    spawns.Add(struct (Some team, Vector3(flat.X, 0.0f, flat.Z)))
        let brushArray = brushes.ToArray()
        let brushGrid = compileBrushGrid brushArray
        let vertices, indices = compileMesh brushArray
        let collision = brushArray |> Array.collect (fun item -> boxTriangles item.Bounds item.Material) |> compileCollision
        // The vertical extent follows the content rather than a fixed 8 m lid,
        // so a bluff or a below-sea-level beach is expressible. Headroom above
        // the tallest brush keeps the top of it reachable.
        let worldBounds =
            let lowest = brushArray |> Array.fold (fun acc item -> min acc item.Bounds.Min.Y) 0.0f
            let highest = brushArray |> Array.fold (fun acc item -> max acc item.Bounds.Max.Y) 0.0f
            { Min = Vector3(bounds.Min.X, lowest, bounds.Min.Z)
              Max = Vector3(bounds.Max.X, max bounds.Max.Y (highest + 4.0f), bounds.Max.Z) }
        { Name = spec.Name
          Revision = 0
          Bounds = worldBounds
          Brushes = brushArray
          BrushGrid = brushGrid
          Collision = collision
          // Spawns and cover both ride on the ground probe, so all three of
          // them, nav included, agree about where the floor is. Cover used to
          // keep whatever Y the DSL author wrote, which left it buried at one
          // end of a slope and floating at the other.
          Cover =
            covers.ToArray()
            |> Array.map (fun point ->
                match surfaceColumn collision point.Pos.X point.Pos.Z with
                | ValueSome(struct (height, _)) -> { point with Pos = Vector3(point.Pos.X, height, point.Pos.Z) }
                | ValueNone -> point)
          Spawns =
            spawns.ToArray()
            |> Array.map (fun struct (team, position) ->
                match surfaceColumn collision position.X position.Z with
                | ValueSome(struct (height, _)) -> struct (team, Vector3(position.X, height, position.Z))
                | ValueNone -> struct (team, position))
          MountedGuns = mountedGuns.ToArray()
          MissionRules =
            spec.Items
            |> List.choose (function MissionRule(condition, action) -> Some { Condition = condition; Action = action; Fired = false } | _ -> None)
            |> List.toArray
          Nav = compileNav bounds brushArray brushGrid collision
          Vertices = vertices
          Indices = indices }

    let rebuild brushes (level: Level) =
        let brushGrid = compileBrushGrid brushes
        let rebuiltCollision = brushes |> Array.collect (fun item -> boxTriangles item.Bounds item.Material) |> compileCollision
        let vertices, indices = compileMesh brushes
        // Bump the revision so renderers holding cached geometry re-upload.
        { level with
            Revision = level.Revision + 1
            Brushes = brushes
            BrushGrid = brushGrid
            Collision = rebuiltCollision
            Nav = compileNav level.Bounds brushes brushGrid rebuiltCollision
            Vertices = vertices
            Indices = indices }
