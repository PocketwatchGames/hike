# World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The **first step in the world-authoring chain**: a broad-brush, in-game paint
program that authors a layered raster *document* and bakes it into a real
`WorldState` / `.hike`. The downstream `WorldEditor` does fine per-voxel detail;
the game loads the baked `.hike`.

## Model: document + bake (not direct-voxel paint)

The authored source of truth is **`WorldMapData`** (`scripts/data/worldmap/`,
a plain `Resource` — deliberately NOT `[Tool]`, or the editor strips its
`genData` reference on every save; see the note on the class) — bake settings (`GenData`, world extent in chunks, default
sea level, height scale) plus references to the **layer files** (openable
directly). It mirrors the `VoxelAtlasManifest` convention: one editor-visible
resource of record + a re-runnable bake (`BakeToWorldFile`, pure C# so it runs
headless).

Layers:
- **Elevation** — `.exr` `Rf`, per voxel **column**, in **voxels relative to
  `seaLevel`, signed** (negative digs seabed). Godot's EXR round-trips negatives
  exactly, verified.
- **Water** — `.exr` `Rf`, per column: the water SURFACE, in voxels relative to
  `seaLevel`, signed like the elevation layer. **The layer is the whole answer —
  there is no waterline rule folded in.** A blank layer is zeros and 0 encodes
  `seaLevel`, so an untouched document is already prefilled with water at the sea
  everywhere; land simply hides the water it stands above. A value below
  `minElevationVoxels` is the "no water at all" sentinel the erase brush writes,
  which is what makes a DRY basin below sea level expressible.
- **Region** / **Zone** — `.png` `R8`, per **chunk** (index → `ChunkState.RegionIndex` / `ZoneIndex`).
- **Props** / **Mobs** — `.png` `Rgba8`, per column (R = set index + 1,
  G = density multiplier), indexing `propSets` / `mobSets`.
- **Ground** — `.png` `R8`, per column (ground set + 1; 0 = `defaultGround`).
- **Paving** — `.png` `R8`, per column (paving block + 1; 0 = none).
- **Scalars** — `.png` `Rgba8`, per column: R = mob level, G = climb route flag,
  B = cliff roughness.
- **Placements** — `.tres` (`WorldMapPlacements`): the subscene stamps, the
  hand-placed entities, and the player spawn. Not a raster: a per-column byte
  cannot hold "this scene, facing that way", nor two of them overlapping, nor a
  footprint that moves as one thing.
- **Tunnels** — `.bin`, per-voxel carve mask (`byte[px,ly,pz]`), too 3D to be a
  useful image; the carved result is captured in the baked `.hike`.

## Runtime + bake (`WorldMapState`)

The mutable runtime document: owns every layer's data, the queries the
tools/views read (`TerrainHeight`, `WaterSurface`, `Underwater`, `Ocean`,
`SolidAt`, `IsTunnel`, `ColumnHeight` against the live `SeaLevel`), and the
deterministic `BuildWorld` bake. **The painter edits only the 2D layer images —
no live voxel `World` is kept.** The `WorldState` is materialized on demand:
`BuildWorld` creates every chunk, stamps regions/zones, stamps all columns,
scatters entities, and propagates sunlight.

**Ctrl+S saves on the main thread and bakes on a background one.** `Save` writes
the layer images — fast, and all it takes not to lose your painting. `Bake` then
runs `BuildWorld` + `WorldFile.Write` (every chunk built, ~7M voxels stamped, a
~57MB file written: ~3s on an 18x16 map, since the relight moved to load) in a `Task`, with a
progress panel bottom-right; painting stays live throughout.

The task bakes its **own `WorldMapState`, constructed on the main thread from the
files Save just wrote** — never the live layer images, which the brush is still
writing into. One bake at a time, and the reason is narrower than it used to be:
the kit palette is no longer process state (the bake builds its own and hands it
to the `WorldState` it creates), and `EntitySerializer`'s path tables are
`[ThreadStatic]`, so neither is a hazard. What remains is **`Blocks.Bind()`**,
which REASSIGNS the global block tables a live main thread is reading, and
**`WorldGen.MobLevelOverride`**, a static delegate the bake installs and clears
around its scatter pass.

**The bake does NOT light the world.** Every consumer of a `.hike` relights on
open — `Main` on both load branches, `WorldEditor` on both its open paths —
because baked light is only as good as the pipeline was at save time, and
`SkyExposure` is not serialized at all, so the format assumes the pass happens on
load. Lighting was ~19s of a ~22s bake and was discarded every time. (A consumer
that ever loads a `.hike` without relighting would get a black world and should
relight, not move the pass back here.) `LightEngine.Relight` keeps its optional
progress callback, which the editor and any future long relight can use. `StampColumns` stamps each column: tunnel-carve → `Air`, else `Terrain`
up to `TerrainHeight`, else `Water` up to `WaterSurface`, else `Air`.

