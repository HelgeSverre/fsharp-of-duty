namespace Ironsight

open System
open System.Numerics

[<RequireQualifiedAccess>]
module Tuning =
    [<Literal>]
    let TickRate = 60

    let TickDuration = Units.seconds (1.0f / float32 TickRate)
    let WalkSpeed = 5.4f
    let SprintMultiplier = 1.55f
    let GroundAcceleration = 38.0f
    let AirAcceleration = 4.0f
    let Gravity = 22.0f
    let PlayerRadius = 0.35f
    /// Steepest surface that can be stood on, as the cosine of its angle from
    /// vertical. Anything steeper is slid down, which is what lets a cliff face
    /// act as a wall without an invisible box around it.
    let MaxSlopeAngle = 45.0f
    let MaxSlopeCosine = MathF.Cos(MaxSlopeAngle * MathF.PI / 180.0f)
    /// Speed factor while standing below the water line. Wading is slow and
    /// loud on purpose: the shallows trade speed for a lower silhouette.
    let WadeSpeedMultiplier = 0.55f
    let StandingHeight = 1.8f
    let CrouchedHeight = 1.3f
    let ProneHeight = 0.6f
    /// Time to bring a new weapon up, during which the weapon clock is frozen
    /// — no firing, no reloading. Tuned against ADS (0.14-0.20 s): a swap that
    /// costs much more than a sight picture makes the sidearm not worth having.
    /// The viewmodel's lower/raise animation reads the same number.
    let WeaponSwitchTime = Units.seconds 0.18f
    let RegenDelay = Units.seconds 4.0f
    let RegenPerSecond = Units.health 40.0f
    let EnemySightRange = 65.0f
    let EnemyMaxPlayerShooters = 7
    let EnemyAimSpreadMultiplier = 2.25f
    // Player-facing enemy fire draws its cone from a fixed spread, decoupled
    // from the weapon's player-facing HipSpread. Tightening a player rifle must
    // not turn massed riflemen into lasers.
    let EnemyHipSpread = 0.045f
    let EnemyDamageScale = 0.05f
    let EnemyFriendlyDamageScale = 0.55f
    let BloomDecayPerSecond = 3.0f
    let BloomMax = 0.045f
    let BloomPerShot = 0.32f
    let MovementSpreadMultiplier = 1.6f
    // Stance accuracy bonus applies to the whole cone (base spread + bloom).
    let CrouchSpreadMultiplier = 0.75f
    let ProneSpreadMultiplier = 0.55f
    let BodyPenetrationCost = 3.0f
    let BodyDamageRetention = 0.6f

    /// Fraction of base damage retained at a hit `distance` metres from the
    /// muzzle, by weapon class (the CoD4/BF1 sidearm pattern): pistols, SMGs,
    /// and shotguns keep their full close-range punch and pay for it over
    /// distance, while rifles and machine guns carry.
    /// Falloff window per class: full damage inside `near` metres, ramping down
    /// to the `retained` fraction at `far`. A retained fraction of 1 means the
    /// class has no falloff.
    let falloffWindow kind =
        match kind with
        | Pistol -> struct (12.0f, 35.0f, 0.5f)
        | Smg -> struct (15.0f, 40.0f, 0.65f)
        | Shotgun -> struct (8.0f, 22.0f, 0.35f)
        | Rifle | SniperRifle | MachineGun -> struct (0.0f, 0.0f, 1.0f)

    let damageFalloff kind (distance: float32) =
        let struct (near, far, retained) = falloffWindow kind
        if retained >= 1.0f || distance <= near then 1.0f
        elif distance >= far then retained
        else 1.0f - (1.0f - retained) * (distance - near) / (far - near)

    let kar98k =
        { Name = "Kar98k"
          Mode = BoltAction
          Kind = Rifle
          Damage = Units.health 85.0f
          RoundsPerMin = 45.0f
          MagSize = 5
          ReloadTime = Units.seconds 2.35f
          Pellets = 1
          AdsTime = Units.seconds 0.16f
          HipSpread = 0.030f
          AdsSpread = 0.003f
          Recoil = [| Vector2(0.003f, 0.025f); Vector2(-0.004f, 0.028f) |]
          Penetration = 18.0f
          HeadshotMultiplier = 2.0f
          MuzzleDistance = 1.10f }

    let kar98kSniper =
        { Name = "Kar98k Sniper"
          Mode = BoltAction
          Kind = SniperRifle
          Damage = Units.health 120.0f
          RoundsPerMin = 38.0f
          MagSize = 5
          ReloadTime = Units.seconds 2.7f
          Pellets = 1
          AdsTime = Units.seconds 0.18f
          HipSpread = 0.050f
          AdsSpread = 0.00045f
          Recoil = [| Vector2(0.002f, 0.042f); Vector2(-0.003f, 0.046f) |]
          Penetration = 24.0f
          HeadshotMultiplier = 2.0f
          MuzzleDistance = 1.10f }

    let thompson =
        { Name = "Thompson"
          Mode = FullAuto
          Kind = Smg
          Damage = Units.health 28.0f
          RoundsPerMin = 700.0f
          MagSize = 30
          ReloadTime = Units.seconds 2.15f
          Pellets = 1
          AdsTime = Units.seconds 0.12f
          HipSpread = 0.055f
          AdsSpread = 0.012f
          Recoil = [| Vector2(0.003f, 0.012f); Vector2(-0.006f, 0.014f); Vector2(0.008f, 0.016f) |]
          Penetration = 8.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 0.92f }

    let m1911 =
        { Name = "M1911"
          Mode = SemiAuto
          Kind = Pistol
          Damage = Units.health 42.0f
          RoundsPerMin = 360.0f
          MagSize = 7
          ReloadTime = Units.seconds 1.6f
          Pellets = 1
          AdsTime = Units.seconds 0.10f
          HipSpread = 0.048f
          AdsSpread = 0.010f
          Recoil = [| Vector2(0.002f, 0.019f); Vector2(-0.003f, 0.022f) |]
          Penetration = 5.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 0.58f }

    // The precision sidearm: weakest round in the game but near-rifle accuracy,
    // a snappy draw, and a 2.0x headshot multiplier. Two-taps with one headshot,
    // three-taps body — the M1911 stays the harder-hitting, looser sibling.
    let luger =
        { Name = "Luger P08"
          Mode = SemiAuto
          Kind = Pistol
          Damage = Units.health 34.0f
          RoundsPerMin = 480.0f
          MagSize = 8
          ReloadTime = Units.seconds 1.5f
          Pellets = 1
          AdsTime = Units.seconds 0.09f
          HipSpread = 0.038f
          AdsSpread = 0.0045f
          Recoil = [| Vector2(0.001f, 0.013f); Vector2(-0.002f, 0.015f) |]
          Penetration = 4.0f
          HeadshotMultiplier = 2.0f
          MuzzleDistance = 0.70f }

    let mg42 =
        { Name = "MG42"
          Mode = FullAuto
          Kind = MachineGun
          Damage = Units.health 30.0f
          RoundsPerMin = 900.0f
          MagSize = 50
          ReloadTime = Units.seconds 4.15f
          Pellets = 1
          AdsTime = Units.seconds 0.18f
          HipSpread = 0.065f
          AdsSpread = 0.025f
          Recoil = [| Vector2(0.012f, 0.018f); Vector2(-0.015f, 0.021f); Vector2(0.020f, 0.024f) |]
          Penetration = 12.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 1.10f }

    let m1897 =
        { Name = "M1897 Trench Gun"
          Mode = BoltAction
          Kind = Shotgun
          Damage = Units.health 16.0f
          RoundsPerMin = 72.0f
          MagSize = 5
          ReloadTime = Units.seconds 1.45f
          Pellets = 8
          AdsTime = Units.seconds 0.14f
          HipSpread = 0.105f
          AdsSpread = 0.045f
          Recoil = [| Vector2(0.012f, 0.045f); Vector2(-0.010f, 0.052f) |]
          Penetration = 2.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 1.18f }

    let m1Garand =
        { Name = "M1 Garand"
          Mode = SemiAuto
          Kind = Rifle
          Damage = Units.health 58.0f
          RoundsPerMin = 300.0f
          MagSize = 8
          ReloadTime = Units.seconds 1.9f
          Pellets = 1
          AdsTime = Units.seconds 0.15f
          HipSpread = 0.035f
          AdsSpread = 0.004f
          Recoil = [| Vector2(0.003f, 0.020f); Vector2(-0.003f, 0.023f) |]
          Penetration = 16.0f
          HeadshotMultiplier = 2.0f
          MuzzleDistance = 1.08f }

    let stg44 =
        { Name = "STG-44"
          Mode = FullAuto
          Kind = Rifle
          Damage = Units.health 40.0f
          RoundsPerMin = 550.0f
          MagSize = 30
          ReloadTime = Units.seconds 2.6f
          Pellets = 1
          AdsTime = Units.seconds 0.16f
          HipSpread = 0.050f
          AdsSpread = 0.008f
          Recoil = [| Vector2(0.004f, 0.016f); Vector2(-0.005f, 0.018f); Vector2(0.007f, 0.020f) |]
          Penetration = 12.0f
          HeadshotMultiplier = 1.6f
          MuzzleDistance = 1.02f }

    let mp40 =
        { Name = "MP40"
          Mode = FullAuto
          Kind = Smg
          Damage = Units.health 25.0f
          RoundsPerMin = 520.0f
          MagSize = 32
          ReloadTime = Units.seconds 2.3f
          Pellets = 1
          AdsTime = Units.seconds 0.12f
          HipSpread = 0.050f
          AdsSpread = 0.011f
          Recoil = [| Vector2(0.002f, 0.009f); Vector2(-0.004f, 0.011f); Vector2(0.005f, 0.012f) |]
          Penetration = 7.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 0.88f }

    let leeEnfield =
        { Name = "Lee-Enfield"
          Mode = BoltAction
          Kind = Rifle
          Damage = Units.health 80.0f
          RoundsPerMin = 60.0f
          MagSize = 10
          ReloadTime = Units.seconds 2.6f
          Pellets = 1
          AdsTime = Units.seconds 0.16f
          HipSpread = 0.032f
          AdsSpread = 0.0035f
          Recoil = [| Vector2(0.003f, 0.023f); Vector2(-0.004f, 0.026f) |]
          Penetration = 17.0f
          HeadshotMultiplier = 2.0f
          MuzzleDistance = 1.08f }

    // Kind = SniperRifle deliberately: the FG42 keeps its scope overlay and the
    // tight ADS field of view while firing full-auto — a scoped automatic.
    let fg42 =
        { Name = "FG42"
          Mode = FullAuto
          Kind = SniperRifle
          Damage = Units.health 42.0f
          RoundsPerMin = 450.0f
          MagSize = 20
          ReloadTime = Units.seconds 2.9f
          Pellets = 1
          AdsTime = Units.seconds 0.20f
          HipSpread = 0.070f
          AdsSpread = 0.004f
          Recoil = [| Vector2(0.010f, 0.030f); Vector2(-0.012f, 0.034f); Vector2(0.014f, 0.038f) |]
          Penetration = 15.0f
          HeadshotMultiplier = 1.6f
          MuzzleDistance = 1.06f }

    let bar =
        { Name = "BAR"
          Mode = FullAuto
          Kind = MachineGun
          Damage = Units.health 45.0f
          RoundsPerMin = 500.0f
          MagSize = 20
          ReloadTime = Units.seconds 3.4f
          Pellets = 1
          AdsTime = Units.seconds 0.22f
          HipSpread = 0.075f
          AdsSpread = 0.020f
          Recoil = [| Vector2(0.009f, 0.022f); Vector2(-0.011f, 0.026f); Vector2(0.015f, 0.030f) |]
          Penetration = 14.0f
          HeadshotMultiplier = 1.5f
          MuzzleDistance = 1.12f }

    // Appended in this order on purpose: existing indices are load-bearing for
    // the online loadout menu and its tests.
    let onlineWeapons = [| kar98k; thompson; m1911; kar98kSniper; m1897; m1Garand; stg44; mp40; leeEnfield; fg42; bar; luger |]

    let defaultWeapon = function Allies -> thompson | Axis -> kar98k

    /// The team's issued sidearm. Every player carries one online; it is not a
    /// menu choice, the way Counter-Strike and Battlefield hand out a pistol.
    let sidearm = function Allies -> m1911 | Axis -> luger

    /// Which number key holds a weapon: 0 = key 1, 4 = key 5. Derived from the
    /// weapon's own stats rather than a table of inventory indices, so it works
    /// for any loadout — the twelve-slot offline sandbox and the two-slot
    /// online kit alike.
    ///
    /// Arm order is load-bearing. The FG42 is a full-auto SniperRifle but
    /// belongs with the scoped guns, and the BAR is a full-auto MachineGun but
    /// belongs with the heavies, so Kind is matched before Mode.
    let categoryOf (weapon: WeaponClass) =
        match weapon.Kind with
        | Pistol -> 2
        | SniperRifle -> 3
        | Shotgun | MachineGun -> 4
        | Smg -> 1
        // The STG-44 is a rifle that fires like an SMG, and sits with them.
        | Rifle -> if weapon.Mode = FullAuto then 1 else 0

    /// Display name of a weapon key, for the loadout picker's group headings.
    /// "Precision" rather than "Scoped": a bow belongs there and has no optic.
    let categoryName =
        function
        | 0 -> "RIFLES"
        | 1 -> "AUTOMATICS"
        | 2 -> "SIDEARMS"
        | 3 -> "PRECISION"
        | _ -> "HEAVY"

    /// Every weapon key, in key order.
    let categories = [| 0 .. 4 |]

    /// Slot indices in `slots` that key `category` selects, in carry order.
    let categorySlots (slots: WeaponSlot array) category =
        slots
        |> Array.indexed
        |> Array.filter (fun (_, slot) -> categoryOf slot.Class = category)
        |> Array.map fst

    let weaponByName name =
        onlineWeapons
        |> Array.tryFind (fun weapon -> String.Equals(weapon.Name, name, StringComparison.OrdinalIgnoreCase))

    let weaponSlot weapon magazines =
        { Class = weapon
          State = Ready
          InMag = weapon.MagSize
          Reserve = weapon.MagSize * magazines
          BurstIx = 0
          Bloom = 0.0f }

