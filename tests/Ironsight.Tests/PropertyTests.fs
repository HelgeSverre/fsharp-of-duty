namespace Ironsight.Tests

open Ironsight
open FsCheck.Xunit

/// Generated invariants complement the hand-picked behavioural examples.
module PropertyTests =
    [<Property(MaxTest = 200)>]
    let ``clamp01 always returns a value in the unit interval`` (value: int16) =
        let clamped = MathEx.clamp01 (float32 value / 100.0f)
        clamped >= 0.0f && clamped <= 1.0f

    [<Property(MaxTest = 200)>]
    let ``equal RNG seeds produce equal streams`` (seed: uint64) (length: byte) =
        let count = int length % 64
        let stream () =
            let mutable rng = Rng.create seed
            Array.init count (fun _ -> Rng.nextUInt64 &rng)
        stream () = stream ()

    [<Property(MaxTest = 200)>]
    let ``RNG floats stay in the half-open unit interval`` (seed: uint64) (length: byte) =
        let mutable rng = Rng.create seed
        let count = int length % 64
        Array.init count (fun _ -> Rng.nextFloat32 &rng)
        |> Array.forall (fun value -> value >= 0.0f && value < 1.0f)
