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

let materialName material =
    match material with
    | Brick -> "brick" | Plaster -> "plaster" | Wood -> "wood" | Mud -> "mud"
    | Snow -> "snow" | Sandbag -> "sandbag" | Metal -> "metal"
    | UniformOlive -> "olive" | UniformFeldgrau -> "feldgrau" | Skin -> "skin"

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

// Brushes, lowest first, so tall geometry paints over the ground slab.
line """<g id="plan-brushes">"""
level.Brushes
|> Array.sortBy (fun item -> item.Bounds.Max.Y)
|> Array.iter (fun item ->
    let b = item.Bounds
    let x, y = planX b.Min.X, planY b.Max.Z
    let w, h = (b.Max.X - b.Min.X) * scale, (b.Max.Z - b.Min.Z) * scale
    // The ground slab would otherwise hide the grid entirely.
    let opacity = if b.Max.Y <= 0.01f then 0.35f else 0.92f
    line $"""<rect class="brush" x="%.1f{x}" y="%.1f{y}" width="%.1f{w}" height="%.1f{h}" fill="%s{heightFill b.Max.Y}" fill-opacity="%.2f{opacity}"><title>%s{materialName item.Material} top %.2f{b.Max.Y}m</title></rect>""")
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
level.Brushes
|> Array.sortBy (fun item -> item.Bounds.Max.Y)
|> Array.iter (fun item ->
    let b = item.Bounds
    let x = planX b.Min.X
    let w = (b.Max.X - b.Min.X) * scale
    let top = elevationY b.Max.Y
    let h = max 1.0f ((b.Max.Y - b.Min.Y) * scale)
    line $"""<rect class="brush" x="%.1f{x}" y="%.1f{top}" width="%.1f{w}" height="%.1f{h}" fill="%s{heightFill b.Max.Y}" fill-opacity="0.5"/>""")
line $"""<text class="small" x="%.1f{margin}" y="%.1f{elevationBase + 16.0f}">elevation — looking along +Z, ceiling %.1f{bounds.Max.Y}m</text>"""
line "</g>"

let coverCount = level.Cover.Length
let alliesSpawns = level.Spawns |> Array.filter (fun struct (o, _) -> o = Some Allies) |> Array.length
let axisSpawns = level.Spawns |> Array.filter (fun struct (o, _) -> o = Some Axis) |> Array.length
let navLinks = level.Nav |> Array.sumBy (fun n -> n.Neighbours.Length) |> fun total -> total / 2
let highest = level.Brushes |> Array.fold (fun acc b -> max acc b.Bounds.Max.Y) 0.0f
line $"""<text class="label" x="%.1f{margin}" y="%.1f{margin - 14.0f}">%s{level.Name} — %.0f{spanX}m x %.0f{spanZ}m — %d{level.Brushes.Length} brushes, %d{level.Nav.Length} nav nodes (%d{navLinks} links), %d{coverCount} cover, spawns %d{alliesSpawns}A/%d{axisSpawns}X, tallest %.1f{highest}m</text>"""
line "</svg>"

let output = IO.Path.Combine(__SOURCE_DIRECTORY__, "..", "map-preview.svg") |> IO.Path.GetFullPath
IO.File.WriteAllText(output, svg.ToString())

printfn "%s: %.0fm x %.0fm" level.Name spanX spanZ
printfn "  brushes   %d (tallest %.2fm, ceiling %.1fm)" level.Brushes.Length highest bounds.Max.Y
printfn "  nav       %d nodes, %d links" level.Nav.Length navLinks
printfn "  cover     %d points" coverCount
printfn "  spawns    %d Allies, %d Axis" alliesSpawns axisSpawns
printfn "  wrote     %s" output
