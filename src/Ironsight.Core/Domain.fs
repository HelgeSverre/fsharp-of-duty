namespace Ironsight

open System.Numerics

[<Struct>]
type EntityId = EntityId of int

type Team = Allies | Axis

type GameMode = Campaign | FreeForAll | TeamDeathmatch

type FireMode = SemiAuto | FullAuto | BoltAction

type WeaponKind = Rifle | SniperRifle | Smg | Pistol | Shotgun | MachineGun

type WeaponClass =
    { Name: string
      Mode: FireMode
      Kind: WeaponKind
      Damage: float32<hp>
      RoundsPerMin: float32
      MagSize: int
      ReloadTime: float32<s>
      Pellets: int
      AdsTime: float32<s>
      HipSpread: float32
      AdsSpread: float32
      Recoil: Vector2 array
      Penetration: float32
      HeadshotMultiplier: float32
      MuzzleDistance: float32 }

type WeaponState =
    | Ready
    | Cooling of remaining: float32<s>
    | Reloading of remaining: float32<s>
    | Switching of incoming: int * remaining: float32<s>

type WeaponSlot =
    { Class: WeaponClass
      State: WeaponState
      InMag: int
      Reserve: int
      BurstIx: int
      Bloom: float32 }

type Stance = Standing | Crouched | Prone

type GrenadeHand =
    | GrenadeIdle of count: int
    | Cooking of fuse: float32<s> * count: int

type Player =
    { Id: EntityId
      Position: Vector3
      Velocity: Vector3
      Yaw: float32
      Pitch: float32
      Stance: Stance
      CrouchLatched: bool
      CrouchPrevHeld: bool
      Sprinting: bool
      Ads: float32
      Health: float32<hp>
      RegenIn: float32<s>
      Slots: WeaponSlot array
      Active: int
      Grenade: GrenadeHand }

type CoverPoint =
    { Pos: Vector3
      PeekDir: Vector3
      Crouch: bool
      Owner: Team option }

type AiBehavior =
    | Idle
    | AdvancingTo of waypoint: Vector3 * path: Vector3 list
    | InCover of CoverPoint * peekPhase: float32<s>
    | Flanking of target: EntityId * path: Vector3 list
    | Suppressed of recoverIn: float32<s>
    | Dying of sinceDeath: float32<s>
    | DyingHeadshot of sinceDeath: float32<s>

type Soldier =
    { Id: EntityId
      Team: Team
      Position: Vector3
      Facing: float32
      Stance: Stance
      Health: float32<hp>
      Behavior: AiBehavior
      Weapon: WeaponSlot
      Squad: int
      Contacts: Map<EntityId, struct (Vector3 * float32<s>)>
      Suppression: float32
      AnimPhase: float32 }

type Material = Brick | Plaster | Wood | Mud | Snow | Sandbag | Metal | UniformOlive | UniformFeldgrau | Skin

type Brush =
    { Bounds: Aabb
      Material: Material }

[<Struct>]
type MeshVertex =
    { Position: Vector3
      Normal: Vector3
      MaterialId: int }

type NavNode =
    { Position: Vector3
      Neighbours: int array }

type BrushGrid =
    { CellSize: float32
      Cells: Map<struct (int * int), int array> }

type TriggerCondition =
    | EnterVolume of Aabb
    | SquadDead of squad: int
    | ObjectiveDone of index: int
    | Delay of float32<s>

type ScriptAction =
    | SpawnWave of team: Team * count: int * position: Vector3
    | SetObjective of index: int * completed: bool
    | Say of speaker: string * line: string
    | OpenPath of brushIndex: int
    | EndMission

type ScriptRule =
    { Condition: TriggerCondition
      Action: ScriptAction
      Fired: bool }

type MountedGun =
    { Position: Vector3
      Facing: float32
      Team: Team }

type Level =
    { Name: string
      Revision: int
      Bounds: Aabb
      Brushes: Brush array
      BrushGrid: BrushGrid
      Cover: CoverPoint array
      Spawns: struct (Team option * Vector3) array
      MountedGuns: MountedGun array
      MissionRules: ScriptRule array
      Nav: NavNode array
      Vertices: MeshVertex array
      Indices: uint32 array }

type Grenade =
    { Owner: EntityId
      Position: Vector3
      Velocity: Vector3
      Fuse: float32<s> }

type Objective = { Text: string; Done: bool }

type ScriptState =
    { MissionTime: float32<s>
      Ended: bool
      Rules: ScriptRule array }

type SquadBlackboard =
    { Contacts: Map<EntityId, struct (Vector3 * float32<s>)>
      Objective: Vector3
      Suppressor: EntityId option }

type RoundState =
    { Number: int
      PlayerScore: int
      EnemyScore: int
      ResetIn: float32<s> option
      LastResult: string option }

type World =
    { Tick: int64
      Rng: Rng.State
      Player: Player
      Soldiers: Soldier array
      Grenades: Grenade array
      Level: Level
      Squads: Map<int, SquadBlackboard>
      Script: ScriptState
      Objectives: Objective array
      Round: RoundState option }

type GameEvent =
    | ShotFired of shooter: EntityId option * origin: Vector3 * direction: Vector3 * weapon: string
    | Impact of position: Vector3 * normal: Vector3 * surface: Material
    | HitConfirmed of victim: EntityId * lethal: bool
    | BloodImpact of position: Vector3 * direction: Vector3 * headshot: bool
    | HeadGib of position: Vector3 * direction: Vector3
    | PlayerHurt of fromDirection: Vector3 * newHealth: float32<hp>
    | Explosion of position: Vector3 * radius: float32
    | FootStep of position: Vector3 * surface: Material
    | Subtitle of speaker: string * line: string
    | ObjectiveUpdated of index: int

[<System.Flags>]
type InputButtons =
    | None = 0
    | Fire = 1
    | Ads = 2
    | Sprint = 4
    | Reload = 8
    | Crouch = 16
    | Prone = 32
    | Jump = 64
    | Grenade = 128
    | Weapon1 = 256
    | Weapon2 = 512
    | Weapon3 = 1024
    | Weapon4 = 2048
    | Weapon5 = 4096

[<Struct>]
type InputFrame =
    { Sequence: int64
      Move: Vector2
      Look: Vector2
      Buttons: InputButtons }

type ShotRequest =
    { DirectionOffset: Vector2
      Damage: float32<hp>
      Penetration: float32
      HeadshotMultiplier: float32 }
