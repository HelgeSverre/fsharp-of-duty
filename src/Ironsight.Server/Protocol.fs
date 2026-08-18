namespace Ironsight.Server

open System
open System.Numerics
open System.Text.Json
open Ironsight

[<CLIMutable>]
type WelcomeMessage =
    { ``type``: string
      version: int
      playerId: int
      sessionToken: string
      tickRate: int
      snapshotRate: int
      level: string
      mapHash: string }

[<CLIMutable>]
type PlayerSnapshot =
    { id: int
      name: string
      team: string
      x: float32
      y: float32
      z: float32
      vx: float32
      vy: float32
      vz: float32
      yaw: float32
      pitch: float32
      stance: string
      crouchLatched: bool
      crouchPrevHeld: bool
      health: float32
      alive: bool
      ready: bool
      ads: float32
      ammo: int
      reserve: int
      weapon: string
      kills: int
      deaths: int
      acknowledgedInput: int64 }

[<CLIMutable>]
type GrenadeSnapshot =
    { ownerId: int
      x: float32
      y: float32
      z: float32
      fuse: float32 }

[<CLIMutable>]
type EventSnapshot =
    { id: int64
      tick: int64
      kind: string
      entityId: int
      recipientId: int
      x: float32
      y: float32
      z: float32
      dx: float32
      dy: float32
      dz: float32
      value: float32
      text: string }

[<CLIMutable>]
type SnapshotMessage =
    { ``type``: string
      version: int
      tick: int64
      mode: string
      level: string
      phase: string
      alliesScore: int
      axisScore: int
      players: PlayerSnapshot array
      grenades: GrenadeSnapshot array
      events: EventSnapshot array }

[<CLIMutable>]
type LeaderboardPlayer =
    { id: int
      name: string
      team: string
      kills: int
      deaths: int
      alive: bool
      weapon: string }

[<CLIMutable>]
type LeaderboardRoom =
    { mode: string
      phase: string
      alliesScore: int
      axisScore: int
      connectedPlayers: int
      players: LeaderboardPlayer array }

[<CLIMutable>]
type LeaderboardResponse =
    { generatedAt: DateTimeOffset
      persistence: string
      capacityPerRoom: int
      rooms: LeaderboardRoom array }

[<CLIMutable>]
type ArsenalWeapon =
    { name: string
      fireMode: string
      damagePerProjectile: float32
      projectilesPerShot: int
      maximumDamagePerShot: float32
      roundsPerMinute: float32
      magazineSize: int
      reloadSeconds: float32
      aimDownSightSeconds: float32
      hipSpread: float32
      aimDownSightSpread: float32
      penetration: float32
      availability: string }

[<CLIMutable>]
type ArsenalResponse =
    { generatedFrom: string
      weapons: ArsenalWeapon array }

