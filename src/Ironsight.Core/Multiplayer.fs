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
      /// Carried weapons and which one is in hand. Online that is a chosen
      /// primary plus the team's issued sidearm; `Active` indexes it.
      Slots: WeaponSlot array
      Active: int
      /// Loadout change requested mid-life; equipped on the next fresh spawn.
      RequestedWeapon: WeaponClass option
      Grenade: GrenadeHand
      Connected: bool
      Ready: bool
      Alive: bool
      RespawnIn: float32<s>
      SpawnProtection: float32<s>
      Kills: int
      Deaths: int
      /// Kills since the last death; `BestStreak` is the round's high-water mark.
      Streak: int
      BestStreak: int
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
    let scoreLimit = function FreeForAll -> 30 | TeamDeathmatch -> 75

    /// Defaults match what every room ran on before rooms were configurable.
    let defaultTimeLimit = Units.seconds 600.0f

    let create mode =
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
          TimeLimit = defaultTimeLimit
          Rng = Rng.create 0xC0D2F5UL }

    /// The one filter for player-supplied text: trims, drops control scalars
    /// and truncates to `maxLength` runes. Names and chat lines both land
    /// verbatim in every other player's HUD, so neither may carry control
    /// characters (tab doubles as the chat wire separator).
    let sanitizeText maxLength (text: string) =
        let trimmed = if isNull text then "" else text.Trim()
        trimmed.EnumerateRunes()
        |> Seq.filter (fun rune -> not (Text.Rune.IsControl rune))
        |> Seq.truncate maxLength
        |> Seq.map string
        |> String.concat ""

    let sanitizeName (name: string) =
        let scalars = sanitizeText 24 name
        if String.IsNullOrWhiteSpace scalars then "Soldier" else scalars

    let areHostile mode (first: NetworkPlayer) (second: NetworkPlayer) =
        match mode with
        | FreeForAll -> first.Id <> second.Id
        | TeamDeathmatch -> first.Team <> second.Team

    /// Kills the given player: zeroes health, starts the respawn timer, and
    /// clears spawn protection so a suicide respawn can't inherit stale
    /// protection from before the death. The one place a player is marked
    /// dead, shared by a normal kill and a self-inflicted (e.g. grenade) one.
    let markDead (id: EntityId) (state: MatchState) =
        match Map.tryFind id state.Players with
        | Some victim ->
            let dead =
                { victim with
                    Health = Units.health 0.0f
                    Alive = false
                    Deaths = victim.Deaths + 1
                    Streak = 0
                    // Dying cancels an interrupted reload the way every FPS
                    // does. Nothing steps a dead player's weapon, so a live
                    // Reloading timer would freeze on the replicated HUD for
                    // the whole respawn wait.
                    Slots = victim.Slots |> Array.map (fun slot -> { slot with State = Ready })
                    RespawnIn = Units.seconds 5.0f
                    SpawnProtection = Units.seconds 0.0f
                    Velocity = Vector3.Zero }
            { state with Players = Map.add id dead state.Players }
        | None -> state

    let recordKill killerId victimId state =
        match Map.tryFind killerId state.Players, Map.tryFind victimId state.Players with
        | Some killer, Some victim when killerId <> victimId && areHostile state.Mode killer victim ->
            // A killer already marked dead earlier this tick (his grenade cooks
            // off after he is shot) keeps the credit but not the streak: the
            // streak died with him.
            let streak = if killer.Alive then killer.Streak + 1 else 0
            let updatedKiller = { killer with Kills = killer.Kills + 1; Streak = streak; BestStreak = max killer.BestStreak streak }
            let deadState = markDead victimId state
            let allies, axis =
                match state.Mode, killer.Team with
                | TeamDeathmatch, Allies -> state.AlliesScore + 1, state.AxisScore
                | TeamDeathmatch, Axis -> state.AlliesScore, state.AxisScore + 1
                | _ -> state.AlliesScore, state.AxisScore
            { deadState with
                Players = deadState.Players |> Map.add killerId updatedKiller
                AlliesScore = allies
                AxisScore = axis }
        | _ -> state

    let hasWinner state =
        match state.Mode with
        | TeamDeathmatch -> state.AlliesScore >= state.ScoreLimit || state.AxisScore >= state.ScoreLimit
        | FreeForAll -> state.Players |> Map.exists (fun _ player -> player.Kills >= state.ScoreLimit)
