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
      mapHash: string
      /// Which room the joiner actually landed in. A client that asked by mode
      /// rather than by id would otherwise have no way to know.
      room: string }

/// One carried weapon. The array replaced four scalar fields once an online
/// player carried more than one gun.
[<CLIMutable>]
type WeaponSlotSnapshot =
    { weapon: string
      ammo: int
      reserve: int
      // Seconds left on the reload, 0 when not reloading. Without it the client
      // rebuilds every online weapon slot as Ready and the reload bar, the
      // viewmodel and the reload SFX all go missing.
      reloadRemaining: float32 }

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
      health: float32
      alive: bool
      ready: bool
      ads: float32
      slots: WeaponSlotSnapshot array
      /// Index into `slots` of the gun in hand.
      active: int
      // A switch in flight, replicated so the viewmodel plays the raise rather
      // than popping straight to the new gun. Player-level because only the
      // active slot can ever be Switching. switchTo = -1 when idle.
      switchTo: int
      switchRemaining: float32
      // The active slot, duplicated flat. Costs four fields and keeps a client
      // built before kits showing the right gun instead of a team default.
      ammo: int
      reserve: int
      weapon: string
      reloadRemaining: float32
      kills: int
      deaths: int
      // Round high-water mark only; the live streak stays server-side because
      // nothing on the client renders it.
      bestStreak: int
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
    { /// Stable key a client puts in its hello to join this exact room.
      id: string
      /// Operator-chosen label; what the server browser lists.
      name: string
      /// Per room now that rooms size independently; the response still
      /// carries capacityPerRoom for clients that predate this field.
      capacity: int
      mode: string
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
      kind: string
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
      falloffStartMetres: float32
      falloffEndMetres: float32
      minimumDamagePerProjectile: float32
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

    let welcome (EntityId playerId) token levelName mapHash room =
        { ``type`` = "welcome"
          version = Version
          playerId = playerId
          sessionToken = token
          tickRate = Tuning.TickRate
          snapshotRate = 20
          level = levelName
          mapHash = mapHash
          room = room }

    /// One joiner's welcome. The level must be the one that host runs *now*,
    /// not the one the process booted with: /map swaps it per host. A swapped
    /// level is always a builtin the client resolves by name, so it needs no
    /// hash — and only the boot map's bytes are served by /maps/{hash}, so
    /// advertising its hash would send the joiner downloading the wrong map.
    let welcomeFor playerId token (bootLevel: string) (bootHash: string) room (state: MatchState) =
        welcome playerId token state.LevelName (if state.LevelName = bootLevel then bootHash else "") room

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
                  health = Units.raw player.Health
                  alive = player.Alive
                  ready = player.Ready
                  ads = player.Ads
                  slots =
                    player.Slots
                    |> Array.map (fun slot ->
                        { weapon = slot.Class.Name
                          ammo = slot.InMag
                          reserve = slot.Reserve
                          reloadRemaining = (match slot.State with Reloading remaining -> Units.raw remaining | _ -> 0.0f) })
                  active = player.Active
                  switchTo = (match player.Slots[player.Active].State with Switching(incoming, _) -> incoming | _ -> -1)
                  switchRemaining = (match player.Slots[player.Active].State with Switching(_, remaining) -> Units.raw remaining | _ -> 0.0f)
                  ammo = player.Slots[player.Active].InMag
                  reserve = player.Slots[player.Active].Reserve
                  weapon = player.Slots[player.Active].Class.Name
                  reloadRemaining = (match player.Slots[player.Active].State with Reloading remaining -> Units.raw remaining | _ -> 0.0f)
                  kills = player.Kills
                  deaths = player.Deaths
                  bestStreak = player.BestStreak
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
            // kill: entityId = victim, x = killer id (0 = world/suicide),
            // value = headshot, text = weapon. Mirrored in OnlineWorld.eventToGameEvent.
            | Kill(killer, victim, weapon, headshot) ->
                let (EntityId killerId) = defaultArg killer (EntityId 0)
                make "kill" victim (Vector3(float32 killerId, 0.0f, 0.0f)) Vector3.Zero (if headshot then 1.0f else 0.0f) weapon
            | PlayerJoined(player, name) -> make "joined" player Vector3.Zero Vector3.Zero 0.0f name
            | PlayerLeft(player, name) -> make "left" player Vector3.Zero Vector3.Zero 0.0f name
            | PhaseChanged phase -> make "phase-change" (EntityId 0) Vector3.Zero Vector3.Zero 0.0f phase
            // chat: entityId = sender (0 = server/system), text = "{name}\t{line}".
            // Tab separates safely because Multiplayer.sanitizeText strips control
            // scalars from both halves. Mirrored in OnlineWorld.eventToGameEvent.
            | Chat(sender, name, line) ->
                make "chat" (defaultArg sender (EntityId 0)) Vector3.Zero Vector3.Zero 0.0f $"{name}\t{line}"
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

    /// One viewer's wire snapshot. A recipient tag is a routing hint, not a
    /// secret, once it is serialized: whispers addressed to anyone else are
    /// dropped here rather than merely hidden by the client.
    let snapshotFor viewer state =
        snapshot { state with Events = state.Events |> List.filter (fun event -> event.Recipient = None || event.Recipient = Some viewer) }

    /// Rooms arrive as (id, name, capacity, state) so the response can name
    /// them; the server browser lists one row per entry.
    let leaderboard (rooms: (string * string * int * MatchState) array) =
        let rooms =
            rooms
            |> Array.map (fun (roomId, roomName, capacity, state) ->
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
                          weapon = player.Slots[player.Active].Class.Name })
                { id = roomId
                  name = roomName
                  capacity = capacity
                  mode = string state.Mode
                  phase = string state.Phase
                  alliesScore = state.AlliesScore
                  axisScore = state.AxisScore
                  connectedPlayers = players.Length
                  players = players })
        { generatedAt = DateTimeOffset.UtcNow
          persistence = "Stats are in-memory and reset on redeploy."
          // Legacy single number for clients that predate per-room capacity;
          // the largest room is the least wrong answer for a mixed server.
          capacityPerRoom = if Array.isEmpty rooms then ServerConfig.DefaultMaxPlayers else rooms |> Array.map (fun room -> room.capacity) |> Array.max
          rooms = rooms }

    let arsenal () =
        let onlineNames = Tuning.onlineWeapons |> Array.map _.Name |> Set.ofArray
        let weapons = Array.append Tuning.onlineWeapons [| Tuning.mg42 |]
        { generatedFrom = "Live Ironsight.Core.Tuning weapon definitions"
          weapons =
            weapons
            |> Array.map (fun weapon ->
                let struct (falloffStart, falloffEnd, retained) = Tuning.falloffWindow weapon.Kind
                { name = weapon.Name
                  kind = string weapon.Kind
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
                  falloffStartMetres = falloffStart
                  falloffEndMetres = falloffEnd
                  minimumDamagePerProjectile = Units.raw weapon.Damage * retained
                  availability = if Set.contains weapon.Name onlineNames then "Player loadout" else "Mounted weapon" }) }
