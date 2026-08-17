namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module AiBrain =
    let private shouldReload (weapon: WeaponSlot) = weapon.InMag = 0 && weapon.Reserve > 0

    // Semi-auto and bolt-action weapons fire one shot per trigger pull. A human
    // re-clicks; the AI must release between shots so BurstIx resets and the
    // weapon cycles at its natural cadence (and eventually empties + reloads).
    let private wantsFire desired (weapon: WeaponSlot) =
        desired && (weapon.Class.Mode = FullAuto || weapon.BurstIx = 0)
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
            let closed = Array.create level.Nav.Length false
            let heuristic node = Vector3.Distance(level.Nav[node].Position, level.Nav[goalNode].Position)
            let heap = Array.create (level.Nav.Length + 1) (struct (0.0f, -1))
            let mutable heapSize = 0
            let heapAdd f node =
                heapSize <- heapSize + 1
                let mutable index = heapSize
                heap[index] <- struct (f, node)
                while index > 1 do
                    let parent = index / 2
                    let struct (parentF, _) = heap[parent]
                    let struct (childF, _) = heap[index]
                    if childF < parentF then
                        let swap = heap[parent]
                        heap[parent] <- heap[index]
                        heap[index] <- swap
                        index <- parent
                    else index <- 1
            let heapPop () =
                let top = heap[1]
                heap[1] <- heap[heapSize]
                heapSize <- heapSize - 1
                let mutable index = 1
                let mutable settled = false
                while not settled && index * 2 <= heapSize do
                    let left = index * 2
                    let right = left + 1
                    let struct (leftF, _) = heap[left]
                    let child = if right <= heapSize then
                                    let struct (rightF, _) = heap[right]
                                    if rightF < leftF then right else left
                                else left
                    let struct (childF, _) = heap[child]
                    let struct (currentF, _) = heap[index]
                    if childF < currentF then
                        let swap = heap[child]
                        heap[child] <- heap[index]
                        heap[index] <- swap
                        index <- child
                    else settled <- true
                top
            costs[startNode] <- 0.0f
            heapAdd (heuristic startNode) startNode
            let mutable found = false
            while heapSize > 0 && not found do
                let struct (_, node) = heapPop ()
                if not closed[node] then
                    closed[node] <- true
                    if node = goalNode then found <- true
                    else
                        for neighbour in level.Nav[node].Neighbours do
                            if not closed[neighbour] then
                                let tentative = costs[node] + Vector3.Distance(level.Nav[node].Position, level.Nav[neighbour].Position)
                                if tentative < costs[neighbour] then
                                    costs[neighbour] <- tentative
                                    previous[neighbour] <- node
                                    heapAdd (tentative + heuristic neighbour) neighbour
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

    let playerHitDistance origin direction (player: Player) =
        let crouch = Ballistics.stanceOffset player.Stance
        [ MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 1.62f - crouch, 0.0f)) (player.Position + Vector3(0.0f, 1.85f - crouch, 0.0f)) 0.13f
          MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 0.85f - crouch, 0.0f)) (player.Position + Vector3(0.0f, 1.55f - crouch, 0.0f)) 0.26f
          MathEx.rayCapsule origin direction (player.Position + Vector3(0.0f, 0.10f - crouch, 0.0f)) (player.Position + Vector3(0.0f, 0.85f - crouch, 0.0f)) 0.20f ]
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
        // Line-of-sight raycasts dominate AI cost, so visibility is computed
        // once per soldier per tick and reused by the firing slots, the contact
        // update, and the tactical fire decision below.
        let playerVisible =
            soldiers
            |> Array.map (fun soldier -> soldier.Team = Axis && Perception.canSeePlayer level player soldier)
        let engagementSlots =
            soldiers
            |> Array.mapi (fun index soldier -> index, soldier)
            |> Array.filter (fun (index, soldier) -> soldier.Team = Axis && playerVisible[index])
            |> Array.sortBy (fun (_, soldier) -> Vector3.DistanceSquared(soldier.Position, player.Position))
            |> Array.truncate Tuning.EnemyMaxPlayerShooters
            |> Array.map (fun (_, soldier) -> soldier.Id)
            |> Set.ofArray
        // A soldier can be visible to both the player and the friendly squad in
        // the same tick. Its weapon state machine must only advance once per
        // tick, so the player-facing pass records who it already stepped and the
        // squad-engagement pass below skips those.
        let steppedWeapons = System.Collections.Generic.HashSet<int>()
        let updatedSoldiers =
            soldiers
            |> Array.mapi (fun index original ->
                let recovered = { original with Suppression = max 0.0f (original.Suppression - Units.raw dt * 0.72f) }
                let perceived = Perception.updateContacts playerVisible[index] dt level updatedPlayer recovered
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
                        let visible = playerVisible[index]
                        let board = Map.tryFind perceived.Squad blackboards
                        let hasCoveringFire = board |> Option.bind (fun value -> value.Suppressor) |> Option.exists ((<>) perceived.Id)
                        let tactical =
                            if perceived.Weapon.Class.Kind = MachineGun then
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
                            Weapons.step dt 0.0f (wantsFire shouldFire tactical.Weapon) (shouldReload tactical.Weapon) 0.0f &localRng tactical.Weapon
                        let armed = { tactical with Weapon = weapon }
                        let (EntityId armedId) = armed.Id
                        steppedWeapons.Add armedId |> ignore
                        for request in requests do
                            // Visibility was sampled at tick start; stop engaging
                            // once the player has fallen to earlier fire this tick.
                            if updatedPlayer.Health > Units.health 0.0f then
                                let origin = Ballistics.soldierMuzzleOrigin armed
                                let target = updatedPlayer.Position + Vector3(0.0f, 1.05f, 0.0f)
                                // Player-facing AI deliberately shoots a loose cone. The cone
                                // tightens toward close range so a massed battlefield stays
                                // threatening without turning every rifleman into a laser or
                                // making point-blank encounters instant kills at range.
                                let range = Vector3.Distance(origin, target)
                                let aimFactor = Math.Clamp(6.0f / MathF.Max(1.0f, range), 0.35f, Tuning.EnemyAimSpreadMultiplier)
                                // Player-facing enemy fire keeps a fixed cone regardless of the
                                // weapon's player-facing hip spread. Scaling the existing offset
                                // preserves both the random distribution and the RNG stream.
                                let spreadScale = Tuning.EnemyHipSpread / MathF.Max(1e-4f, weapon.Class.HipSpread)
                                let direction = aimDirection origin target (request.DirectionOffset * spreadScale * aimFactor)
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
                        Weapons.step dt 0.0f (wantsFire shouldFire positioned.Weapon) (shouldReload positioned.Weapon) 0.72f &localRng positioned.Weapon
                    combatSoldiers[allyIndex] <- { positioned with Weapon = weapon }
                    for request in requests do
                        let origin = Ballistics.soldierMuzzleOrigin positioned
                        let targetPoint = enemy.Position + Vector3(0.0f, 1.05f, 0.0f)
                        let direction = aimDirection origin targetPoint request.DirectionOffset
                        events.Add(ShotFired(Some positioned.Id, origin, direction, weapon.Class.Name))
                        let hitSoldiers, hitEvents =
                            Ballistics.applyShotFiltered (fun candidate -> candidate.Team = Axis) origin direction request.Damage request.Penetration request.HeadshotMultiplier level combatSoldiers
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
                    let (EntityId axisId) = axis.Id
                    if steppedWeapons.Contains axisId then
                        // Already fired or cycled against the player this tick;
                        // just turn to face the squad without advancing twice.
                        combatSoldiers[axisIndex] <- aimed
                    else
                        let struct (weapon, requests) =
                            Weapons.step dt 0.0f (wantsFire true aimed.Weapon) (shouldReload aimed.Weapon) 0.0f &localRng aimed.Weapon
                        combatSoldiers[axisIndex] <- { aimed with Weapon = weapon }
                        for request in requests do
                            let origin = Ballistics.soldierMuzzleOrigin aimed
                            let targetPoint = friendly.Position + Vector3(0.0f, 1.05f, 0.0f)
                            let direction = aimDirection origin targetPoint request.DirectionOffset
                            events.Add(ShotFired(Some aimed.Id, origin, direction, weapon.Class.Name))
                            let hitSoldiers, hitEvents =
                                Ballistics.applyShotFiltered (fun candidate -> candidate.Team = Allies) origin direction (request.Damage * Tuning.EnemyFriendlyDamageScale) request.Penetration request.HeadshotMultiplier level combatSoldiers
                            combatSoldiers <- hitSoldiers
                            events.AddRange(hitEvents |> List.filter (function HitConfirmed _ -> false | _ -> true))
                            if combatSoldiers[targetIndex].Health <= Units.health 0.0f then
                                combatSoldiers[targetIndex] <- { combatSoldiers[targetIndex] with Behavior = Dying(Units.seconds 0.0f) }
        rng <- localRng
        updatedPlayer, combatSoldiers, List.ofSeq events
