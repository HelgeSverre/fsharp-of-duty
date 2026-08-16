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
    let spawnSquad team count center = SpawnSquad(team, count, center)
    let objective text = Objective text
    let trigger condition action = MissionRule(condition, action)
