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

    /// The grenade a player would release right now. Shared by the throw itself
    /// and the aiming preview so the two can never disagree about where a
    /// grenade goes.
    let throwFrom (fuse: float32<s>) (player: Player) =
        let direction = throwDirection player
        { Owner = player.Id
          Position = player.Position + Vector3(0.0f, 1.45f, 0.0f) + direction * 0.35f
          // Horizontal launch speed sets the range; the vertical component
          // stays put so the arc height (and flight time) is unchanged.
          Velocity = direction * 18.0f + Vector3.UnitY * 4.5f + player.Velocity * 0.35f
          Fuse = fuse }

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
            { player with Grenade = GrenadeIdle(count - 1) }, Some(throwFrom fuse player)
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

    /// Where a grenade thrown right now would travel, as successive tick
    /// positions. Runs the real projectile integrator rather than re-deriving
    /// the ballistics, so bounces match and the preview cannot drift from the
    /// simulation when the tuning changes.
    /// ponytail: fixed tick horizon rather than simulating to rest; raise
    /// `steps` if long lobs ever need a longer preview.
    let predictPath (level: Level) (steps: int) (player: Player) =
        let points = ResizeArray<Vector3>()
        // A fuse far longer than the horizon keeps the preview from detonating
        // mid-flight, which would truncate the path for no visible reason.
        let mutable active = [| throwFrom (Units.seconds 600.0f) player |]
        let mutable step = 0
        let mutable resting = false
        while step < steps && not resting && active.Length > 0 do
            let next, _ = stepProjectilesOwned Tuning.TickDuration level active
            active <- next
            if next.Length > 0 then
                let grenade = next[0]
                points.Add grenade.Position
                // Once it has settled on the ground the rest of the horizon is
                // just the same point repeated, so stop and let the last entry
                // stand as the landing spot.
                if grenade.Velocity.Length() < 0.6f && grenade.Position.Y <= 0.12f then resting <- true
            step <- step + 1
        points.ToArray()

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