**The bake ends on `WorldGen.StampGradeShapes`, and must.** `StampColumns`
writes every ground voxel with a blanket `SharpAxes.Y`, which is right for a
terrace and wrong for a slope: the mesher then draws flat treads with 1 m
vertical risers, so painted ground that reads as a ramp on the map is a
staircase rather than a ramp, and the player does not climb it. The pass
re-derives the shape channel from
the FINISHED voxels — a surface whose neighbours are within `maxGradeStep` on
either axis becomes `SharpAxes.None` and meshes as a real plane. Measured with
`mesher_probe`: 1-in-1 gives normal.y 0.707 (45 degrees) dead flat across the
face, 1-in-2 gives 0.894 (26.6) and 1-in-3 gives 0.948 (18.4). `maxGradeStep`
is 1 and no `.tres` overrides it, so this reaches ONE-voxel steps only — a
2-voxel step is not a grade, stays a crisp wall, and gets a `LedgeBarrier` on
top. A painted hillside steeper than a voxel per metre is therefore a MIX of
graded 45s and hard walls, which is what it looks like. It runs last,
after scenes / routes / scatter, because it classifies whatever geometry is
actually there. It is worldgen's own pass, given world bounds instead of a
`HeightMap` (the height field was only ever its horizontal extent); a
painter-side reimplementation is how the waterfall shading became two copies
that drifted.

**A kit's ambient scatter is a shared `SpawnSetData`, not its own list.**
`TerrainKitData.forest` references one, and `Trees` / `Foliage` /
`ForestFrequency` / `ForestThreshold` / `ForestDensity` / `TreesPerChunkMin/Max`
resolve to it, falling back to the kit's inline fields for anything not yet
migrated. So a pine stand is defined ONCE and used by several kits and by the
painter's palette — the duplication that existed while both carried their own
copy is exactly how the two would drift.

**The zone tool paints `ZoneData` — theme and weather, nothing else.** The
palette is `WorldMapData.zones`, and `WorldState.Zones` is built from that same
list, so a chunk's stamped index and the runtime zone table cannot drift apart.

It briefly painted `ZoneGenData` instead, for its kits and spawn lists. Once
ground became its own layer and props their own palette, the only thing left in
that resource for a painter was one dereference to `.zone` — everything else it
bundles is either a separate painted layer now or meaningless here, because
painting IS the placement and a zone's bounds, fixtures and terrain tuning
describe how worldgen would place it. Ground for an unpainted column comes from
`WorldMapData.defaultGround`, not from the zone, so the two layers are genuinely
independent.

**A ground set may only name kits reachable from the document's `genData`
zones.** The per-voxel `TerrainId` is an index into the kit palette, and the game
rebuilds that palette from `genData` when it loads the `.hike` — so a kit with no
slot bakes as slot 0, and appending it at bake time would shift every index
instead. `SlotOf` warns by name when this happens.

**Detail sprites come from the ground too.** Every surface voxel is stamped with
its kit's `defaultDetail` and a strength ramped off `detailNoise`. They belong to
the ground layer rather than to props because they are part of what the material
looks like up close, not something standing on it — which is also why they live
on `TerrainKitData` and not in a `SpawnSetData`.

It is `WorldGen.StampDetailScatter` itself, not a painter-side copy of its math,
and like worldgen the bake runs it **LAST — after the scenes, the routes and the
scatter.** Every ground-moving pass overwrites the per-voxel channels wholesale,
so detail stamped per column during `StampColumns` was erased wherever a subscene
stamp landed: the building's footprint and the terrain it re-textured to the
local kit came out bald, which is the same failure worldgen's ordering comment
records. The pass takes two knobs so both callers can share it — `skipColumn`
(worldgen's road tread, the painter's paving, both bare by construction) and
`dominantZoneKit`, off here because a painted world assigns kits per column
deterministically and has no zone-weight kernel to take an argmax of.

