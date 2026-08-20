// Renders a compiled level to a standalone SVG: top-down plan coloured by
// height, plus a side elevation. Levels are data, so this needs no window and
// no GPU — which is the point. Iterating on a layout by launching the game and
// walking around is far slower than reading a plan view.
//
//   just map-preview canal
//
// ponytail: brush AABBs only. Extend to the triangle collision mesh when that
// lands, otherwise sloped terrain will preview as its bounding box.

#r "../src/Ironsight.Core/bin/Debug/net10.0/Ironsight.Core.dll"

open System
open System.Numerics
open System.Text
open Ironsight
open Ironsight.ProcGen

let requested =
    match fsi.CommandLineArgs |> Array.skip 1 |> Array.tryHead with
    | Some name -> name
    | None -> "paintball"

let level =
    Levels.all
    |> Array.tryFind (fun candidate -> candidate.Name.ToLowerInvariant().Contains(requested.ToLowerInvariant()))
    |> function
        | Some found -> found
        | None ->
            let names = Levels.all |> Array.map (fun l -> l.Name) |> String.concat ", "
            failwith $"No level matching '%s{requested}'. Available: %s{names}"

// One SVG pixel per 10 cm keeps a 70 m map readable at a glance without
// producing a file too wide to open.
let scale = 10.0f
let margin = 40.0f
let bounds = level.Bounds
let spanX = bounds.Max.X - bounds.Min.X
let spanZ = bounds.Max.Z - bounds.Min.Z
let planWidth = spanX * scale
let planHeight = spanZ * scale
// The elevation sits under the plan, sharing the X axis.
let elevationHeight = max 120.0f ((bounds.Max.Y - min 0.0f bounds.Min.Y) * scale)
let totalWidth = planWidth + margin * 2.0f
let totalHeight = planHeight + elevationHeight + margin * 3.0f

/// World XZ to plan pixels. +Z is drawn upward, so north on the plan is +Z.
let planX (x: float32) = margin + (x - bounds.Min.X) * scale
let planY (z: float32) = margin + (bounds.Max.Z - z) * scale
let elevationBase = margin * 2.0f + planHeight + elevationHeight
let elevationY (y: float32) = elevationBase - (y - min 0.0f bounds.Min.Y) * scale

let waterLevel = defaultArg level.WaterLevel Single.NegativeInfinity

let materialName material =
    match material with
    | Brick -> "brick" | Plaster -> "plaster" | Wood -> "wood" | Mud -> "mud"
    | Snow -> "snow" | Sandbag -> "sandbag" | Metal -> "metal"
    | UniformOlive -> "olive" | UniformFeldgrau -> "feldgrau" | Skin -> "skin" | Water -> "water"
    | Sand -> "sand" | RustedMetal -> "rusted" | Concrete -> "concrete"

/// Height drives the fill so elevation reads at a glance; material only tints
/// it, because "how high is this" is the question a plan view has to answer.
let heightFill (top: float32) =
    let highest = max 1.0f (bounds.Max.Y)
    let t = Math.Clamp(top / highest, 0.0f, 1.0f)
    let r = int (40.0f + t * 200.0f)
    let g = int (60.0f + t * 150.0f)
    let b = int (90.0f + t * 60.0f)
    $"rgb(%d{r},%d{g},%d{b})"

let svg = StringBuilder()
let line (text: string) = svg.AppendLine text |> ignore

line $"""<svg xmlns="http://www.w3.org/2000/svg" width="%.0f{totalWidth}" height="%.0f{totalHeight}" viewBox="0 0 %.0f{totalWidth} %.0f{totalHeight}">"""
line """<style>
  text { font-family: ui-monospace, Menlo, monospace; fill: #dfe4dc; }
  .brush { stroke: rgba(0,0,0,0.55); stroke-width: 1; }
  .nav { stroke: rgba(120,220,255,0.30); stroke-width: 1; }
  .navnode { fill: rgba(120,220,255,0.55); }
  .allies { fill: #5bc0ff; }
  .axis { fill: #ff7a5b; }
  .neutral { fill: #cfcfcf; }
  .cover { stroke: #ffd166; stroke-width: 1.5; }
  .grid { stroke: rgba(255,255,255,0.07); stroke-width: 1; }
  .label { font-size: 13px; }
  .small { font-size: 10px; fill: #9aa39a; }
</style>"""
line $"""<rect width="100%%" height="100%%" fill="#12151a"/>"""

