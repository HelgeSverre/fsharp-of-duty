namespace Ironsight.Shell

open System.Numerics

/// What colour the generated sky is, per level.
///
/// Keyed by level name rather than carried on the Level record: the sky is a
/// client-side look with no bearing on simulation, and keeping it out of Level
/// keeps it out of the .map format and off the wire. A level nobody has an
/// entry for gets the overcast Normandy default, which is what every map used
/// when this was hardcoded into the shader.
type SkyPalette =
    { Low: Vector3
      High: Vector3
      Cloud: Vector3
      /// Distant silhouette ridge; the near ridge is drawn at half this.
      Ridge: Vector3
      CloudAmount: float32
      /// How far the horizon washes out toward `Low`. Deserts sit high.
      Haze: float32 }

[<RequireQualifiedAccess>]
module SkyPalette =
    let overcast =
        { Low = Vector3(0.43f, 0.45f, 0.42f)
          High = Vector3(0.12f, 0.22f, 0.31f)
          Cloud = Vector3(0.52f, 0.53f, 0.49f)
          Ridge = Vector3(0.20f, 0.24f, 0.22f)
          CloudAmount = 1.0f
          Haze = 0.22f }

    /// Hot, dry and bleached: a thin high blue burning off into pale dust at
    /// the horizon, almost no cloud, and sand-coloured hills behind it.
    let desert =
        { Low = Vector3(0.78f, 0.70f, 0.56f)
          High = Vector3(0.24f, 0.42f, 0.62f)
          Cloud = Vector3(0.86f, 0.82f, 0.74f)
          Ridge = Vector3(0.56f, 0.48f, 0.36f)
          CloudAmount = 0.30f
          Haze = 0.62f }

    let forLevel (name: string) =
        match name with
        | "Rust" -> desert
        | _ -> overcast