[<RequireQualifiedAccess>]
module Protocol =
    [<Literal>]
    let Version = 1

    [<Literal>]
    let MaxMessageBytes = 16 * 1024

    let jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    let tryString (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.String -> property.GetString() |> Option.ofObj
        | _ -> None

    let tryInt64 (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetInt64() with true, value -> Some value | _ -> None
        | _ -> None

    let tryFloat32 (name: string) (element: JsonElement) =
        match element.TryGetProperty name with
        | true, property when property.ValueKind = JsonValueKind.Number ->
            match property.TryGetSingle() with
            | true, value when Single.IsFinite value -> Some value
            | _ -> None
        | _ -> None

    let serialize<'a> (value: 'a) = JsonSerializer.SerializeToUtf8Bytes(value, jsonOptions)

    let welcome (EntityId playerId) token levelName mapHash =
        { ``type`` = "welcome"
          version = Version
          playerId = playerId
          sessionToken = token
          tickRate = Tuning.TickRate
          snapshotRate = 20
          level = levelName
          mapHash = mapHash }

    let snapshot state =
        let players =
            state.Players
            |> Map.toArray
            |> Array.filter (fun (_, player) -> player.Connected)
            |> Array.map (fun (_, player) ->
                let (EntityId id) = player.Id
                { id = id
                  name = player.Name
                  team = string player.Team
                  x = player.Position.X
                  y = player.Position.Y
                  z = player.Position.Z
                  // Velocity and stance make the client's replayed prediction
                  // bit-identical to the server's simulation (the QuakeWorld
                  // lesson: rebasing from position alone cannot be smooth).
                  vx = player.Velocity.X
                  vy = player.Velocity.Y
                  vz = player.Velocity.Z
                  yaw = player.Yaw
                  pitch = player.Pitch
                  stance = string player.Stance
                  crouchLatched = player.CrouchLatched
                  crouchPrevHeld = player.CrouchPrevHeld
                  health = Units.raw player.Health
                  alive = player.Alive
                  ready = player.Ready
                  ads = player.Ads
                  ammo = player.Weapon.InMag
                  reserve = player.Weapon.Reserve
                  weapon = player.Weapon.Class.Name
                  kills = player.Kills
                  deaths = player.Deaths
                  acknowledgedInput = player.LastInputSequence })
        let grenades =
            state.Grenades
            |> Array.map (fun grenade ->
                let (EntityId ownerId) = grenade.Owner
                { ownerId = ownerId
                  x = grenade.Position.X
                  y = grenade.Position.Y
                  z = grenade.Position.Z
                  fuse = Units.raw grenade.Fuse })
        let eventSnapshot (replicated: ReplicatedEvent) =
            let make (kind: string) (entity: EntityId) (position: Vector3) (direction: Vector3) (value: float32) (text: string) =
                let (EntityId entityId) = entity
                { id = replicated.Id; tick = replicated.Tick; kind = kind; entityId = entityId
                  recipientId = replicated.Recipient |> Option.map (fun (EntityId id) -> id) |> Option.defaultValue 0
                  x = position.X; y = position.Y; z = position.Z
                  dx = direction.X; dy = direction.Y; dz = direction.Z; value = value; text = text }
            match replicated.Event with
            | ShotFired(shooter, position, direction, weapon) -> make "shot" (defaultArg shooter (EntityId 0)) position direction 0.0f weapon
            | Impact(position, normal, surface) -> make "impact" (EntityId 0) position normal 0.0f (string surface)
            | HitConfirmed(victim, lethal) -> make "hit" victim Vector3.Zero Vector3.Zero (if lethal then 1.0f else 0.0f) ""
            | BloodImpact(position, direction, headshot) -> make "blood" (EntityId 0) position direction (if headshot then 1.0f else 0.0f) ""
            | HeadGib(position, direction) -> make "head-gib" (EntityId 0) position direction 0.0f ""
            | PlayerHurt(direction, health) -> make "hurt" (EntityId 0) Vector3.Zero direction (Units.raw health) ""
            | Explosion(position, radius) -> make "explosion" (EntityId 0) position Vector3.Zero radius ""
            | FootStep(position, surface) -> make "footstep" (EntityId 0) position Vector3.Zero 0.0f (string surface)
            | Subtitle(speaker, line) -> make "subtitle" (EntityId 0) Vector3.Zero Vector3.Zero 0.0f $"{speaker}: {line}"
            | ObjectiveUpdated index -> make "objective" (EntityId index) Vector3.Zero Vector3.Zero 0.0f ""
        let events = state.Events |> List.map eventSnapshot |> List.toArray
        { ``type`` = "snapshot"
          version = Version
          tick = state.Tick
          mode = string state.Mode
          level = state.LevelName
          phase = string state.Phase
          alliesScore = state.AlliesScore
          axisScore = state.AxisScore
          players = players
          grenades = grenades
          events = events }

    let leaderboard states =
        let rooms =
            states
            |> Array.map (fun state ->
                let players =
                    state.Players
                    |> Map.toArray
                    |> Array.map snd
                    |> Array.filter (fun player -> player.Connected)
                    |> Array.sortBy (fun player -> -player.Kills, player.Deaths, player.Name)
                    |> Array.map (fun player ->
                        let (EntityId id) = player.Id
                        { id = id
                          name = player.Name
                          team = string player.Team
                          kills = player.Kills
                          deaths = player.Deaths
                          alive = player.Alive
                          weapon = player.Weapon.Class.Name })
                { mode = string state.Mode
                  phase = string state.Phase
                  alliesScore = state.AlliesScore
                  axisScore = state.AxisScore
                  connectedPlayers = players.Length
                  players = players })
        { generatedAt = DateTimeOffset.UtcNow
          persistence = "Memory, bravely surviving until the next deploy."
          capacityPerRoom = 16
          rooms = rooms }

    let arsenal () =
        let onlineNames = Tuning.onlineWeapons |> Array.map _.Name |> Set.ofArray
        let weapons = Array.append Tuning.onlineWeapons [| Tuning.mg42 |]
        { generatedFrom = "Live Ironsight.Core.Tuning weapon definitions"
          weapons =
            weapons
            |> Array.map (fun weapon ->
                { name = weapon.Name
                  fireMode = string weapon.Mode
                  damagePerProjectile = Units.raw weapon.Damage
                  projectilesPerShot = weapon.Pellets
                  maximumDamagePerShot = Units.raw weapon.Damage * float32 weapon.Pellets
                  roundsPerMinute = weapon.RoundsPerMin
                  magazineSize = weapon.MagSize
                  reloadSeconds = Units.raw weapon.ReloadTime
                  aimDownSightSeconds = Units.raw weapon.AdsTime
                  hipSpread = weapon.HipSpread
                  aimDownSightSpread = weapon.AdsSpread
                  penetration = weapon.Penetration
                  availability = if Set.contains weapon.Name onlineNames then "Player loadout" else "Mounted weapon" }) }
