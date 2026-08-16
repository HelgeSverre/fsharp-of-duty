# Original design specification

> Historical design document. This was the blueprint used to start IRONSIGHT;
> it is not a statement that every described feature is complete. The current
> repository layout, commands, and implemented behavior are documented in the
> [project README](../README.md), while multiplayer behavior is documented in
> [MULTIPLAYER.md](MULTIPLAYER.md).

## CoD2-style FPS in F#, zero external assets

Working title: **IRONSIGHT** (rename freely)

Target: a linear, scripted, squad-based WWII-flavored FPS in the Call of Duty 2 mold — hitscan weapons, ADS, regenerating health, grenade cooking, cover-using AI — where **every mesh, texture, sound, font, and level is generated in code at startup or on demand**. No content pipeline, no asset files, `dotnet run` and you're in.

---

## 1. Stack

| Concern | Choice | Why |
|---|---|---|
| Runtime | .NET 9, F# | Modern GC, `System.Numerics` SIMD |
| Windowing/Input | **Silk.NET.Windowing + Silk.NET.Input** | Thin, modern, no engine opinions, first-class on .NET |
| Graphics | **Silk.NET.OpenGL** (GL 4.1 core) | 4.1 keeps macOS in play; you don't need more for 2005-era visuals |
| Audio | **Silk.NET.OpenAL** | You'll synthesize PCM buffers yourself; OpenAL just plays + spatializes them |
| Math | **System.Numerics** (`Vector3`, `Matrix4x4`, `Quaternion`) | JIT-intrinsified, allocation-free structs |
| Physics | **Hand-rolled kinematic** (collide-and-slide capsule vs. brush list) | CoD2 levels are static geometry + a few grenades; a full physics engine is dead weight |
| ECS | **None. Plain records + arrays.** | An FPS of this style has <200 live entities. Records, DUs, and arrays-of-structs are more idiomatic F# and easier to read than any ECS. (If you later want one, Garnet is the F#-native option.) |
| Noise | Port/embed a single-file FastNoiseLite-style module in F# | One file, no dependency |

Everything below assumes this stack.

### The one architectural rule

**Functional core, imperative shell.**

- The *simulation* is `step : Input -> World -> struct (World * GameEvent list)` — deterministic, no I/O, no GL, no AL, testable in `dotnet test` headlessly.
- The *shell* owns the window, GL objects, AL sources, and mutable interpolation state. It feeds inputs in, applies `GameEvent`s out (play sound, spawn tracer, shake screen, print subtitle).

Fixed 60 Hz tick with an accumulator; rendering interpolates between the previous and current `World` using alpha. This buys you determinism (replays, headless AI tests, seed-stable levels) for nearly free.

---

## 2. Project layout (compile order is the architecture)

F#'s linear compile order is a feature: dependencies can only point *up* the file list, so the layering below is compiler-enforced.

```
src/Ironsight/
  Prelude.fs            // small helpers, Rng (splitmix64), units of measure
  Noise.fs              // value/simplex noise, fbm, domain warp
  MathEx.fs             // AABB, Ray, Plane, capsule sweeps, easing
  Domain.fs             // ALL game types. The heart of the design. No logic.
  Tuning.fs             // weapon tables, movement constants, AI numbers

  ProcGen/
    Materials.fs        // material DU + params consumed by the uber-shader
    MeshGen.fs          // mesh combinators: box, cylinder, lathe, union, mirror
    Guns.fs             // viewmodels/worldmodels built from MeshGen combinators
    Humanoid.fs         // skeleton + capsule/box body parts + proc animation
    LevelDsl.fs         // declarative layout combinators (rooms, streets, trenches)
    LevelCompile.fs     // DSL -> brushes -> render mesh + collision set + navgraph
    AudioSynth.fs       // DSP: gunshots, footsteps, explosions -> PCM buffers
    FontGen.fs          // stroke font -> rasterized atlas at startup

  Sim/
    Movement.fs         // capsule controller: accel, friction, stances, sprint
    Ballistics.fs       // hitscan rays, spread, penetration, grenade arcs
    Weapons.fs          // weapon state machine step
    Damage.fs           // health, regen, hit reactions, death
    Perception.fs       // AI senses: FOV cone + LOS rays + heard-shots memory
    AiBrain.fs          // behavior DU step, cover selection, squad blackboard
    Script.fs           // mission script: objectives, triggers, spawn waves
    Sim.fs              // World.step — composes all of the above, emits events

  Shell/
    Gl.fs               // tiny GL wrapper: Buffer, Vao, Shader, Texture, Fbo
    Shaders.fs          // GLSL sources as F# string literals
    Renderer.fs         // sun + shadow map, level pass, characters, viewmodel
    Hud.fs              // quads + FontGen atlas: crosshair, ammo, hit vignette
    Particles.fs        // muzzle flash, tracers, dust, smoke (CPU-simmed quads)
    Audio.fs            // AL sources pool, 3D positioning, event -> buffer map
    Input.fs            // Silk input -> InputFrame record
    Program.fs          // game loop, accumulator, event dispatch
```

