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

    /// How many triangles the capsule is touching. A count rather than a bool
    /// so two positions can be compared: "already stuck in this" versus
    /// "moving into something new".
    let private collisionCount (level: Level) (stance: Stance) (position: Vector3) =
        LevelCompile.trianglesNear position (Tuning.PlayerRadius + 0.6f) level
        |> Array.sumBy (fun triangle ->
            if (MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius (stanceHeight stance) position triangle.A triangle.B triangle.C).IsSome
            then 1 else 0)

    let private collides (level: Level) (stance: Stance) (position: Vector3) =
        collisionCount level stance position > 0

    /// Horizontal contact planes that this displacement is moving into. A
    /// capsule may already overlap a triangle by a tiny amount (especially at
    /// imported BSP seams); that triangle is harmless when the new movement is
    /// parallel to it and must not make the whole candidate position invalid.
    let private blockingWallNormals (level: Level) (stance: Stance) (position: Vector3) (displacement: Vector3) =
        LevelCompile.trianglesNear position (Tuning.PlayerRadius + 0.6f) level
        |> Array.choose (fun triangle ->
            match MathEx.capsuleIntersectsTriangle Tuning.PlayerRadius (stanceHeight stance) position triangle.A triangle.B triangle.C with
            | ValueNone -> None
            | ValueSome contact ->
                let faceNormal = MathEx.horizontal triangle.Normal |> MathEx.normalizedOrZero
                let radialNormal = MathEx.horizontal (position - contact) |> MathEx.normalizedOrZero
                let normal =
                    if faceNormal.LengthSquared() > 0.5f then
                        // Collision triangles are two-sided. Orient the face
                        // toward the capsule rather than trusting BSP winding.
                        if Vector3.Dot(faceNormal, radialNormal) < 0.0f then -faceNormal else faceNormal
                    else radialNormal
                if normal.LengthSquared() > 0.5f && Vector3.Dot(displacement, normal) < -0.000001f then
                    Some normal
                else None)
        // A BSP wall is commonly split into many coplanar triangles. Treat it
        // as one plane so seams do not consume the resolver's iteration budget.
        |> Array.fold (fun normals normal ->
            if normals |> List.exists (fun existing -> Vector3.Dot(existing, normal) > 0.999f) then normals
            else normal :: normals) []

    /// Move up to the first wall contact and project the unspent part of the
    /// tick along its plane. Repeating handles a second plane at a corner while
    /// retaining the original wall plane so the projection cannot cut through
    /// it on the next bump.
    let private slideHorizontal level stance (minBound: Vector3) (maxBound: Vector3) (oldPosition: Vector3) (displacement: Vector3) =
        let clampPosition (position: Vector3) = Vector3.Clamp(position, minBound, maxBound)
        let addPlanes planes blockers =
            blockers
            |> List.fold (fun accumulated normal ->
                if accumulated |> List.exists (fun existing -> Vector3.Dot(existing, normal) > 0.999f) then accumulated
                else normal :: accumulated) planes
        let clip planes (movement: Vector3) =
            let permitted candidate =
                planes |> List.forall (fun normal -> Vector3.Dot(candidate, normal) >= -0.000001f)
            // Projecting sequentially is order-dependent: at a convex edge it
            // can manufacture a left/right preference that was not present in
            // the input. In horizontal 2D the closest legal result is either
            // the original movement, one plane's tangent, or zero.
            seq {
                yield Vector3.Zero
                if permitted movement then yield movement
                for normal in planes do
                    let projected = movement - normal * Vector3.Dot(movement, normal)
                    if permitted projected then yield projected
            }
            |> Seq.minBy (fun candidate -> Vector3.DistanceSquared(candidate, movement))
        let rec resolve bumps (position: Vector3) (remaining: Vector3) planes =
            if bumps >= 3 || remaining.LengthSquared() < 0.00000001f then position
            else
                let endpoint = clampPosition (position + remaining)
                let travel = endpoint - position
                let blockers = blockingWallNormals level stance endpoint travel
                if List.isEmpty blockers then endpoint
                else
                    // Find the last unblocked point, preserving the portion of
                    // the movement completed before first contact. When the
                    // capsule starts in contact the fraction is simply zero.
                    let mutable safe = 0.0f
                    if List.isEmpty (blockingWallNormals level stance position travel) then
                        let mutable blocked = 1.0f
                        for _ in 1..10 do
                            let middle = (safe + blocked) * 0.5f
                            if List.isEmpty (blockingWallNormals level stance (position + travel * middle) travel) then
                                safe <- middle
                            else
                                blocked <- middle
                    let contact = position + travel * safe
                    let unspent = travel * (1.0f - safe)
                    let contactPlanes = addPlanes planes blockers
                    resolve (bumps + 1) contact (clip contactPlanes unspent) contactPlanes
        resolve 0 oldPosition displacement []

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

    /// The ladder volume the player is standing in, if any. Measured at the
    /// feet: a ladder is authored to end a little above the lip it serves, so
    /// climbing continues until you are standing level with the platform.
    let ladderAt (level: Level) (position: Vector3) =
        level.Ladders
        |> Array.tryFind (fun volume ->
            position.X >= volume.Min.X - Tuning.PlayerRadius && position.X <= volume.Max.X + Tuning.PlayerRadius
            && position.Z >= volume.Min.Z - Tuning.PlayerRadius && position.Z <= volume.Max.Z + Tuning.PlayerRadius
            && position.Y >= volume.Min.Y - 0.3f && position.Y <= volume.Max.Y)
        |> ValueOption.ofOption

    let onLadder (level: Level) (position: Vector3) = (ladderAt level position).IsSome

    /// Whether a surface is shallow enough to stand on rather than slide down.
    let walkableNormal (normal: Vector3) = normal.Y >= Tuning.MaxSlopeCosine

    /// A vertical capsule resting on a slope has its feet above the plane even
    /// though the round base is touching it. Account for that geometric gap in
    /// the grounded test; otherwise sufficiently inclined (but still walkable)
    /// ramps alternate between grounded and airborne and eventually pin the
    /// player against the surface.
    let private slopeContactClearance (normal: Vector3) =
        Tuning.PlayerRadius * (1.0f / normal.Y - 1.0f)

    let grounded (level: Level) (position: Vector3) =
        match surfaceUnder level position with
        | ValueSome(struct (height, normal)) when walkableNormal normal ->
            abs (position.Y - height) <= 0.055f + slopeContactClearance normal
        | ValueNone -> false
        | _ -> false

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
                    let displacement = horizontal - oldPosition
                    slideHorizontal level stance minBound maxBound oldPosition displacement
        // Settle onto the surface underfoot, but only if it is shallow enough to
        // stand on — a steep face lets you keep falling, which is what turns a
        // cliff into a wall without needing an invisible box around it.
        match surfaceUnder level horizontalPosition with
        | ValueSome(struct (floorY, normal)) when walkableNormal normal && requestedPosition.Y <= floorY + 0.05f ->
            Vector3(horizontalPosition.X, floorY, horizontalPosition.Z)
        | _ ->
            let vertical = Vector3(horizontalPosition.X, bounded.Y, horizontalPosition.Z)
            // Pressed against a wall or standing at the foot of a slope, the
            // capsule is already touching geometry — and re-testing that same
            // overlap at a higher Y refused the move outright, which is what
            // swallowed a jump taken anywhere within a body's width of a face.
            // Refuse only when the new height touches MORE than staying put
            // does, so a ceiling still stops a jump dead.
            let hits = collisionCount level stance vertical
            if hits = 0 || hits <= collisionCount level stance horizontalPosition then vertical
            else horizontalPosition

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

    /// An explicit ladder edge is already the collision-safe route through the
    /// world. Clamp an AI climber to the level, but do not run its standing
    /// capsule into the wall or deck the ladder is attached to: either contact
    /// would pin the agent before its feet reached the dismount node.
    let internal resolveClimbingAgent (level: Level) requestedPosition =
        let radius = Tuning.PlayerRadius
        Vector3.Clamp(
            requestedPosition,
            level.Bounds.Min + Vector3(radius, 0.0f, radius),
            level.Bounds.Max - Vector3(radius, 0.0f, radius))

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
        // Holding jump lets go of a ladder, which is also what stops you
        // re-attaching to the one you just pushed off.
        let ladder =
            if Input.hasButton InputButtons.Jump input.Buttons || player.IsDead then ValueNone
            else ladderAt level player.Position
        let climbing = ladder.IsSome
        // Near the top, forward goes back to meaning forward, so leaving a
        // ladder is walking off it rather than a jump taken blind.
        let dismounting =
            match ladder with
            | ValueSome volume -> player.Position.Y >= volume.Max.Y - 0.6f
            | ValueNone -> false
        // On a ladder forward is up, so it must not also walk you off the
        // rungs: only strafe steers, until the dismount at the top.
        let wishDirection =
            if climbing && not dismounting then MathEx.normalizedOrZero (MathEx.yawRight yaw * move.X)
            else MathEx.normalizedOrZero (MathEx.yawRight yaw * move.X + MathEx.yawForward yaw * move.Y)
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
        // A climber steers as if on the ground: at the top of a ladder you walk
        // forward onto the platform rather than having to jump for it.
        let acceleration = if onGround || climbing then Tuning.GroundAcceleration else Tuning.AirAcceleration
        let blend = 1.0f - MathF.Exp(-acceleration * seconds)
        let horizontalVelocity = Vector3.Lerp(MathEx.horizontal player.Velocity, targetVelocity, blend)
        let verticalVelocity =
            // On a ladder the stick drives height directly: no gravity, no
            // momentum, and forward is up whichever way you are looking, so a
            // climb does not become an exercise in aiming.
            if climbing then move.Y * Tuning.ClimbSpeed
            elif onGround && Input.hasButton InputButtons.Jump input.Buttons && stance = Standing then 7.0f
            elif onGround && player.Velocity.Y <= 0.0f then 0.0f
            else player.Velocity.Y - Tuning.Gravity * seconds
        let velocity = Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z)
        let requested = basePosition + velocity * seconds
        let position = resolveWorld level stance basePosition requested onGround
        let blockedVelocity =
            let horizontalBlocked =
                abs (position.X - requested.X) > 0.0001f
                || abs (position.Z - requested.Z) > 0.0001f
            let resolvedHorizontal = (position - basePosition) / seconds
            let x = if horizontalBlocked then resolvedHorizontal.X else velocity.X
            let y = if abs (position.Y - requested.Y) > 0.0001f then 0.0f else velocity.Y
            let z = if horizontalBlocked then resolvedHorizontal.Z else velocity.Z
            Vector3(x, y, z)
        // Right-click is the katana's overhead attack, not an optical ADS
        // state: no zoom, accuracy transition or bow-like recentering.
        let adsAllowed = player.Slots[player.Active].Class.Mechanism <> Katana
        let adsTarget = if adsAllowed && Input.hasButton InputButtons.Ads input.Buttons && not wantsSprint then 1.0f else 0.0f
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
