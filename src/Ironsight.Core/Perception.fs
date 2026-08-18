namespace Ironsight

open System
open System.Numerics

[<RequireQualifiedAccess>]
module Perception =
    let canSeePlayer (level: Level) (player: Player) (soldier: Soldier) =
        if soldier.IsDead || player.IsDead then false
        else
            let eye = soldier.Position + Vector3(0.0f, 1.55f, 0.0f)
            let torso = player.Position + Vector3(0.0f, 1.05f, 0.0f)
            let direction = MathEx.normalizedOrZero (torso - eye)
            let facing = MathEx.yawForward soldier.Facing
            Vector3.DistanceSquared(eye, torso) <= Tuning.EnemySightRange * Tuning.EnemySightRange
            && Vector3.Dot(facing, MathEx.horizontal direction |> MathEx.normalizedOrZero) >= MathF.Cos(MathF.PI / 3.0f)
            && Ballistics.lineOfSight eye torso level

    let updateContacts visible dt level (player: Player) (soldier: Soldier) =
        let aged =
            soldier.Contacts
            |> Map.toSeq
            |> Seq.choose (fun (id, struct (position, age)) ->
                let nextAge = age + dt
                if nextAge < Units.seconds 8.0f then Some(id, struct (position, nextAge)) else None)
            |> Map.ofSeq
        let contacts =
            if visible then Map.add player.Id (struct (player.Position, Units.seconds 0.0f)) aged
            else aged
        { soldier with Contacts = contacts }

    let heardShot source radius (soldier: Soldier) =
        if soldier.IsAlive && Vector3.Distance(source, soldier.Position) <= radius then
            { soldier with Contacts = Map.add (EntityId -1) (struct (source, Units.seconds 0.0f)) soldier.Contacts }
        else soldier
