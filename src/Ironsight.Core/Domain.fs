namespace Ironsight

open System.Numerics

[<Struct>]
type EntityId = EntityId of int

type Team = Allies | Axis

type GameMode = FreeForAll | TeamDeathmatch

type FireMode = SemiAuto | FullAuto | BoltAction

type WeaponKind = Rifle | SniperRifle | Smg | Pistol | Shotgun | MachineGun

/// How weapon input is resolved. Most physical projectile cases are an
/// offline toybox. Bow and melee have authoritative online resolvers so their
/// charge, held-contact and sweep state can be shared by both modes.
type WeaponMechanism =
    | Hitscan
    | Paintball
    | FoamDart
    | Rocket
    | FlameJet
    | WaterJet
    | Nail
    | Harpoon
    | Bow
    | Laser
    | Katana

/// Melee is resolved as a swept volume, never as a zero-range bullet. The
/// attack variant is carried with the server request so secondary fire can
/// select the katana's top-down cut without trusting a client-reported hit.
type MeleeAttack =
    | KatanaSweep
    | KatanaOverhead

/// Anatomical regions are deliberately finer than the old head/torso/legs
/// ballistics capsules. They are stable network identifiers as well as the
/// authored boundaries used to split a ragdoll.
type BodyPart =
    | BodyHead
    | BodyTorso
    | BodyLeftUpperArm
    | BodyLeftLowerArm
    | BodyRightUpperArm
    | BodyRightLowerArm
    | BodyLeftUpperLeg
    | BodyLeftLowerLeg
    | BodyRightUpperLeg
    | BodyRightLowerLeg

type CutSite =
    | CutNeck
    | CutWaist
    | CutLeftUpperArm
    | CutLeftLowerArm
    | CutRightUpperArm
    | CutRightLowerArm
    | CutLeftUpperLeg
    | CutLeftLowerLeg
    | CutRightUpperLeg
    | CutRightLowerLeg

/// Immutable server/offline result of a lethal melee contact. Geometry is
/// victim-local so snapshots can recreate the same sever after the corpse has
/// entered a client-local ragdoll. Fraction selects the point along the named
/// anatomical segment; the three directions retain the real blade plane.
type CutDescriptor =
    { DeathRevision: int64
      Site: CutSite
      Fraction: float32
      LocalPoint: Vector3
      LocalPlaneNormal: Vector3
      LocalBladeTangent: Vector3
      LocalSweepDirection: Vector3
      Impulse: Vector3
      CosmeticSeed: int }

type WeaponClass =
    { Name: string
      Mode: FireMode
      Kind: WeaponKind
      Mechanism: WeaponMechanism
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
    | Drawing of charge: float32<s>
    | Cooling of remaining: float32<s>
    | Reloading of remaining: float32<s>
    | Switching of incoming: int * remaining: float32<s>

type WeaponSlot =
    { Class: WeaponClass
      State: WeaponState
      InMag: int
      Reserve: int
      BurstIx: int
      Bloom: float32
      LastMelee: MeleeAttack option }

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
      Sprinting: bool
      Ads: float32
      Health: float32<hp>
      RegenIn: float32<s>
      Slots: WeaponSlot array
      Active: int
      Grenade: GrenadeHand }

    member this.IsAlive = this.Health > Units.health 0.0f
    member this.IsDead = not this.IsAlive

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

    member this.IsAlive = this.Health > Units.health 0.0f
    member this.IsDead = not this.IsAlive

type Material =
    | Brick | Plaster | Wood | Mud | Snow | Sandbag | Metal
    | UniformOlive | UniformFeldgrau | Skin | Water
    // Render-only palette entries. Appended so existing map material tags stay
    // stable; collision geometry never uses these cases.
    | PaintRed | PaintBlue | PaintGreen | PaintYellow | PaintPurple | PaintOrange
    | FoamBlue | FoamOrange
    | ToolBlack | WaterBlue | WetDark

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

/// A single collidable face. Boxes compile down to twelve of these, and sloped
/// terrain emits them directly, so every collision query has one shape to
/// handle instead of a special case per primitive.
[<Struct>]
type Tri =
    { A: Vector3
      B: Vector3
      C: Vector3
      Normal: Vector3
      Material: Material }

/// The world as triangles, with the same flat XZ hash the brush grid uses.
type CollisionMesh =
    { Triangles: Tri array
      CellSize: float32
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
      /// Geometry that is not a box: ramps, terrain, oriented walls. Kept apart
      /// from Brushes so a rebuild can re-merge it instead of dropping it.
      Sloped: Tri array
      Collision: CollisionMesh
      Cover: CoverPoint array
      Spawns: struct (Team option * Vector3) array
      MountedGuns: MountedGun array
      MissionRules: ScriptRule array
      Nav: NavNode array
      /// Sea/flood height. Render-only surface; the sim reads it to slow
      /// anyone wading below it.
      WaterLevel: float32 option
      Vertices: MeshVertex array
      Indices: uint32 array }