**The zone under a column chooses its material.** Each solid voxel is written
with the palette slot of one of that zone's kits — submerged where water stands
over it, shore within `shoreBandVoxels` of the waterline, surface above that, and
the cave kit below the top `surfaceDepthVoxels` so a tunnel bored through a
hillside has rock walls instead of a cross-section of grass. Before this the bake
never wrote `TerrainId` at all, so every painted world came out in whichever kit
happened to occupy palette slot 0 (zone 0's surface kit) no matter what zones
were painted — the zones behaved correctly at runtime and looked wrong. The
elevation+water images REPLACE WorldGen's noise height/water; WorldGen's other
per-column logic (ramps, shore, kit blending) is out of scope — a clean focused
stamp, not a fork. Also resolves the shared view colours (`ElevationColor` from
the authored bands, plus `RegionColor` / `ZoneColor`).

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
  `WorldMapData.elevationBandHues` + `metersPerBand` (4): the band a height falls
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
`WorldMapState.ShowWater` (**W**, display-only, never saved) gates `WithWater`,
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

## Water is painted, and the map answers back

Every column the brush touches is filled from its ground up to the selected
surface level. A BRUSH, not a fill.

**There is no waterline.** The water layer alone says where water is, and the
world starts PREFILLED with it at `seaLevel` — which costs nothing, because a
blank layer is zeros and 0 already encodes `seaLevel`. Land hides the water it
stands above; carving that land away reveals water that was there the whole
time. So a sea is not a rule about low ground, it is the water nobody erased,
and three things fall out that the old `max(seaLevel, painted)` could not
express:

- **Erasing water is a real edit** (RMB), so a basin dug below sea level can be
  DRY. Under the old rule the sea went wherever the ground was low, whatever the
  author wanted.
- **Water can be painted below the sea** — a signed surface, like elevation.
- **Water survives the land above it.** Paint a lake across a hillside and
  nothing shows; carve the hillside down and the lake is already filled. This is
  the river/lake workflow: shape the water first, then cut the channel into it.

Latent water is deliberately INVISIBLE on the map — drawing it would show a
shoreline the bake does not produce — so the hover readout reports it instead
(`WATER Y=… (depth n)` where it stands, `water Y=… (buried)` where the ground
covers it, `no water` where it has been erased).

Two consequences elsewhere, both from the same removal. `CanSpawnAt` tests DRY
and nothing else, so a drained basin scatters like any other ground; and the
shore band is measured from **the water beside a column** rather than from
`seaLevel`, which is what stops a dry canyon floor coming out as beach sand —
and incidentally gives a mountain lake the shore it never had.

**The flood fill was built twice and removed.** It answered a question the author
had already answered by clicking, and it could not tell a lake from a river
without being told which it was looking at — a seed high in the mountains has no
way to know whether it should pond to its own height or run downhill. Splitting
it into two tools, then unifying them behind a floor bound, both made the model
smaller without making it right. A brush has nothing to decide: water goes where
you put it, at the level you chose, and a lake and a river become the same act
with a different shape of stroke.

What the fill was really providing was FEEDBACK — "here is what your click
implies" — and the map now provides that directly, which is why the simpler model
is not a step backwards:

- **Depth shading.** Water lerps `shallowWaterColor` → `deepWaterColor` across
  `waterDeepAtVoxels` (4), so a shoreline reads pale and a bed dark. Two authored
  stops rather than a long ramp: the shore is the edge an author aims at, and
  more stops make that edge harder to find.
- **Waterfall ink.** Any edge where the water on one side STANDS ABOVE what is
  visible on the other — bare ground below it, or a lower pool — is inked in
  `waterfallInk`,
  a bright teal at full alpha, on the POOL side of the lip (the ink goes into the
  higher cell, and for a spill the higher visible surface is always the water).
  It is checked BEFORE the height buckets, because a one-metre lip is still a
  waterfall and the minor bucket it would fall into is not drawn on the elevation
  map at all. Spill is the one thing about painted water that depth cannot show,
  and the thing most likely to be an accident.

  **The low side is its VISIBLE surface, not its ground**, and that distinction
  is the whole rule. A river dropping into a lower pool is the commonest cascade
  there is; testing "the other side is dry" instead — which this did — excluded
  every one of them, and the lip drew as an ordinary height step. The tell is a
  line DARKER than the water: `waterfallInk` is brighter than any water shade, so
  a dark line at a lip means the edge was never classified as a spill at all.
  (Two columns of one pool share a surface, so the strict comparison already
  does the work a "different bodies" test would.) Measured on the default
  document: 0 cascades under the old rule, 8 under this one.

  It is drawn at its OWN width (`waterfallEdgeWidthFraction`, 0.67 of a metre
  cell, never below 2px where the zoom allows one) rather than at
  `edgeWidthFraction`. A waterfall edge is a warning, not a height cue, and at
  the contour width it comes out as a single pixel — a bright teal that reads as
  a faint fringe on the very shoreline it is flagging.

  It shows on **every map that shows water**, not just the water tool's — a
  spill is a fact about the terrain you need while painting the things beside it,
  the same argument climbing routes are drawn everywhere. The gate is
  `IWorldMapView.DrawsWater` (and **W**), never the active tool, so the only maps
  without it are the ones where water is not on screen to be poured: zone and
  tunnel. The region map counts as showing water — it does not composite blue
  over the region wash, but it darkens every submerged column, which is why it
  declares `DrawsWater` and takes both the water-surface outlines and the teal.

**Every edge the map inks is a cascade in the baked world.** One rule
(`WorldMapState.SpillsOver`) answers both, so the map cannot promise a waterfall
the bake does not build. `BuildWaterfallSites` groups those edges into cascades —
a LIP is the dry column the water leaves over plus the direction away from the
pool feeding it, which is the contract `WaterfallMeshBuilder` sweeps its sheet
from — and hands them to **worldgen's own `WorldGen.PlaceWaterfalls`**, so the
"a surface sits one voxel above the voxel it caps" convention has one home rather
than two that drift. Lips group 8-connected AND by the level they pour from: a
five-wide sheet is one cascade wanting one effect, an outside corner turns
through a diagonal and its two perpendicular strips must reach the same entity,
and two pools at different heights spilling past each other stay two falls. The
drop itself stays AIR, exactly as in a generated world. How SMALL a drop is worth
drawing is not decided here — `SimData.waterfalls` tiers that, and a fall below
the first tier draws nothing.

The brush is **hard-edged**, ignoring the falloff, for the reason Flatten is: a
water surface is level, and easing it in by weight would tilt every stroke's rim
into a ring of half-steps. It writes every column it covers, including ones whose
ground stands above the surface — that is the latent water above. **alt+click**
samples a height to aim at, the same eyedropper the elevation tool has.

The surface is cached per column beside the weathered heights and refreshed by
the same rect rebuild — no longer global, since a painted surface has no
neighbourhood to spread through. Weathering still measures against it
(`StandHeight`) while it measures against RAW heights, which is what keeps the
two from depending on each other.

## How the map is drawn (relief + step outlines)

The painter renders the map at **`pixelsPerMeter` (3) pixels per metre** into a
managed `byte[]`, and the canvas draws that 1:1 rather than fitting it to the
window. Both halves matter: the sub-metre resolution is what lets a step outline
be a thin line on a voxel edge instead of a metre-wide block, and drawing it
unscaled is what keeps those one-pixel lines crisp (a fitted map resamples metres
to fractional pixels and smears them).

**Ctrl+wheel zooms** between `minPixelsPerMeter` and `maxPixelsPerMeter`, and
**middle-drag pans** once the map overflows the window. A zoom anchors on the
metre under the cursor — the canvas records which metre that is, lets the painter
rescale, then solves the pan that puts the same metre back under the pointer, so
zooming into a feature cannot throw it off screen. Zooming reallocates the
buffer, the image and the texture; their cost grows with the SQUARE of the scale,
so the ceiling is a memory bound rather than a taste one (at 8 px/m a 288x256 map
is already ~19MB of RGBA).

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
- **Step outlines** on voxel edges where the VISIBLE surface changes — the
  water surface where a water-drawing view has water switched on, the ground
  otherwise, so the sea is one flat sheet outlined only at its shore rather than
  a contour map of a seabed hidden under opaque water. Inked on the
  HIGHER side so a line reads as the rim of its plateau. The ink comes from
  `WorldMapData.edgeInkSub2m` / `edgeInk2m` / `edgeInkOver2m` (alpha in the
  colour), bucketed by the size of the step: **under 2m, exactly 2m, and more
  than 2m**. The minor bucket is drawn only where `IWorldMapView.ShowsAllSteps`
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

## Undo / redo (`scripts/worldmap/undo/`)

**Snapshot-on-touch, the same contract as the world editor's** (`IMapEditAspect`
mirrors `IEditorEditAspect`) — a separate hierarchy only because none of the
state overlaps: that one moves voxels and entities in a `WorldState`, this one
moves pixels in layer images. Ctrl+Z undoes, Ctrl+Shift+Z or Ctrl+Y redoes, and
a whole drag is one edit (opened on press, committed on release).

