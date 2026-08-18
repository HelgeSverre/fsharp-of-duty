namespace Ironsight.Shell

open System.Numerics

/// Screen-space rectangle in the HUD's top-left-origin logical pixels.
type Rect =
    { X: float32
      Y: float32
      W: float32
      H: float32 }

[<RequireQualifiedAccess>]
module Rect =
    let inset dx dy r =
        { X = r.X + dx; Y = r.Y + dy; W = r.W - dx * 2.0f; H = r.H - dy * 2.0f }

    /// Row slot `index` inside `r`, rows stacked from r.Y.
    let slot (rowHeight: float32) (index: int) r =
        { r with Y = r.Y + rowHeight * float32 index; H = rowHeight }

    /// y that vertically centers a glyph line of `scale` (12 logical pixels
    /// tall at scale 1) in a slot, so cells drawn at different scales share
    /// the slot's midline.
    let textY (scale: float32) (slot: Rect) = slot.Y + (slot.H - 12.0f * scale) * 0.5f

    /// Which of `count` row slots stacked from r.Y the point falls in — the
    /// hover hit-test, derived from the same rect the rows are drawn in.
    let slotAt (rowHeight: float32) (count: int) (point: Vector2) (r: Rect) =
        if point.X >= r.X && point.X <= r.X + r.W && point.Y >= r.Y && point.Y < r.Y + rowHeight * float32 count then
            Some(int ((point.Y - r.Y) / rowHeight))
        else
            None