/// Everything derivable about a weapon from its stats, in one place. The
/// website's arsenal endpoint and the in-game loadout picker both render this,
/// so the numbers a player compares before a match are by construction the ones
/// he compares during it.
type WeaponStats =
    { Weapon: WeaponClass
      /// Per projectile; a shotgun fires `Pellets` of these.
      DamagePerProjectile: float32
      MaximumDamagePerShot: float32
      /// Damage retained per projectile at maximum falloff range.
      MinimumDamagePerProjectile: float32
      FalloffStartMetres: float32
      FalloffEndMetres: float32
      /// Shots to drop a full-health target at point-blank range, every
      /// projectile connecting.
      ShotsToKill: int
      /// Seconds those shots take, which is the number that actually decides a
      /// pick. Zero for a one-shot kill: no second shot has to be waited for.
      TimeToKillSeconds: float32 }

[<RequireQualifiedAccess>]
module WeaponStats =
    /// Full health, matching the spawn value in Sim/MatchHost.
    [<Literal>]
    let private FullHealth = 100.0f

    let of' (weapon: WeaponClass) =
        let struct (falloffStart, falloffEnd, retained) = Tuning.falloffWindow weapon.Kind
        let damage = Units.raw weapon.Damage
        let perShot = damage * float32 weapon.Pellets
        let shots = if perShot <= 0.0f then 0 else int (ceil (FullHealth / perShot))
        { Weapon = weapon
          DamagePerProjectile = damage
          MaximumDamagePerShot = perShot
          MinimumDamagePerProjectile = damage * retained
          FalloffStartMetres = falloffStart
          FalloffEndMetres = falloffEnd
          ShotsToKill = shots
          // The first shot costs no wait; only the gaps between shots do.
          TimeToKillSeconds =
            if shots <= 1 || weapon.RoundsPerMin <= 0.0f then 0.0f
            else float32 (shots - 1) * 60.0f / weapon.RoundsPerMin }