**The HOST touches, not the tool.** `WorldMapPainter` opens an edit on every
press and declares the brush rect before each `Paint` call, so a tool is
undoable the moment it is added to the list and there is nothing a tool can
forget to declare. The price is that every layer over that region is
snapshotted, since the host does not know which one the tool writes — and it is
paid back at commit, where each aspect drops the tiles that did not change, so a
stroke that moved one layer keeps one layer.

That also makes it safe to open an edit on presses that paint nothing (an
alt+click pick, a click on empty ground): an edit that captured no change is
discarded instead of costing an undo slot.

Three aspects, split by what a snapshot naturally costs:

- **`RasterTilesAspect`** — the layer images, one chunk-square tile at a time.
  Tiled because a stroke is local: a brush touching a few tiles should cost
  kilobytes, not a copy of every layer in the document.
- **`TunnelTilesAspect`** — the carve mask, as whole-height columns over a tile.
  Separate because it is the one 3D layer and the one where a whole-layer copy
  would really hurt (~6MB on an 18x16 map). Whole-height because the tunnel tool
  can move its cross-section mid-stroke, and taking the column entire costs one
  snapshot instead of per-slice bookkeeping.
- **`PlacementsAspect`** — everything in `WorldMapPlacements` (stamps, entities,
  spawn), snapshotted whole: a handful of entries, and add / delete / move /
  rotate have no useful spatial extent. It restores VALUES into the existing
  instances rather than replacing them with copies, which is what lets a tool's
  selection survive an undo of the drag that moved it.

  **Which values it captures comes from the resource, not from a list in the
  aspect.** It asks each placement for its script-declared properties
  (`ScriptVariable` usage, so engine bookkeeping like `resource_path` is left
  alone) and captures all of them, so a property added to `SubscenePlacement` is
  undoable the day it is added. The hand-written list it replaced named anchor /
  rotation / path only, which left `yOffset` — written by the scene tool's
  alt+click — outside undo entirely, and nothing failed to say so: a field
  missing from a snapshot does not error, it just stops being undoable. Values
  compare as TEXT because `Variant` does not compare by value here (measured:
  two Variants holding the same `Vector2I` are not `Equal`, which made every
  press register as a change and cost an undo slot).

R/F are bracketed like a stroke, because for the scene tool they turn the
selected stamp — document state — while for every other tool they move a tool
parameter and the edit drops itself.

## Resizing a document (`WorldMapResize`)

Two operations, and the difference matters:

- **`worldmap_resize <chunksX> <chunksZ> <res://doc.tres>`** rescales the world.
  The same coastline, bigger. Every layer is resampled.
- **`worldmap_canvas <chunksX> <chunksZ> <res://doc.tres>`** changes the extent
  and nothing else. Every painted metre stays the metre it was and the map gains
  (or loses) ground around it; nothing is resampled, so nothing can be lost
  except what falls outside a shrink. Placements do not even move — they are
  authored in world coordinates and the origin does not shift. This is the one
  to reach for when a world simply needs more room.

Chunks are the unit for both because the per-column images are always
`sizeChunks * 16`, so any chunk count is reachable and the ratio need not be
whole. **Close the painter first** — it holds the old images and its next save
would put them straight back.

**Every layer is CATEGORICAL, so nothing is ever interpolated.** An elevation 6
beside an 8 is two terraces with a wall between them, and a filter that averages
invents a 7 — a terrace nobody painted. The same filter on the ground layer
blends "forest" and "desert" into whichever index lies between. So the one rule
is that an output pixel is a verbatim COPY of some input pixel, enforced by
working on raw bytes a whole pixel at a time. That also keeps a pixel's channels
together, which the spawn layers need: R is a set index and G is that set's
density, and a per-channel filter would pair one set's index with another's
density.

