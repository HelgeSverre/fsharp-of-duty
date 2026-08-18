namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

type CharacterRegion = Head | Torso | Legs

type TraceHit =
    | SurfaceHit of distance: float32 * exitDistance: float32 * normal: Vector3 * material: Material
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

    let stanceOffset = function
        | Standing -> 0.0f
        | Crouched -> 0.34f
        | Prone -> 0.90f

    /// Shared by the camera and the authoritative hit trace so what the player
    /// sees over, the bullet clears.
    let eyeHeight = function
        | Standing -> 1.62f
        | Crouched -> 1.15f
        | Prone -> 0.52f

    /// Authoritative shot origin: the eye/camera position. Hitscan games trace
    /// from here ("if I can see it, I can hit it") and only draw tracers from
    /// the muzzle; tracing from the barrel makes near cover eat shots the
    /// player can clearly see over.
    let playerEyeOrigin (player: Player) =
        player.Position + Vector3.UnitY * eyeHeight player.Stance

    /// Cosmetic shot origin: where tracers and muzzle flash appear to start.
    /// Never used for hit detection.
    let playerMuzzleOrigin (player: Player) (weapon: WeaponClass) =
        let forward = directionFromAngles player.Yaw player.Pitch Vector2.Zero
        let right = MathEx.yawRight player.Yaw
        let hip = 1.0f - player.Ads
        player.Position
        + Vector3.UnitY * (eyeHeight player.Stance - 0.14f - 0.10f * hip)
        + right * (0.20f * hip)
        + forward * weapon.MuzzleDistance

    let soldierMuzzleOrigin (soldier: Soldier) =
        let forward = directionFromAngles soldier.Facing 0.0f Vector2.Zero
        soldier.Position + Vector3(0.0f, 1.42f, 0.0f) + forward * soldier.Weapon.Class.MuzzleDistance

    let private materialResistance = function
        | Wood -> 1.0f
        | Plaster -> 1.4f
        | Sandbag -> 2.2f
        | Snow | Mud -> 3.0f
        | Brick -> 8.0f
        | Metal -> 20.0f
        | UniformOlive | UniformFeldgrau | Skin -> 0.5f
        // Render-only; never in the collision mesh, kept for match totality.
        | Water -> 0.0f

    // Head/torso/legs capsule bounds, single source of truth: shared by the
    // authoritative player-shot trace (soldierHit below) and the AI's
    // shot-at-player check (AiBrain.playerHitDistance).
    let private HeadLow, HeadHigh, HeadRadius = 1.62f, 1.85f, 0.13f
    let private TorsoLow, TorsoHigh, TorsoRadius = 0.85f, 1.55f, 0.26f
    let private LegsLow, LegsHigh, LegsRadius = 0.10f, 0.85f, 0.20f

    /// Crouch-corrected capsule bounds (low point, high point, radius) for a
    /// character at `position`, in head/torso/legs order.
    let hitCapsules stance (position: Vector3) : struct (Vector3 * Vector3 * float32) array =
        let crouch = stanceOffset stance
        let at height = position + Vector3(0.0f, height - crouch, 0.0f)
        [| struct (at HeadLow, at HeadHigh, HeadRadius)
           struct (at TorsoLow, at TorsoHigh, TorsoRadius)
           struct (at LegsLow, at LegsHigh, LegsRadius) |]

    let private soldierHit origin direction index (soldier: Soldier) =
        if soldier.Health <= Units.health 0.0f then None
        else
            let hits =
                hitCapsules soldier.Stance soldier.Position
                |> Array.choose (fun struct (low, high, radius) -> MathEx.rayCapsule origin direction low high radius)
            match hits with
            | [||] -> None
            | distances ->
                let distance = Array.min distances
                let crouch = stanceOffset soldier.Stance
                let height = (origin + direction * distance).Y - soldier.Position.Y
                let torsoTop = TorsoHigh - crouch
                let torsoBottom = TorsoLow - crouch
                let region =
                    if height >= torsoTop then Head
                    elif height >= torsoBottom then Torso
                    else Legs
                Some(SoldierHit(distance, index, region))

    let traceFilteredExcluding canHit excludeIndexes origin direction (level: Level) (soldiers: Soldier array) =
        // Triangle soup has no notion of "a solid", so thickness is recovered by
        // pairing the nearest front face with the next back face along the ray.
        // A front face with no matching exit — an open terrain surface — is
        // treated as unpenetrable rather than infinitely thin.
        let surfaceHits =
            let ordered =
                LevelCompile.trianglesAlongRay origin direction 200.0f level
                |> Array.choose (fun triangle ->
                    match MathEx.rayTriangle origin direction triangle.A triangle.B triangle.C with
                    | ValueSome distance when distance <= 200.0f -> Some(struct (distance, triangle))
                    | _ -> None)
                |> Array.sortBy (fun struct (distance, _) -> distance)
            let entry =
                ordered |> Array.tryFind (fun struct (_, triangle) -> Vector3.Dot(triangle.Normal, direction) < 0.0f)
            match entry with
            | None -> [||]
            | Some(struct (entryDistance, entryTriangle)) ->
                let exitDistance =
                    ordered
                    |> Array.tryFind (fun struct (distance, triangle) ->
                        distance > entryDistance + 0.0001f && Vector3.Dot(triangle.Normal, direction) > 0.0f)
                    |> function
                        | Some(struct (distance, _)) -> distance
                        | None -> Single.PositiveInfinity
                [| SurfaceHit(entryDistance, exitDistance, entryTriangle.Normal, entryTriangle.Material) |]
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
            | Some(SurfaceHit(distance, exitDistance, normal, material)) when distance <= remainingRange ->
                let impactPosition = currentOrigin + direction * distance
                events.Add(Impact(impactPosition, normal, material))
                let thicknessCentimetres = max 0.1f ((exitDistance - distance) * 100.0f)
                let cost = thicknessCentimetres * materialResistance material
                if budget >= cost && exitDistance > distance && Single.IsFinite exitDistance then
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
            LevelCompile.trianglesAlongRay origin direction distance level
            |> Array.forall (fun triangle ->
                match MathEx.rayTriangle origin direction triangle.A triangle.B triangle.C with
                | ValueSome entry when entry < distance -> false
                | _ -> true)
