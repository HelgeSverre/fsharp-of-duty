namespace Ironsight

open System
open System.Numerics

type MatchPhase = Waiting | Warmup | Playing | Results

type NetworkPlayer =
    { Id: EntityId
      Name: string
      Team: Team
      Position: Vector3
      Velocity: Vector3
      Yaw: float32
      Pitch: float32
      Stance: Stance
      Sprinting: bool
      Ads: float32
      Health: float32<hp>
      RegenIn: float32<s>
      Weapon: WeaponSlot
      Grenade: GrenadeHand
      Connected: bool
      Ready: bool
      Alive: bool
      RespawnIn: float32<s>
      SpawnProtection: float32<s>
      Kills: int
      Deaths: int
      LastInputSequence: int64 }

type ReplicatedEvent =
    { Id: int64
      Tick: int64
      Recipient: EntityId option
      Event: GameEvent }

type MatchState =
    { Tick: int64
      Mode: GameMode
      LevelName: string
      Phase: MatchPhase
      PhaseRemaining: float32<s>
      Players: Map<EntityId, NetworkPlayer>
      Grenades: Grenade array
      Events: ReplicatedEvent list
      NextEventId: int64
      AlliesScore: int
      AxisScore: int
      ScoreLimit: int
      TimeLimit: float32<s>
      Rng: Rng.State }

[<RequireQualifiedAccess>]
module Multiplayer =
    let scoreLimit = function FreeForAll -> 30 | TeamDeathmatch -> 75 | Campaign -> 0

    let create mode =
        if mode = Campaign then invalidArg (nameof mode) "Campaign cannot run on a multiplayer server."
        { Tick = 0L
          Mode = mode
          LevelName = ""
          Phase = Waiting
          PhaseRemaining = Units.seconds 0.0f
          Players = Map.empty
          Grenades = [||]
          Events = []
          NextEventId = 1L
          AlliesScore = 0
          AxisScore = 0
          ScoreLimit = scoreLimit mode
          TimeLimit = Units.seconds 600.0f
          Rng = Rng.create 0xC0D2F5UL }

    let sanitizeName (name: string) =
        let trimmed = if isNull name then "" else name.Trim()
        let scalars = trimmed.EnumerateRunes() |> Seq.truncate 24 |> Seq.map string |> String.concat ""
        if String.IsNullOrWhiteSpace scalars then "Soldier" else scalars

    let areHostile mode (first: NetworkPlayer) (second: NetworkPlayer) =
        match mode with
        | FreeForAll -> first.Id <> second.Id
        | TeamDeathmatch -> first.Team <> second.Team
        | Campaign -> false

    let recordKill killerId victimId state =
        match Map.tryFind killerId state.Players, Map.tryFind victimId state.Players with
        | Some killer, Some victim when killerId <> victimId && areHostile state.Mode killer victim ->
            let updatedKiller = { killer with Kills = killer.Kills + 1 }
            let updatedVictim =
                { victim with
                    Health = Units.health 0.0f
                    Alive = false
                    Deaths = victim.Deaths + 1
                    RespawnIn = Units.seconds 5.0f
                    SpawnProtection = Units.seconds 0.0f
                    Velocity = Vector3.Zero }
            let allies, axis =
                match state.Mode, killer.Team with
                | TeamDeathmatch, Allies -> state.AlliesScore + 1, state.AxisScore
                | TeamDeathmatch, Axis -> state.AlliesScore, state.AxisScore + 1
                | _ -> state.AlliesScore, state.AxisScore
            { state with
                Players = state.Players |> Map.add killerId updatedKiller |> Map.add victimId updatedVictim
                AlliesScore = allies
                AxisScore = axis }
        | _ -> state

    let hasWinner state =
        match state.Mode with
        | TeamDeathmatch -> state.AlliesScore >= state.ScoreLimit || state.AxisScore >= state.ScoreLimit
        | FreeForAll -> state.Players |> Map.exists (fun _ player -> player.Kills >= state.ScoreLimit)
        | Campaign -> false