Copying alone gives nearest-neighbour, which staircases every diagonal by the
scale factor — the thing the resize is trying to avoid. The fix is **EPX /
Scale2x, iterated**: double with the corner rule while the target has room, then
land on the exact size. The rule fires only where the two neighbours meeting at a
corner AGREE and the opposite two DISAGREE, which identifies a corner and never a
line, so a one-metre ridge cannot be eroded.

Measured at 4x — worth not re-deriving:

| approach | worst step | flat run | invented values | 1px ridge |
|---|---|---|---|---|
| nearest | 4 px | 4.0 px | 0 | intact |
| majority filter | — | — | 0 | eroded, or no effect at all |
| one corner pass at 4x | 4 px | ~4 px | 0 | intact |
| **Scale2x iterated** | **2 px** | **1.4 px** | **0** | **intact** |

The two rejected ones are instructive. A majority filter sized to see across a
step (radius ~ the scale factor) erodes any feature that small — and after an
upscale a one-metre ridge is exactly that size — while sized smaller it changes
nothing at all (zero pixels at 3x). A single corner pass at the full factor only
nips the corner of an N-pixel step; cutting deeper made it worse, because the
cuts either side of the boundary land a cell apart and zigzag. Iterating is what
works, because the second pass chamfers the first pass's chamfer.

Shrinking takes the **mode** of the region each destination pixel covers, ties to
the centre sample: lossy but never stepped, and picking one sample instead would
drop thin features at random.

**Tunnels are resampled in XZ like everything else** — they have to be, or a
passage would no longer meet the hillside it was bored into. Only their Y is left
alone, for the same reason heights are. They take *any* carve over the region
rather than a mode, because a passage that silently seals is worse than one that
comes out a metre wide.

Two more things do not follow the images. **Heights are not scaled** — doubling the
map's width must not double how tall its walls are, since wall height is a
gameplay quantity the terrain rules pin independently of extent. **Stamps keep
their size** and are moved to the same relative spot, since a house does not grow
with the map.

## Tools + views (the extensible part)

Each tool is an **`IWorldMapTool`** and carries its own variables (brush
`Radius`, op, active elevation, cross-section, index...) and its own
**`IWorldMapView`** (`ColorAt(ctx, px, pz)`). The active tool decides BOTH what a
stroke does AND how the 2D map is coloured — switch tool, switch view.

| Tool | Paints | Extra vars | View |
|------|--------|-----------|------|
| `ElevationTool` | elevation (raise/lower/flatten/flatten-soft/smooth/lift/smear) **and** cliff weathering (roughen) | `Op`, `VoxelsPerStroke`, `TargetVoxels`, `RoughenStopIndex`; `AdjustLevel` steps whichever number the op uses | one band per lattice step, eroded heights, water overlaid when `ShowWater` (**W**) |
| `WaterTool` | sets each painted column's water surface (RMB removes it) | `SurfaceVoxels` (R/F, signed; alt+click samples) | water shaded by depth, dry land dimmed |
| `TunnelTool` | carve at a cross-section | `CrossSectionY`, `CarveHeight` | white=land at slice, grey=2 below, blue=2 above, red=existing carve |
| `RegionTool` | per-chunk region index | `RegionIndex`, named in the option row | region colours, **50% darker under water** |
| `ZoneTool` | per-chunk zone index | `ZoneIndex`, named in the option row | zone colours, **brightness by elevation** |
| `ScatterTool` | which `SpawnSetData` covers a column + density | `SetIndex`, `Density` | ground colour + a dot per prop spawn |
| `MobTool` | the same, on the mob layer | `SetIndex`, `Density` | ground colour + a dot per mob spawn |
| `MobLevelTool` | per-column danger level | `Level` | terrain recoloured, one shade per level |
| `ClimbTool` | climbing route on a column's walls | none | the elevation map, routed edges inked magenta |
| `PaveTool` | a block on the column's top voxel | `BlockIndex` | the ground map (paving resolves inside `GroundColorAt`) |
| `SceneTool` | `.hikescene` stamps — place / select / move / rotate / delete | `SceneIndex`, `Selected` | the ground map, footprints washed, selected one stronger |
| `EntityTool` | individual entities, and the player spawn | `PaletteIndex`, `Selected` | the ground map, a mark per entity |

A spawn brush writes only its raster; `RescatterColumns` resolves it during the
bake, running **worldgen's own placement math** per column — the two-pass tree
scatter, the noise-gated grass, and `SpawnListData` entities at their authored
area rates. Every decision is a hash of the column rather than a running
`Random`, which is what lets the map preview reach the same answer without
replaying the pass.

`WorldMapBrush` (`Resource`) is the shared, layer-agnostic stamp engine
(falloff/flow/noise + `Stamp(center, radius, w, h, apply)` callback); each tool
supplies its own radius and per-texel write. Add a new tool by implementing the
two interfaces and appending it to `WorldMapPainter._tools`.

## Host (`WorldMapPainter : Node3D`)

A **pure 2D in-game program** — no live `World`, no `GameCamera`, no chunk
meshes. Launched from the main menu (`GuiMainMenu.OnStartPainter` →
`Main.StartPainter`), which just instantiates + `Init()`s the scene, so it opens
instantly. Holds the tool list + a colourised `Rgba8` display image fed to
**Prop dots draw on every ground-based view** (`ESpawnPreview` is a flag SET, not
a choice): props are what the ground is furnished with, and nothing else on those
maps answers "is this spot already taken". Mob dots stay with the layers that
paint mobs — they are about encounters, not terrain — and draw last, since two
dots cannot share a cell and what LIVES somewhere is the more urgent answer.

