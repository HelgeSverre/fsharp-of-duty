namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Movement =
    let stanceHeight = function
        | Standing -> Tuning.StandingHeight
        | Crouched -> Tuning.CrouchedHeight
        | Prone -> Tuning.ProneHeight

    // Hold-based: the buttons ARE the stance. Toggle behaviour is an input-
    // layer concern (InputSampler latches and keeps the button held), so the
    // server never needs latch state and clients can pick either mode freely.
    let private requestedStance buttons =
        if Input.hasButton InputButtons.Prone buttons then Prone
        elif Input.hasButton InputButtons.Crouch buttons then Crouched
        else Standing

    let private collides (level: Level) (stance: Stance) (position: Vector3) =
        LevelCompile.trianglesNear position (Tuning.PlayerRadius + 0.6f) level
        |> Array.exists (fun triangle ->
            (MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius (stanceHeight stance) position triangle.A triangle.B triangle.C).IsSome)

    /// The surface under a position: its height and its normal. Probes the
    /// capsule footprint rather than a single point, so standing on the lip of a
    /// ledge still finds support. Returns the highest surface at or below the
    /// feet, which is what you are standing on.
    // Hoisted: surfaceUnder runs several times per entity per tick.
    let private probeOffsets =
        let radius = Tuning.PlayerRadius * 0.7f
        [| Vector3.Zero
           Vector3(radius, 0.0f, 0.0f); Vector3(-radius, 0.0f, 0.0f)
           Vector3(0.0f, 0.0f, radius); Vector3(0.0f, 0.0f, -radius) |]

    let surfaceUnder (level: Level) (position: Vector3) =
        // Start slightly above the feet so a surface flush with them still registers.
        let ceiling = position.Y + 0.055f
        let triangles = LevelCompile.trianglesNear position (Tuning.PlayerRadius + 0.6f) level
        let mutable bestHeight = Single.NegativeInfinity
        let mutable bestNormal = Vector3.UnitY
        for offset in probeOffsets do
            let origin = position + offset + Vector3(0.0f, 0.6f, 0.0f)
            for triangle in triangles do
                // Downward-facing triangles are ceilings, never support.
                if triangle.Normal.Y > 0.0001f then
                    match MathEx.rayTriangle origin -Vector3.UnitY triangle.A triangle.B triangle.C with
                    | ValueSome distance ->
                        let height = origin.Y - distance
                        if height <= ceiling && height > bestHeight then
                            bestHeight <- height
                            bestNormal <- triangle.Normal
                    | ValueNone -> ()
        if Single.IsNegativeInfinity bestHeight then ValueNone else ValueSome(struct (bestHeight, bestNormal))

    /// Whether a surface is shallow enough to stand on rather than slide down.
    let walkableNormal (normal: Vector3) = normal.Y >= Tuning.MaxSlopeCosine

    let grounded (level: Level) (position: Vector3) =
        match surfaceUnder level position with
        | ValueSome(struct (height, normal)) -> abs (position.Y - height) <= 0.055f && walkableNormal normal
        | ValueNone -> false

    let private resolveWorld (level: Level) (stance: Stance) (oldPosition: Vector3) (requestedPosition: Vector3) (wasGrounded: bool) =
        let radius = Tuning.PlayerRadius
        let minBound = level.Bounds.Min + Vector3(radius, 0.0f, radius)
        let maxBound = level.Bounds.Max - Vector3(radius, 0.0f, radius)
        let bounded = Vector3.Clamp(requestedPosition, minBound, maxBound)
        let horizontal = Vector3(bounded.X, oldPosition.Y, bounded.Z)
        let horizontalPosition =
            if not (collides level stance horizontal) then horizontal
            else
                let stepped =
                    if wasGrounded then
                        [ 0.1f; 0.2f; 0.3f; 0.4f ]
                        |> List.tryPick (fun height ->
                            let candidate = Vector3(horizontal.X, oldPosition.Y + height, horizontal.Z)
                            if collides level stance candidate then None else Some candidate)
                    else None
                match stepped with
                | Some position -> position
                | None ->
                    let alongX = Vector3(bounded.X, oldPosition.Y, oldPosition.Z)
                    let xResolved = if collides level stance alongX then oldPosition else alongX
                    let alongZ = Vector3(xResolved.X, xResolved.Y, bounded.Z)
                    if collides level stance alongZ then xResolved else alongZ
        // Settle onto the surface underfoot, but only if it is shallow enough to
        // stand on — a steep face lets you keep falling, which is what turns a
        // cliff into a wall without needing an invisible box around it.
        match surfaceUnder level horizontalPosition with
        | ValueSome(struct (floorY, normal)) when walkableNormal normal && requestedPosition.Y <= floorY + 0.05f ->
            Vector3(horizontalPosition.X, floorY, horizontalPosition.Z)
        | _ ->
            let vertical = Vector3(horizontalPosition.X, bounded.Y, horizontalPosition.Z)
            if collides level stance vertical then horizontalPosition else vertical

    /// Resolve a grounded humanoid displacement through the same capsule and
    /// broadphase used by the player controller.
    /// ponytail: soldiers have no velocity, so they are snapped to the surface
    /// rather than falling ballistically. Give Soldier a velocity if AI ever
    /// needs to be launched or to jump.
    let resolveAgent level oldPosition requestedPosition =
        let resolved = resolveWorld level Standing oldPosition requestedPosition true
        match surfaceUnder level resolved with
        | ValueSome(struct (floorY, normal)) when walkableNormal normal -> Vector3(resolved.X, floorY, resolved.Z)
        | _ -> resolved

    let step (dt: float32<s>) (input: InputFrame) (level: Level) (player: Player) : Player =
        let seconds = Units.raw dt
        let yaw = player.Yaw + input.Look.X
        let pitch = Math.Clamp(player.Pitch + input.Look.Y, -1.45f, 1.45f)
        let move = if input.Move.LengthSquared() > 1.0f then Vector2.Normalize input.Move else input.Move
        let stance = requestedStance input.Buttons
        // Feet below the sea surface: wading. Applies on the server and in
        // client prediction alike, since both run this same step.
        let wading =
            match level.WaterLevel with
            | Some water -> player.Position.Y < water - 0.05f
            | None -> false
        let wantsSprint = Input.hasButton InputButtons.Sprint input.Buttons && move.Y > 0.1f && stance = Standing && not wading
        let targetSpeed =
            Tuning.WalkSpeed
            * (if wantsSprint then Tuning.SprintMultiplier else 1.0f)
            * (if wading then Tuning.WadeSpeedMultiplier else 1.0f)
        let wishDirection = MathEx.normalizedOrZero (MathEx.yawRight yaw * move.X + MathEx.yawForward yaw * move.Y)
        let targetVelocity = wishDirection * targetSpeed
        let onGround = grounded level player.Position
        // Air crouch-jump, Source-style: crouching mid-air tucks the legs up
        // (feet rise by the stand/crouch height difference, head stays put),
        // which is what lets a crouch-jump clear ledges a plain jump cannot.
        // Standing back up mid-air lowers the feet again, but only into space.
        let heightDelta = Tuning.StandingHeight - Tuning.CrouchedHeight
        let basePosition, stance =
            if onGround then player.Position, stance
            else
                match player.Stance, stance with
                | Standing, Crouched -> player.Position + Vector3.UnitY * heightDelta, stance
                | Crouched, Standing ->
                    let lowered = player.Position - Vector3.UnitY * heightDelta
                    if collides level Standing lowered then player.Position, Crouched
                    else lowered, Standing
                | _ -> player.Position, stance
        let acceleration = if onGround then Tuning.GroundAcceleration else Tuning.AirAcceleration
        let blend = 1.0f - MathF.Exp(-acceleration * seconds)
        let horizontalVelocity = Vector3.Lerp(MathEx.horizontal player.Velocity, targetVelocity, blend)
        let verticalVelocity =
            if onGround && Input.hasButton InputButtons.Jump input.Buttons && stance = Standing then 7.0f
            elif onGround && player.Velocity.Y <= 0.0f then 0.0f
            else player.Velocity.Y - Tuning.Gravity * seconds
        let velocity = Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z)
        let requested = basePosition + velocity * seconds
        let position = resolveWorld level stance basePosition requested onGround
        let blockedVelocity =
            let x = if abs (position.X - requested.X) > 0.0001f then 0.0f else velocity.X
            let y = if abs (position.Y - requested.Y) > 0.0001f then 0.0f else velocity.Y
            let z = if abs (position.Z - requested.Z) > 0.0001f then 0.0f else velocity.Z
            Vector3(x, y, z)
        let adsTarget = if Input.hasButton InputButtons.Ads input.Buttons && not wantsSprint then 1.0f else 0.0f
        let adsTime = max 0.01f (Units.raw player.Slots[player.Active].Class.AdsTime)
        let adsDirection = if adsTarget > player.Ads then 1.0f elif adsTarget < player.Ads then -1.0f else 0.0f
        let ads = MathEx.clamp01 (player.Ads + adsDirection * seconds / adsTime)

        { player with
            Position = position
            Velocity = blockedVelocity
            Yaw = yaw
            Pitch = pitch
            Stance = stance
            Sprinting = wantsSprint
            Ads = ads }
