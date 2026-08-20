namespace Ironsight.Shell

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module RenderInterpolation =
    let world alpha (previous: World) (current: World) =
        let amount = Math.Clamp(alpha, 0.0f, 1.0f)
        let slots = Array.copy current.Player.Slots
        // Geometry driven from Drawing must interpolate too; otherwise player
        // movement is buttery while the string advances in visible 60 Hz
        // notches. Only blend the same active bow—switches stay discrete.
        if previous.Player.Active = current.Player.Active
           && current.Player.Active >= 0
           && current.Player.Active < slots.Length
           && current.Player.Active < previous.Player.Slots.Length
           && previous.Player.Slots[current.Player.Active].Class.Name = slots[current.Player.Active].Class.Name then
            let earlier = previous.Player.Slots[current.Player.Active].State
            let later = slots[current.Player.Active].State
            let state =
                match earlier, later with
                | Drawing first, Drawing second ->
                    Drawing(Units.seconds (Units.raw first + (Units.raw second - Units.raw first) * amount))
                | Ready, Drawing second -> Drawing(Units.seconds (Units.raw second * amount))
                | _ -> later
            slots[current.Player.Active] <- { slots[current.Player.Active] with State = state }
        let player =
            { current.Player with
                Position = Vector3.Lerp(previous.Player.Position, current.Player.Position, amount)
                Velocity = Vector3.Lerp(previous.Player.Velocity, current.Player.Velocity, amount)
                Yaw = MathEx.lerpAngle previous.Player.Yaw current.Player.Yaw amount
                Pitch = previous.Player.Pitch + (current.Player.Pitch - previous.Player.Pitch) * amount
                Ads = previous.Player.Ads + (current.Player.Ads - previous.Player.Ads) * amount
                Slots = slots }
        let previousSoldiers = previous.Soldiers |> Array.map (fun soldier -> soldier.Id, soldier) |> Map.ofArray
        let soldiers =
            current.Soldiers
            |> Array.map (fun soldier ->
                match Map.tryFind soldier.Id previousSoldiers with
                | Some earlier ->
                    { soldier with
                        Position = Vector3.Lerp(earlier.Position, soldier.Position, amount)
                        Facing = MathEx.lerpAngle earlier.Facing soldier.Facing amount
                        AnimPhase = earlier.AnimPhase + (soldier.AnimPhase - earlier.AnimPhase) * amount }
                | None -> soldier)
        // Projectiles are dead-reckoned on their own velocity rather than
        // lerped between two states: they spawn and are consumed too fast to
        // pair up by index, and a wrong pairing drags an arrow across the map.
        // Online they only move when a snapshot lands, so without this an arrow
        // at 88 m/s steps four metres at a time.
        let projectiles =
            current.SpecialProjectiles
            |> Array.map (fun projectile ->
                { projectile with
                    Position = projectile.Position + projectile.Velocity * (amount * Units.raw Tuning.TickDuration) })
        { current with Player = player; Soldiers = soldiers; SpecialProjectiles = projectiles }
