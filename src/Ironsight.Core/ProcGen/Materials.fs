namespace Ironsight.ProcGen

open Ironsight

[<RequireQualifiedAccess>]
module Materials =
    let id = function
        | Brick -> 0 | Plaster -> 1 | Wood -> 2 | Mud -> 3 | Snow -> 4
        | Sandbag -> 5 | Metal -> 6 | UniformOlive -> 7 | UniformFeldgrau -> 8 | Skin -> 9