// Ten-metre grid, so distances and sightlines can be read off directly.
let firstGridX = ceil (bounds.Min.X / 10.0f) * 10.0f
let mutable gx = firstGridX
while gx <= bounds.Max.X do
    line $"""<line class="grid" x1="%.1f{planX gx}" y1="%.1f{margin}" x2="%.1f{planX gx}" y2="%.1f{margin + planHeight}"/>"""
    line $"""<text class="small" x="%.1f{planX gx + 3.0f}" y="%.1f{margin + 12.0f}">%.0f{gx}</text>"""
    gx <- gx + 10.0f
let firstGridZ = ceil (bounds.Min.Z / 10.0f) * 10.0f
let mutable gz = firstGridZ
while gz <= bounds.Max.Z do
    line $"""<line class="grid" x1="%.1f{margin}" y1="%.1f{planY gz}" x2="%.1f{margin + planWidth}" y2="%.1f{planY gz}"/>"""
    line $"""<text class="small" x="%.1f{margin + 3.0f}" y="%.1f{planY gz - 3.0f}">%.0f{gz}</text>"""
    gz <- gz + 10.0f

// Every upward-facing collision triangle, lowest first. Drawing the collision
// mesh rather than the brushes is what makes ramps and slopes visible at all —
// they are not boxes and would otherwise be missing from the plan entirely.
line """<g id="plan-surfaces">"""
level.Collision.Triangles
|> Array.filter (fun t -> t.Normal.Y > 0.3f)
|> Array.sortBy (fun t -> (t.A.Y + t.B.Y + t.C.Y) / 3.0f)
|> Array.iter (fun t ->
    let mid = (t.A.Y + t.B.Y + t.C.Y) / 3.0f
    // The ground slab would otherwise hide the grid entirely.
    let opacity = if mid <= 0.01f then 0.30f else 0.92f
    let points =
        [ t.A; t.B; t.C ]
        |> List.map (fun p -> $"%.1f{planX p.X},%.1f{planY p.Z}")
        |> String.concat " "
    // Walkable faces are drawn solid; anything past the slope limit is hatched
    // dark, because "can I climb this" is the plan view's other job.
    let walkable = t.Normal.Y >= Tuning.MaxSlopeCosine
    let fill =
        if mid < waterLevel then "#1d4a5c"           // under the sea
        elif walkable then heightFill mid
        else "#2a1c1c"
    line $"""<polygon class="brush" points="%s{points}" fill="%s{fill}" fill-opacity="%.2f{opacity}"><title>%s{materialName t.Material} y %.2f{mid} %s{if walkable then "walkable" else "too steep"}</title></polygon>""")
line "</g>"

// Nav links show where the AI can actually walk — usually the first thing that
// is wrong when terrain changes.
line """<g id="plan-nav">"""
level.Nav
|> Array.iteri (fun index node ->
    for neighbour in node.Neighbours do
        // Each edge appears in both nodes; draw it once.
        if neighbour > index then
            let other = level.Nav[neighbour]
            line $"""<line class="nav" x1="%.1f{planX node.Position.X}" y1="%.1f{planY node.Position.Z}" x2="%.1f{planX other.Position.X}" y2="%.1f{planY other.Position.Z}"/>""")
level.Nav
|> Array.iter (fun node ->
    line $"""<circle class="navnode" cx="%.1f{planX node.Position.X}" cy="%.1f{planY node.Position.Z}" r="1.6"><title>nav y=%.2f{node.Position.Y}</title></circle>""")
line "</g>"

