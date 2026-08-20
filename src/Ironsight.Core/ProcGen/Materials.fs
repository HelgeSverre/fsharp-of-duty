namespace Ironsight.ProcGen

open Ironsight

[<RequireQualifiedAccess>]
module Materials =
    /// Canonical order, also MapFile's serialization tag order (index =
    /// tag/id) — new cases go at the END so on-disk tags stay stable.
    let all =
        [| Brick; Plaster; Wood; Mud; Snow; Sandbag; Metal; UniformOlive; UniformFeldgrau; Skin; Water
           PaintRed; PaintBlue; PaintGreen; PaintYellow; PaintPurple; PaintOrange; FoamBlue; FoamOrange
           ToolBlack; WaterBlue; WetDark |]

    let id material = Array.findIndex ((=) material) all

    let parse (name: string) =
        all |> Array.tryFind (fun material -> string material = name)
