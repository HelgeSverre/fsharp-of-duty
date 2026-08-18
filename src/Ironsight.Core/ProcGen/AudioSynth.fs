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
