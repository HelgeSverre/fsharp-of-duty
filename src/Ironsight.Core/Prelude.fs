namespace Ironsight

open System

[<Measure>]
type s

[<Measure>]
type hp

[<RequireQualifiedAccess>]
module Rng =
    [<Struct>]
    type State = private State of uint64

    let create seed = State seed

    let nextUInt64 (state: byref<State>) =
        let (State value) = state
        let next = value + 0x9E3779B97F4A7C15UL
        state <- State next
        let mutable z = next
        z <- (z ^^^ (z >>> 30)) * 0xBF58476D1CE4E5B9UL
        z <- (z ^^^ (z >>> 27)) * 0x94D049BB133111EBUL
        z ^^^ (z >>> 31)

    let nextFloat32 (state: byref<State>) =
        let bits = nextUInt64 &state >>> 40
        float32 bits / float32 (1UL <<< 24)

[<RequireQualifiedAccess>]
module Units =
    let inline seconds (value: float32) = LanguagePrimitives.Float32WithMeasure<s> value
    let inline health (value: float32) = LanguagePrimitives.Float32WithMeasure<hp> value
    let inline raw (value: float32<'u>) = float32 value

[<RequireQualifiedAccess>]
module Input =
    /// Generic over any `[<Flags>]` enum (InputButtons here) via SRTP, so this
    /// can live in Prelude ahead of Domain.fs, where InputButtons is declared.
    let inline hasButton (button: ^T) (buttons: ^T) : bool =
        (^T: (member HasFlag: ^T -> bool) (buttons, button))
