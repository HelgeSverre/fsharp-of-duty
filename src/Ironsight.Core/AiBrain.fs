namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module AiBrain =
    let private shouldReload (weapon: WeaponSlot) = weapon.InMag = 0 && weapon.Reserve > 0
    let private findPath (level: Level) startPosition goalPosition =
        if level.Nav.Length = 0 then []
        else
            let nearest position =
                level.Nav
                |> Array.mapi (fun index node -> index, Vector3.DistanceSquared(position, node.Position))
                |> Array.minBy snd
                |> fst
            let startNode, goalNode = nearest startPosition, nearest goalPosition
            let costs = Array.create level.Nav.Length Single.PositiveInfinity
            let previous = Array.create level.Nav.Length -1
            let openSet = ResizeArray<int>()
            let closed = Array.create level.Nav.Length false
            costs[startNode] <- 0.0f
            openSet.Add startNode
            while openSet.Count > 0 && not closed[goalNode] do
                let current =
                    openSet
                    |> Seq.minBy (fun index -> costs[index] + Vector3.Distance(level.Nav[index].Position, level.Nav[goalNode].Position))
                openSet.Remove current |> ignore
                closed[current] <- true
                for neighbour in level.Nav[current].Neighbours do
                    if not closed[neighbour] then
                        let tentative = costs[current] + Vector3.Distance(level.Nav[current].Position, level.Nav[neighbour].Position)
                        if tentative < costs[neighbour] then
                            costs[neighbour] <- tentative
                            previous[neighbour] <- current
                            if not (openSet.Contains neighbour) then openSet.Add neighbour
            if goalNode <> startNode && previous[goalNode] < 0 then []
            else
                let result = ResizeArray<Vector3>()
                let mutable node = goalNode
                while node <> startNode && node >= 0 do
                    result.Add level.Nav[node].Position
                    node <- previous[node]
                result |> Seq.rev |> Seq.toList

    let private closestCover target (level: Level) (soldier: Soldier) =
        level.Cover
        |> Array.filter (fun cover ->
            (cover.Owner.IsNone || cover.Owner = Some soldier.Team)
            && Vector3.DistanceSquared(cover.Pos, soldier.Position) < 900.0f)
        |> Array.sortBy (fun cover -> Vector3.DistanceSquared(cover.Pos, soldier.Position) + Vector3.DistanceSquared(cover.Pos, target) * 0.2f)
        |> Array.tryHead

    let private moveTowards dt (level: Level) target (soldier: Soldier) =
        let offset = MathEx.horizontal (target - soldier.Position)
        let distance = offset.Length()
        if distance < 0.05f then soldier, true
        else
            let direction = offset / distance
            let step = min distance (2.2f * Units.raw dt)
            let facing = MathF.Atan2(direction.X, -direction.Z)
            let requested = soldier.Position + direction * step
            let position = Movement.resolveAgent level soldier.Position requested
            let travelled = Vector3.Distance(soldier.Position, position)
            { soldier with Position = position; Facing = facing; AnimPhase = soldier.AnimPhase + travelled * 3.0f }, distance <= 0.2f

    let private followRoute dt level goal (soldier: Soldier) =
        let route =
            match soldier.Behavior with
            | AdvancingTo(previousGoal, path) when Vector3.DistanceSquared(previousGoal, goal) < 9.0f -> path
            | _ -> findPath level soldier.Position goal
        match route with
        | nextNode :: remaining ->
            let moved, arrived = moveTowards dt level nextNode soldier
            { moved with Behavior = AdvancingTo(goal, if arrived then remaining else route) }
        | [] ->
            let moved, _ = moveTowards dt level goal soldier
            { moved with Behavior = AdvancingTo(goal, []) }

    let private aimDirection (source: Vector3) (target: Vector3) (offset: Vector2) =
        let forward = MathEx.normalizedOrZero (target - source)
        let right = MathEx.normalizedOrZero (Vector3.Cross(forward, Vector3.UnitY))
        MathEx.normalizedOrZero (forward + right * offset.X + Vector3.UnitY * offset.Y)

    let private playerHitDistance origin direction (player: Player) =
        [ MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 1.48f, 0.0f)) (player.Position + Vector3(0.0f, 1.70f, 0.0f)) 0.16f
          MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 0.78f, 0.0f)) (player.Position + Vector3(0.0f, 1.36f, 0.0f)) 0.28f
          MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 0.18f, 0.0f)) (player.Position + Vector3(0.0f, 0.76f, 0.0f)) 0.22f ]
        |> List.choose id
        |> function [] -> None | hits -> Some(List.min hits)

    let private staticHitDistance origin direction (level: Level) =
        LevelCompile.brushesAlongRay origin direction 200.0f level
        |> Array.choose (fun brush -> MathEx.rayAabb origin direction brush.Bounds |> Option.map (fun struct (entry, _, _) -> entry))
        |> function [||] -> Single.PositiveInfinity | hits -> Array.min hits

    let step dt (rng: byref<Rng.State>) (level: Level) (blackboards: Map<int, SquadBlackboard>) (player: Player) (soldiers: Soldier array) =
        let mutable localRng = rng
        let mutable updatedPlayer = player
        let events = ResizeArray<GameEvent>()
        // Hundreds of troops may share contact, but only the nearest handful get
        // a clean firing lane at once. The rest advance/suppress instead of all
        // deleting the player on the same frame.
        let engagementSlots =
            soldiers
            |> Array.filter (fun soldier -> soldier.Team = Axis && Perception.canSeePlayer level player soldier)
            |> Array.sortBy (fun soldier -> Vector3.DistanceSquared(soldier.Position, player.Position))
            |> Array.truncate Tuning.EnemyMaxPlayerShooters
            |> Array.map (fun soldier -> soldier.Id)
            |> Set.ofArray
        let updatedSoldiers =
            soldiers
            |> Array.map (fun original ->
                let recovered = { original with Suppression = max 0.0f (original.Suppression - Units.raw dt * 0.72f) }
                let perceived = Perception.updateContacts dt level updatedPlayer recovered
                match perceived.Behavior with
                | Dying sinceDeath -> { perceived with Behavior = Dying(sinceDeath + dt) }
                | DyingHeadshot sinceDeath -> { perceived with Behavior = DyingHeadshot(sinceDeath + dt) }
                | Suppressed remaining when remaining > dt -> { perceived with Behavior = Suppressed(remaining - dt) }
                | Suppressed _ -> { perceived with Behavior = Idle }
                | _ when perceived.Team = Allies -> perceived
                | _ ->
                    match Map.tryFind updatedPlayer.Id perceived.Contacts with
                    | None when level.Name = "Paintball Killhouse" ->
                        // Push each bot through one of the three lanes until it
                        // acquires the player. This is intentionally deterministic:
                        // the navmesh supplies the route, while the role supplies
                        // a readable opening instead of random wandering.
                        let (EntityId soldierId) = perceived.Id
                        let lane = [| 16.0f; -16.0f; -2.5f; 2.5f |][abs soldierId % 4]
                        let patrolGoal = Vector3(lane, perceived.Position.Y, 3.5f)
                        followRoute dt level patrolGoal perceived
                    | None -> { perceived with Behavior = Idle }
                    | Some(struct (lastKnown, contactAge)) ->
                        let visible = Perception.canSeePlayer level updatedPlayer perceived
                        let board = Map.tryFind perceived.Squad blackboards
                        let hasCoveringFire = board |> Option.bind (fun value -> value.Suppressor) |> Option.exists ((<>) perceived.Id)
                        let tactical =
                            if perceived.Weapon.Class.Name = "MG42" then
                                let facing = MathF.Atan2(lastKnown.X - perceived.Position.X, -(lastKnown.Z - perceived.Position.Z))
                                let cover =
                                    { Pos = perceived.Position
                                      PeekDir = MathEx.yawForward facing
                                      Crouch = true
                                      Owner = Some perceived.Team }
                                { perceived with Facing = facing; Behavior = InCover(cover, Units.seconds 1.1f) }
                            else
                                match perceived.Behavior with
                                | Idle ->
                                    let (EntityId soldierId) = perceived.Id
                                    if hasCoveringFire && contactAge > Units.seconds 0.65f && soldierId % 4 = 0 then
                                        let toward = MathEx.horizontal (lastKnown - perceived.Position) |> MathEx.normalizedOrZero
                                        let side = Vector3(-toward.Z, 0.0f, toward.X) * (if soldierId % 2 = 0 then 9.0f else -9.0f)
                                        let flankPoint = lastKnown + side
                                        { perceived with Behavior = Flanking(updatedPlayer.Id, findPath level perceived.Position flankPoint) }
                                    else
                                        match closestCover lastKnown level perceived with
                                        | Some cover -> { perceived with Behavior = AdvancingTo(cover.Pos, findPath level perceived.Position cover.Pos) }
                                        | None -> { perceived with Behavior = AdvancingTo(lastKnown, findPath level perceived.Position lastKnown) }
                                | AdvancingTo(waypoint, path) ->
                                    match path with
                                    | nextNode :: remaining ->
                                        let moved, arrived = moveTowards dt level nextNode perceived
                                        { moved with Behavior = AdvancingTo(waypoint, if arrived then remaining else path) }
                                    | [] ->
                                        let moved, arrived = moveTowards dt level waypoint perceived
                                        if arrived then
                                            match closestCover lastKnown level moved with
                                            | Some cover -> { moved with Behavior = InCover(cover, Units.seconds 0.0f) }
                                            | None -> { moved with Behavior = AdvancingTo(lastKnown, findPath level moved.Position lastKnown) }
                                        else moved
                                | InCover(cover, phase) ->
                                    let nextPhase = phase + dt
                                    { perceived with
                                        Position = cover.Pos
                                        Behavior = InCover(cover, if nextPhase >= Units.seconds 1.8f then Units.seconds 0.0f else nextPhase) }
                                | Flanking(target, nextNode :: remaining) ->
                                    let moved, arrived = moveTowards dt level nextNode perceived
                                    { moved with Behavior = Flanking(target, if arrived then remaining else nextNode :: remaining) }
                                | Flanking(_, []) ->
                                    { perceived with Behavior = AdvancingTo(lastKnown, findPath level perceived.Position lastKnown) }
                                | state -> { perceived with Behavior = state }
                        let isSuppressor = board |> Option.bind (fun value -> value.Suppressor) = Some tactical.Id
                        let shouldFire =
                            visible && Set.contains tactical.Id engagementSlots
                            && (isSuppressor
                                || match tactical.Behavior with
                                   | InCover(_, phase) -> phase >= Units.seconds 0.55f && phase <= Units.seconds 1.25f
                                   | AdvancingTo _ -> true
                                   | Flanking _ -> true
                                   | _ -> false)
                        let struct (weapon, requests) =
                            Weapons.step dt shouldFire (shouldReload tactical.Weapon) 0.0f &localRng tactical.Weapon
                        let armed = { tactical with Weapon = weapon }
                        for request in requests do
                            let origin = Ballistics.soldierMuzzleOrigin armed
                            let target = updatedPlayer.Position + Vector3(0.0f, 1.05f, 0.0f)
                            // Player-facing AI deliberately shoots a loose cone. This keeps
                            // a massed battlefield threatening without turning every rifleman
                            // into a synchronized hitscan turret.
                            let direction = aimDirection origin target (request.DirectionOffset * Tuning.EnemyAimSpreadMultiplier)
                            events.Add(ShotFired(Some armed.Id, origin, direction, weapon.Class.Name))
                            match playerHitDistance origin direction updatedPlayer with
                            | Some hitDistance when hitDistance < staticHitDistance origin direction level ->
                                let hurt, hurtEvent = Damage.hurtPlayer (request.Damage * Tuning.EnemyDamageScale) (-direction) updatedPlayer
                                updatedPlayer <- hurt
                                events.Add hurtEvent
                            | _ -> ()
                        armed)
        let mutable combatSoldiers = updatedSoldiers
        for allyIndex in 0..combatSoldiers.Length - 1 do
            let ally = combatSoldiers[allyIndex]
            if ally.Team = Allies && ally.Health > Units.health 0.0f then
                let target =
                    combatSoldiers
                    |> Array.mapi (fun index soldier -> index, soldier)
                    |> Array.filter (fun (_, soldier) ->
                        soldier.Team = Axis && soldier.Health > Units.health 0.0f
                        && Vector3.DistanceSquared(ally.Position, soldier.Position) < 2500.0f
                        && Ballistics.lineOfSight (ally.Position + Vector3(0.0f, 1.45f, 0.0f)) (soldier.Position + Vector3(0.0f, 1.05f, 0.0f)) level)
                    |> Array.sortBy (fun (_, soldier) -> Vector3.DistanceSquared(ally.Position, soldier.Position))
                    |> Array.tryHead
                match target with
                | None ->
                    let objective = Vector3(0.0f, ally.Position.Y, level.Bounds.Min.Z + 4.0f)
                    if ally.Position.Z > objective.Z + 0.5f then
                        combatSoldiers[allyIndex] <- followRoute dt level objective ally
                | Some(targetIndex, enemy) ->
                    let distance = Vector3.Distance(ally.Position, enemy.Position)
                    let positioned =
                        if distance > 18.0f then
                            followRoute dt level enemy.Position ally
                        else
                            let contact = Map.add enemy.Id (struct (enemy.Position, Units.seconds 0.0f)) ally.Contacts
                            { ally with Facing = MathF.Atan2(enemy.Position.X - ally.Position.X, -(enemy.Position.Z - ally.Position.Z)); Contacts = contact }
                    let shouldFire = distance <= 32.0f
                    let struct (weapon, requests) =
                        Weapons.step dt shouldFire (shouldReload positioned.Weapon) 0.72f &localRng positioned.Weapon
                    combatSoldiers[allyIndex] <- { positioned with Weapon = weapon }
                    for request in requests do
                        let origin = Ballistics.soldierMuzzleOrigin positioned
                        let targetPoint = enemy.Position + Vector3(0.0f, 1.05f, 0.0f)
                        let direction = aimDirection origin targetPoint request.DirectionOffset
                        events.Add(ShotFired(Some positioned.Id, origin, direction, weapon.Class.Name))
                        let hitSoldiers, hitEvents =
                            Ballistics.applyShotFiltered (fun candidate -> candidate.Team = Axis) origin direction request.Damage request.Penetration level combatSoldiers
                        combatSoldiers <- hitSoldiers
                        events.AddRange(hitEvents |> List.filter (function HitConfirmed _ -> false | _ -> true))
                        if combatSoldiers[targetIndex].Health <= Units.health 0.0f then
                            combatSoldiers[targetIndex] <- { combatSoldiers[targetIndex] with Behavior = Dying(Units.seconds 0.0f) }
        // Axis troops also engage the advancing friendly squad. This produces a
        // battlefield-wide firefight instead of every enemy waiting exclusively
        // for the player to enter its perception cone.
        for axisIndex in 0..combatSoldiers.Length - 1 do
            let axis = combatSoldiers[axisIndex]
            if axis.Team = Axis && axis.Health > Units.health 0.0f then
                let candidates =
                    combatSoldiers
                    |> Array.mapi (fun index soldier -> index, soldier)
                    |> Array.filter (fun (_, soldier) ->
                        soldier.Team = Allies && soldier.Health > Units.health 0.0f
                        && Vector3.DistanceSquared(axis.Position, soldier.Position) < 45.0f * 45.0f)
                    |> Array.sortBy (fun (_, soldier) -> Vector3.DistanceSquared(axis.Position, soldier.Position))
                    |> Array.truncate 5
                let target =
                    candidates
                    |> Array.tryFind (fun (_, soldier) ->
                        Ballistics.lineOfSight (axis.Position + Vector3(0.0f, 1.45f, 0.0f)) (soldier.Position + Vector3(0.0f, 1.05f, 0.0f)) level)
                match target with
                | None -> ()
                | Some(targetIndex, friendly) ->
                    let facing = MathF.Atan2(friendly.Position.X - axis.Position.X, -(friendly.Position.Z - axis.Position.Z))
                    let aimed = { axis with Facing = facing; Contacts = Map.add friendly.Id (struct (friendly.Position, Units.seconds 0.0f)) axis.Contacts }
                    let struct (weapon, requests) =
                        Weapons.step dt true (shouldReload aimed.Weapon) 0.0f &localRng aimed.Weapon
                    combatSoldiers[axisIndex] <- { aimed with Weapon = weapon }
                    for request in requests do
                        let origin = Ballistics.soldierMuzzleOrigin aimed
                        let targetPoint = friendly.Position + Vector3(0.0f, 1.05f, 0.0f)
                        let direction = aimDirection origin targetPoint request.DirectionOffset
                        events.Add(ShotFired(Some aimed.Id, origin, direction, weapon.Class.Name))
                        let hitSoldiers, hitEvents =
                            Ballistics.applyShotFiltered (fun candidate -> candidate.Team = Allies) origin direction (request.Damage * Tuning.EnemyFriendlyDamageScale) request.Penetration level combatSoldiers
                        combatSoldiers <- hitSoldiers
                        events.AddRange(hitEvents |> List.filter (function HitConfirmed _ -> false | _ -> true))
                        if combatSoldiers[targetIndex].Health <= Units.health 0.0f then
                            combatSoldiers[targetIndex] <- { combatSoldiers[targetIndex] with Behavior = Dying(Units.seconds 0.0f) }
        rng <- localRng
        updatedPlayer, combatSoldiers, List.ofSeq events