**Props and mobs are the same machinery twice** — one `SpawnSetData` type, two
palettes (`propSets`, `mobSets`), two rasters of identical shape, one column
routine at bake, one dot preview parameterised by `IWorldMapView.PreviewLayer`.
They need separate LAYERS rather than separate types because a raster holds one
set per column: sharing a layer would make painting wolves erase the pine stand
under them. A mob set is simply a set whose tree and foliage slots are empty and
whose `entities` list carries the mobs.

Difficulty is deliberately not on the set — it is its own scalar layer, so
"which creatures" and "how dangerous" are painted apart. A level band on the set
would need "wolves-easy" and "wolves-hard" as separate assets.

**Difficulty is its own layer and its own colouring.** `MobLevelTool` paints a
per-column level into the shared scalar image (`R` = mob level, `G` reserved for
climb), and its view recolours the whole terrain one shade per level so a glance
answers "how dangerous is it here" with nothing competing for the colour.

The field is CONTINUOUS and smoothed **where it is painted**: the brush eases
toward the level you picked so its falloff is the gradient, and the map lerps the
ramp stops linearly so a soft edge reads as a fade rather than a ring. It is
rounded to a whole level only where a mob needs one. Smoothing at paint time
rather than at bake is what keeps the map honest — a bake that re-smoothed would
mean the shades on screen were not the levels the mobs got, the same
preview-versus-bake gap the spawn dots exist to close. Worldgen lerps difficulty
across a noise field for the same reason: a raw per-column byte would step a
whole level in one metre.

**Subscene stamps are a LIST, and the tool is a pointer, not a brush.** Click
empty ground to drop the palette's scene, click a stamp to select it, drag to
slide it (grabbed where you clicked, so it does not snap its anchor to the
cursor), **R/F** to turn it 90°, RMB to delete the one under the press. The press
only ever DECIDES what the stroke is about — it cannot place, because the right
button fires it too and a right-click on bare ground would drop a building for
the erase that follows to delete again.

`IWorldMapTool.TouchRect` is the other half of `LastPaintRect`, and undo needs
it: a tool that writes outside the brush disk must say so BEFORE it writes, or
the snapshot cannot cover it. The lake tool's body erase clears seeds anywhere
the fill reached, so it touches the whole map.

Two things this needed from the host, both small and both general: `Options` is
filled by **scanning the subscene directory** rather than an authored palette (a
`.hikescene` is made in the world editor, and a registration step in a second
resource is one that gets forgotten), and `IWorldMapTool.LastPaintRect` lets a
tool report the columns it actually changed — a stamp moves its whole footprint,
which is nowhere near the cursor's disk, and the move has to repaint the ground
it LEFT as well as the ground it arrived on.

**A stamp draws its own contents**, seen from above: the topmost solid voxel of
each footprint column in its block's `minimapColor`, shaded by height within the
scene so walls read brighter than the floor they stand on. That is what makes a
stamp placeable at all — which way a house faces and where its walls are cannot
be read off a rectangle. Built once per (scene, rotation) and cached beside the
rotated state, because the map asks per texel per rebuild and scanning a
building's full height every time would show. Columns the scene leaves empty (a
courtyard, the gap around a tower) still take the wash, or a stamp's extent would
vanish wherever its scene authors nothing.

**Y is derived, with an authored nudge.** The seat is `WorldGen`'s own
`FootprintPlateauY` — the most common ground level across the footprint, ties to
the lower — refactored to take the ground lookup instead of a `HeightMap`, since
the painter has none. Averaging or taking the max would float a building over a
dip; the stamp overwrites its whole bbox, so cutting in is self-correcting and
floating is not.

`SubscenePlacement.yOffset` nudges that seat, and is deliberately a NUDGE rather
than an absolute Y: the seat is recomputed from the ground under the footprint,
so a scene follows terrain that moves under it while the offset keeps saying "and
a metre lower than that". An absolute Y would pin the building while the hill
walked out from under it. **alt+click solves the nudge from the ground under the
cursor** — point at the terrace you want the floor on — because the number that
matters is where the floor LANDS, not how far it moved. `SeatY` is one method
used by both the bake and the tool, so the height the HUD shows is the height the
bake uses.

Footprints are excluded from `CanSpawnAt`, the way worldgen reserves them with
`MarkNoSpawn`.

**`EntityTool` is that same interaction with two parts swapped**, which is what
the scene tool was shaped for: the palette is `WorldMapData.entityPalette` and
the hit test is a proximity check, because an entity is a point rather than a
footprint. Everything else — press decides what the stroke is about, drag slides
from where you grabbed, R/F turns the selection, RMB deletes what was under the
press, once — is the same code shape.

The palette is **`SpawnEntryData`, the same entries the scatter layers use**, so
one palette covers props, mobs, chests, loot and NPCs, and a hand-placed chest
spawns through exactly the `TrySpawn` path a scattered one does. A placement
references its entry DIRECTLY rather than by palette index, so reordering the
palette cannot silently turn every chest in the world into a goblin.