line """<g id="plan-cover">"""
level.Cover
|> Array.iter (fun point ->
    let x, y = planX point.Pos.X, planY point.Pos.Z
    let peek = MathEx.normalizedOrZero point.PeekDir * 1.2f
    line $"""<line class="cover" x1="%.1f{x}" y1="%.1f{y}" x2="%.1f{planX (point.Pos.X + peek.X)}" y2="%.1f{planY (point.Pos.Z + peek.Z)}"/>""")
line "</g>"

line """<g id="plan-spawns">"""
level.Spawns
|> Array.iter (fun struct (owner, position) ->
    let cls = match owner with Some Allies -> "allies" | Some Axis -> "axis" | None -> "neutral"
    line $"""<circle class="%s{cls}" cx="%.1f{planX position.X}" cy="%.1f{planY position.Z}" r="3.2"><title>spawn y=%.2f{position.Y}</title></circle>""")
line "</g>"

// Side elevation: every brush projected onto XY, which is how you check that a
// slope actually climbs and that nothing pokes through the ceiling.
line """<g id="elevation">"""
line $"""<line class="grid" x1="%.1f{margin}" y1="%.1f{elevationY 0.0f}" x2="%.1f{margin + planWidth}" y2="%.1f{elevationY 0.0f}"/>"""
level.Collision.Triangles
|> Array.sortBy (fun t -> (t.A.Y + t.B.Y + t.C.Y) / 3.0f)
|> Array.iter (fun t ->
    let points =
        [ t.A; t.B; t.C ]
        |> List.map (fun p -> $"%.1f{planX p.X},%.1f{elevationY p.Y}")
        |> String.concat " "
    let mid = (t.A.Y + t.B.Y + t.C.Y) / 3.0f
    line $"""<polygon class="brush" points="%s{points}" fill="%s{heightFill mid}" fill-opacity="0.28"/>""")
line $"""<text class="small" x="%.1f{margin}" y="%.1f{elevationBase + 16.0f}">elevation — looking along +Z, ceiling %.1f{bounds.Max.Y}m</text>"""
line "</g>"

let coverCount = level.Cover.Length
let alliesSpawns = level.Spawns |> Array.filter (fun struct (o, _) -> o = Some Allies) |> Array.length
let axisSpawns = level.Spawns |> Array.filter (fun struct (o, _) -> o = Some Axis) |> Array.length
let navLinks = level.Nav |> Array.sumBy (fun n -> n.Neighbours.Length) |> fun total -> total / 2
let highest = level.Brushes |> Array.fold (fun acc b -> max acc b.Bounds.Max.Y) 0.0f
line $"""<text class="label" x="%.1f{margin}" y="%.1f{margin - 14.0f}">%s{level.Name} — %.0f{spanX}m x %.0f{spanZ}m — %d{level.Brushes.Length} brushes, %d{level.Nav.Length} nav nodes (%d{navLinks} links), %d{coverCount} cover, spawns %d{alliesSpawns}A/%d{axisSpawns}X, tallest %.1f{highest}m</text>"""
line "</svg>"

// One file per level under debug/, so previewing a second map does not
// overwrite the first and the repo root stays free of build artefacts.
let outputDir = IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "debug", "map-preview") |> IO.Path.GetFullPath
IO.Directory.CreateDirectory outputDir |> ignore
let slug = level.Name.ToLowerInvariant().Replace(" ", "-")
let output = IO.Path.Combine(outputDir, slug + ".svg")
IO.File.WriteAllText(output, svg.ToString())

printfn "%s: %.0fm x %.0fm" level.Name spanX spanZ
printfn "  brushes   %d (tallest %.2fm, ceiling %.1fm)" level.Brushes.Length highest bounds.Max.Y
printfn "  nav       %d nodes, %d links" level.Nav.Length navLinks
printfn "  cover     %d points" coverCount
printfn "  spawns    %d Allies, %d Axis" alliesSpawns axisSpawns
printfn "  wrote     %s" output
