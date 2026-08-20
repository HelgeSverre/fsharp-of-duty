namespace Ironsight.ProcGen

open System.Numerics
open Ironsight

[<RequireQualifiedAccess>]
module Materials =
    /// Canonical order, also MapFile's serialization tag order (index =
    /// tag/id) — new cases go at the END so on-disk tags stay stable.
    let all =
        [| Brick; Plaster; Wood; Mud; Snow; Sandbag; Metal; UniformOlive; UniformFeldgrau; Skin; Water
           Sand; RustedMetal; Concrete
           PaintRed; PaintBlue; PaintGreen; PaintYellow; PaintPurple; PaintOrange; FoamBlue; FoamOrange
           ToolBlack; WaterBlue; WetDark |]

    let id material = Array.findIndex ((=) material) all

    /// Flat display colour for a material, for anything that draws geometry
    /// without the game's shader: the gun-preview tool and the website's
    /// arsenal models. The shader itself derives richer procedural surfaces
    /// from the same ids; this is the one-colour stand-in they agree on.
    let previewColour material =
        match material with
        | Wood -> Vector3(0.55f, 0.34f, 0.16f)
        | Plaster -> Vector3(0.82f, 0.84f, 0.86f)
        | Metal -> Vector3(0.62f, 0.65f, 0.70f)
        | RustedMetal -> Vector3(0.45f, 0.26f, 0.14f)
        | Concrete -> Vector3(0.52f, 0.51f, 0.48f)
        | Sand -> Vector3(0.76f, 0.68f, 0.50f)
        | Brick -> Vector3(0.43f, 0.16f, 0.09f)
        | Mud -> Vector3(0.34f, 0.29f, 0.18f)
        | Snow -> Vector3(0.88f, 0.91f, 0.92f)
        | Sandbag -> Vector3(0.58f, 0.48f, 0.30f)
        | Skin -> Vector3(0.85f, 0.66f, 0.52f)
        | UniformOlive -> Vector3(0.34f, 0.38f, 0.22f)
        | UniformFeldgrau -> Vector3(0.27f, 0.30f, 0.26f)
        | Water -> Vector3(0.10f, 0.20f, 0.24f)
        | PaintRed -> Vector3(0.95f, 0.06f, 0.08f)
        | PaintBlue -> Vector3(0.04f, 0.30f, 0.98f)
        | PaintGreen -> Vector3(0.08f, 0.90f, 0.22f)
        | PaintYellow -> Vector3(1.0f, 0.82f, 0.04f)
        | PaintPurple -> Vector3(0.68f, 0.08f, 0.92f)
        | PaintOrange | FoamOrange -> Vector3(1.0f, 0.30f, 0.03f)
        | FoamBlue -> Vector3(0.04f, 0.22f, 0.72f)
        | ToolBlack -> Vector3(0.035f, 0.04f, 0.045f)
        | WaterBlue -> Vector3(0.05f, 0.52f, 0.88f)
        | WetDark -> Vector3(0.075f, 0.10f, 0.12f)

    let parse (name: string) =
        all |> Array.tryFind (fun material -> string material = name)
