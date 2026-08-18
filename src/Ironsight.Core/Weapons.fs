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

    let step (dt: float32<s>) moveSpeed stance trigger reload ads (rng: byref<Rng.State>) slot =
        let current = advanceState dt slot
        let movementFactor = 1.0f + MathEx.clamp01 (moveSpeed / Tuning.WalkSpeed) * Tuning.MovementSpreadMultiplier
        let stanceFactor =
            match stance with
            | Standing -> 1.0f
            | Crouched -> Tuning.CrouchSpreadMultiplier
            | Prone -> Tuning.ProneSpreadMultiplier
        let bloom = max 0.0f (current.Bloom - Tuning.BloomDecayPerSecond * Units.raw dt)

        if reload && current.State = Ready && current.InMag < current.Class.MagSize && current.Reserve > 0 then
            struct ({ current with State = Reloading current.Class.ReloadTime; BurstIx = 0; Bloom = 0.0f }, [])
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
                      HeadshotMultiplier = current.Class.HeadshotMultiplier }
            let recoil = current.Class.Recoil
            let kick = if recoil.Length = 0 then 0.0f else MathF.Abs recoil[min current.BurstIx (recoil.Length - 1)].Y
            let nextBloom = min Tuning.BloomMax (bloom + kick * Tuning.BloomPerShot)
            let cooldown = Units.seconds (60.0f / current.Class.RoundsPerMin)
            struct ({ current with State = Cooling cooldown; InMag = current.InMag - 1; BurstIx = current.BurstIx + 1; Bloom = nextBloom }, List.ofSeq shots)
        else
            let burstIx = if trigger then current.BurstIx else 0
            struct ({ current with Bloom = bloom; BurstIx = burstIx }, [])
