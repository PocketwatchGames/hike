# The elevation model

## The elevation model: signed, and snapped to a lattice


Two rules, both about making a map an author can actually READ and AIM:

- **Heights are signed around sea level, not measured up from it.** The layer
  spans `minElevationVoxels`..`maxElevationVoxels` (-32..64) relative to
  `seaLevel`, so lowering the brush digs ocean floor and raising it builds land,
  with the shore at exactly 0. The earlier encoding was a 0..1 fraction of the
  height ABOVE sea level, which made every painted column land at or above the
  waterline — the only way to get water was nudging a live ocean-Y knob whose
  value was never saved. `seaLevel` is now a document constant; there is no live
  ocean knob and nothing to lose on save.
- **Every height snaps to `elevationStepVoxels` (1, i.e. whole metres).**
  `ColumnHeight` is the one place that does it, so the map, the brushes and the
  bake cannot disagree about where a step lands. Raising it forces coarser
  terracing; 1 simply means the voxel grid is the lattice.
- **Colour is banded, and the palette is authored, not code.**
  `WorldMapInkData.elevationBandHues` + `metersPerBand` (4): the band a height falls
  in picks a colour from the cycle, and the metre within the band lifts it toward
  white by a fraction of each channel's own headroom. The authored colour is the
  band's BASE, its lowest metre, so it is authored at part value: a fully
  saturated base has no headroom and its metres would be indistinguishable. The
  band's TOP metre lands at `elevationBandMaxBrightness` (0.5) of the way to
  white and the metres between divide that evenly, so one knob sets a band's
  whole contrast range — 1 would bleach the top metre until it no longer says
  which band it is, 0 would flatten every metre onto the base and lose the step. Every step is therefore visible on its own terms —
  a lift within a band, a hue change across one — which a continuous hypsometric
  ramp cannot do: spread over dozens of levels, neighbours differ by a few
  percent and the map reads as a smudge.

**Water is a toggleable overlay, not part of the height colouring** —
`WorldMapView.ShowWater` (**W**, display-only, never saved) gates `WithWater`,
which every land-showing view composites on top of its terrain colour. Off, you
see the bare banded height field, which is what shaping a lake bed or an
already-flooded coast needs.

On, submerged ground is **opaque** water in two shades (`shallowWaterColor` down
to `shallowWaterDepth`, `deepWaterColor` below), and the painter skips relief
shading there. Both are deliberate: the elevation bands and a tinted seabed would
otherwise speak the same colour language, and shading would put the bed's shape
back into a colour whose job is to say the ground is not visible. Step outlines
still draw over water, so the bed's SHAPE stays readable while its HEIGHT does
not.

**Alt is "aim at the height I clicked"**, delivered through `BeginStroke` — the
one hook on `IWorldMapTool` that fires on mouse-down, before a stroke paints.
`ElevationTool` adopts the height under the cursor as its Flatten target, once
per stroke; re-reading it per stamp would make the brush chase what it had just
painted and drift off the picked value. Three behaviours fall out of the one
modifier:

- **alt+click** — pure eyedropper. The target persists and shows in the HUD, so
  it is also the fast way to aim Flatten at all: R/F walks one lattice step per
  press, and picking a plateau you already built beats forty of them.
- **alt+drag** — the picked height spread wherever you drag, hard-edged under
  Flatten and feathered under FlattenSoft.

A pick press does not paint the metre it sampled, and the stroke stays held until
the cursor leaves that metre — so a click is only ever a pick, and only a real
drag spreads anything. That is why alt is a modifier on the press rather than a
fifth `EBrushOp` (which is what this was first built as): picking a height while
Raise happened to be selected would otherwise raise the spot you sampled, and the
op axis (how it writes) and the target axis (what it aims at) would multiply
instead of composing.

**Lift offsets each column ONCE per stroke** (`LiftVoxels`, R/F-adjusted, RMB
inverts it). Raise accumulates per motion event, so scrubbing an area compounds
it and the middle of a region ends up higher than its edges; Lift moves
everything it touches by exactly its amount, so a region keeps its own relief and
simply sits higher. The per-stroke ledger that makes it idempotent is the same
array the constraints use, with one extra state.

