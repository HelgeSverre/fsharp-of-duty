namespace Ironsight.ProcGen

open System.Numerics
open Ironsight

type RuinCondition = Intact | BlownOut

type LevelItem =
    | Street of length: float32 * width: float32 * surface: Material
    | Ruin of center: Vector3 * size: Vector2 * height: float32 * facade: Material * condition: RuinCondition
    | SandbagLine of startPoint: Vector3 * endPoint: Vector3 * owner: Team option
    | FenceLine of startPoint: Vector3 * endPoint: Vector3
    | Trench of startPoint: Vector3 * endPoint: Vector3 * width: float32
    | Mg42 of position: Vector3 * facing: float32 * owner: Team
    | Block of center: Vector3 * size: Vector3 * material: Material
    /// Arbitrary procedural geometry placed in the world: whatever MeshGen can
    /// build — cylinders, lathes, trusses, unions of all three — dropped in at
    /// a position and a yaw. This is how the level gets round and angled shapes
    /// out of a brush set that is otherwise axis-aligned boxes.
    ///
    /// It contributes rendering and collision, but NOT a nav obstruction: the
    /// navmesh only consults brushes. Put a Block inside anything a bot must
    /// path around; the prop shell hides it.
    | Prop of mesh: ProceduralMesh * position: Vector3 * yaw: float32
    /// A complete authored world mesh with explicit bounds. Unlike a Prop it
    /// replaces the DSL's generated street floor: imported BSP geometry
    /// already contains every floor, wall, roof and underground surface.
    | StaticWorld of mesh: ProceduralMesh * bounds: Aabb * textureAtlas: LevelTextureAtlas option
    /// Independently removable authored geometry, currently GoldSrc glass
    /// brush models. Its stable source id is replicated when the piece breaks.
    | BreakableWorld of id: int * mesh: ProceduralMesh * bounds: Aabb
    /// A genuinely sloped surface between two heights, solid down to the ground
    /// beneath it. Walkable if the grade is inside the slope limit.
    | Ramp of startPoint: Vector3 * endPoint: Vector3 * width: float32 * material: Material
    /// A climbable ladder rising from `foot`, facing `facing` (the yaw you look
    /// along to stand at its base). Its rails and rungs are drawn but not solid:
    /// a ladder you collide with is a ladder you cannot stand inside.
    | Ladder of foot: Vector3 * height: float32 * facing: float32
    /// Organic terrain sampled from a height function over an XZ rectangle.
    /// `cells` is the sample count per side; the surface is closed by a skirt so
    /// it behaves as a solid for penetration.
    | Heightfield of center: Vector3 * size: Vector2 * cells: int * height: (float32 -> float32 -> float32) * material: Material
    /// Sea level. One per map; the surface is drawn across the whole level
    /// and anyone wading below it moves slower.
    | WaterPlane of height: float32
    | SpawnSquad of team: Team * count: int * center: Vector3
    | Objective of text: string
    | MissionRule of condition: TriggerCondition * action: ScriptAction

type LevelSpec =
    { Name: string
      Items: LevelItem list }

[<RequireQualifiedAccess>]
module LevelDsl =
    let level name items = { Name = name; Items = items }
    let street length width surface = Street(length, width, surface)
    let ruin center size height facade condition = Ruin(center, size, height, facade, condition)
    let sandbags startPoint endPoint owner = SandbagLine(startPoint, endPoint, owner)
    let fence startPoint endPoint = FenceLine(startPoint, endPoint)
    let trench startPoint endPoint width = Trench(startPoint, endPoint, width)
    let mg42 position facing owner = Mg42(position, facing, owner)
    let block center size material = Block(center, size, material)
    let prop mesh position yaw = Prop(mesh, position, yaw)
    let staticWorld mesh bounds = StaticWorld(mesh, bounds, None)
    let texturedStaticWorld mesh bounds textureAtlas = StaticWorld(mesh, bounds, Some textureAtlas)
    let breakableWorld id mesh bounds = BreakableWorld(id, mesh, bounds)
    let ramp startPoint endPoint width material = Ramp(startPoint, endPoint, width, material)
    let ladder foot height facing = Ladder(foot, height, facing)
    let heightfield center size cells height material = Heightfield(center, size, cells, height, material)
    let water height = WaterPlane height
    let spawnSquad team count center = SpawnSquad(team, count, center)
    let objective text = Objective text
    let trigger condition action = MissionRule(condition, action)
