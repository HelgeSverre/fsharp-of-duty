namespace Ironsight

open System
open System.Numerics
open Ironsight.ProcGen

[<RequireQualifiedAccess>]
module Script =
    let private conditionMet (world: World) = function
        | EnterVolume volume -> MathEx.overlapsPoint world.Player.Position volume
        | SquadDead squad ->
            world.Soldiers
            |> Array.filter (fun (soldier: Soldier) -> soldier.Squad = squad)
            |> fun members -> members.Length > 0 && Array.forall (fun (soldier: Soldier) -> soldier.Health <= Units.health 0.0f) members
        | ObjectiveDone index -> index >= 0 && index < world.Objectives.Length && world.Objectives[index].Done
        | Delay time -> world.Script.MissionTime >= time

    let private spawnWave team count position (world: World) =
        let nextId =
            world.Soldiers
            |> Array.map (fun soldier -> let (EntityId id) = soldier.Id in id)
            |> fun ids -> if ids.Length = 0 then 100 else Array.max ids + 1
        let wave =
            Array.init count (fun index ->
                { Id = EntityId(nextId + index)
                  Team = team
                  Position = position + Vector3((float32 (index % 3) - 1.0f) * 1.1f, 0.0f, float32 (index / 3) * 1.1f)
                  Facing = if team = Allies then 0.0f else MathF.PI
                  Stance = Standing
                  Health = Units.health 100.0f
                  Behavior = Idle
                  Weapon = Tuning.weaponSlot (if team = Allies then Tuning.thompson else Tuning.kar98k) 3
                  Squad = if team = Allies then 3 else 4
                  Contacts = Map.empty
                  Suppression = 0.0f
                  AnimPhase = 0.0f })
        { world with Soldiers = Array.append world.Soldiers wave }, []

    let private applyAction action world =
        match action with
        | SpawnWave(team, count, position) -> spawnWave team count position world
        | SetObjective(index, completed) when index >= 0 && index < world.Objectives.Length ->
            let objectives = Array.copy world.Objectives
            objectives[index] <- { objectives[index] with Done = completed }
            { world with Objectives = objectives }, [ ObjectiveUpdated index ]
        | Say(speaker, line) -> world, [ Subtitle(speaker, line) ]
        | OpenPath brushIndex when brushIndex >= 0 && brushIndex < world.Level.Brushes.Length ->
            let brushes = world.Level.Brushes |> Array.mapi (fun index brush -> index, brush) |> Array.choose (fun (index, brush) -> if index = brushIndex then None else Some brush)
            { world with Level = LevelCompile.rebuild brushes world.Level }, []
        | EndMission -> { world with Script = { world.Script with Ended = true } }, [ Subtitle("HQ", "Mission complete") ]
        | _ -> world, []

    let step world =
        let mutable current = world
        let events = ResizeArray<GameEvent>()
        let rules = Array.copy world.Script.Rules
        for index in 0..rules.Length - 1 do
            let rule = rules[index]
            if not rule.Fired && conditionMet current rule.Condition then
                let next, emitted = applyAction rule.Action current
                current <- next
                events.AddRange emitted
                rules[index] <- { rule with Fired = true }
        { current with Script = { current.Script with Rules = rules } }, List.ofSeq events
