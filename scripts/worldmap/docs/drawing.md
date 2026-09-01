# Drawing the map, and what a rebuild costs

## How the map is drawn (relief + step outlines)


The painter renders the map at **`rasterPixelsPerMeter` (3) pixels per metre**
into a managed `byte[]`, and the canvas draws that at an integer scale rather
than fitting it to the window. Both halves matter: the sub-metre resolution is what lets a step outline
be a thin line on a voxel edge instead of a metre-wide block, and drawing it
unscaled is what keeps those one-pixel lines crisp (a fitted map resamples metres
to fractional pixels and smears them).

**Ctrl+wheel zooms** and **middle-drag pans** once the map overflows the window.
A zoom anchors on the metre under the cursor — the canvas records which metre
that is, lets the painter rescale, then solves the pan that puts the same metre
back under the pointer, so zooming into a feature cannot throw it off screen.

**Rasterising and magnifying are SEPARATE, and that is what makes zoom cheap.**
`rasterPixelsPerMeter` (3) is the resolution the buffer is drawn at;
`WorldMapCanvas.Zoom` is an integer multiple applied at DRAW time, with nearest
filtering, so an integer multiple cannot resample a metre onto fractional pixels
and the one-pixel outlines stay exact. Ctrl+wheel walks one ladder of screen
pixels per metre — 1, 2, then the raster magnified 1..`maxZoomFactor`, i.e.
3, 6, 9, 12 — and **only a change of RASTER touches the buffer**:

| screen px/m | raster x zoom | buffer | cost |
|---|---|---|---|
| 1, 2 | 1x1, 2x1 | 1 MB, 4 MB | rebuild (the cheap end) |
| 3 | 3x1 | 10 MB | rebuild |
| 6, 9, 12 | 3x2, 3x3, 3x4 | 10 MB | **one QueueRedraw** |

The two were one number before, so every notch reallocated the buffer, repainted
all ~295k cells into it, and re-uploaded the result — 72 MB and ~240 ms of CPU at
8 px/m before the GPU saw any of it, which is why zooming in was the slowest
thing in the painter. Nothing about the IMAGE changes when you magnify, only how
big it is drawn. Peak memory fell with it, from 72 MB to a fixed 10 MB, so the
ceiling on zoom is now taste rather than memory — which is why it reaches 12 px/m
where it used to stop at 8.

Over whatever colour the active view returns, the painter composites two things,
so every tool gets the same terrain readability:

- **Relief shading** — a hillshade of the **raw, unsnapped** height field, lit
  from the NW at 45 degrees. **Off by default** (`reliefStrength = 0`): once the
  palette became exact authored bands, shading fought it on two counts. It
  multiplies every band by 0.6-1.1, so no band is the colour it was authored as;
  and it lights one side of every step and darkens the other, which reads as a
  BEVEL around a flatten stroke whose data is perfectly flat (measured: the disk
  is exactly the target height to its last texel, while the relief multiplier
  swings 1.10 to 0.61 across the rim). Height is carried by the bands and the
  step outlines instead. Raise the strength to get the hillshade back.
- **Step outlines** on voxel edges where the surface a BODY meets changes — the
  water where a water-drawing view has water switched on, the ground otherwise,
  so the sea is one flat sheet outlined only at its shore rather than a contour
  map of a seabed hidden under opaque water. Inked on the
  HIGHER side so a line reads as the rim of its plateau. The ink comes from
  `WorldMapInkData.edgeInkSub2m` / `edgeInk2m` / `edgeInkOver2m` (alpha in the
  colour), bucketed by the size of the step: **under 2m, exactly 2m, and more
  than 2m** — which is a TRAVERSAL legend (walk up / mantle / wall) and not a
  height one. That is why the outlines read `WorldMapState.StandSurface` rather
  than the drawn surface: every number in those buckets is measured off the level
  a body is held at, and in water that is `WaterStandDrop` (1m) below the free
  surface — the convention `WalkabilityGrid` stores and the mantle is judged
  against. Without the drop a bank one voxel proud of a lake, which is a real
  mantle out of the water, inked as a walk-up. Depth does not enter into it:
  wading and swimming differ in how a body is held up, not in where the surface
  holding it sits. Every column of one body drops together, so a lake stays one
  flat sheet and only the bucket at its shore changes. The minor bucket is drawn only where `IWorldMapView.ShowsAllSteps`
  is true — on the elevation map the bands already say the height, so a line on
  every metre of every slope is noise, while on a region or zone map the outlines
  are the only height cue there is. Width is
  `edgeWidthFraction` of a metre (min 1px), so lines thicken with zoom instead of
  staying a hairline against big cells, and they grow INTO the higher cell so a
  wide line never spills onto the lower plateau.

Rebuilds inflate their rect by one metre (relief reads neighbours, and the cell
outside a stroke owns the edge it shares with one inside), and run base colour
and outlines as two passes so a line is never overpainted. The texture upload is
flagged and done once per frame in `_Process`, not per motion event.

## What a display rebuild costs


**A full rebuild is ~295k texels, and everything resolved per texel is multiplied
by that.** It matters because **T/G rebuilds the whole map** — the cutaway changes
every cell — so scrubbing it is the worst case the painter has. Measured on the
default document, a full rebuild was ~620 ms, which under key repeat queued
faster than it could run and stopped the painter answering the keyboard at all.
Four things were wrong, and each is a shape worth not reintroducing:

- **A string-keyed dictionary lookup in the texel loop.** Every texel rebuilt each
  candidate stamp's footprint and hashed a `(path, rotation)` key to find its
  cached plan: 277 ms, scaling with how many buildings the document holds.
  `WorldMapState.StampPlan` (from `PlanStamps`) resolves footprints, plan colours,
  per-column tops and seats ONCE per rebuild, so the per-texel cost is a rect test
  and two array reads — **19 ms**.
- **An argument computed to be multiplied by zero.** `Lerp(1, ReliefShade(…), 0)`
  still ran the hillshade — four `Image.GetPixel` calls per cell — because C#
  evaluates arguments eagerly. 120 ms for a feature that is off by default, plus
  30 ms for the `IsSubmerged` test that only exists to gate it. Both are behind
  `reliefStrength > 0` now.
- **The same query asked four times per cell.** `CutawayFloor` ran once for the
  colour and three times more as each cell's neighbours recomputed it for the
  outlines. `ResolveCutaway` fills a map-sized scratch pair (`_cutSurface`,
  `_buried`) once, which both passes then index. It must reach **one cell past
  the fill on every side** — the outline pass starts a cell back AND asks its +X
  and +Z neighbours — or the rebuilt rect's own border inks against a cell this
  pass never wrote.
- **No coalescing.** `RebuildFull` now only sets a flag; `_Process` does the work,
  so several calls in one frame cost one rebuild.

Two cheaper reads fell out of the same pass and are worth keeping: `ElevationColorAt`
memoises its banding math over the document's signed range (the palette is
authored and immutable at runtime), and `WithWaterOver` lets a caller that already
knows the surface skip resolving it a second time.

The rebuild is ~4x cheaper for it. If it needs to get cheaper again, the lever is
rebuilding only the **visible** region rather than the whole map — the canvas
draws unscaled with pan, so a window typically shows a small fraction of a large
document — which needs dirty-tracking so panning rebuilds what it exposes.
