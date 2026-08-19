namespace Ironsight

open System
open System.Numerics

[<RequireQualifiedAccess>]
module Noise =
    let private hash seed x y =
        let mutable value = uint32 seed ^^^ (uint32 x * 0x9E3779B9u) ^^^ (uint32 y * 0x85EBCA6Bu)
        value <- (value ^^^ (value >>> 16)) * 0x7FEB352Du
        value <- (value ^^^ (value >>> 15)) * 0x846CA68Bu
        float32 (value ^^^ (value >>> 16)) / float32 UInt32.MaxValue

    let private smooth value = value * value * (3.0f - 2.0f * value)

    let value2 seed (point: Vector2) =
        let x0, y0 = int (MathF.Floor point.X), int (MathF.Floor point.Y)
        let tx, ty = smooth (point.X - float32 x0), smooth (point.Y - float32 y0)
        let bottom = hash seed x0 y0 + (hash seed (x0 + 1) y0 - hash seed x0 y0) * tx
        let top = hash seed x0 (y0 + 1) + (hash seed (x0 + 1) (y0 + 1) - hash seed x0 (y0 + 1)) * tx
        bottom + (top - bottom) * ty

    let fbm2 seed octaves (point: Vector2) =
        let mutable frequency, amplitude = 1.0f, 0.5f
        let mutable total, normalizer = 0.0f, 0.0f
        for octave in 0..max 1 octaves - 1 do
            total <- total + value2 (seed + octave * 1013) (point * frequency) * amplitude
            normalizer <- normalizer + amplitude
            frequency <- frequency * 2.03f
            amplitude <- amplitude * 0.5f
        total / max 0.0001f normalizer

