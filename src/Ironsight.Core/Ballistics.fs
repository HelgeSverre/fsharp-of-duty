namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

type CharacterRegion = Head | Torso | Legs

type TraceHit =
    | SurfaceHit of distance: float32 * exitDistance: float32 * normal: Vector3 * brush: Brush
    | SoldierHit of distance: float32 * soldierIndex: int * region: CharacterRegion

[<RequireQualifiedAccess>]
module Ballistics =
    let directionFromAngles yaw pitch (offset: Vector2) =
        let forward =
            Vector3(
                MathF.Sin yaw * MathF.Cos pitch,
                MathF.Sin pitch,
                -MathF.Cos yaw * MathF.Cos pitch)
        MathEx.normalizedOrZero (forward + MathEx.yawRight yaw * offset.X + Vector3.UnitY * offset.Y)

    let private muzzleDistance weaponName =
        match weaponName with
        | "M1911" -> 0.58f
        | "Thompson" -> 0.92f
        | "M1897 Trench Gun" -> 1.18f
        | _ -> 1.10f

    let stanceOffset = function
        | Standing -> 0.0f
        | Crouched -> 0.34f
        | Prone -> 0.90f

    let playerMuzzleOrigin (player: Player) weaponName =
        let eyeHeight =
            match player.Stance with
            | Standing -> 1.62f
            | Crouched -> 1.18f
            | Prone -> 0.55f
        let forward = directionFromAngles player.Yaw player.Pitch Vector2.Zero
        let right = MathEx.yawRight player.Yaw
        let hip = 1.0f - player.Ads
        player.Position
        + Vector3.UnitY * (eyeHeight - 0.14f - 0.10f * hip)
        + right * (0.20f * hip)
        + forward * muzzleDistance weaponName

    let soldierMuzzleOrigin (soldier: Soldier) =
        let forward = directionFromAngles soldier.Facing 0.0f Vector2.Zero
        soldier.Position + Vector3(0.0f, 1.42f, 0.0f) + forward * muzzleDistance soldier.Weapon.Class.Name

    let private materialResistance = function
        | Wood -> 1.0f
        | Plaster -> 1.4f
        | Sandbag -> 2.2f
        | Snow | Mud -> 3.0f
        | Brick -> 8.0f
        | Metal -> 20.0f
        | UniformOlive | UniformFeldgrau | Skin -> 0.5f

    let private soldierHit origin direction index (soldier: Soldier) =
        if soldier.Health <= Units.health 0.0f then None
        else
            let crouch = stanceOffset soldier.Stance
            let capsule low high radius =
                MathEx.rayCapsule origin direction
                    (soldier.Position + Vector3(0.0f, low - crouch, 0.0f))
                    (soldier.Position + Vector3(0.0f, high - crouch, 0.0f)) radius
            let hits =
                [ capsule 1.62f 1.85f 0.13f
                  capsule 0.85f 1.55f 0.26f
                  capsule 0.10f 0.85f 0.20f ]
                |> List.choose id
            match hits with
            | [] -> None
            | distances ->
                let distance = List.min distances
                let height = (origin + direction * distance).Y - soldier.Position.Y
                let torsoTop = 1.55f - crouch
                let torsoBottom = 0.85f - crouch
                let region =
                    if height >= torsoTop then Head
                    elif height >= torsoBottom then Torso
                    else Legs
                Some(SoldierHit(distance, index, region))

    let traceFilteredExcluding canHit excludeIndexes origin direction (level: Level) (soldiers: Soldier array) =
        let surfaceHits =
            LevelCompile.brushesAlongRay origin direction 200.0f level
            |> Array.choose (fun item ->
                MathEx.rayAabb origin direction item.Bounds
                |> Option.map (fun struct (entry, exit, normal) -> SurfaceHit(entry, exit, normal, item)))
        let soldierHits =
            soldiers
            |> Array.mapi (fun index soldier ->
                if canHit soldier && not (Set.contains index excludeIndexes) then soldierHit origin direction index soldier else None)
            |> Array.choose id
        Array.append surfaceHits soldierHits
        |> function
            | [||] -> None
            | hits -> Some(Array.minBy (function SurfaceHit(distance, _, _, _) | SoldierHit(distance, _, _) -> distance) hits)

    let traceFiltered canHit origin direction (level: Level) (soldiers: Soldier array) =
        traceFilteredExcluding canHit Set.empty origin direction level soldiers

    let trace origin direction level soldiers = traceFiltered (fun _ -> true) origin direction level soldiers

    let applyShotFiltered canHit (origin: Vector3) (direction: Vector3) (damage: float32<hp>) (penetration: float32) (headshotMultiplier: float32) (level: Level) (soldiers: Soldier array) =
        let mutable currentOrigin = origin
        let mutable budget = penetration
        let mutable currentDamage = damage
        let mutable remainingRange = 200.0f
        let mutable updated = Array.copy soldiers
        let events = ResizeArray<GameEvent>()
        let mutable tracing = true
        let mutable penetrations = 0
        let mutable passedThrough = Set.empty
        while tracing && remainingRange > 0.0f && penetrations <= 4 do
            match traceFilteredExcluding canHit passedThrough currentOrigin direction level updated with
            | None -> tracing <- false
            | Some(SoldierHit(distance, index, region)) when distance <= remainingRange ->
                let multiplier = match region with Head -> headshotMultiplier | Torso -> 1.0f | Legs -> 0.65f
                let victim = updated[index]
                let health = max (Units.health 0.0f) (victim.Health - currentDamage * multiplier)
                let lethal = health <= Units.health 0.0f
                let headshot = region = Head
                let deathBehavior = if headshot then DyingHeadshot(Units.seconds 0.0f) else Dying(Units.seconds 0.0f)
                updated[index] <- { victim with Health = health; Behavior = if lethal then deathBehavior else victim.Behavior }
                let hitPosition = currentOrigin + direction * distance
                events.Add(HitConfirmed(victim.Id, lethal))
                events.Add(BloodImpact(hitPosition, direction, headshot))
                if lethal && headshot then events.Add(HeadGib(hitPosition, direction))
                if budget >= Tuning.BodyPenetrationCost then
                    budget <- budget - Tuning.BodyPenetrationCost
                    currentDamage <- currentDamage * Tuning.BodyDamageRetention
                    currentOrigin <- currentOrigin + direction * (distance + 0.15f)
                    remainingRange <- remainingRange - (distance + 0.15f)
                    penetrations <- penetrations + 1
                    passedThrough <- Set.add index passedThrough
                else tracing <- false
            | Some(SurfaceHit(distance, exitDistance, normal, item)) when distance <= remainingRange ->
                let impactPosition = currentOrigin + direction * distance
                events.Add(Impact(impactPosition, normal, item.Material))
                let thicknessCentimetres = max 0.1f ((exitDistance - distance) * 100.0f)
                let cost = thicknessCentimetres * materialResistance item.Material
                if budget >= cost && exitDistance > distance then
                    budget <- budget - cost
                    currentDamage <- currentDamage * 0.72f
                    let advance = exitDistance + 0.002f
                    currentOrigin <- currentOrigin + direction * advance
                    remainingRange <- remainingRange - advance
                    penetrations <- penetrations + 1
                else tracing <- false
            | _ -> tracing <- false
        updated, List.ofSeq events

    let applyShot origin direction damage penetration headshotMultiplier level soldiers =
        applyShotFiltered (fun _ -> true) origin direction damage penetration headshotMultiplier level soldiers

    let lineOfSight (origin: Vector3) (target: Vector3) (level: Level) =
        let offset = target - origin
        let distance = offset.Length()
        if distance < 0.0001f then true
        else
            let direction = offset / distance
            LevelCompile.brushesAlongRay origin direction distance level
            |> Array.forall (fun item ->
                match MathEx.rayAabb origin direction item.Bounds with
                | Some(struct (entry, _, _)) when entry < distance -> false
                | _ -> true)