**The player spawn is the first palette entry, not a tool of its own.** There is
exactly one of it, so placing it MOVES it — a tool whose whole job is to move a
single point does not need a button in the toolbar, and having it here means it
is placed against the same map, with the same cursor, as everything else standing
on the ground. It cannot be deleted, only moved: a world without a spawn is a
world you cannot enter, and the bake would silently fall back to the origin (which
is still what an unplaced spawn means, for documents that predate this).

**Paving paints a BLOCK, where worldgen's roads paint an OVERLAY.** That is a
real divergence and a deliberate one. `CarveRoads` lays a `BlockSurfaceData`
tread as an additive skin (`SetOverlayIdWorld`) over whatever kit block is
already there: it blends softly into the terrain via the surface's own alpha, but
an overlay "names a LAYER, not a block", so it carries no footstep sound, no
speed multiplier and no dig yield, and it occupies the single overlay slot that
climbing routes and moss also want. A hand-painted road is a deliberate object,
so it gets to BE its material — the same call `StampDirtPatches` makes for dirt.
The cost is a hard 1 m kerb instead of a blended edge; if that matters, the
answer is an overlay-painting layer ALONGSIDE this one, not a switch on it.

Only the TOP voxel is paved, and the kit channel is left alone: the kit says what
the column is made of, which a road laid over it does not change, and the rock
under a road is still the hillside's. A paved column also stamps no detail
sprites and is excluded from `CanSpawnAt` — worldgen's road pass deletes the
scatter standing in its tread, and grass growing through paving is the tell that
a road was painted rather than built.

**Paving resolves inside `GroundColorAt`, not in the paving view**, so a road
appears on EVERY view that draws ground — you cannot lay props or mobs sensibly
along a road you cannot see. The colour is the block's own `minimapColor`, since
a block already authors what it looks like from above and a second palette would
only drift from it.

**Climbing routes are the second scalar, and they are AUTHORED, not covered.**
`ZoneGenData.climbCoverage` asks "how much of this zone's rock is climbable" and
worldgen answers it with cellular patches; the painter asks "where is the way
up", which is a route-design question with a specific answer. A coverage field
was built first and is the wrong shape for it: a fraction cannot say *this* wall,
and a patchy face is not a route. So the layer is a per-column FLAG.

Routed edges are inked on **every view, not just the climb tool's** — a route is
a fact about the terrain rather than a mode you switch into, and it has to stay
visible while you paint the things that route past it. The lookup is gated on the
step height first, which is already in hand, so the flat majority of edges never
touch the image.

The tool paints over the **elevation view, unchanged** — that is the map the
decision is read from — and a routed wall is drawn in `climbInk` (magenta)
**instead of its height ink**, so the tall edge you clicked is recoloured rather
than covered by a mark floating above it. Only columns that own a wall of at
least `climbRouteMinWallVoxels` (**4** — a 3m wall is not climbable, so a route
on one would promise a way up the player cannot take) take the flag, which is the same set of edges the
outline pass inks: the tool paints exactly what you can see, and dragging across
the flat ground between two cliffs marks neither. The brush is not eased by its
falloff either — a route is a thing or it is not, and its radius is how WIDE the
route is.

The bake runs `WorldGen.StampClimbSurfaces`, which now takes the per-column
answers it cannot look up in a painted world (a route flag instead of a zone's
coverage, the painted water layer instead of a `HeightMap`) plus its wall minimum
and whether to be patchy — worldgen patchy, the painter not. Everything else is
shared: the exposed-face walk, the run heights, the per-block growth table.
Reimplementing that painter-side is exactly how the waterfall shading became two
copies that drifted. A marked column's whole exposed face is dressed, so a route
is currently a plain vertical column of climbable surface.

Both scalars are deliberately absent from the preset brush: difficulty does not follow
biome, and neither does where the player is MEANT to be able to climb — both are
route-design decisions, so folding them in would tie together the layers that
most want to vary independently.

Mobs read it through `WorldGen.MobLevelOverride`, a seam the bake installs and
clears. `MobSpawnEntry` asks `ComputeMobLevel` for its level, and that reads zone
bands and noise a `Generate()` run leaves behind — which a painted world never
produces, so without the seam every painted mob spawned at its species base.

The **preset brush writes every per-column layer** — ground, props and mobs — so
the ordinary "this is boreal forest" stroke stays one stroke, and each layer is
still independently repaintable after. Zone stays its own tool: it is chunk
resolution, so a preset stroke narrower than a chunk would flip that chunk's
weather, and one zone covers ground of many kinds anyway. That split — per-column
layers composited by the preset, per-chunk layers painted alone — is what decides
whether a new layer belongs in it.

The HUD carries **two button groups, both built from lists rather than authored
one-per-node**, so adding a tool — or an op to a tool — cannot leave a stale
button behind. The first is one button per `IWorldMapTool`; the second is the
active tool's `Options(ctx)`, labelled with its **1-9** hotkey and rebuilt on
every tool change. `Options` takes the document because zone and region names
come out of `genData` — a region's authored `displayName`, a zone's gen-resource
file name — so those layers are painted by NAME rather than by index
(empty for tools whose primary parameter is not a small fixed set, like a region
index). Every way of changing either — button, Tab, number key, Q/E — routes
through `SelectTool` / `SelectOption`, so the bars cannot disagree with what the
map is showing.

The brush ring takes its colour from `IWorldMapTool.CursorColor`: while
flattening it is the band colour of the target height, so the cursor answers
"what am I about to paint" against the map it is hovering over. Ops that move a
column relative to where it already is (Raise, Lower, Smooth) have no single
value to show and stay white.

