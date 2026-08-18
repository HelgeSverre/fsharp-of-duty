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
    let StandingHeight = 1.8f
    let CrouchedHeight = 1.3f
    let ProneHeight = 0.6f
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
    let BodyPenetrationCost = 3.0f
    let BodyDamageRetention = 0.6f

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

    let onlineWeapons = [| kar98k; thompson; m1911; kar98kSniper; m1897 |]

    let defaultWeapon = function Allies -> thompson | Axis -> kar98k

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
