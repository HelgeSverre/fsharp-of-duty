namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module SpecialProjectiles =
    [<Literal>]
    let PaintballSpeed = 90.0f

    [<Literal>]
    let DartSpeed = 72.0f

    [<Literal>]
    let RocketSpeed = 65.0f

    [<Literal>]
    let WaterSpeed = 32.0f

    [<Literal>]
    let NailSpeed = 115.0f

    [<Literal>]
    let HarpoonSpeed = 105.0f

    [<Literal>]
    let ArrowSpeed = 88.0f

    [<Literal>]
    let HarpoonRadius = 0.08f

    [<Literal>]
    let MaxSkeweredVictims = 3

    [<Literal>]
    let RocketArmingDistance = 5.0f

    [<Literal>]
    let RocketBlastRadius = 6.0f

    [<Literal>]
    let MaxBounces = 3

    [<Literal>]
    let FlameRange = 9.0f

    [<Literal>]
    let WaterDropletsPerPulse = 6

    [<Literal>]
    let WaterDamagePerDroplet = 0.125f

    [<Literal>]
    let BurnDamagePerSecond = 8.0f

    let WetDuration = Units.seconds 15.0f
    let BurnDuration = Units.seconds 4.0f
    let IgniteExposure = Units.seconds 0.35f

    // Angles are measured from the surface normal. A paintball bursts when it
    // arrives within 45 degrees of head-on; darts and nails need a straighter
    // impact to stick instead of ricocheting.
    let private paintBurstIncidence = MathF.Cos(45.0f * MathF.PI / 180.0f)
    let private stickIncidence = MathF.Cos(35.0f * MathF.PI / 180.0f)

    let paintPalette =
        [| PaintRed; PaintBlue; PaintGreen; PaintYellow; PaintPurple; PaintOrange |]

    let choosePaintColor (rng: byref<Rng.State>) =
        let index = min (paintPalette.Length - 1) (int (Rng.nextFloat32 &rng * float32 paintPalette.Length))
        paintPalette[index]

    let chooseNextPaintColor current (rng: byref<Rng.State>) =
        let choices = paintPalette |> Array.filter ((<>) current)
        let index = min (choices.Length - 1) (int (Rng.nextFloat32 &rng * float32 choices.Length))
        choices[index]

    let spawn owner color mechanism position direction =
        let make kind speed remaining =
            Some
                { Owner = owner
                  Kind = kind
                  Position = position
                  Velocity = MathEx.normalizedOrZero direction * speed
                  DistanceTravelled = 0.0f
                  Bounces = 0
                  Remaining = Units.seconds remaining }
        match mechanism with
        | Paintball -> make (PaintBall color) PaintballSpeed 4.0f
        | FoamDart -> make NerfDart DartSpeed 5.0f
        | Rocket -> make BazookaRocket RocketSpeed 8.0f
        | Nail -> make NailRound NailSpeed 4.0f
        | Harpoon -> make (HarpoonRound []) HarpoonSpeed 8.0f
        | Hitscan | FlameJet | WaterJet | Bow -> None

    let spawnArrow owner damage power position direction =
        { Owner = owner
          Kind = ArrowRound damage
          Position = position
          Velocity = MathEx.normalizedOrZero direction * (ArrowSpeed * (0.55f + 0.45f * MathEx.clamp01 power))
          DistanceTravelled = 0.0f
          Bounces = 0
          Remaining = Units.seconds 6.0f }

    let spawnWaterBurst owner position direction (rng: byref<Rng.State>) =
        let forward = MathEx.normalizedOrZero direction
        let reference = if abs forward.Y < 0.9f then Vector3.UnitY else Vector3.UnitX
        let right = Vector3.Cross(forward, reference) |> MathEx.normalizedOrZero
        let up = Vector3.Cross(right, forward) |> MathEx.normalizedOrZero
        let droplets = ResizeArray<SpecialProjectile>()
        for _ in 1..WaterDropletsPerPulse do
            let angle = Rng.nextFloat32 &rng * MathF.Tau
            let radius = MathF.Sqrt(Rng.nextFloat32 &rng) * 0.032f
            let spread = right * (MathF.Cos angle * radius) + up * (MathF.Sin angle * radius)
            droplets.Add
                { Owner = owner
                  Kind = WaterDroplet
                  Position = position
                  Velocity = MathEx.normalizedOrZero (forward + spread) * WaterSpeed
                  DistanceTravelled = 0.0f
                  Bounces = 0
                  Remaining = Units.seconds 2.0f }
        droplets.ToArray()

    type private Collision =
        | Surface of distance: float32 * normal: Vector3
        | Character of distance: float32 * index: int * normal: Vector3
        | LocalPlayer of distance: float32 * normal: Vector3

    type private AttachedMark =
        | PaintMark of Material
        | DartMark
        | NailMark
        | ArrowMark of direction: Vector3

    let private emptyStatus =
        { WetFor = Units.seconds 0.0f
          BurningFor = Units.seconds 0.0f
          Heat = Units.seconds 0.0f
          BurnOwner = None }

    let private nearestCapsuleHit origin direction maxDistance collisionRadius stance position =
        Ballistics.hitCapsules stance position
        |> Array.choose (fun struct (low, high, radius) ->
            match MathEx.rayCapsule origin direction low high (radius + collisionRadius) with
            | Some distance when distance <= maxDistance -> Some distance
            | _ -> None)
        |> function
            | [||] -> None
            | distances -> Some(Array.min distances)

    let private ownerTeam owner (player: Player) (soldiers: Soldier array) =
        if owner = player.Id then Some Allies
        else soldiers |> Array.tryFind (fun soldier -> soldier.Id = owner) |> Option.map _.Team

    let private trace owner origin direction distance collisionRadius hitsAnyTeam (level: Level) (player: Player) (soldiers: Soldier array) =
        let hits = ResizeArray<Collision>()
        let sourceTeam = ownerTeam owner player soldiers
        LevelCompile.trianglesAlongRay origin direction (distance + 0.06f) level
        |> Array.iter (fun triangle ->
            if Vector3.Dot(triangle.Normal, direction) < 0.0f then
                match MathEx.rayTriangle origin direction triangle.A triangle.B triangle.C with
                | ValueSome hitDistance when hitDistance <= distance + 0.06f -> hits.Add(Surface(hitDistance, triangle.Normal))
                | _ -> ())
        soldiers
        |> Array.iteri (fun index soldier ->
            let hostile = hitsAnyTeam || (sourceTeam |> Option.forall ((<>) soldier.Team))
            if soldier.IsAlive && soldier.Id <> owner && hostile then
                match nearestCapsuleHit origin direction distance collisionRadius soldier.Stance soldier.Position with
                | Some hitDistance ->
                    let hit = origin + direction * hitDistance
                    let centre = soldier.Position + Vector3(0.0f, 1.0f - Ballistics.stanceOffset soldier.Stance, 0.0f)
                    let normal = MathEx.normalizedOrZero (hit - centre)
                    hits.Add(Character(hitDistance, index, if normal.LengthSquared() < 0.01f then -direction else normal))
                | None -> ())
        let hostileToPlayer = hitsAnyTeam || (sourceTeam |> Option.exists ((=) Axis))
        if player.IsAlive && player.Id <> owner && hostileToPlayer then
            match nearestCapsuleHit origin direction distance collisionRadius player.Stance player.Position with
            | Some hitDistance ->
                let hit = origin + direction * hitDistance
                let centre = player.Position + Vector3(0.0f, 1.0f - Ballistics.stanceOffset player.Stance, 0.0f)
                let normal = MathEx.normalizedOrZero (hit - centre)
                hits.Add(LocalPlayer(hitDistance, if normal.LengthSquared() < 0.01f then -direction else normal))
            | None -> ()
        if hits.Count = 0 then None
        else
            hits
            |> Seq.minBy (function Surface(distance, _) | Character(distance, _, _) | LocalPlayer(distance, _) -> distance)
            |> Some

    let private nearestSurface origin direction distance (level: Level) =
        LevelCompile.trianglesAlongRay origin direction distance level
        |> Array.choose (fun triangle ->
            if Vector3.Dot(triangle.Normal, direction) >= 0.0f then None
            else
                match MathEx.rayTriangle origin direction triangle.A triangle.B triangle.C with
                | ValueSome hitDistance when hitDistance <= distance -> Some(hitDistance, triangle.Normal)
                | _ -> None)
        |> Array.sortBy fst
        |> Array.tryHead

    let private lineClear (level: Level) fromPoint toPoint =
        Ballistics.lineOfSight (fromPoint + Vector3.UnitY * 0.03f) toPoint level

    let private rocketDamage distance =
        if distance >= RocketBlastRadius then Units.health 0.0f
        elif distance <= 1.5f then Units.health 250.0f
        else
            let t = (distance - 1.5f) / (RocketBlastRadius - 1.5f)
            Units.health (250.0f + (25.0f - 250.0f) * t)

    let private explode position owner (level: Level) (player: Player) (soldiers: Soldier array) =
        let events = ResizeArray<GameEvent>()
        events.Add(Explosion(position, RocketBlastRadius))
        let updatedSoldiers =
            soldiers
            |> Array.map (fun soldier ->
                let torso = soldier.Position + Vector3(0.0f, 1.0f, 0.0f)
                let distance = Vector3.Distance(position, torso)
                let damage = rocketDamage distance
                if soldier.IsAlive && damage > Units.health 0.0f && lineClear level position torso then
                    let health = max (Units.health 0.0f) (soldier.Health - damage)
                    let lethal = health <= Units.health 0.0f
                    events.Add(HitConfirmed(soldier.Id, lethal))
                    if lethal then events.Add(Kill(Some owner, soldier.Id, "Bazooka", false))
                    { soldier with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else soldier.Behavior }
                else soldier)
        let torso = player.Position + Vector3(0.0f, 1.0f, 0.0f)
        let distance = Vector3.Distance(position, torso)
        let damage = rocketDamage distance
        let updatedPlayer =
            if player.IsAlive && damage > Units.health 0.0f && lineClear level position torso then
                let health = max (Units.health 0.0f) (player.Health - damage)
                events.Add(PlayerHurt(MathEx.normalizedOrZero (torso - position), health))
                { player with Health = health; RegenIn = Tuning.RegenDelay }
            else player
        updatedPlayer, updatedSoldiers, List.ofSeq events

    let private markTarget (targetPosition: Vector3) targetId (hitPosition: Vector3) (normal: Vector3) mark =
        let offset =
            match mark with
            | PaintMark _ -> normal * 0.008f
            | DartMark -> normal * 0.025f
            | NailMark -> normal * 0.018f
            | ArrowMark _ -> normal * 0.035f
        let position = hitPosition + offset
        let localOffset = position - targetPosition
        match mark with
        | PaintMark color -> PaintSplat(position, normal, color, Some targetId, localOffset)
        | DartMark -> StuckDart(position, normal, Some targetId, localOffset)
        | NailMark -> StuckNail(position, normal, Some targetId, localOffset)
        | ArrowMark direction -> StuckArrow(position, direction, Some targetId, localOffset)

    let private damageSoldier weapon damage owner hitPosition normal mark index (soldiers: Soldier array) (events: ResizeArray<GameEvent>) (marks: ResizeArray<PersistentMark>) =
        let updated = Array.copy soldiers
        let victim = updated[index]
        let health = max (Units.health 0.0f) (victim.Health - damage)
        let lethal = health <= Units.health 0.0f
        updated[index] <- { victim with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else victim.Behavior }
        events.Add(HitConfirmed(victim.Id, lethal))
        if lethal then events.Add(Kill(Some owner, victim.Id, weapon, false))
        mark |> Option.iter (fun value -> marks.Add(markTarget victim.Position victim.Id hitPosition normal value))
        updated

    let private soak target (position: Vector3) (statuses: Map<EntityId, ElementalStatus>) (events: ResizeArray<GameEvent>) =
        let current = Map.tryFind target statuses |> Option.defaultValue emptyStatus
        if current.BurningFor > Units.seconds 0.0f then events.Add(Extinguished(target, position))
        let next = { current with WetFor = WetDuration; BurningFor = Units.seconds 0.0f; Heat = Units.seconds 0.0f; BurnOwner = None }
        Map.add target next statuses

    let private refreshWetMark (targetPosition: Vector3) (targetId: EntityId option) (hitPosition: Vector3) (normal: Vector3) (marks: ResizeArray<PersistentMark>) =
        let position = hitPosition + normal * 0.009f
        let localOffset = position - targetPosition
        let comparisonPoint = if targetId.IsSome then localOffset else position
        let nearby =
            marks
            |> Seq.mapi (fun index mark -> index, mark)
            |> Seq.tryPick (fun (index, mark) ->
                match mark with
                | WetPatch(existing, _, existingTarget, existingOffset, _, _) when existingTarget = targetId ->
                    let existingPoint = if targetId.IsSome then existingOffset else existing
                    if Vector3.DistanceSquared(existingPoint, comparisonPoint) < 0.35f * 0.35f then Some(index, mark) else None
                | _ -> None)
        match nearby with
        | Some(index, WetPatch(existing, existingNormal, existingTarget, existingOffset, _, saturation)) ->
            marks[index] <- WetPatch(existing, existingNormal, existingTarget, existingOffset, WetDuration, min 1.0f (saturation + 0.18f))
        | _ -> marks.Add(WetPatch(position, normal, targetId, localOffset, WetDuration, 0.32f))

    let applyBackblast (_muzzle: Vector3) (forward: Vector3) (level: Level) (player: Player) (soldiers: Soldier array) =
        let rear = -MathEx.normalizedOrZero forward
        let origin = player.Position + Vector3.UnitY * 1.35f + MathEx.normalizedOrZero forward * 0.05f
        let events = ResizeArray<GameEvent>()
        events.Add(Backblast(origin, rear))
        let blocked =
            LevelCompile.trianglesAlongRay origin rear 1.5f level
            |> Array.exists (fun triangle ->
                Vector3.Dot(triangle.Normal, rear) < 0.0f
                && match MathEx.rayTriangle origin rear triangle.A triangle.B triangle.C with
                   | ValueSome distance -> distance <= 1.5f
                   | _ -> false)
        let player =
            if blocked then
                let health = max (Units.health 0.0f) (player.Health - Units.health 100.0f)
                events.Add(PlayerHurt(forward, health))
                { player with Health = health; RegenIn = Tuning.RegenDelay }
            else player
        let soldiers =
            soldiers
            |> Array.map (fun soldier ->
                let chest = soldier.Position + Vector3(0.0f, 1.0f, 0.0f)
                let offset = chest - origin
                let distance = offset.Length()
                let inside = distance > 0.05f && distance <= 3.0f && Vector3.Dot(Vector3.Normalize offset, rear) >= 0.72f
                if soldier.IsAlive && inside && lineClear level origin chest then
                    let damage = Units.health (100.0f * (1.0f - distance / 3.5f))
                    let health = max (Units.health 0.0f) (soldier.Health - damage)
                    let lethal = health <= Units.health 0.0f
                    events.Add(HitConfirmed(soldier.Id, lethal))
                    if lethal then events.Add(Kill(Some player.Id, soldier.Id, "Bazooka backblast", false))
                    { soldier with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else soldier.Behavior }
                else soldier)
        player, soldiers, List.ofSeq events

    let private flameContact owner target (position: Vector3) (statuses: Map<EntityId, ElementalStatus>) (events: ResizeArray<GameEvent>) =
        let current = Map.tryFind target statuses |> Option.defaultValue emptyStatus
        let next =
            if current.WetFor > Units.seconds 0.0f then
                { current with
                    WetFor = max (Units.seconds 0.0f) (current.WetFor - Units.seconds 0.3f)
                    BurningFor = Units.seconds 0.0f
                    Heat = Units.seconds 0.0f
                    BurnOwner = None }
            else
                let heat = current.Heat + Units.seconds 0.1f
                let ignites = current.BurningFor <= Units.seconds 0.0f && heat >= IgniteExposure
                if ignites then events.Add(Ignited(target, position))
                { current with
                    Heat = heat
                    BurningFor = if heat >= IgniteExposure then BurnDuration else current.BurningFor
                    BurnOwner = if heat >= IgniteExposure then Some owner else current.BurnOwner }
        Map.add target next statuses

    let applyFlameJet owner origin direction (level: Level) (player: Player) (soldiers: Soldier array) statuses =
        let forward = MathEx.normalizedOrZero direction
        let surface = nearestSurface origin forward FlameRange level
        let reach = surface |> Option.map fst |> Option.defaultValue FlameRange
        let endpoint = origin + forward * reach
        let events = ResizeArray<GameEvent>()
        events.Add(FlameStream(origin, endpoint))
        surface |> Option.iter (fun (_, normal) -> events.Add(FlameImpact(endpoint + normal * 0.025f, normal)))
        let mutable currentStatuses = statuses
        let sourceTeam = ownerTeam owner player soldiers
        let characterContact stance position =
            Ballistics.hitCapsules stance position
            |> Array.tryPick (fun struct (low, high, capsuleRadius) ->
                [| low; (low + high) * 0.5f; high |]
                |> Array.tryFind (fun sample ->
                    let offset = sample - origin
                    let along = Vector3.Dot(offset, forward)
                    let radius = 0.16f + along * 0.045f + capsuleRadius
                    along > 0.0f
                    && along <= reach
                    && Vector3.DistanceSquared(origin + forward * along, sample) <= radius * radius
                    && lineClear level origin sample))
        let updatedSoldiers =
            soldiers
            |> Array.map (fun soldier ->
                let hostile = sourceTeam |> Option.forall ((<>) soldier.Team)
                let contact = characterContact soldier.Stance soldier.Position
                if soldier.IsAlive && soldier.Id <> owner && hostile && contact.IsSome then
                    let hitPosition = contact.Value
                    events.Add(FlameImpact(hitPosition, -forward))
                    let health = max (Units.health 0.0f) (soldier.Health - Units.health 3.0f)
                    let lethal = health <= Units.health 0.0f
                    events.Add(HitConfirmed(soldier.Id, lethal))
                    if lethal then events.Add(Kill(Some owner, soldier.Id, "Flamethrower", false))
                    if not lethal then currentStatuses <- flameContact owner soldier.Id hitPosition currentStatuses events
                    { soldier with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else soldier.Behavior }
                else soldier)
        let hostilePlayer = sourceTeam |> Option.exists ((=) Axis)
        let playerContact = characterContact player.Stance player.Position
        let updatedPlayer =
            if player.IsAlive && player.Id <> owner && hostilePlayer && playerContact.IsSome then
                let hitPosition = playerContact.Value
                events.Add(FlameImpact(hitPosition, -forward))
                let health = max (Units.health 0.0f) (player.Health - Units.health 3.0f)
                events.Add(PlayerHurt(-forward, health))
                if health > Units.health 0.0f then currentStatuses <- flameContact owner player.Id hitPosition currentStatuses events
                { player with Health = health; RegenIn = Tuning.RegenDelay }
            else player
        updatedPlayer, updatedSoldiers, currentStatuses, List.ofSeq events

    let private nonzeroStatus status =
        status.WetFor > Units.seconds 0.0f || status.BurningFor > Units.seconds 0.0f || status.Heat > Units.seconds 0.0f

    let stepElemental (dt: float32<s>) (player: Player) (soldiers: Soldier array) statuses =
        let seconds = Units.raw dt
        let events = ResizeArray<GameEvent>()
        let mutable remaining = Map.empty
        let advance id position health behavior status =
            let wetFor = max (Units.seconds 0.0f) (status.WetFor - dt)
            let heat = max (Units.seconds 0.0f) (status.Heat - Units.seconds (seconds * 0.25f))
            let burnSeconds = if wetFor > Units.seconds 0.0f then 0.0f else min seconds (Units.raw status.BurningFor)
            let burningFor = if wetFor > Units.seconds 0.0f then Units.seconds 0.0f else max (Units.seconds 0.0f) (status.BurningFor - dt)
            let nextHealth = max (Units.health 0.0f) (health - Units.health (BurnDamagePerSecond * burnSeconds))
            let lethal = health > Units.health 0.0f && nextHealth <= Units.health 0.0f
            if burnSeconds > 0.0f then events.Add(Burning(id, position))
            if lethal then
                events.Add(HitConfirmed(id, true))
                events.Add(Kill(status.BurnOwner, id, "Flamethrower", false))
            let next = { WetFor = wetFor; BurningFor = burningFor; Heat = heat; BurnOwner = if burningFor > Units.seconds 0.0f then status.BurnOwner else None }
            if nonzeroStatus next then remaining <- Map.add id next remaining
            nextHealth, if lethal then Dying(Units.seconds 0.0f) else behavior
        let updatedSoldiers =
            soldiers
            |> Array.map (fun soldier ->
                match Map.tryFind soldier.Id statuses with
                | None -> soldier
                | Some status ->
                    let health, behavior = advance soldier.Id (soldier.Position + Vector3(0.0f, 1.0f, 0.0f)) soldier.Health soldier.Behavior status
                    { soldier with Health = health; Behavior = behavior })
        let updatedPlayer =
            match Map.tryFind player.Id statuses with
            | None -> player
            | Some status ->
                let health, _ = advance player.Id (player.Position + Vector3(0.0f, 1.0f, 0.0f)) player.Health Idle status
                if health < player.Health then events.Add(PlayerHurt(Vector3.Zero, health))
                { player with Health = health; RegenIn = if health < player.Health then Tuning.RegenDelay else player.RegenIn }
        updatedPlayer, updatedSoldiers, remaining, List.ofSeq events

    let stepWithStatus (dt: float32<s>) (level: Level) (player: Player) (soldiers: Soldier array) (projectiles: SpecialProjectile array) (existingMarks: PersistentMark array) statuses =
        let seconds = Units.raw dt
        let active = ResizeArray<SpecialProjectile>()
        let marks = ResizeArray<PersistentMark>()
        existingMarks
        |> Array.iter (function
            | WetPatch(position, normal, target, localOffset, remaining, saturation) when remaining > dt -> marks.Add(WetPatch(position, normal, target, localOffset, remaining - dt, saturation))
            | WetPatch _ -> ()
            | mark -> marks.Add mark)
        let events = ResizeArray<GameEvent>()
        let mutable currentPlayer = player
        let mutable currentSoldiers = soldiers
        let mutable currentStatuses = statuses

        let bounce maxBounces (projectile: SpecialProjectile) (position: Vector3) (normal: Vector3) (factor: float32) =
            let velocity = Vector3.Reflect(projectile.Velocity, normal) * factor
            if projectile.Bounces < maxBounces && velocity.Length() > 5.0f then
                active.Add
                    { projectile with
                        Position = position + normal * 0.055f
                        Velocity = velocity
                        Bounces = projectile.Bounces + 1
                        Remaining = projectile.Remaining - dt }
                true
            else false

        for projectile in projectiles do
            let gravityScale =
                match projectile.Kind with
                | HarpoonRound _ -> 0.15f
                | ArrowRound _ -> 0.55f
                | _ -> 1.0f
            let velocity = projectile.Velocity + Vector3(0.0f, -Tuning.Gravity * gravityScale * seconds, 0.0f)
            let travel = velocity * seconds
            let distance = travel.Length()
            let direction = if distance < 0.00001f then Vector3.UnitZ else travel / distance
            let projectile = { projectile with Velocity = velocity }
            let collisionRadius =
                match projectile.Kind with
                | HarpoonRound _ -> HarpoonRadius
                | ArrowRound _ -> 0.025f
                | _ -> 0.0f
            let hitsAnyTeam = match projectile.Kind with HarpoonRound _ -> true | _ -> false
            match trace projectile.Owner projectile.Position direction distance collisionRadius hitsAnyTeam level currentPlayer currentSoldiers with
            | None when projectile.Remaining > dt ->
                active.Add { projectile with Position = projectile.Position + travel; DistanceTravelled = projectile.DistanceTravelled + distance; Remaining = projectile.Remaining - dt }
            | None -> ()
            | Some collision ->
                let hitDistance, normal =
                    match collision with
                    | Surface(d, n) | LocalPlayer(d, n) -> d, n
                    | Character(d, _, n) -> d, n
                let hitPosition = projectile.Position + direction * hitDistance
                let projectile = { projectile with DistanceTravelled = projectile.DistanceTravelled + hitDistance }
                match projectile.Kind, collision with
                | PaintBall color, Surface _ ->
                    let bursts = Vector3.Dot(-direction, normal) >= paintBurstIncidence
                    if bursts || not (bounce MaxBounces projectile hitPosition normal 0.65f) then
                        marks.Add(PaintSplat(hitPosition + normal * 0.006f, normal, color, None, Vector3.Zero))
                        events.Add(PaintImpact(hitPosition, normal, color))
                | PaintBall color, Character(_, index, _) ->
                    currentSoldiers <- damageSoldier "Paintball Marker" (Units.health 200.0f) projectile.Owner hitPosition normal (Some(PaintMark color)) index currentSoldiers events marks
                    events.Add(PaintImpact(hitPosition, normal, color))
                | PaintBall color, LocalPlayer _ ->
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health 200.0f)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    marks.Add(markTarget currentPlayer.Position currentPlayer.Id hitPosition normal (PaintMark color))
                    events.Add(PaintImpact(hitPosition, normal, color))
                    events.Add(PlayerHurt(-direction, health))
                | NerfDart, Surface _ ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    if sticks then
                        marks.Add(StuckDart(hitPosition + normal * 0.025f, normal, None, Vector3.Zero))
                        events.Add(DartImpact(hitPosition, normal, true))
                    elif not (bounce MaxBounces projectile hitPosition normal 0.55f) then events.Add(DartImpact(hitPosition, normal, false))
                | NerfDart, Character(_, index, _) ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    currentSoldiers <- damageSoldier "Nerf Blaster" (Units.health 200.0f) projectile.Owner hitPosition normal (if sticks then Some DartMark else None) index currentSoldiers events marks
                    events.Add(DartImpact(hitPosition, normal, sticks))
                    if not sticks then bounce MaxBounces projectile hitPosition normal 0.55f |> ignore
                | NerfDart, LocalPlayer _ ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health 200.0f)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    if sticks then marks.Add(markTarget currentPlayer.Position currentPlayer.Id hitPosition normal DartMark)
                    else bounce MaxBounces projectile hitPosition normal 0.55f |> ignore
                    events.Add(DartImpact(hitPosition, normal, sticks))
                    events.Add(PlayerHurt(-direction, health))
                | NailRound, Surface _ ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    if sticks then
                        marks.Add(StuckNail(hitPosition + normal * 0.018f, normal, None, Vector3.Zero))
                        events.Add(NailImpact(hitPosition, normal, true))
                    elif not (bounce 2 projectile hitPosition normal 0.42f) then events.Add(NailImpact(hitPosition, normal, false))
                | NailRound, Character(_, index, _) ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    currentSoldiers <- damageSoldier "Nailgun" (Units.health 66.0f) projectile.Owner hitPosition normal (if sticks then Some NailMark else None) index currentSoldiers events marks
                    events.Add(NailImpact(hitPosition, normal, sticks))
                    if not sticks then bounce 2 projectile hitPosition normal 0.42f |> ignore
                | NailRound, LocalPlayer _ ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health 66.0f)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    if sticks then marks.Add(markTarget currentPlayer.Position currentPlayer.Id hitPosition normal NailMark)
                    else bounce 2 projectile hitPosition normal 0.42f |> ignore
                    events.Add(NailImpact(hitPosition, normal, sticks))
                    events.Add(PlayerHurt(-direction, health))
                | ArrowRound _, Surface _ ->
                    let sticks = Vector3.Dot(-direction, normal) >= stickIncidence
                    if sticks then
                        marks.Add(StuckArrow(hitPosition + normal * 0.035f, direction, None, Vector3.Zero))
                        events.Add(ArrowImpact(hitPosition, normal, true))
                    elif not (bounce 2 projectile hitPosition normal 0.38f) then
                        events.Add(ArrowImpact(hitPosition, normal, false))
                | ArrowRound damage, Character(_, index, _) ->
                    currentSoldiers <-
                        damageSoldier "Bow" damage projectile.Owner hitPosition normal
                            (Some(ArrowMark direction))
                            index currentSoldiers events marks
                    events.Add(ArrowImpact(hitPosition, normal, true))
                    events.Add(BloodImpact(hitPosition, direction, false))
                | ArrowRound damage, LocalPlayer _ ->
                    let health = max (Units.health 0.0f) (currentPlayer.Health - damage)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    marks.Add(markTarget currentPlayer.Position currentPlayer.Id hitPosition normal (ArrowMark direction))
                    events.Add(ArrowImpact(hitPosition, normal, true))
                    events.Add(BloodImpact(hitPosition, direction, false))
                    events.Add(PlayerHurt(-direction, health))
                | WaterDroplet, Surface _ ->
                    refreshWetMark Vector3.Zero None hitPosition normal marks
                    events.Add(WaterImpact(hitPosition, normal))
                | WaterDroplet, Character(_, index, _) ->
                    let victim = currentSoldiers[index]
                    let health = max (Units.health 0.0f) (victim.Health - Units.health WaterDamagePerDroplet)
                    let lethal = health <= Units.health 0.0f
                    currentSoldiers[index] <- { victim with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else victim.Behavior }
                    if lethal then
                        events.Add(HitConfirmed(victim.Id, true))
                        events.Add(Kill(Some projectile.Owner, victim.Id, "Super Soaker", false))
                    currentStatuses <- soak victim.Id hitPosition currentStatuses events
                    refreshWetMark victim.Position (Some victim.Id) hitPosition normal marks
                    events.Add(WaterImpact(hitPosition, normal))
                | WaterDroplet, LocalPlayer _ ->
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health WaterDamagePerDroplet)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    currentStatuses <- soak currentPlayer.Id hitPosition currentStatuses events
                    refreshWetMark currentPlayer.Position (Some currentPlayer.Id) hitPosition normal marks
                    events.Add(WaterImpact(hitPosition, normal))
                    events.Add(PlayerHurt(-direction, health))
                | BazookaRocket, _ when projectile.DistanceTravelled >= RocketArmingDistance ->
                    let blastPosition = hitPosition + normal * 0.08f
                    let nextPlayer, nextSoldiers, explosionEvents = explode blastPosition projectile.Owner level currentPlayer currentSoldiers
                    currentPlayer <- nextPlayer
                    currentSoldiers <- nextSoldiers
                    events.AddRange explosionEvents
                | BazookaRocket, Character(_, index, _) ->
                    let victim = currentSoldiers[index]
                    let health = max (Units.health 0.0f) (victim.Health - Units.health 30.0f)
                    let lethal = health <= Units.health 0.0f
                    currentSoldiers[index] <- { victim with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else victim.Behavior }
                    events.Add(HitConfirmed(victim.Id, lethal))
                    if lethal then events.Add(Kill(Some projectile.Owner, victim.Id, "Bazooka dud", false))
                    events.Add(RocketDud(hitPosition, normal))
                | BazookaRocket, LocalPlayer _ ->
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health 30.0f)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    events.Add(RocketDud(hitPosition, normal))
                    events.Add(PlayerHurt(-direction, health))
                | BazookaRocket, Surface _ -> events.Add(RocketDud(hitPosition, normal))
                | HarpoonRound skewered, Surface _ ->
                    marks.Add(EmbeddedHarpoon(hitPosition + normal * 0.035f, direction, skewered))
                    events.Add(HarpoonEmbedded(hitPosition, normal))
                | HarpoonRound skewered, Character(_, index, _) ->
                    let victim = currentSoldiers[index]
                    let health = max (Units.health 0.0f) (victim.Health - Units.health 200.0f)
                    let lethal = health <= Units.health 0.0f
                    currentSoldiers[index] <- { victim with Health = health; Behavior = if lethal then Dying(Units.seconds 0.0f) else victim.Behavior }
                    events.Add(HitConfirmed(victim.Id, lethal))
                    if lethal then events.Add(Kill(Some projectile.Owner, victim.Id, "Harpoon Gun", false))
                    events.Add(HarpoonSkewer(hitPosition, direction, victim.Id))
                    let nextSkewered =
                        if skewered.Length >= MaxSkeweredVictims then skewered
                        else
                            skewered
                            @ [ { Victim = victim.Id
                                  DistanceBehindTip = 0.42f + float32 skewered.Length * 0.40f } ]
                    if projectile.Remaining > dt then
                        active.Add
                            { projectile with
                                Kind = HarpoonRound nextSkewered
                                Position = hitPosition + direction * 0.11f
                                Velocity = projectile.Velocity * 0.85f
                                Remaining = projectile.Remaining - dt }
                | HarpoonRound _, LocalPlayer _ ->
                    let health = max (Units.health 0.0f) (currentPlayer.Health - Units.health 200.0f)
                    currentPlayer <- { currentPlayer with Health = health; RegenIn = Tuning.RegenDelay }
                    events.Add(HarpoonSkewer(hitPosition, direction, currentPlayer.Id))
                    events.Add(PlayerHurt(-direction, health))
                    if projectile.Remaining > dt then
                        active.Add
                            { projectile with
                                Position = hitPosition + direction * 0.11f
                                Velocity = projectile.Velocity * 0.85f
                                Remaining = projectile.Remaining - dt }

        let targetOf = function
            | PaintSplat(_, _, _, target, _)
            | StuckDart(_, _, target, _)
            | WetPatch(_, _, target, _, _, _)
            | StuckNail(_, _, target, _) -> target
            | StuckArrow(_, _, target, _) -> target
            | EmbeddedHarpoon _ -> None
        let allMarks = marks.ToArray()
        let wetMarks = allMarks |> Array.filter (function WetPatch _ -> true | _ -> false) |> Array.rev |> Array.truncate 128 |> Array.rev
        let durableMarks =
            let mutable counts = Map.empty<EntityId, int>
            allMarks
            |> Array.filter (function WetPatch _ -> false | _ -> true)
            |> Array.rev
            |> Array.choose (fun mark ->
                match targetOf mark with
                | None -> Some mark
                | Some target ->
                    let count = Map.tryFind target counts |> Option.defaultValue 0
                    if count >= 32 then None
                    else
                        counts <- Map.add target (count + 1) counts
                        Some mark)
            |> Array.rev
            |> fun bounded -> if bounded.Length <= 256 then bounded else bounded[bounded.Length - 256 ..]
        active.ToArray(), Array.append durableMarks wetMarks, currentPlayer, currentSoldiers, currentStatuses, List.ofSeq events

    let step dt level player soldiers projectiles existingMarks =
        let active, marks, nextPlayer, nextSoldiers, _, events = stepWithStatus dt level player soldiers projectiles existingMarks Map.empty
        active, marks, nextPlayer, nextSoldiers, events