---

## 3. Domain modeling (the idiomatic-F# part)

Everything in `Domain.fs`. Records for state, DUs for state *machines*, structs on the hot path, events as a DU list. No classes, no interfaces in the core.

```fsharp
[<Struct>] type EntityId = EntityId of int
[<Measure>] type s        // seconds
[<Measure>] type hp

// ---------- Weapons ----------

type FireMode = SemiAuto | FullAuto | BoltAction

type WeaponClass = {
    Name        : string
    Mode        : FireMode
    Damage      : float32<hp>
    RoundsPerMin: float32
    MagSize     : int
    ReloadTime  : float32<s>
    AdsTime     : float32<s>
    HipSpread   : float32          // radians
    AdsSpread   : float32
    Recoil      : Vector2[]        // per-shot kick pattern, CoD-style
    Penetration : float32          // how many cm of "wood" a round survives
}

/// The state machine. Impossible states are unrepresentable:
/// you cannot be mid-reload with a fire cooldown.
type WeaponState =
    | Ready
    | Cooling   of remaining: float32<s>
    | Reloading of remaining: float32<s>
    | Switching of incoming: int * remaining: float32<s>

type WeaponSlot = {
    Class   : WeaponClass
    State   : WeaponState
    InMag   : int
    Reserve : int
    BurstIx : int                  // index into Recoil pattern, resets on release
}

// ---------- Player ----------

type Stance = Standing | Crouched | Prone

type Player = {
    Position  : Vector3            // feet
    Velocity  : Vector3
    Yaw       : float32
    Pitch     : float32
    Stance    : Stance
    Sprinting : bool
    Ads       : float32            // 0..1, eased; drives FOV + spread + viewmodel
    Health    : float32<hp>
    RegenIn   : float32<s>         // countdown since last damage (CoD regen)
    Slots     : WeaponSlot[]       // 2 primaries + pistol, CoD2-style
    Active    : int
    Grenade   : GrenadeHand
}
and GrenadeHand =
    | GrenadeIdle of count: int
    | Cooking     of fuse: float32<s> * count: int   // hold-to-cook

// ---------- AI ----------

type CoverPoint = { Pos: Vector3; PeekDir: Vector3; Crouch: bool }

type AiBehavior =
    | Idle
    | AdvancingTo of waypoint: Vector3 * path: Vector3 list
    | InCover     of CoverPoint * peekPhase: float32<s>
    | Flanking    of target: EntityId * path: Vector3 list
    | Suppressed  of recoverIn: float32<s>
    | Dying       of sinceDeath: float32<s>

type Soldier = {
    Id       : EntityId
    Team     : Team
    Position : Vector3
    Facing   : float32
    Health   : float32<hp>
    Behavior : AiBehavior
    Weapon   : WeaponSlot
    Squad    : int
    /// perception memory: last known enemy positions with staleness
    Contacts : Map<EntityId, struct (Vector3 * float32<s>)>
    AnimPhase: float32             // drives procedural walk/aim cycles
}
and Team = Allies | Axis

// ---------- World & events ----------

type Objective = { Text: string; Done: bool }

type World = {
    Tick      : int
    Rng       : Rng.State          // deterministic, threaded through step
    Player    : Player
    Soldiers  : Soldier[]
    Grenades  : Grenade[]
    Level     : Level              // compiled: brushes, navgraph, covers, spawns
    Script    : ScriptState        // mission progress, pending triggers
    Objectives: Objective[]
}

/// Everything the imperative shell needs to know about, and nothing else.
type GameEvent =
    | ShotFired    of shooter: EntityId option * origin: Vector3 * dir: Vector3 * weapon: string
    | Impact       of pos: Vector3 * normal: Vector3 * surface: Material
    | HitConfirmed of victim: EntityId * lethal: bool
    | PlayerHurt   of fromDir: Vector3 * newHealth: float32<hp>
    | Explosion    of pos: Vector3 * radius: float32
    | FootStep     of pos: Vector3 * surface: Material
    | Subtitle     of speaker: string * line: string
    | ObjectiveUpdated of index: int
```

