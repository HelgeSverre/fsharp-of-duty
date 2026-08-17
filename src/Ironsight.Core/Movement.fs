namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Movement =
    let private hasButton button (buttons: InputButtons) = buttons.HasFlag button

    let stanceHeight = function
        | Standing -> Tuning.StandingHeight
        | Crouched -> Tuning.CrouchedHeight
        | Prone -> Tuning.ProneHeight

    let private requestedStance (player: Player) buttons =
        let proneHeld = hasButton InputButtons.Prone buttons
        let crouchHeld = hasButton InputButtons.Crouch buttons
        let crouchPressed = crouchHeld && not player.CrouchPrevHeld
        let latched = if crouchPressed then not player.CrouchLatched else player.CrouchLatched
        let stance =
            if proneHeld then Prone
            elif latched then Crouched
            elif player.Stance = Prone then Standing
            elif player.Stance = Crouched then Standing
            else player.Stance
        stance, latched, crouchHeld

    let private collides (level: Level) (stance: Stance) (position: Vector3) =
        LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) level
        |> Array.exists (fun brush -> MathEx.capsuleIntersectsAabb Tuning.PlayerRadius (stanceHeight stance) position brush.Bounds)

    let private grounded (level: Level) (position: Vector3) =
        position.Y <= 0.002f
        || LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) level
           |> Array.exists (fun brush ->
               let top = brush.Bounds.Max.Y
               abs (position.Y - top) <= 0.055f
               && position.X >= brush.Bounds.Min.X - Tuning.PlayerRadius
               && position.X <= brush.Bounds.Max.X + Tuning.PlayerRadius
               && position.Z >= brush.Bounds.Min.Z - Tuning.PlayerRadius
               && position.Z <= brush.Bounds.Max.Z + Tuning.PlayerRadius)

    let private supportHeight (level: Level) (position: Vector3) =
        LevelCompile.brushesNear position (Tuning.PlayerRadius + 0.1f) level
        |> Array.choose (fun brush ->
            if brush.Bounds.Max.Y <= position.Y + 0.055f
               && position.X >= brush.Bounds.Min.X - Tuning.PlayerRadius
               && position.X <= brush.Bounds.Max.X + Tuning.PlayerRadius
               && position.Z >= brush.Bounds.Min.Z - Tuning.PlayerRadius
               && position.Z <= brush.Bounds.Max.Z + Tuning.PlayerRadius then Some brush.Bounds.Max.Y
            else None)
        |> function [||] -> 0.0f | heights -> max 0.0f (Array.max heights)

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
        let floorY = supportHeight level horizontalPosition
        if requestedPosition.Y <= floorY + 0.05f then Vector3(horizontalPosition.X, floorY, horizontalPosition.Z)
        else
            let vertical = Vector3(horizontalPosition.X, bounded.Y, horizontalPosition.Z)
            if collides level stance vertical then horizontalPosition else vertical

    /// Resolve a grounded humanoid displacement through the same capsule and
    /// broadphase used by the player controller.
    let resolveAgent level oldPosition requestedPosition =
        resolveWorld level Standing oldPosition requestedPosition true

    let step (dt: float32<s>) (input: InputFrame) (level: Level) (player: Player) : Player =
        let seconds = Units.raw dt
        let yaw = player.Yaw + input.Look.X
        let pitch = Math.Clamp(player.Pitch + input.Look.Y, -1.45f, 1.45f)
        let move = if input.Move.LengthSquared() > 1.0f then Vector2.Normalize input.Move else input.Move
        let stance, crouchLatched, crouchPrevHeld = requestedStance player input.Buttons
        let wantsSprint = hasButton InputButtons.Sprint input.Buttons && move.Y > 0.1f && stance = Standing
        let targetSpeed = Tuning.WalkSpeed * (if wantsSprint then Tuning.SprintMultiplier else 1.0f)
        let wishDirection = MathEx.normalizedOrZero (MathEx.yawRight yaw * move.X + MathEx.yawForward yaw * move.Y)
        let targetVelocity = wishDirection * targetSpeed
        let onGround = grounded level player.Position
        let acceleration = if onGround then Tuning.GroundAcceleration else Tuning.AirAcceleration
        let blend = 1.0f - MathF.Exp(-acceleration * seconds)
        let horizontalVelocity = Vector3.Lerp(MathEx.horizontal player.Velocity, targetVelocity, blend)
        let verticalVelocity =
            if onGround && hasButton InputButtons.Jump input.Buttons && stance = Standing then 7.0f
            elif onGround && player.Velocity.Y <= 0.0f then 0.0f
            else player.Velocity.Y - Tuning.Gravity * seconds
        let velocity = Vector3(horizontalVelocity.X, verticalVelocity, horizontalVelocity.Z)
        let requested = player.Position + velocity * seconds
        let position = resolveWorld level stance player.Position requested onGround
        let blockedVelocity =
            let x = if abs (position.X - requested.X) > 0.0001f then 0.0f else velocity.X
            let y = if abs (position.Y - requested.Y) > 0.0001f then 0.0f else velocity.Y
            let z = if abs (position.Z - requested.Z) > 0.0001f then 0.0f else velocity.Z
            Vector3(x, y, z)
        let adsTarget = if hasButton InputButtons.Ads input.Buttons && not wantsSprint then 1.0f else 0.0f
        let adsTime = max 0.01f (Units.raw player.Slots[player.Active].Class.AdsTime)
        let adsDirection = if adsTarget > player.Ads then 1.0f elif adsTarget < player.Ads then -1.0f else 0.0f
        let ads = MathEx.clamp01 (player.Ads + adsDirection * seconds / adsTime)

        { player with
            Position = position
            Velocity = blockedVelocity
            Yaw = yaw
            Pitch = pitch
            Stance = stance
            CrouchLatched = crouchLatched
            CrouchPrevHeld = crouchPrevHeld
            Sprinting = wantsSprint
            Ads = ads }