**Two constraints, because one comparison cannot do both jobs.** **Shift** paints only columns EQUAL to the height under the press — one terrace,
leaving its neighbours alone. **Ctrl** paints columns at or ABOVE it, which is
the one that scales: lifting a continent means "everything from the shoreline
up", across ground of every height, and equality cannot express that. Both hold
at-or-above when held together, since painting less than asked is the worse
failure.

Eligibility is judged the first time a column is touched and remembered for the
rest of the stroke, so the mask is the PRE-stroke shape: re-testing the live
height every stamp would let a stroke erode its own mask, moving a column off the
constrained height with the first stamp and then refusing to touch it with the
second. The mask fills lazily rather than by scanning the map at press, since a
stroke only touches its own path.

**Flatten and FlattenSoft are a hard/soft pair aimed at the same target.**
Flatten writes `TargetVoxels` to every texel inside the radius, ignoring both the
falloff and `flow`, because a plateau wants a crisp edge and easing it in by
weight bevels the rim into rings of half-steps. FlattenSoft is that same easing,
kept deliberately as its own op for when you WANT the ramp — grading a plateau
into its surroundings.

**Roughen is erosion, not smoothing.** Smooth blurs toward a neighbourhood
average, which rounds a cliff into a slope; Roughen leaves the wall a WALL and
attacks only its edges — talus piling at the foot, the lip crumbling off.

Like Smear it is an `EBrushOp` on `ElevationTool` rather than a tool beside it:
it is aimed with the same brush at the same cliffs, off the same map, and the
choice between "raise this" and "weather this" is the same kind of choice as the
one between Raise and Flatten. Its strength rides **R/F** (`RoughenStopIndex`,
25/50/75/100%), because the option row belongs to the op list now. Two things it
does NOT share with the other ops: it writes the roughness channel instead of the
elevation layer, and its repaint rect is the brush inflated by
`roughenMaxSpreadVoxels` — talus lands on columns the cursor never covered, and
without the inflation the erosion appears clipped to the stroke until something
else repaints that ground. Its UNDO rect stays the plain brush disk, because the
strengths it writes are only under the disk; the heights are recomputed, never
stored.

**It is a LAYER, not an edit**, and that is the whole design. The brush writes a
strength per column (`B` of the scalar image) and the erosion is recomputed from
the PRISTINE elevation every time a height is asked for, so painting the same
wall twice cannot crumble it: the second stroke only raises a strength already
capped at 1. It was first built as an in-place edit of the elevation raster and
had exactly that failure, plus a dependence on which way the drag swept, since
raising a foot column changed the drop its neighbour measured. Recomputing also
means the map draws precisely what the bake will stamp, because both arrive
through `TerrainHeight`.

The budget is the cliff's height minus `roughenKeepBandVoxels` (3): **one voxel
at 4m, two at 5m**, and so on, scaled by the painted strength. The band is what
keeps an eroded cliff a cliff.

**Spending that budget correctly is the whole difficulty**, and three passes each
exist because of a way the naive version broke a cliff open. All three were found
by looking at the map: a 2m step draws in black, so a wall turning into a
staircase is visible at a glance.

1. **Spread.** The budget is splatted as a cone decaying a voxel per
   `roughenTalusRunPerVoxel` metre, not dumped into the column at the wall.
   Dumping it made a step as tall as the budget — a 5m wall became a 2m step plus
   a 3m wall, and **a 2m step is mantleable**, so the "erosion" handed the player
   a stair.
2. **Cap.** A column may not move so far that it shortens ANY wall it touches
   below the band. Talus is computed from one cliff and is blind to the others
   its column abuts: at a junction of a 0m, 4m and 6m plateau, the 6m wall's
   talus rose 2m at the foot and left the neighbouring 4m wall only 2m tall.
3. **Relax.** The cap alone puts the steps back, because a capped column sits
   beside an uncapped one. Both fields are forced 1-Lipschitz afterwards. This
   can only REDUCE them, which is what lets it run after the cap without
   reopening what the cap closed.