Simulation modules are then just functions between these types:

```fsharp
module Weapons =
    /// Pure. Returns the updated slot plus zero or more shots taken this tick.
    val step : dt: float32<s> -> trigger: TriggerState -> ads: float32
             -> rng: byref<Rng.State> -> slot: WeaponSlot
             -> struct (WeaponSlot * ShotRequest list)

module Sim =
    val step : InputFrame -> World -> struct (World * GameEvent list)
```

Hot-path convention: `Soldier[]`/`Grenade[]` arrays rebuilt per tick with `Array.map`-style transforms; the GC handles gen-0 arrays of this size trivially at 60 Hz. If profiling ever disagrees, switch those records to `[<Struct>]` and mutate in place behind the module boundary — callers never know.

---

## 4. Procedural assets

### 4.1 Level generation — a layout DSL, not noise

CoD2 levels are *authored*, linear, scripted. So don't roguelike it: write levels **as F# code** in a combinator DSL, and let the compiler be your level editor. Randomness only for detail (rubble, crate placement), driven by the world seed.

```fsharp
let stalingradStreet =
    level "Downtown" {
        street  (len 60.f) (width 12.f) Rubbled
        ruin    (at 8.f  3.f) (floors 2) (facade Brick) (blownOut East)
        ruin    (at 24.f -8.f) (floors 3) (facade Plaster) Intact
        sandbags (from 30.f 2.f) (to' 34.f 2.f) |> coverLine Axis
        trench  (from 40.f -5.f) (to' 55.f -5.f)
        mg42    (at 52.f 0.f) (facing West)
        spawnSquad Axis   (count 6) (around 45.f 0.f)
        spawnSquad Allies (count 3) (around 4.f 0.f) Friendly
        trigger (volume (at 20.f 0.f) (size 6.f)) (Wave (Axis, 4, at 38.f 6.f))
        objective "Clear the MG nest at the end of the street"
    }
```

`LevelCompile.fs` lowers this to:

1. **Brush list** — axis-aligned boxes + wedges (window sills, stairs, rubble piles). Rooms are hollowed boxes (6 wall brushes). No real CSG needed if the DSL emits walls directly.
2. **Render mesh** — brushes → quads, split per material, greedy-merged coplanar faces, per-vertex world position (the shader textures from world-space, so no UV unwrapping ever).
3. **Collision set** — the same brushes in a uniform grid broadphase (cell ≈ 4 m).
4. **Navgraph** — flood-fill walkable cells (1 m grid) from spawns, respecting brush clearance; promote to waypoints; A* over it.
5. **Cover points** — for each low brush (sandbag/wall ≈ 1–1.4 m) adjacent to walkable cells, emit a `CoverPoint` with peek direction = away from the brush. The DSL's `coverLine` just tags them with an owning-team hint.

This one pass gives you geometry, collision, pathfinding, and AI cover *from the same source of truth* — the biggest win of going asset-free.

### 4.2 Materials & textures — do it in the fragment shader

Skip texture files *and* CPU texture baking: one **uber-shader** with a material ID per vertex, generating pattern + noise in-shader with triplanar world-space mapping.

