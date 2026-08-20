namespace Ironsight

open System
open System.Numerics

[<RequireQualifiedAccess>]
module Weapons =
    let private advanceState dt slot =
        match slot.State with
        | Ready -> slot
        | Cooling remaining when remaining <= dt -> { slot with State = Ready }
        | Cooling remaining -> { slot with State = Cooling(remaining - dt) }
        | Reloading remaining when remaining <= dt ->
            let needed = slot.Class.MagSize - slot.InMag
            let loaded = min needed slot.Reserve
            { slot with State = Ready; InMag = slot.InMag + loaded; Reserve = slot.Reserve - loaded }
        | Reloading remaining -> { slot with State = Reloading(remaining - dt) }
        | Switching(incoming, remaining) when remaining <= dt ->
            { slot with State = Switching(incoming, Units.seconds 0.0f) }
        | Switching(incoming, remaining) -> { slot with State = Switching(incoming, remaining - dt) }

    /// Belt-fed guns run hot. Nothing else in the arsenal carries enough rounds
    /// to matter, so the rule is derived from the belt rather than a per-weapon
    /// flag that would have to be set on every gun that will never use it.
    let overheats (weapon: WeaponClass) = weapon.MagSize >= 100

    /// Extra ticks of dwell heat adds between rounds. Cold the gun cycles at
    /// its rated rate; glowing it crawls. It is never taken out of the player's
    /// hands — it just stops being a hose, which reads as the barrels bogging
    /// down rather than as the game refusing an input.
    let heatDwell (weapon: WeaponClass) (heat: float32) =
        if not (overheats weapon) then 0.0f
        else
            let bite = MathF.Pow(MathEx.clamp01 heat, Tuning.HeatBiteExponent)
            float32 (int (bite * Tuning.MaxHeatExtraTicks + 0.5f)) * Units.raw Tuning.TickDuration

    let step (dt: float32<s>) moveSpeed stance trigger reload ads (rng: byref<Rng.State>) slot =
        let current = advanceState dt slot
        let movementFactor = 1.0f + MathEx.clamp01 (moveSpeed / Tuning.WalkSpeed) * Tuning.MovementSpreadMultiplier
        let stanceFactor =
            match stance with
            | Standing -> 1.0f
            | Crouched -> Tuning.CrouchSpreadMultiplier
            | Prone -> Tuning.ProneSpreadMultiplier
        let bloom = max 0.0f (current.Bloom - Tuning.BloomDecayPerSecond * Units.raw dt)
        // Heat sheds whenever a round is not going out this tick, including
        // mid-reload and while the trigger is held on an empty gun.
        let cooled = max 0.0f (current.Heat - Tuning.HeatCoolPerSecond * Units.raw dt)

        if reload && current.State = Ready && current.InMag < current.Class.MagSize && current.Reserve > 0 then
            struct ({ current with State = Reloading current.Class.ReloadTime; BurstIx = 0; Bloom = 0.0f; Heat = cooled }, [])
        elif trigger && current.State = Ready && current.InMag > 0
             && (current.Class.Mode = FullAuto || current.BurstIx = 0) then
            // Accuracy arrives by the time the scope/iron sight becomes visually
            // usable. This keeps fast ADS meaningful instead of punishing a shot
            // that was taken on the final few frames of the transition.
            let sightedAccuracy = MathEx.clamp01 (ads / 0.72f)
            let spread =
                (current.Class.HipSpread + (current.Class.AdsSpread - current.Class.HipSpread) * sightedAccuracy + bloom)
                * movementFactor
                * stanceFactor
            let shots = ResizeArray<ShotRequest>()
            for _ in 1..max 1 current.Class.Pellets do
                let angle = Rng.nextFloat32 &rng * MathF.Tau
                let radius = MathF.Sqrt(Rng.nextFloat32 &rng) * spread
                shots.Add
                    { DirectionOffset = Vector2(MathF.Cos angle * radius, MathF.Sin angle * radius)
                      Damage = current.Class.Damage
                      Penetration = current.Class.Penetration
                      HeadshotMultiplier = current.Class.HeadshotMultiplier
                      Kind = current.Class.Kind }
            let recoil = current.Class.Recoil
            let kick = if recoil.Length = 0 then 0.0f else MathF.Abs recoil[min current.BurstIx (recoil.Length - 1)].Y
            let nextBloom = min Tuning.BloomMax (bloom + kick * Tuning.BloomPerShot)
            // A hot gun waits longer between rounds. The heat this shot adds
            // lands first, so holding the trigger is self-limiting within the
            // same burst rather than only from the next one.
            let heat =
                if overheats current.Class then min 1.0f (current.Heat + 1.0f / Tuning.OverheatShots)
                else 0.0f
            let cooldown = Units.seconds (60.0f / current.Class.RoundsPerMin + heatDwell current.Class heat)
            struct ({ current with
                        State = Cooling cooldown
                        InMag = current.InMag - 1
                        BurstIx = current.BurstIx + 1
                        Bloom = nextBloom
                        Heat = heat },
                    List.ofSeq shots)
        else
            let burstIx = if trigger then current.BurstIx else 0
            struct ({ current with Bloom = bloom; BurstIx = burstIx; Heat = cooled }, [])