The result: every step weathering creates is a single voxel, and the only thing
left taller is the band itself.

**Everything above is measured against `StandHeight`** — the ground, or the water
surface where water stands over it — and not against the raw ground. A cliff at a
shoreline is only as tall as the part out of the water, and measuring to the
seabed made a 6m sea cliff over a -2m bed read as an 8m cliff: it drew the budget
of one and piled a talus shelf that surfaced at +3, turning a cliff nothing could
climb into a ledge reachable by swimming. Talus is also clamped so it fills the
shallows but never breaks the surface, since a shelf that surfaces is somewhere
to stand and standing there is what shortens the cliff. Painted lakes get the
same protection as the sea; both are just a water surface here.

Heights are cached in a field rebuilt over a dirty rect (`InvalidateHeights`),
because the spread reads a neighbourhood and `TerrainHeight` is called for every
cell of every rebuild and every column of the bake. The painter invalidates after
each stamp and drops the cache entirely after undo.

**The noise splits the budget between the two ends** — all talus here, all
crumbled lip a few metres along — which is what stops a weathered cliff reading
as a uniform double step. Both ends sample it at the FOOT column so they agree
about the split; sampling each end at its own position would let both take the
large share and eat through the band. Each end is gated on its own painted
strength, so painting just the foot piles talus without touching the lip — safe
as well as useful, since the two shares are the floor and ceiling of
complementary parts of one budget and sum to exactly the budget even at full
strength on both sides.

Two rules that keep the model from feeding on itself: erosion is always measured
against `RawHeight` (the painted lattice height), never against an
already-weathered neighbour, and `TerrainHeight` early-outs on an unpainted
column so the hot path costs one extra read where nothing is roughened.

**Smooth is not part of that pair.** It blurs toward the neighbourhood average
and has no target at all, so it is elevation-independent: it takes roughness out
of whatever is already there rather than moving it toward a height you chose. It
averages over the **whole brush**, not the cells immediately around each texel —
the brush is how big an area you meant to affect, and a fixed 3x3 kernel ignored
it, so a wider brush just spread the same barely-there smoothing over more
ground. Prepared per stamp as a separable box blur over the affected region,
since the direct form is quadratic in the radius per texel (~280k image reads a
motion event at radius 12, against ~5k this way).

**Smear is a RAMP, not a smudge**, and the difference is the whole design. It is
an `EBrushOp` on `ElevationTool` like the rest — it sculpts the same layer with
the same brush, so it belongs on the same tool rather than beside it. A
paint-program smudge samples "a bit behind me" and blends it forward, which on a
heightfield leaves a rounded ditch down the middle of the stroke: the leading
edge carries the high ground forward while the ground it already passed keeps
sampling from further back, so the middle is pulled down twice. Every parameter
that controls the effect also controls the artefact, so it cannot be tuned away.

So the profile is linear BY CONSTRUCTION. The press anchors one end at the height
under it, the cursor is the other end at the height that was there before the
stroke, and every column between takes the height its position along that line
asks for — a straight line has no middle to dip. Three things fall out of the
same construction: the ends do not move, so a ramp meets both plateaus flush;
the whole corridor is rewritten on every motion event, so dragging further
re-solves the ramp over its full length instead of leaving the earlier part at a
steeper slope; and repeating a stroke converges rather than accumulating, because
the target depends only on the two PRE-stroke heights. That last one is why the
tool keeps its own pre-stroke snapshot — reading the far end live would let the
ramp chase its own tail.

`TouchRect` and `LastPaintRect` both report the corridor rather than the brush
disk while Smear is the active op, so undo covers ground the cursor never passed
over and the display repaints the whole ramp. Smear is also the one op where the
brush `Radius` is a half-WIDTH: the corridor's length is however far you drag.

Brush strength is in **voxels per stroke event** (`VoxelsPerStroke`, 0.5) rather
than a fraction of the full range — a motion event used to move ~1.3 voxels, so
a flick crossed tens of voxels. Several events per visible step is the point.
