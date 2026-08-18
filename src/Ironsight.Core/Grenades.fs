namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Grenades =
    let private throwDirection (player: Player) =
        Ballistics.directionFromAngles player.Yaw player.Pitch Vector2.Zero

    [<Literal>]
    let BlastRadius = 6.0f

    /// Shared occlusion-and-falloff rule for every explosion consumer
    /// (campaign soldiers, the campaign player, and the multiplayer server).
    let explosionDamageAt (level: Level) (position: Vector3) (targetPosition: Vector3) =
        let torso = targetPosition + Vector3(0.0f, 1.0f, 0.0f)
        let distance = Vector3.Distance(position, torso)
        if distance < BlastRadius && Ballistics.lineOfSight position torso level then
            Some(Units.health (110.0f * (1.0f - distance / BlastRadius) ** 1.5f))
        else None

    let stepHand dt held (player: Player) =
        match player.Grenade, held with
        | GrenadeIdle count, true when count > 0 -> { player with Grenade = Cooking(Units.seconds 4.0f, count) }, None
        | Cooking(fuse, count), true ->
            let nextFuse = fuse - dt
            if nextFuse <= Units.seconds 0.0f then
                { player with Grenade = GrenadeIdle(count - 1) },
                Some { Owner = player.Id; Position = player.Position + Vector3(0.0f, 1.4f, 0.0f); Velocity = Vector3.Zero; Fuse = Units.seconds 0.0f }
            else { player with Grenade = Cooking(nextFuse, count) }, None
        | Cooking(fuse, count), false ->
            let direction = throwDirection player
            let grenade =
                { Owner = player.Id
                  Position = player.Position + Vector3(0.0f, 1.45f, 0.0f) + direction * 0.35f
                  // Horizontal launch speed sets the range; the vertical component
                  // stays put so the arc height (and flight time) is unchanged.
                  Velocity = direction * 18.0f + Vector3.UnitY * 4.5f + player.Velocity * 0.35f
                  Fuse = fuse }
            { player with Grenade = GrenadeIdle(count - 1) }, Some grenade
        | _ -> player, None

    let private collisionNormal (point: Vector3) (bounds: Aabb) =
        let distances =
            [| point.X - bounds.Min.X, -Vector3.UnitX
               bounds.Max.X - point.X, Vector3.UnitX
               point.Y - bounds.Min.Y, -Vector3.UnitY
               bounds.Max.Y - point.Y, Vector3.UnitY
               point.Z - bounds.Min.Z, -Vector3.UnitZ
               bounds.Max.Z - point.Z, Vector3.UnitZ |]
        distances |> Array.minBy fst |> snd

    let stepProjectilesOwned (dt: float32<s>) (level: Level) (grenades: Grenade array) =
        let seconds = Units.raw dt
        let active = ResizeArray<Grenade>()
        let exploded = ResizeArray<struct (EntityId * Vector3)>()
        for grenade in grenades do
            let velocity = grenade.Velocity + Vector3(0.0f, -Tuning.Gravity * seconds, 0.0f)
            let requested = grenade.Position + velocity * seconds
            // Sample the swept segment as well as the endpoint so a fast grenade
            // cannot tunnel straight through a thin brush or sandbag.
            let collision =
                [| 0.25f; 0.5f; 0.75f; 1.0f |]
                |> Array.tryPick (fun t ->
                    let point = Vector3.Lerp(grenade.Position, requested, t)
                    LevelCompile.brushesNear point 0.25f level
                    |> Array.tryFind (fun item -> MathEx.overlapsPoint point item.Bounds)
                    |> Option.map (fun item -> point, item))
            let position, bouncedVelocity =
                match collision with
                | Some(point, item) ->
                    let normal = collisionNormal point item.Bounds
                    grenade.Position, Vector3.Reflect(velocity, normal) * 0.3f
                | None when requested.Y < 0.08f ->
                    Vector3(requested.X, 0.08f, requested.Z), Vector3(velocity.X * 0.65f, MathF.Abs velocity.Y * 0.3f, velocity.Z * 0.65f)
                | None -> requested, velocity
            let fuse = grenade.Fuse - dt
            if fuse <= Units.seconds 0.0f then exploded.Add(struct (grenade.Owner, position))
            else active.Add { grenade with Position = position; Velocity = bouncedVelocity; Fuse = fuse }
        active.ToArray(), exploded.ToArray()

    let stepProjectiles dt level grenades =
        let active, exploded = stepProjectilesOwned dt level grenades
        active, exploded |> Array.map (fun struct (_, position) -> position)

    let applyExplosions (level: Level) (positions: Vector3 array) (soldiers: Soldier array) =
        let mutable updated = Array.copy soldiers
        let events = ResizeArray<GameEvent>()
        for position in positions do
            events.Add(Explosion(position, BlastRadius))
            updated <-
                updated
                |> Array.map (fun (soldier: Soldier) ->
                    match explosionDamageAt level position soldier.Position with
                    | Some damage when soldier.Health > Units.health 0.0f ->
                        let health = max (Units.health 0.0f) (soldier.Health - damage)
                        { soldier with Health = health; Behavior = if health <= Units.health 0.0f then Dying(Units.seconds 0.0f) else soldier.Behavior }
                    | _ -> soldier)
        updated, List.ofSeq events

    let applyExplosionsToPlayer (level: Level) (positions: Vector3 array) (player: Player) =
        positions
        |> Array.fold (fun ((current: Player), (events: GameEvent list)) position ->
            match explosionDamageAt level position current.Position with
            | Some damage when current.Health > Units.health 0.0f ->
                let torso = current.Position + Vector3(0.0f, 1.0f, 0.0f)
                let health = max (Units.health 0.0f) (current.Health - damage)
                let direction = MathEx.normalizedOrZero (torso - position)
                { current with Health = health; RegenIn = Tuning.RegenDelay }, PlayerHurt(direction, health) :: events
            | _ -> current, events) (player, ([]: GameEvent list))
