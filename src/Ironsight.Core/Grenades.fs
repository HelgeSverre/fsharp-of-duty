namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Grenades =
    let private throwDirection (player: Player) =
        Vector3(
            MathF.Sin player.Yaw * MathF.Cos player.Pitch,
            MathF.Sin player.Pitch,
            -MathF.Cos player.Yaw * MathF.Cos player.Pitch)
        |> MathEx.normalizedOrZero

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
                  Velocity = direction * 12.0f + Vector3.UnitY * 4.5f + player.Velocity * 0.35f
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
            let collision = LevelCompile.brushesNear requested 0.25f level |> Array.tryFind (fun item -> MathEx.overlapsPoint requested item.Bounds)
            let position, bouncedVelocity =
                match collision with
                | Some item ->
                    let normal = collisionNormal requested item.Bounds
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
            events.Add(Explosion(position, 6.0f))
            updated <-
                updated
                |> Array.map (fun (soldier: Soldier) ->
                    let torso = soldier.Position + Vector3(0.0f, 1.0f, 0.0f)
                    let distance = Vector3.Distance(position, torso)
                    if soldier.Health > Units.health 0.0f && distance < 6.0f && Ballistics.lineOfSight position torso level then
                        let damage = Units.health (110.0f * (1.0f - distance / 6.0f) ** 1.5f)
                        let health = max (Units.health 0.0f) (soldier.Health - damage)
                        { soldier with Health = health; Behavior = if health <= Units.health 0.0f then Dying(Units.seconds 0.0f) else soldier.Behavior }
                    else soldier)
        updated, List.ofSeq events

    let applyExplosionsToPlayer (level: Level) (positions: Vector3 array) (player: Player) =
        positions
        |> Array.fold (fun ((current: Player), (events: GameEvent list)) position ->
            let torso = current.Position + Vector3(0.0f, 1.0f, 0.0f)
            let distance = Vector3.Distance(position, torso)
            if current.Health > Units.health 0.0f && distance < 6.0f && Ballistics.lineOfSight position torso level then
                let damage = Units.health (110.0f * (1.0f - distance / 6.0f) ** 1.5f)
                let health = max (Units.health 0.0f) (current.Health - damage)
                let direction = MathEx.normalizedOrZero (torso - position)
                { current with Health = health; RegenIn = Tuning.RegenDelay }, PlayerHurt(direction, health) :: events
            else current, events) (player, ([]: GameEvent list))