type Grenade =
    { Owner: EntityId
      Position: Vector3
      Velocity: Vector3
      Fuse: float32<s> }

type HarpoonAttachment =
    { Victim: EntityId
      DistanceBehindTip: float32 }

type SpecialProjectileKind =
    | PaintBall of color: Material
    | NerfDart
    | BazookaRocket
    | WaterDroplet
    | NailRound
    | HarpoonRound of skewered: HarpoonAttachment list
    | ArrowRound of damage: float32<hp>

type SpecialProjectile =
    { Owner: EntityId
      Kind: SpecialProjectileKind
      Position: Vector3
      Velocity: Vector3
      DistanceTravelled: float32
      Bounces: int
      Remaining: float32<s> }

type PersistentMark =
    | PaintSplat of position: Vector3 * normal: Vector3 * color: Material * target: EntityId option * localOffset: Vector3
    | StuckDart of position: Vector3 * normal: Vector3 * target: EntityId option * localOffset: Vector3
    | WetPatch of position: Vector3 * normal: Vector3 * target: EntityId option * localOffset: Vector3 * remaining: float32<s> * saturation: float32
    | StuckNail of position: Vector3 * normal: Vector3 * target: EntityId option * localOffset: Vector3
    | EmbeddedHarpoon of tip: Vector3 * direction: Vector3 * skewered: HarpoonAttachment list
    | StuckArrow of position: Vector3 * direction: Vector3 * target: EntityId option * localOffset: Vector3

type ElementalStatus =
    { WetFor: float32<s>
      BurningFor: float32<s>
      Heat: float32<s>
      BurnOwner: EntityId option }

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
      SpecialProjectiles: SpecialProjectile array
      PersistentMarks: PersistentMark array
      ElementalStatus: Map<EntityId, ElementalStatus>
      /// Persistent while a corpse exists. Events are transient, so this map
      /// is also what makes an online late join reconstruct the same cut.
      Dismemberments: Map<EntityId, CutDescriptor>
      PaintColor: Material
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
    | PaintImpact of position: Vector3 * normal: Vector3 * color: Material
    | DartImpact of position: Vector3 * normal: Vector3 * stuck: bool
    | RocketDud of position: Vector3 * normal: Vector3
    | Backblast of position: Vector3 * direction: Vector3
    | FlameStream of origin: Vector3 * endpoint: Vector3
    | LaserBeam of origin: Vector3 * endpoint: Vector3
    | MeleeTrace of attacker: EntityId * origin: Vector3 * endpoint: Vector3 * attack: MeleeAttack
    | Dismembered of victim: EntityId * position: Vector3 * cut: CutDescriptor
    | FlameImpact of position: Vector3 * normal: Vector3
    | WaterImpact of position: Vector3 * normal: Vector3
    | NailImpact of position: Vector3 * normal: Vector3 * stuck: bool
    | HarpoonSkewer of position: Vector3 * direction: Vector3 * victim: EntityId
    | HarpoonEmbedded of position: Vector3 * normal: Vector3
    | ArrowImpact of position: Vector3 * normal: Vector3 * stuck: bool
    | Ignited of victim: EntityId * position: Vector3
    | Extinguished of victim: EntityId * position: Vector3
    | Burning of victim: EntityId * position: Vector3
    | FootStep of position: Vector3 * surface: Material
    | Subtitle of speaker: string * line: string
    | ObjectiveUpdated of index: int
    /// Killer is None for world/self-inflicted kills. Names are resolved by the
    /// client at receipt, while both players are still in the snapshot.
    | Kill of killer: EntityId option * victim: EntityId * weapon: string * headshot: bool
    | PlayerJoined of player: EntityId * name: string
    | PlayerLeft of player: EntityId * name: string
    /// Phase as a string: MatchPhase is declared after this union.
    | PhaseChanged of phase: string
    /// Sender is None for server/system lines. The name is baked in for the
    /// same reason Kill names are resolved at receipt: the sender can
    /// disconnect before the row expires from the client's chat log.
    | Chat of sender: EntityId option * name: string * text: string

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
    /// Scroll wheel, Half-Life style: step through the carried weapons in
    /// slot order rather than jumping to a category.
    | WeaponNext = 8192
    | WeaponPrev = 16384

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
      HeadshotMultiplier: float32
      Kind: WeaponKind
      Melee: MeleeAttack option }