- `Brick` — mortar grid via `fract`/`step`, per-brick tint from hashed cell ID, fbm grime
- `Plaster` — low-freq fbm + bullet-pock darkening near `Impact` decal points
- `Wood` — 1-D stripes domain-warped by noise
- `Mud`/`Snow` — fbm albedo + slope-based blend (CoD2's Eastern Front vs. Africa palettes are basically a uniform block: albedo ramp + fog color)
- `Sandbag` — quantized bump rows + noise

Lighting: one directional sun, single 2048² shadow map, hemispheric ambient, distance fog. That combination *is* the CoD2 look. Decals (bullet holes, scorch) as a small SSBO of world-space points the shader darkens around — no decal geometry.

### 4.3 Weapon viewmodels — mesh combinators

`MeshGen.fs` exposes a tiny CSG-free kit: `box`, `cylinder`, `tube`, `lathe profile`, `wedge`, plus `translate/rotate/scale/mirrorX/union` and `paint material`. A Kar98k is ~15 primitives (tapered lathe barrel, box receiver, lathe bolt handle, wedge stock); a Thompson maybe 20. At 2005 fidelity with the viewmodel filling 200 px of screen, this reads shockingly well — silhouette and animation sell it, not the mesh.

Viewmodel animation is procedural too: sway from view-delta with spring damping, bob from speed, ADS as a pose lerp (hip transform → sights-aligned transform, defined per gun as part of its combinator build), recoil kick as an impulse into the same spring. Reload = keyframed transform curves in code (drop mag node, insert, charge) — each gun's `Guns.fs` builder returns named part nodes precisely so reloads can move them.

### 4.4 Humanoids — capsules with a skeleton, animated by math

`Humanoid.fs`: ~12-bone skeleton (pelvis, spine, head, 2×upper/lower arm, 2×thigh/shin, feet), each bone rendered as a capsule/box with the uniform material (`Uniform Feldgrau` vs `Uniform Olive`). Animation is procedural:

- **Walk/run**: legs = sine phase pair, arms counter-phase, pelvis bob; phase speed ∝ velocity (`AnimPhase` in `Soldier`)
- **Aim**: spine + head yaw/pitch toward target, rifle bone glued to hands with a fixed two-hand offset
- **Cover**: crouch pose = fixed joint targets, peek = additive lean rotation
- **Death**: no ragdoll physics — a canned "crumple" curve on joints plus fall to ground, ~0.7 s, then static. Reads fine at this fidelity.

Hit detection against characters: 3 capsules (head/torso/legs) with damage multipliers, not per-bone.

### 4.5 Audio — a 200-line synth

`AudioSynth.fs` renders everything to 44.1 kHz mono PCM at startup (~1 s total synth time):

- **Gunshot** = white-noise burst (5–15 ms) → lowpass sweep 8 kHz→400 Hz + a 60–90 Hz sine "body" thump + exponential decay tail; per-weapon params (bolt rifles longer/deeper, SMGs snappier). Slight per-shot pitch jitter at play time so autofire doesn't machine-gun-comb.
- **Distant gunfire** = same, heavier lowpass + delay-based slapback.
- **Explosion** = brown noise, 300 ms lowpass sweep, soft-clip, sub sine.
- **Footsteps** = filtered noise ticks per surface material.
- **Reload foley** = short metallic clicks (band-passed noise + resonant ping).
- **Voice** = don't synthesize speech; CoD2 squad chatter becomes **subtitles** (`Subtitle` event) plus a radio-squelch blip. Honest and stylish.
- **Ambience** = looping filtered noise (wind) + randomly scheduled distant-fire one-shots.

OpenAL gives distance attenuation + panning for free; keep a pool of ~24 sources.

### 4.6 Font — stroke font rasterized at boot

`FontGen.fs`: define A–Z/0–9/punct as polyline strokes (a Hershey-style font is ~2 KB of coordinate data in an F# array), rasterize with thickness into a single-channel atlas at startup. HUD text, subtitles, objectives all draw from it. Stencil-military aesthetic fits the theme for free.

---

## 5. Simulation details worth pinning down

**Movement (`Movement.fs`)** — kinematic capsule (r 0.35 m, h 1.8/1.3/0.6 by stance). Collide-and-slide: sweep vs. broadphase brushes, clip velocity against hit planes, ≤3 iterations; step-up ≤ 0.4 m for stairs/rubble. Ground accel ~10× air accel, sprint = 1.4× (blocks ADS + fire, CoD2-style), friction exponential. No bunny-hop preservation — this is CoD, not Quake.

**Ballistics** — hitscan: ray vs. brush grid and character capsules, closest wins. Spread = cone sampled by RNG, radius lerped hip→ADS by `player.Ads`, widened by movement + stance. Recoil consumed from `Recoil` pattern by `BurstIx`. **Penetration**: on brush hit, continue the ray, find exit point, subtract material-scaled thickness from a penetration budget; re-enter world with reduced damage — shooting through wooden fences is a CoD2 signature and cheap here because brushes make thickness trivial to compute. Grenades: ballistic integration, restitution 0.3 vs. brushes, fuse from `Cooking`, explosion = radial damage with LOS occlusion check.

**Damage/regen** — `RegenIn` set to 4 s on hit; when it hits 0, regen 40 hp/s. `PlayerHurt` event carries direction for the HUD's directional red arc; low health = red vignette + heartbeat (synthesized, naturally).

**AI (`AiBrain.fs`)** — per-soldier behavior DU stepped each tick + a per-squad blackboard (known contacts, advance line, who's suppressing). Core loop: pick cover advancing toward squad objective → suppress-or-peek cycle in cover (`peekPhase` timer) → advance under squadmate fire → flank if target stationary too long. `Suppressed` triggers on N near-misses within a window (near-miss = shot ray passes within 1 m — you already have the rays). Perception: FOV 120°, LOS ray to torso, hearing radius per weapon; contacts decay in `Contacts` map so AI hunts last-known-position, not the player transform. This ~5-state machine plus suppression is genuinely most of what CoD2 AI observably does.

**Mission script (`Script.fs`)** — a list of `(TriggerCondition, ScriptAction)` pairs: `EnterVolume`, `SquadDead n`, `ObjectiveDone i`, `Delay t` → `SpawnWave`, `SetObjective`, `Say`, `OpenPath`, `EndMission`. Declared alongside the level in the same DSL block. This is the entire "engine" of CoD2's linearity.

---

## 6. Rendering pipeline (one file's worth)

1. Shadow pass: sun-oriented ortho, level + soldiers, 2048² depth
2. Main pass: level mesh (uber-shader, triplanar materials, shadow sample, fog) → soldiers (capsule instances, per-part material) → grenades/particles (tracers as camera-facing stretched quads, smoke as fbm-alpha billboards)
3. Viewmodel pass: clear depth, draw active gun at ~55° FOV regardless of world FOV (world FOV lerps 65→40 with `Ads` for the zoom feel)
4. HUD pass: ortho quads — crosshair (opens with spread), ammo, compass strip, objective text, subtitle bar, hit vignette

Frame budget is a non-issue: a full level is maybe 60k triangles.

---

## 7. Build order (each milestone is playable)

1. **Walk the box** — window, loop, capsule movement in one hard-coded room, mouse look
2. **Level DSL v1** — street + two ruins compile to brushes/mesh/collision; triplanar shader; sun + fog
3. **First gun** — Kar98k combinator model, viewmodel sway/bob, hitscan + impact events, synth gunshot, crosshair
4. **Someone shoots back** — humanoid, walk cycle, 3-capsule hits, Idle/Advance/Attack AI, death crumple, damage + regen + vignette
5. **CoD-ness** — ADS, sprint, recoil patterns, penetration, grenades + cooking, cover AI + suppression, squad blackboard
6. **Mission** — script triggers, objectives, subtitles, friendly squad, MG42 setpiece, mission end
7. **Polish** — decals, particles, ambience, shadow tuning, second weapon set, second level in the DSL

Steps 1–3 are ~a weekend each; the sim being pure means AI and ballistics get real unit tests (`Sim.step` a scenario N ticks, assert outcomes) — rare luxury in gamedev.

## 8. Risks / honest caveats

- **GL 4.1 vs macOS**: fine, but if you ever want compute shaders, macOS caps at 4.1 — everything above avoids them deliberately.
- **Procedural humanoids** will look like toy soldiers. Lean into it (matte materials, strong silhouettes, fog) rather than chasing realism.
- **No speech** is the biggest atmosphere gap vs. CoD2; subtitles + radio squelch is the mitigation.
- **F#-specific**: keep `Vector3` math in `let inline` helpers; avoid closures in the per-tick hot loop (allocations show up fast at 60 Hz × 100 entities); prefer `for` over `Seq` chains inside `Sim`.