`WorldMapCanvas` (a dumb viewer: places the image at native scale with
middle-drag pan, draws the cursor, reports texel strokes via `OnPaint`, hover via
`OnHover` and wheel notches via `OnAdjustRadius`). Each tool view reads the layer
images directly, so nothing here needs the voxel world.

Keys: LMB paint / RMB erase · **1-9** pick the active tool's option · **Tab** or
the HUD toolbar cycle tool (+view) · **Q/E** step a parameter the option row
cannot show (carve height) · **R/F** Flatten target level /
cross-section · **W** show/hide
water · **Ctrl+Z** undo, **Ctrl+Shift+Z** / **Ctrl+Y** redo
· **alt+click** pick a height (alt+drag spreads it) · **shift+drag**
constrain to that one height · **ctrl+drag** constrain to that height and above
· **wheel** or
**`[` `]`** brush size (proportional step) ·
**ctrl+wheel** zoom (cursor-anchored) · **middle-drag** pan
· **Ctrl+S** save layers, then bake the `.hike` in the background.

## Verifying a change to the painter

**`worldmap_check` is the loop** — `--headless -- "worldmap_check
res://path/to/world_map.tres"`, ~5 s, self-quitting. It opens a document's layer
images (no world built, no `.hike` written) and reports what the bake would make
of its water and which cascades it would file. Extend it rather than reaching for
a bake: every question about a painted document that does not involve voxels can
be answered here.

Four invariants are worth re-checking after any change to placement or to how the
map is drawn. Each was a real bug, and none of them was visible by reading:

- **Preview == bake.** Count columns where `PreviewSpawnAt(px, pz) >= 0` and
  compare against the columns that actually received entities after `BuildWorld`.
  It must be **zero disagreement, not merely equal totals** — the totals matched
  while 665 columns disagreed. This is what `CanSpawnAt` being the single gate
  for both the dots and the scatter buys, so a new eligibility rule goes THERE
  and nowhere else.
- **A partial rebuild reproduces a full one.** Hash `_pixels` after a full
  `RebuildDisplay`, then again after rebuilding a small rect over unchanged data;
  they must be identical. Two intermediate states looked "stable" while still
  being wrong — self-consistent partial rebuilds that disagreed with the full
  one.
- **A drag never removes a dot.** Stamp along a line and diff the dot set at each
  step; `vanished` must be 0.
- **Placement is a pure hash, never a sequential `Random`.** The hash decides
  WHERE; a `Random` only fills in details once a spawn is committed. Break that
  and live preview becomes impossible, because the preview cannot replay the
  pass.

Two traps this file has already paid for, kept because they are easy to
reintroduce:

- **A per-column CHANCE cannot express "a tree every 64 m"** — it tops out at one
  per square metre. Everything is an inverted rate (`SquareMetersPerSpawn`) or
  worldgen's own density-times-ramp.
- **Never `string.GetHashCode()` for a seed.** .NET randomises it per process, so
  patches move between the session that painted them and the bake that reads
  them.

## Known gaps

- ~~A ground set may only name kits some `genData` zone names.~~ **Fixed.** The
  kit palette is authored (`WorldGenData.kitPalette`, a `KitPaletteData`) rather
  than derived from the zone list, so a ground set may name any kit the palette
  carries whether a zone places it or not. `swamp_highlands`, `swamp_mud` and
  `swamp_village` were APPENDED to it — appending is the one safe edit, since it
  moves no existing slot and therefore re-textures nothing already baked. A kit
  still absent from the palette bakes as slot 0 and `SlotOf` warns by name; the
  fix for that is to append it, never to add a zone for it.
- **Subscene stamps fill no marker pools.** Worldgen pulls `MarkerSimState`s out
  of a scene before stamping and then fills each `SubscenePlacement.variants`
  pool; the painter stamps them straight through, so a scene authored with a
  pool bakes with its markers still in it and nobody standing on them. Markers
  and path hints only spawn under the world editor, so they are inert in game —
  the loss is the variants, not the stray entities.
- **Path hints register no POIs in a painted world**, and there is no road pass
  for them to be endpoints of — paving is a hand-painted material here, not a
  routed, graded corridor.
- **Not yet exercised in a bake or in game:** mob sets, climbing routes, paving,
  placements, undo/redo by hand, `worldmap_resize` on a real document, composite
  `SpawnGroupData` entries and chest loot, and the water/waterfall model. The
  last full game load predates all of it.

## Not yet (future steps)

**Walls** — these ARE a raster, for tileable sets: a per-column mask plus a type
index, with the baker doing neighbour-bitmask tile selection (straight / corner /
T / end / cross). The neighbour mask *is* the continuity, so nothing needs to
store adjacency. Only a wall wanting continuous rotation would need vector data,
and this is a 1 m voxel grid.


**On-demand 3D preview** — a fly-over of the baked world, built only when the
user asks for it (and torn down on exit) rather than kept live while painting.
This was the original design; it was removed because building/maintaining the
full voxel `WorldState` made the tool slow to open and taxed every brush stroke.
When it returns it should reuse `BuildWorld` to materialize a transient world,
not re-couple the tools to a live one.

Also: point-placement of singular interactives (signposts/doors/specific
chests), in-world 3D region/zone tint overlay (a `ShaderGlobals` LUT + terrain
shader), and tiling the per-column images per chunk-footprint for streaming-scale
worlds (see `scripts/voxels/CLAUDE.md`).
