namespace Ironsight.ProcGen

open System
open Ironsight

type SynthSound =
    { Samples: int16 array
      SampleRate: int }

[<RequireQualifiedAccess>]
module AudioSynth =
    [<Literal>]
    let SampleRate = 44100

    let private clip value = int16 (Math.Clamp(value, -1.0f, 1.0f) * 32767.0f)

    let private render seconds seed sample =
        let count = int (seconds * float32 SampleRate)
        let mutable state = uint32 seed
        let noise () =
            state <- state * 1664525u + 1013904223u
            (float32 (state >>> 8) / float32 0xFFFFFFu) * 2.0f - 1.0f
        { Samples = Array.init count (fun index -> sample (float32 index / float32 SampleRate) (noise ()) |> clip)
          SampleRate = SampleRate }

    let gunshot deep =
        render (if deep then 0.48f else 0.26f) (if deep then 9173 else 4157) (fun time white ->
            let transient = white * MathF.Exp(-time * (if deep then 18.0f else 31.0f))
            let body = MathF.Sin(MathF.Tau * (if deep then 72.0f else 96.0f) * time) * MathF.Exp(-time * 13.0f)
            MathF.Tanh((transient * 1.35f + body * 0.58f) * 1.5f) * 0.72f)

    /// The minigun's individual round: darker and shorter than a rifle's, with
    /// a hard mechanical knock in front of a low body that is gone before the
    /// next round leaves. A long, soft sample stacked sixty deep a second is
    /// what turns a minigun into a bee swarm; this stays a series of thumps.
    let minigunShot () =
        render 0.12f 5501 (fun time white ->
            let knock = white * MathF.Exp(-time * 120.0f)
            let body = MathF.Sin(MathF.Tau * 54.0f * time) * MathF.Exp(-time * 34.0f)
            let rotor = MathF.Sin(MathF.Tau * 128.0f * time) * MathF.Exp(-time * 60.0f)
            MathF.Tanh((knock * 1.6f + body * 0.9f + rotor * 0.35f) * 1.7f) * 0.8f)
    let paintballPop () =
        render 0.11f 1977 (fun time white ->
            let gas = white * MathF.Exp(-time * 58.0f)
            let snap = MathF.Sin(MathF.Tau * 420.0f * time) * MathF.Exp(-time * 44.0f)
            (gas * 0.42f + snap * 0.24f) * 0.72f)

    let foamThump () =
        render 0.09f 2468 (fun time white ->
            let spring = MathF.Sin(MathF.Tau * 135.0f * time) * MathF.Exp(-time * 48.0f)
            let click = white * MathF.Exp(-time * 95.0f)
            (spring * 0.32f + click * 0.16f) * 0.65f)

    let rocketLaunch () =
        let mutable low = 0.0f
        render 0.55f 8642 (fun time white ->
            low <- low * 0.88f + white * 0.12f
            let roar = low * MathF.Exp(-time * 4.0f)
            let ignition = white * MathF.Exp(-time * 38.0f)
            MathF.Tanh(roar * 2.8f + ignition * 0.7f) * 0.68f)

    let flameWhoosh () =
        let mutable low = 0.0f
        render 0.28f 7319 (fun time white ->
            low <- low * 0.90f + white * 0.10f
            let high = white - low
            let attack = min 1.0f (time / 0.018f)
            let body = MathF.Exp(-time * 3.2f)
            let gas = (low * 1.9f + high * 0.34f) * attack * body
            let roar = MathF.Sin(MathF.Tau * 58.0f * time) * MathF.Exp(-time * 7.0f) * 0.22f
            let valve = white * MathF.Exp(-time * 105.0f) * 0.34f
            let crackle = high * (0.55f + 0.45f * MathF.Sin(MathF.Tau * 31.0f * time)) * MathF.Exp(-time * 5.5f) * 0.22f
            MathF.Tanh(gas + roar + valve + crackle) * 0.62f)

    let flameImpact () =
        let mutable low = 0.0f
        render 0.22f 9327 (fun time white ->
            low <- low * 0.84f + white * 0.16f
            let spit = (white - low) * MathF.Exp(-time * 13.0f)
            let thump = MathF.Sin(MathF.Tau * 92.0f * time) * MathF.Exp(-time * 20.0f)
            MathF.Tanh(spit * 1.4f + thump * 0.28f) * 0.44f)

    let waterSquirt () =
        render 0.15f 5813 (fun time white ->
            let hiss = white * MathF.Exp(-time * 18.0f)
            let pump = MathF.Sin(MathF.Tau * 118.0f * time) * MathF.Exp(-time * 34.0f)
            (hiss * 0.24f + pump * 0.12f) * 0.55f)

    let nailSnap () =
        render 0.10f 4431 (fun time white ->
            let snap = white * MathF.Exp(-time * 82.0f)
            let body = MathF.Sin(MathF.Tau * 210.0f * time) * MathF.Exp(-time * 45.0f)
            MathF.Tanh(snap * 0.62f + body * 0.30f) * 0.62f)

    let laserClick () =
        render 0.045f 6217 (fun time white ->
            let switch = white * MathF.Exp(-time * 150.0f)
            let casing = MathF.Sin(MathF.Tau * 680.0f * time) * MathF.Exp(-time * 105.0f)
            (switch * 0.11f + casing * 0.045f) * 0.50f)

    let katanaSwing () =
        let mutable air = 0.0f
        render 0.18f 7213 (fun time white ->
            air <- air * 0.84f + white * 0.16f
            let high = white - air
            let progress = MathEx.clamp01 (time / 0.18f)
            // A short broadband air cut: no pitched sci-fi whistle. The
            // asymmetric pulse makes it read as one fast blade passing by.
            let body = MathF.Sin(MathF.PI * progress) ** 2.0f
            let pulseTime = (time - 0.055f) / 0.030f
            let passing = MathF.Exp(-(pulseTime * pulseTime))
            MathF.Tanh(high * (body * 0.42f + passing * 0.48f) + air * body * 0.16f) * 0.58f)

    let harpoonLaunch () =
        let mutable low = 0.0f
        render 0.46f 6673 (fun time white ->
            low <- low * 0.89f + white * 0.11f
            let pressure = low * MathF.Exp(-time * 7.5f) * 1.8f
            let cable = MathF.Sin(MathF.Tau * (520.0f + time * 880.0f) * time) * MathF.Exp(-time * 9.0f) * 0.24f
            let thump = MathF.Sin(MathF.Tau * 64.0f * time) * MathF.Exp(-time * 15.0f) * 0.55f
            MathF.Tanh(pressure + cable + thump) * 0.68f)

    let harpoonImpact () =
        render 0.34f 7751 (fun time white ->
            let metal =
                MathF.Sin(MathF.Tau * 310.0f * time) * MathF.Exp(-time * 12.0f)
                + MathF.Sin(MathF.Tau * 487.0f * time) * MathF.Exp(-time * 16.0f) * 0.65f
            let hit = white * MathF.Exp(-time * 42.0f)
            MathF.Tanh(metal * 0.42f + hit * 0.85f) * 0.58f)

    let bowRelease () =
        render 0.24f 9017 (fun time white ->
            let stringTone =
                MathF.Sin(MathF.Tau * (188.0f + time * 620.0f) * time) * MathF.Exp(-time * 22.0f)
                + MathF.Sin(MathF.Tau * 376.0f * time) * MathF.Exp(-time * 31.0f) * 0.38f
            let snap = white * MathF.Exp(-time * 105.0f)
            (stringTone * 0.46f + snap * 0.20f) * 0.62f)

    let arrowImpact () =
        render 0.18f 1081 (fun time white ->
            let thunk = MathF.Sin(MathF.Tau * 92.0f * time) * MathF.Exp(-time * 27.0f)
            let shaft = MathF.Sin(MathF.Tau * 530.0f * time) * MathF.Exp(-time * 36.0f)
            MathF.Tanh(white * MathF.Exp(-time * 70.0f) * 0.45f + thunk * 0.45f + shaft * 0.18f) * 0.58f)

    /// The M1 Garand's en-bloc clip ejecting: a bright metallic ring of
    /// inharmonic partials over a short noise transient.
    let garandPing () =
        render 0.38f 3141 (fun time white ->
            let partial frequency decay gain = MathF.Sin(MathF.Tau * frequency * time) * MathF.Exp(-time * decay) * gain
            let ring =
                partial 2380.0f 9.0f 0.42f
                + partial 3160.0f 12.0f 0.30f
                + partial 4270.0f 16.0f 0.20f
                + partial 5480.0f 22.0f 0.12f
            let transient = white * MathF.Exp(-time * 90.0f) * 0.25f
            (ring + transient) * 0.8f)

    let explosion () =
        let mutable brown = 0.0f
        render 0.72f 77191 (fun time white ->
            brown <- brown * 0.94f + white * 0.06f
            let sub = MathF.Sin(MathF.Tau * 54.0f * time) * MathF.Exp(-time * 7.0f)
            MathF.Tanh((brown * 4.2f + sub * 0.7f) * MathF.Exp(-time * 2.8f)) * 0.85f)

    let footstep () =
        render 0.13f 2219 (fun time white -> white * MathF.Exp(-time * 35.0f) * 0.35f)

    let reloadClick () =
        render 0.16f 8807 (fun time white ->
            let ping = MathF.Sin(MathF.Tau * 2450.0f * time) * MathF.Exp(-time * 42.0f)
            (ping * 0.34f + white * MathF.Exp(-time * 70.0f) * 0.20f))

    let heartbeat () =
        render 0.48f 1 (fun time _ ->
            let thump center =
                let local = max 0.0f (time - center)
                if time < center then 0.0f else MathF.Sin(MathF.Tau * 58.0f * local) * MathF.Exp(-local * 34.0f)
            (thump 0.0f + thump 0.19f * 0.72f) * 0.48f)

    let radio () =
        render 0.18f 991 (fun time white ->
            let envelope = MathF.Sin(MathF.PI * MathEx.clamp01 (time / 0.18f))
            (white * 0.22f + MathF.Sin(MathF.Tau * 1180.0f * time) * 0.06f) * envelope)

    let wind () =
        let mutable filtered = 0.0f
        render 3.0f 481516 (fun time white ->
            filtered <- filtered * 0.992f + white * 0.008f
            let gust = 0.55f + 0.45f * MathF.Sin(MathF.Tau * 0.33f * time)
            filtered * gust * 0.42f)
