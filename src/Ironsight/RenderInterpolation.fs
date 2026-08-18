namespace Ironsight.Shell

open System
open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module RenderInterpolation =
    let world alpha (previous: World) (current: World) =
        let amount = Math.Clamp(alpha, 0.0f, 1.0f)
        let player =
            { current.Player with
                Position = Vector3.Lerp(previous.Player.Position, current.Player.Position, amount)
                Velocity = Vector3.Lerp(previous.Player.Velocity, current.Player.Velocity, amount)
                Yaw = MathEx.lerpAngle previous.Player.Yaw current.Player.Yaw amount
                Pitch = previous.Player.Pitch + (current.Player.Pitch - previous.Player.Pitch) * amount
                Ads = previous.Player.Ads + (current.Player.Ads - previous.Player.Ads) * amount }
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
        { current with Player = player; Soldiers = soldiers }
