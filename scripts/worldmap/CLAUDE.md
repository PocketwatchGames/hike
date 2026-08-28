# World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The **first step in the world-authoring chain**: a broad-brush, in-game paint
program that authors a layered raster *document* and bakes it into a real
`WorldState` / `.hike`. The downstream `WorldEditor` does fine per-voxel detail;
the game loads the baked `.hike`.

## Model: document + bake (not direct-voxel paint)

The authored source of truth is **`WorldMapData`** (`scripts/data/worldmap/`,
a plain `Resource` — deliberately NOT `[Tool]`, or the editor strips every
typed reference it holds on save; see the note on the class) — bake settings
(world extent in chunks, default sea level, height scale) plus references to the
**layer files** (openable directly). It mirrors the `VoxelAtlasManifest` convention: one editor-visible
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
- **Water type** — `.png` `R8`, per column (index into `WorldMapData.waterTypes`
  + 1; 0 = "whatever the zone says", which is what every document meant before
  the layer existed). The palette holds `BlockData` references directly, which is
  safe here and not on `ZoneWaterData`: `WorldMapData` is deliberately not
  `[Tool]`, so nothing it holds is subject to the `[Tool]` closure rule.
- **Region** / **Zone** — `.png` `R8`, per **chunk** (index → `ChunkState.RegionIndex` / `ZoneIndex`).
- **Wind** — `.png` `Rgba8`, per **chunk**: R = compass angle over a full turn,
  G = strength, with **0 reserved for UNPAINTED**. Per chunk because that is the
  granularity the bake seeds `ChunkState`'s wind-velocity subgrid at.
- **Props** / **Mobs** — `.png` `Rgba8`, per column (R = set index + 1,
  G = density multiplier), indexing `propSets` / `mobSets`.
- **Ground** — `.png` `R8`, per column (ground set + 1; 0 = `defaultGround`).
- **Paving** — `.png` `Rgba8`, per column: R = paving block + 1 (0 = none),
  G/B = the world Y it is laid at + 1, low byte first, with **0 meaning "on
  whatever surface is under it"**. Two channels because a document may span more
  than 255 voxels of height. A layer written before levels existed is
  single-channel and converts with G/B zero, i.e. surface-seated, which is what
  it always meant.
- **Scalars** — `.png` `Rgba8`, per column: R = mob level, G = climb route flag,
  B = cliff roughness.
- **Placements** — `.tres` (`WorldMapPlacements`): the subscene stamps, the
  hand-placed entities, and the player spawn. Not a raster: a per-column byte
  cannot hold "this scene, facing that way", nor two of them overlapping, nor a
  footprint that moves as one thing.
- **Tunnels** — `.bin`, per-voxel EDIT mask (`byte[px,ly,pz]`: 0 untouched,
  1 carved away, 2 added), too 3D to be a useful image; the result is captured
  in the baked `.hike`.

## What a painted world needs that is not the generator's

**`WorldMapData` holds no `WorldGenData` at all.** It used to reach everything
through a `genData`, which made a document that authors no terrain depend on the
generator's authoring asset for values with nothing to do with generating:

| On `WorldMapData` | Is |
|---|---|
| `startContent` (`WorldStartData`) | quests, party, initial knowledge — what a RUN begins with |
| `finish` (`WorldFinishData`) | the moss / climb / fog tuning `WorldFinish.Finish` consumes, plus `mobLevelCap` and `maxGradeStep` |
| `kitPalette`, `simData` | already standalone resources; `genData` was only the bag holding them |
| `regions` (`RegionData[]`) | the mirror of `zones`. **Order is the wire format** — the painted region raster stores indices into it |

The generator holds its own references to the same four types. That is the point:
neither side reaches through the other. Leaving the document pointing at a
`WorldGenData` "for the rest" meant two independently-editable pointers at one
file with nothing checking they agreed — and only the kit palette had a backstop
(the `.hike` records its slots and `Main.LoadWorldFromFile` refuses a mismatch).
A divergent `simData` would have been silent.

`WorldStartData` is also what a `.hike` records. The header stores THAT
resource's path (`WorldFile` v48) rather than a `WorldGenData`'s, because
`initialKnowledge` is authored as embedded sub-resources with no path of their
own — the owner is the only addressable thing. Storing a `WorldGenData` path
meant opening any world dragged the whole generator graph (zones, terrain
approaches, spawn lists) into memory to read three fields. It is also what lets
a painted world BEGIN differently: give the document its own `WorldStartData`
and it gets its own party and quests instead of inheriting the generator's.

**`maxGradeStep` moved to `WorldFinishData` to make that true.** It lived on
`TerrainGenData` as a per-APPROACH knob, and it was the last thing the painter
borrowed from a generator — for one `int`, which is what kept a whole
`WorldGenData` reference alive. It belongs with the finish tuning on the merits
as well: `StampGradeShapes` is one of the passes `WorldFinish.Finish` runs, both
producers need the same answer, and a painted document has no approach to read it
off. `WorldGen` now reads `genData.finish.maxGradeStep` in its three sites.

## Runtime + bake (`WorldMapState`)

The mutable runtime document: owns every layer's data, the queries the
tools/views read (`TerrainHeight`, `WaterSurface`, `Underwater`, `Ocean`,
`SolidAt`, `SurfaceBelow`, `VoxelEdit`, `ColumnHeight` against the live
`SeaLevel`), and the
deterministic `BuildWorld` bake. **The painter edits only the 2D layer images —
no live voxel `World` is kept.** The `WorldState` is materialized on demand:
`BuildWorld` creates every chunk, stamps regions/zones, stamps all columns,
and scatters entities. Lighting is a later step of the bake — see below.

**Ctrl+S saves on the main thread and bakes on a background one.** `Save` writes
the layer images — fast, and all it takes not to lose your painting. `Bake` then
builds every chunk (~7M voxels stamped), floods the sun, and writes a ~57MB file
in a `Task`, with a progress panel bottom-right; painting stays live throughout.

The task bakes its **own `WorldMapState`, constructed on the main thread from the
files Save just wrote** — never the live layer images, which the brush is still
writing into. One bake at a time, and the reason is narrower than it used to be:
the kit palette is no longer process state (the bake builds its own and hands it
to the `WorldState` it creates), and `EntitySerializer`'s path tables are
`[ThreadStatic]`, so neither is a hazard. What remains is **`Blocks.Bind()`**,
which REASSIGNS the global block tables a live main thread is reading. (The
difficulty seams are no longer among the reasons: they moved off `WorldGen`
statics onto `SpawnContext`, so they reach exactly the pass that installed them
instead of switching behaviour process-wide from a background thread.)

**The bake DOES light the world, and that is why it is three steps.** Nothing
relights a `.hike` on load any more — `Main` trusts the file's sunlight (see
`LightEngine.LIGHT_VERSION`), so a bake that skipped the flood would write a world
that loads black. The flood cannot simply go inside `BuildWorld`, because it has
to see the tree canopy and `FoliageStamper` instantiates each tree scene, which
the bake thread must not do. So `Bake` is:

| Step | Thread | Is |
|---|---|---|
| `BakeBuild` | worker | the painted document to voxels — `BuildWorld` |
| `BakeStampOccluders` | **main** | rasterize canopies / roofs / entity voxels (ms) |
| `BakeRelightAndWrite` | worker | `LightEngine.Relight`, then `WorldFile.Write` |

`WorldMapPainter.SaveAndBake` drives those three and hops threads between them;
`Bake()` runs all three straight through for a caller that is already the main
thread (`WorldMapData.BakeToWorldFile`). It is the same closing move `Main` makes
after `WorldGen.Generate`, for the same reason. `LightEngine.Relight`'s optional
progress callback feeds the panel through the lighting step.

**Sky exposure is the exception, and it is not lighting.** `WorldFinish.Finish`
runs `ComputeSkyExposure` even here, because `Interiorness` floods from it and
`Interiorness` IS serialized — as are `EnvTag` and the fog it feeds. Skip it and
a painted world's buildings and tunnels come back reading `Outdoor`. It is cheap
(one column per XZ, no flood); the expensive `ComputeSunlight` is what stays
off. `StampColumns` stamps each column: carved → `Water` where it is under the
column's water surface and `Air` above it, else `Terrain` up to `TerrainHeight`
**or wherever the edit layer added a voxel**, else `Water` up to `WaterSurface`,
else `Air`. A carve removes GROUND and what stands in the space it opens is the
water layer's business, exactly as it is above the terrain — so a passage bored
below a painted surface comes out FLOODED. That is the only way to paint water in
a tunnel at all, and it is undone the way water is undone anywhere else, by
erasing the column's water. The limit is that the water layer is per COLUMN, so a
dry passage cannot run under a lake: the erase that drains the tunnel drains the
lake above it too. Added geometry takes the zone's surface kit
(its submerged one where it stands under water), since it is ground standing
above the ground.

**The bake ends on `WorldFinish.Finish`, the same list worldgen ends on.** That
one call runs every channel a finished world derives from its own voxels — the
grade shapes, the detail scatter, the roof/sky/classify air pipeline, the fog
bucket-fill, the wind seeding, the water currents and the cascades — so a
channel added there reaches a painted world the day it is added.

It used to be four hand-picked calls into `WorldGen`, and the cost was silent:
the channels the painter did not know to call — `FogDensity`, `EnvTag`,
`Interiorness`, `CurrentX/Z` — baked as ZEROS, and nothing recomputes them on
load (`Main` relights a `.hike` but does not reclassify it). A painted world had
no fog anywhere, read `Outdoor` in every stamped building and carved tunnel, and
had no water currents. Nothing errored; the bytes were simply blank.

Three things differ from a generated world, and each is a fact about a painted
one rather than a switch: there is no zone-weight kernel, so the detail scatter
takes each voxel's own kit; there is no river-flow field, so water gets the
ambient drift only; and the sunlight flood is skipped, because every consumer of
a `.hike` relights on open. Sky exposure still runs — it is not serialized, but
`Interiorness`, which is, floods from it.

**The grade pass in that list is what stops a painted slope being a staircase.**
`StampColumns`
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
actually there. It lives on `TerrainMath`, given world bounds instead of a
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

**Wind direction is PAINTED, not derived.** The tool writes a per-chunk angle
and strength, and the bake turns each chunk's pixel into the uniform velocity
`WindGen` seeds that chunk's subgrid with; an unpainted chunk falls back to
`ZoneState.WindDirection` exactly as worldgen does for every chunk it bakes.

Three consequences worth knowing:

- **Direction is a GESTURE.** `Stroke` lays the wind along the drag, `Inward` /
  `Outward` aim every texel in the stamp at the brush centre. "Everything blows
  toward the middle of the map" is therefore one `Inward` stamp at map scale,
  not an angle worked out per zone — which is what a per-zone constant could
  not express anyway: one vector for a sea that RINGS the map blows the same way
  on the west shore and the east.
- **The runtime wind follows it**, because `ZoneBlend` now takes the direction
  from each chunk in its kernel (`ChunkState.GetWindVelocity` at the centre
  cell) instead of from the zone table, weighted by the same smoothstep — so a
  convergent field reads as a smooth turn rather than a 16 m staircase, and
  everything downstream of `ws.WindDirection` (cloud scroll, rain tilt, water
  ripple drift, scent, player wind drag) follows without its own copy of the
  rule. A world baked before the layer existed has a zero subgrid, falls back to
  the zone, and behaves exactly as it did.
- **The bake seeds wind at all now.** It never called `WindGen`, so every
  painted `.hike` shipped a subgrid of signed zeros — not a wrong wind, no wind:
  grass sway, motes and mob drift had nothing to read.

Strength is normalized in the image and scaled by `WorldMapData.windPaintMaxSpeed`,
so the authored range lives in one place. It feeds the baked velocity only —
`WeatherData.windSpeed` is still what the weather simulation blends per zone, so
painting a gale changes what the grass and the motes do, not the forecast.

**A ground set may only name kits the document's `kitPalette` carries.** The
per-voxel `TerrainId` is an index into that palette — so a kit with no slot bakes
as slot 0, and appending it at bake time would shift every index instead. The fix
is to APPEND it to the `KitPaletteData`, which is the one edit that moves nothing;
`SlotOf` warns by name when this happens.

**Detail sprites come from the ground too.** Every surface voxel is stamped with
its kit's `defaultDetail` and a strength ramped off `detailNoise`. They belong to
the ground layer rather than to props because they are part of what the material
looks like up close, not something standing on it — which is also why they live
on `TerrainKitData` and not in a `SpawnSetData`.

It is `WorldFinish.StampDetailScatter` itself, not a painter-side copy of its math,
and like worldgen the bake runs it **LAST — after the scenes, the routes and the
scatter.** Every ground-moving pass overwrites the per-voxel channels wholesale,
so detail stamped per column during `StampColumns` was erased wherever a subscene
stamp landed: the building's footprint and the terrain it re-textured to the
local kit came out bald, which is the same failure worldgen's ordering comment
records. The pass takes two knobs so both callers can share it — `skipColumn`
(worldgen's road tread, the painter's paving, both bare by construction) and
`zones`, null here because a painted world assigns kits per column
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
  without it are the ones where water is not on screen to be poured: zone.
  The region map counts as showing water — it does not composite blue
  over the region wash, but it darkens every submerged column, which is why it
  declares `DrawsWater` and takes both the water-surface outlines and the teal.

**The ink is a PREVIEW, not the cascade list.** `WorldMapState.SpillsOver` is a
flat two-layer test — painted water on one side standing above the visible
surface on the other — and it is all the map needs to warn you about a lip while
you paint. What the bake actually files runs over the FINISHED voxels instead
(`WaterfallFinder`, through worldgen's own `WorldFinish.PlaceWaterfalls`), so a fall
off a stamped scene, into a carved tunnel or over a hand-edited voxel is found
here exactly as it is in a generated world, and the surface-Y convention has one
home. The painter used to carry a second site-builder off its painted layers; it
could only ever see the falls the layers themselves described. The drop stays
AIR either way. How SMALL a drop is worth drawing is not decided here —
`SimData.waterfalls` tiers that, and a fall below the first tier (1 m) draws nothing.

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

**Water TYPE is painted beside the surface, and REPLACE is why it is its own
mode.** The option row is the document's `waterTypes` palette with "Zone's" as
entry 0 — the unpainted state — so there is always a way back to it and it is a
special case nowhere. The ordinary brush writes the surface AND the type
together, which is right when you are putting water somewhere; **X** switches to
REPLACE, which only retypes columns that already hold water and never touches a
surface. Without it, recolouring a lake would mean filling it to whatever level
the HUD happened to hold, and getting that exactly right across a hand-painted
shoreline is not something anyone should have to do to change its colour.

Erasing a column's water erases its type with it: a column with no water has no
type, and one left behind would silently retype whatever gets painted there next.

**A painted type TINTS the water on the map**, on every view that draws water —
resolved inside `WorldMapState.WithWaterOver`, the shared "there is water here"
path, exactly as paving resolves inside `GroundColorAt`. You cannot paint scum
along a shoreline you cannot see, and a type invisible on the map is one you
cannot tell you have already laid down. It TINTS rather than replaces
(`WorldMapData.waterTypeTintStrength`, 0.62) so the depth shading underneath
survives: the map still has to say how deep the water is.

What the map draws IS what the bake stamps: nothing derives a water type, so
there is no second source to disagree with. (A per-zone rule briefly existed and
was removed for exactly that reason — it dressed swamp water in scum the painter
never drew.) The colour is the block's own `minimapColor`, resolved into a table
at load: this is reached for every wet texel of every rebuild, so a resource
dereference per texel would land in the map's hottest loop.

**The painted layer is the only source** — it reaches the shared pass as
`WorldFinish.Options.PaintedWaterBlockAt`, the same seam shape `MossCoverageAt`
uses, and worldgen passes null and keeps standard water. Two things the bake will
NOT stamp, both of which
`worldmap_check` reports separately because their absence is otherwise silent:
a type on **latent** water (painted, but buried under ground not yet carved — no
free surface to dress until the land goes) and a type on a **dry** column.

## Carving and building (`VoxelEditTool`)

The one layer that is not a heightfield: a per-voxel byte saying either "this
voxel the height map would have filled is gone" (`EditCarve`) or "this voxel it
would have left as air is solid" (`EditAdd`).

**It is plain block drawing, and it is TWO tools over one implementation.** The
brush is a BOX — `Radius` wide, `Height` tall (**Q/E**, 3 m by default), hung off
`PaintY` (**R/F**, or alt+click).

**The box hangs off the level in the direction the tool writes**: a carve runs UP
from it, so `PaintY` is the first metre removed; a fill runs DOWN from it, so
`PaintY` is the new surface and the thickness goes under it out of sight. Either
way `PaintY` is the voxel you are acting ON, which is what lets one eyedropper
serve both.

**alt+LMB lands `PaintY` EXACTLY on the elevation sampled** — the highest floor
under the cut. Not one above it: that was tried, so a carve would preserve the
floor it sampled rather than take it, and it made the HUD disagree with every
pick. An eyedropper whose value is not the value you pointed at is not an
eyedropper. Two things about which floor: a FLOOR, not merely the highest solid
voxel, because on rock the latter is the cut plane itself, which is not a surface
anyone pointed at (there the pick is a no-op); and under the CUT, because
sampling the column's true top hands back the hilltop over a corridor instead of
the corridor's own floor, which is the one place the eyedropper is most useful.

**The hover readout reports the same number.** It read `TerrainHeight` — the raw
height field — so inside a passage it named the hilltop overhead while the map
drew the floor beneath and the pick sampled that floor. Three answers to one
question, and the two that were wrong were the ones an author checks a pick
against. Anything the painter says about "the elevation here" now comes from
`CutawayFloor` at the active view's plane. **`Tunnel` turns the box to
air on LMB; `Block` turns it to ground.** RMB, on both, reverts the box to the
height field.

One direction per tool, because that is the painter's convention everywhere
else: **LMB does the thing and RMB undoes it** — water fills / removes, climb
marks / unmarks, paving lays / lifts. Carve-on-one-button and build-on-the-other
made RMB a second POSITIVE action, the one shape no other tool has, and it left
the tool's own name lying about which button it was for. The two differ by
`PaintsSolid` and nothing else, so `BlockTool` is a six-line subclass rather than
a second tool to keep in step.

**The layer records only a DISAGREEMENT with the height field.** Carving a voxel
that is already air writes nothing, filling one the terrain already fills writes
nothing, and RMB writes `EditNone` outright. Three things fall out, and the first
is the one that matters:

- **Erasing a tunnel restores the hillside and cannot leave blocks standing
  where the height field has none.** A box straddling the surface carves only
  the solid half, so reverting it has nothing above the terrain to put back.
- Drawing a block back into a hole you cut leaves the mask genuinely *empty*
  rather than holding a cancelling pair, so there is no separate "revert to
  natural" binding to find.
- `CanSpawnAt` stays honest, because it asks whether the top solid voxel is
  still the painted ground.

**The cutaway is INDEPENDENT of the floor being painted** — **T/G**
(`EditorClipUp` / `EditorClipDown`) or **alt+wheel**, not R/F. That is the whole reason a 2D map
can answer "how tall is this corridor": sweep the plane up through one you have
cut and the metre it stops being drawn as open is its ceiling. While the plane
was pinned to the height being written it could only ever show the one slice
being painted into, which is what made an existing tunnel's height unreadable
and a too-tall one hard to fix.

It lives on `WorldMapState` beside `ShowWater` — display-only, never saved —
rather than on any tool, so every cutting view cuts at the same plane and
switching between them keeps the slice you were reading. `IWorldMapView.CutsAway`
is the one thing a view declares; the painter resolves the clip once per rebuild
and passes `int.MaxValue` for anything drawing the world from above.

**Six views cut**: the two voxel-edit tools, the **water** tool (so a passage can
be flooded deliberately), the **climb** tool (so a route can be painted on a
passage's walls), the **entity** tool (so a chest can stand in one) and the
**paving** tool (so a road can run through one, or under an arch). They share two
views, not six: the voxel-edit and climb tools take `CutawayElevationView`, and
the two that place something ON a floor — paving and entities — take
`CutawayGroundView`, which is the plain ground map above the plane and the
cutaway below it. Tools that differ in what they WRITE and in the ink the outline
pass lays over them, not in how the terrain is drawn, share the view; copies
would only drift.

Both remaining per-column layers are worth knowing about: a climb route and a
water surface are both per COLUMN, so marking one inside a passage marks the
whole column, exactly as draining a passage drains the lake above it. **Paving is
per column too**, and it is the same limit: a column carries ONE road, so a
passage cannot be paved under a paved hillside.

**An entity remembers the floor it was placed on** (`EntityPlacement.floorY`),
because nothing about the column describes where a passage's floor is. Two cases,
and the split is whether the floor is the TOP of the column:

- **On top** — the surface, or a deck you built. It records `OnTheGround` and is
  re-seated from the column at every bake, so it follows: raise the hill under it
  and it rises, dig a pit and it drops in, delete the deck and it lands on the
  ground. Seated from the top SOLID VOXEL, not from `TerrainHeight` — the two
  differ wherever something was carved or built, and an entity that says it
  stands on the ground should stand on the ground that is actually there rather
  than hang at the height the height field still claims.
- **Under something** — a passage, the underside of a deck. It records the
  ABSOLUTE Y, because re-seating would put it on the roof, and an offset from the
  ground would drag it around when the hill above is repainted. A passage is
  carved at a fixed Y and stays there.

`OnTheGround` is also what a document written before the field existed loads
with, since a field absent from a `.tres` keeps its C# initializer. The seat is
re-resolved as an entity is dragged, so sliding one along a passage keeps it on
that floor and sliding it out of the mouth puts it back on the ground.

The entity map itself switches: **lowering the plane turns it from the ground
layer into the cutaway**, because underground there is no ground TYPE to show —
the question becomes which floor is down there to stand something on. Entity
MARKERS stay visible whatever the plane is doing — and on every other map that
shows props, since the painter composites them. They are single texels and the
thing you need most from them is where they are; hiding them the way stamps hide
would make an entity you are looking for impossible to find.

**It starts parked at the top of the world, i.e. NOT CUT**, and `IsCutAway` says
so. A cutting view is then EXACTLY an uncut one until the plane is lowered, which
is what lets a tool whose ordinary job is the surface — the water tool — share
the mechanism without opening full of rock.

**The plane spans the world's whole height, so REACHING it is the problem.** On
the default document it starts at Y=79 and T/G walks it a metre a press, which
put most of a hundred presses between picking up a tool and the plane touching
any ground — and the painter said nothing about where the plane was, so the keys
read as doing nothing at all. Three things answer that, and none of them is a
bigger stride:

- **alt+wheel** is T/G under the hand already on the mouse, and scrubbing is how
  the plane is actually moved. Wheel DOWN lowers it — the same inversion the
  brush notch takes, and the same sense as scrolling down a page.
- **alt+RMB** aims it at the floor under the cursor plus `cutawayHeadroom`.
- **The HUD reports it on EVERY tool**, including the ten whose view does not
  cut — the plane is shared state and T/G moves it whatever is active, so a tool
  that said nothing about it made the control look broken. `CutawayText`
  distinguishes the three states that matter: a Y, "off (above the world)", and
  "this tool does not cut". The two tools that used to print the Y themselves
  (voxel-edit, paving) no longer do, so it is stated once.

**Two gestures bring it down to where you are working**, because a plane parked in
the sky is useless and hunting for it with T/G is worse. `IWorldMapTool.CutawayFor`
lets a tool ask for a plane when it is picked up — the voxel-edit tools want
`PaintY + cutawayHeadroom` (3), just over the level they paint at — and **alt+RMB**
aims it at the floor under the cursor plus the same headroom. The alt+RMB gesture
is live only where a cutaway is actually on screen; elsewhere it would swallow a
press whose effect nothing can show, so there it keeps its ordinary tool-pick
meaning. It reaches `BeginStroke` through `EStrokeMods.Secondary`, which is not a
modifier key but arrives the same way and is the only thing that distinguishes
alt+LMB (aim the brush) from alt+RMB (aim the plane).

**The view draws the highest FLOOR under the cut** — `WorldMapState.CutawayFloor`,
the highest solid voxel with air above it at or below the plane, in its own
elevation band. Where the cut is open that is simply the ground; where the cut
passes through rock it is the floor of the highest hollow beneath, so **the map
sees THROUGH the mountain to the passage under it** instead of stopping at the
rock. Only a column solid the whole way down with nothing hollow anywhere beneath
draws `cutawayRockColor` — the one case with no floor to draw at all.

**A floor found through rock keeps its EXACT band and is DITHERED** against
`cutawayRockColor`, checkerboarded on absolute display pixels (so the pattern
runs continuously across a whole buried passage instead of restarting every
metre). Not tinted: a tint moves the band into a shade some other height already
owns, which is the one thing an exactly-banded palette exists to prevent, so the
buried passage would start lying about its depth. The texture says "you are
looking at this through something" without touching the colour. It is the same
distinction the erase refuses to act on.

Step outlines follow the same floor, or they would contour one surface while the
colours show another; rock with no floor reads as a single flat level so that
only its edge inks and never a contour inside it. Water is composited only where
the cut is open to it, since a floor seen through rock is not under the pool
standing on top of that rock.

**RMB removes the WHOLE thing you made at a column** — the contiguous run of that
tool's own edit touching the exposed floor, however far it reaches ABOVE the cut
(a carve stands above the floor it left; an added slab IS the floor and stacks
below it). A box-shaped bite out of a passage leaves a metre of it behind and
needs the brush aimed at a height you may not know; "undo what is here" needs
neither. Undo covers it for free, because `TunnelTilesAspect` snapshots
whole-height columns.

**It refuses to erase what the cut is not open to** — a dimmed, roofed passage is
left alone. You are seeing it *through* something, and erasing what you cannot
see the top of is how a network loses a corridor silently. Lower the cutaway into
it and it erases like anything else.

**Stamps are SLICED at the plane, exactly as the terrain around them is.** The
plan draws the topmost solid voxel of the scene *at or below* the cut, so
lowering the plane walks down through a building's storeys — walls at that level
as content, the floor beneath them wherever the room is open. Only once the plane
drops BELOW a stamp's base (`StampBaseY` against `ClipY`) is it hidden outright,
which is when the cut has genuinely taken the building away and its plan would
otherwise paint over the passage you are boring under it. At or above its base it
draws, footprint wash included, so a plane parked over everything renders exactly
what no plane at all would.

The plan is therefore cached per **(scene, rotation, slice)**, and every plane at
or above a scene's own roof collapses onto ONE entry with the unclipped plan —
without that clamp a plane parked over the world would mint an entry per stamp
seat. Scrubbing the cutaway through a building costs one entry per metre of that
building, which is what bounds the cache.

It drew a fixed top-down ROOF plan before, cached per (scene, rotation) with no
notion of the plane at all, so the only thing a cutaway could do to a stamp was
make it vanish: a house was a roof or it was nothing, and its ground floor was
unreachable from the map. `worldmap_check` reports the slice as at-plane against
below-plane columns per level of the tallest stamp — a solid slab answers "all
at" every level, a building answers "walls at, floor below", and a sequence that
does not move as the level descends means the roof plan is back.

It was briefly hidden from BOTH sides, on the theory that a stamp entirely under
the cut is not what you are looking at either. That is wrong twice: a cutaway
shows you everything at or below the plane, so a building under it is precisely
what you SHOULD see; and it made every stamp vanish the moment the plane moved
at all, since the plane starts above the world and almost nothing reaches it.

The seats come from the per-rebuild `StampPlan` — `SeatY` walks the whole
footprint, so asking per texel would put that scan in the map's hottest loop.
`worldmap_check` runs its partial-vs-full comparison a second time WITH a clip,
because pairing a candidate list with a parallel seat array is exactly the shape
the prefilter could break.

**Edits are visible on EVERY view, not only this one** — a bridge deck standing
above the height map is a fact about the ground you need while painting the
things beside it, the same argument stamps and climbing routes are drawn
everywhere. The seam is `WorldMapState.SurfaceBelow` (view colours) and
`StandSurface` (step outlines), read instead of `TerrainHeight`;
`IWorldMapView.ClipY` is `int.MaxValue` on all of them but the tunnel one, so
only that view cuts anything away.

**`TerrainHeight` is deliberately NOT moved by the edit layer.** It is the
painted heightfield — what erosion is measured against, what the bake stamps
terrain up to, what a stamp seats on. A carve is a hole in that surface, not a
lower surface, and folding the two together would make weathering and grading
read a hillside that is not there. The one place the two meet is `CanSpawnAt`,
which asks that the top solid voxel still BE the painted ground: it refuses a
column whose top was carved away (the scatter would hang over a hole) and one
built over (the scatter would grow under a deck).

The per-column summary of how high the edits reach (`_topEdit`) is what keeps
this cheap: an unedited column answers every surface query with one array read,
and only edited ones walk their voxels. Anything rewriting the mask wholesale —
an undo restore, a resize — calls `InvalidateVoxelEdits` rather than maintaining
it.

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
  `WorldMapData.edgeInkSub2m` / `edgeInk2m` / `edgeInkOver2m` (alpha in the
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
  can move its level mid-stroke, and taking the column entire costs one
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

  The same capture reaches one level DEEPER for entities, into a
  placement-OWNED entry — otherwise editing a signpost's text would be outside
  undo, since `entry` is captured as a reference and the reference does not move
  when a field inside it does. An entry still pointing at its palette file is
  skipped: it is shared with every other placement using it and is not ours to
  restore, and there the reference IS the change (the fork replaces it).

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

**Voxel edits are resampled in XZ like everything else** — they have to be, or a
passage would no longer meet the hillside it was bored into. Only their Y is left
alone, for the same reason heights are. They take *any* edit over the region
rather than a mode, and a carve beats an add, because a passage that silently
seals is worse than one that comes out a metre wide.

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
| `WaterTool` | each painted column's water surface AND its water type (RMB removes) | `SurfaceVoxels` (R/F, signed; alt+click samples), type (1-9 / Q/E), `ReplaceOnly` (**X**) | water shaded by depth, dry land dimmed — **cuts away** (T/G), so water can be painted inside a passage |
| `TunnelTool` | LMB carves the box UP from `PaintY`; RMB erases the whole exposed passage | `PaintY` (R/F), `Height` (Q/E) | `CutawayElevationView` — the elevation map cut at `ctx.CutawayY` (T/G): the highest floor under the cut in its own band, dithered where seen through rock |
| `BlockTool` | the same box, LMB filling it DOWN from `PaintY` | the same | the same view |
| `RegionTool` | per-chunk region index | `RegionIndex`, named in the option row | region colours, **50% darker under water** |
| `ZoneTool` | per-chunk zone index | `ZoneIndex`, named in the option row | zone colours, **brightness by elevation** |
| `WindTool` | per-chunk wind direction + strength (RMB clears back to the zone's) | `Mode` (Stroke / Inward / Outward), `AdjustLevel` = strength in m/s; alt+click samples | hue = compass angle, a sawtooth ramp ALONG the flow, unpainted chunks flat grey |
| `ScatterTool` | which `SpawnSetData` covers a column + density | `SetIndex`, `Density` | ground colour + a dot per prop spawn |
| `MobTool` | the same, on the mob layer | `SetIndex`, `Density` | ground colour + a dot per mob spawn |
| `MobLevelTool` | per-column danger level | `Level` | terrain recoloured, one shade per level |
| `ClimbTool` | climbing route on a column's walls | none | `CutawayElevationView`, routed edges inked magenta — **cuts away** (T/G), so a route can be painted on a passage's walls |
| `PaveTool` | a block on the floor the map is SHOWING — the surface, or a passage's floor under the cut | `BlockIndex` | `CutawayGroundView` — the ground map, **cutting away** (T/G) once the plane comes down |
| `SceneTool` | `.hikescene` stamps — place / select / move / rotate / delete | `SceneIndex`, `Selected` | the ground map (the stamps themselves draw on EVERY view) |
| `EntityTool` | individual entities, their per-placement properties, and the player spawn | `PaletteIndex`, `Selected` | the ground map (the marks themselves draw on EVERY view that shows props) |

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

**WHICH document opens is picked in the menu.** The World Map Painter button
shows the same file selector New Game and the World Editor use, listing every
`WorldMapData` under `GuiMainMenu.worldMapSearchDirs`; `Main.StartPainter` loads
the pick and assigns `painter.data` before `Init`, and an empty pick keeps the
document the scene authors. The `.tres` files are filtered by the class named in
their HEADER LINE rather than by loading them — the layer images, the brush and
the placements list share that directory and that extension, and loading a
document to find out what it is pulls in its whole `WorldGenData` graph. There is
no "new document" row: a document is a `.tres` plus the layer files it names, so
making one is an authoring step, not something a picker can mint.

**Escape opens a menu rather than leaving.** `WorldMapPauseMenu` is a full-rect
Control in the HUD layer, so an open menu also stops the canvas painting under
it, with Save & Bake / Resume / Quit to Menu wired to the Actions the painter
assigns — the same three paths Ctrl+S, Resume and the quit callback already
take, not a second implementation. Every other binding is refused while it is
open; **Ctrl+S is the exception**, since it means the same thing whatever is on
screen. With no menu wired Escape keeps its old meaning and quits, so the
painter is never a screen you cannot leave.
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

**A mob set's `entities` is a PAINTER-OWNED list, forked from worldgen's.**
`mob_sets/*.tres` point at `resources/data/worldmap/spawn_lists/ambient_*.tres`,
not at the `surface_entities_*.tres` that `zone_gen/*.tres` uses, even though the
ambient lists were filtered out of exactly those files. The split is by how a
thing wants to be placed: a brush places by AREA, which suits what you want many
of and do not care about the exact spot of — mobs, forage, traps, berry trees,
cacti. A well, a climbable tree, a chest or a goblin camp is a landmark, and the
map is the place to AIM one, so those live in `entityPalette` and go down one at
a time. Painting and hand-placing are not two qualities of content, they are two
questions about where it goes.

It is a fork rather than shared entries, and the reason is that worldgen is
frozen rather than co-maintained. Sharing would mean promoting the embedded
`[sub_resource]` entries to standalone files and rewriting worldgen's lists to
reference them — and there is no `spawn_check` to prove that rewrite preserved
what those lists resolve to, only reading the diff. Copies are the honest
encoding now that the two lists genuinely mean different things: worldgen's is
"everything a generated forest contains", the painter's is "the ambient stuff a
brush may produce". Rebalancing one SHOULD NOT move the other.

**Hiding a list from the painter needs no mechanism** — `propSets`, `mobSets` and
`entityPalette` are explicit authored arrays and are the only doors in. There is
no discovery and no scan (`SceneTool` globbing `.hikescene` is the one directory
scan in the painter), so a worldgen list is invisible the moment nothing names
it, and moving files between directories filters nothing.

**A palette entry is a FAMILY, and the member is picked per placement.** One
`goblin` row covering all 13 goblin descriptors, one `npc` row covering every
villager rig and outfit — because the question the palette answers is "what am I
placing", and "which biome's goblin" is a property of the one you placed. The
list is 26 entries where it was 54.

That is also what makes the map's highlight useful: selecting `npc` lights up
**every** NPC in the world, not one villager type. It needed no separate
mechanism, because `EntityPlacement.IsFrom` already matched on which palette file
a placement (or its fork) came from — collapsing the palette is the whole change.

**Family is `SpawnEntryData.FamilyName`, and it is the ONE answer.** The file's
basename, or the explicit `family` export where that is not enough. Everything
that groups placements reads it — the highlight, the hover, `worldmap_check`'s
by-entry listing — and a local reimplementation is how six migrated NPCs came
back reported as `NpcSpawnEntry`. It is deliberately NOT `DisplayName`, which now
decorates a family with the variant (`npc: villager_elder_m *`): a match must not
depend on a decoration, or an NPC would stop matching the entry it came from the
moment its appearance was picked. The tool's option row shows the family; the
hover readout and the panel title show family + variant, since there the
individual IS the answer being asked for.

**`family` is the explicit form, for an entry whose family is not its file.**
Nothing in the palette needs it today — it is what the seven migrated NPC forks
in `placements.tres` declare, since a hand-written fork has no `resource_name`
for `FamilyName` to fall back on. It is also the mechanism if a second authored
row of one family ever returns (see the merchant, below). A runtime fork needs
neither: `Duplicate` carries the export and `EditableEntry` sets the name.

**Identity is now WHICH FAMILY, not which member** (`IsIdentityProperty`:
`family`, `variants`, `appearances`, `scene`, `altScene`, `outfit`, `palette`). A
fork keeps its palette file as its NAME, so what must stay un-editable is
anything that can move an entry OUT of its family — otherwise a placement that IS
a drake is still called `npc_hermit` by the panel title, the hover readout,
`worldmap_check` and the highlight. `variants` / `appearances` are hidden for a
sharper reason: they DEFINE the family, so showing them is the one edit that
could widen it from the inside.

**Which member is safe to edit precisely because the candidates are
constrained.** `SpawnEntryData.ResourceCandidates` is the resource analogue of
`NameCandidates` — the entry answers what its own row may hold, and the panel
uses that verbatim instead of the project-wide scan. The `goblin` entry offers 13
goblins and cannot reach a spider; `npc` offers its 8 appearances. Every mob
family constrains its row, including the single-member ones, or a dog could be
turned into a drake and still be called a dog.

**A row an entry type cannot use is hidden by that type** —
`SpawnEntryData.ShowsProperty`, the instance form of the static rule, which is
what the panel and `worldmap_check` both call. An NPC hides three:

| Hidden on an NPC | Because |
|---|---|
| `descriptor` | the two humanoid descriptors resolve to the SAME `MobData` and differ only in a bestiary `displayName`, so the row cannot change anything an author can see. Who this individual is was already settled by the appearance and the conversation. |
| `levelOverride` | a difficulty tier for a villager in a doorway is meaningless; the field belongs to the mobs it was added for. |
| `initialBehavior` | an NPC runs its conversation and its idle pose, not a combat brain's entry state. |

**Row order is the entry type's statement** (`PropertyOrder`), because
declaration order is the C# field order across a hierarchy — which floats the
base class's bookkeeping above the fields an author came to set. An NPC reads
appearance → idle animation → conversation → language → recruit template, then
everything unnamed in declaration order. The panel and the check share ONE
enumeration (`WorldMapEntityInspector.OrderedProperties`), so the report is of
the panel that will actually be built.

The lists are AUTHORED (`MobSpawnEntry.variants`, `NpcSpawnEntry.appearances`),
not derived, because neither derivable answer is right: grouping by `SpeciesData`
is per-BIOME (one swamp goblin's plain/elite/torchbearer share a species, the
forest goblin does not), and a filename prefix makes a naming rule load-bearing
with nothing enforcing it — the same reasoning that keeps the animation-clip list
un-filtered. Authoring it also lets the author say where a family's edges are,
e.g. whether a cube and a sphere slime are one creature.

**An NPC's look is ONE pick, not three.** `NpcAppearanceData` bundles
`scene` + `outfit` + `palette`, because those three are not independent: an
outfit names meshes that exist only in a particular rig, and the recolor names
them again. As three rows the only thing preventing a male rig in a female outfit
is the author remembering, and the failure is SILENT — the meshes do not resolve
and the NPC spawns in its rig's default clothes. It also sidesteps both of the
panel's standing read-only cases at once (`PackedScene` is excluded as a rig
choice, and `outfit` is an array). `NpcSpawnEntry` keeps the raw trio for
worldgen's house lists, which author it inline and always have; the bundle wins
where set, resolved once through `Rig` / `Outfit` / `Recolor` so the spawn path
and the idle-animation picker cannot disagree about which rig is in play.

**`idleAnimation` stays its own row** rather than joining the bundle: it is
genuinely per-individual (villagers built from one `MobData` each rest
differently) and its picker is driven by the chosen appearance's rig.

**Level is a field on the ENTRY, never the descriptor.**
`MobSpawnEntry.levelOverride` (negative = the descriptor's own). A descriptor is
shared by every placement of that variant AND by worldgen's spawns, and
`EntityPlacement`'s fork is shallow, so editing `descriptor.level` through the
panel would retune all of them at once. It keeps the field's semantics — a FLOOR,
with the painted difficulty layer still adding on top through
`SpawnContext.MobLevel`.

**A row is shown only if it can change something**, which is
`ShowsInPlacementEditor` — two independent reasons not to, kept as separate
questions because they mean different things: the value cannot reach a hand
placement (`IsHandPlacedProperty`), or it is implicit in the entry that was
chosen (`IsIdentityProperty`). What is deliberately still shown is the third
case: a property that WOULD vary per placement and simply has no editor yet — a
chest's `lootItems`, an NPC's `inventory` / `loyaltyGifts` / `itemPreferences`,
all of which want list editing. Those are marked `no editor yet` rather than
hidden, because a dimmed row otherwise reads as "this cannot change" when the
truth is "not here, not yet".

`spawn_entries/mobs/` is 11 family entries covering all 33 `MobDescriptor`s
(biome variants, elites and torchbearers included) — it was 33 one-field
wrappers, each holding nothing but a `descriptor`. `elites/elite_*.tres` are
excluded: they are `EliteMobDescriptor`, which decorates a descriptor rather than
being one.

**NPCs are palette entries like anything else** — `NpcSpawnEntry` is a
`SpawnEntryData`, so the entity tool places one, the property panel reflects its
fields (language, conversation, appearance, idle pose, recruit template), and the
copy-on-write fork makes each placement its own individual. There is ONE row —
`npc` — and its eight looks live in
`resources/data/characters/npcs/appearances/`, extracted from the per-NPC entries
they replace; the conversation, language, idle pose and recruit template each one
used are picked per placement, which is what let eight files become one.

**The leather merchant is a PLACEMENT, not a palette row.** Its `inventory` and
`loyaltyGifts` are arrays that no placement editor can author yet, which is an
argument for keeping the merchant that exists — it lives in `placements.tres` as
a fork carrying its own stock — and not an argument for a second palette entry,
which would have been a workaround for the missing list editor sitting
permanently in the list. The cost is real and worth knowing: **a NEW merchant
cannot be given stock from the painter.** Place an `npc` and author its
`inventory` in the resource, or add the list editor.

They remain **copies** of the NPCs embedded in worldgen's house spawn lists
(`world_gen/spawn_lists/hub_house01`, `house_hermit`, `hub_house02`,
`village_house01`-`04`), not references to them — the same fork convention the
mob sets follow, so retuning a village cannot silently move what the map paints
or the reverse. The hermit and Talia carry a `recruitTemplate` and are
recruitable where they are placed.

**A spawn entry carries only what a hand placement can change.** Four fields
came off the panel and two off `NpcSpawnEntry` outright, on one rule: a control
that cannot change the result invites tuning that does nothing.

| Removed / hidden | Because |
|---|---|
| `squareMetersPerSpawn`, `placeAtAnchor`, `clusterCountMin/Max` | container-edge rules — the area roll and `SpawnGroupData`'s scatter. A hand-placed entity is one entity at one spot by construction. |
| `minSpacing` | a rejection radius is how densely a PASS may sprinkle something. Authored in 4 files project-wide, all scatter lists or worldgen fixtures, never a palette entry. Now skipped for an authored position. |
| `initialBehaviorChance` | a POPULATION fraction ("a quarter of spawned goblins start in Wander"), authored in 50+ scatter entries and no palette one. It has nothing to be a fraction of for one placement, so an authored position always takes the behaviour it names. |
| `tamed`, `persistent` (deleted) | the starter-companion pair. Becoming a companion is a RUNTIME transition owning both halves — `Mob.Tame` flips `MobSimState.Tamed` at `MobData.tameLoyalty` and `Sim.PromoteCompanionToPersistent` moves the mob into the persistent store at that same moment. Nothing authored either flag. |

The last row is the one worth not undoing: a spawn-time shortcut is a second way
into a two-part transition, which is how the two parts come apart.

**A placement's R/F turn reaches the bake.** `StampEntities` sets
`SpawnContext.FacingY` from `placement.rotation` and clears it again — the shared
bake context is the one caller with a facing, and everything else it answers must
keep giving a scattered entity its random yaw. Without it the tool's rotation
readout was decorative and every hand-placed NPC faced a direction the hash
picked, which for a villager standing in a doorway is the whole point of aiming
one.

A palette entry deliberately leaves `squareMetersPerSpawn` at its 0 default:
`TrySpawn` — the path `EntityPlacement` takes — never consults it, while
`RollAreaChance` returns false at 0, so an entry meant for hand placement is
inert if it is ever dropped into a spawn list by mistake.
`SpawnEntryData.IsHandPlacedProperty` is the other side of that: the property
panel hides `squareMetersPerSpawn`, `placeAtAnchor`, `minSpacing` and
`clusterCountMin`/`clusterCountMax`, none of which changes what a hand placement
produces — the cluster count reaches nothing but `RollCount`, whose one caller is
`SpawnGroupData`'s scatter, and a hand-placed entity is one entity at one spot by
construction. A control that cannot change the result is worse than a missing
one, because it invites tuning that does nothing. Which fields the path reads is
the entry class's business, so the answer lives there rather than in the UI.

**`minSpacing` is skipped for an authored position, which is why it can be
hidden.** It is a rejection radius — a statement about how densely a PASS may
sprinkle something, not about a spot someone chose — and the whole project
authors it away from its 0.5 m default in exactly four files: two scatter lists
and two worldgen fixtures. Not one palette entry. It stays exported for that
path and no longer runs for a placement, on the same argument as the lateral
clearance beside it: the author put the mark exactly there and every other mark
is drawn on the same map. The cost is that a hand-placed entity may now land on
a SCATTERED prop, which the map only shows as per-column dots — thin at 0.5 m,
but not nothing.

**Hand placing an entity does NOT guarantee it spawns, and every rejection is
SILENT.** `TrySpawn` still runs its placement gates, and a rejected entity is
simply absent from the baked world with nothing said — the map cannot show it,
so short of going to stand where you put it there is no way to find out. Two
consequences:

- **`AuthoredPosition` is set for these, and only for these.** It skips the
  lateral-clearance gate, which wants 4-connected air around the anchor — which
  is exactly what a wall is not, so a villager placed in the doorway you aimed
  at would silently never spawn — and the `minSpacing` overlap gate with it. It is the same claim `WorldGen` makes for an
  entry it drops on an authored subscene marker. It **cannot** go on the shared
  `SpawnContextForBake`, because `RescatterColumns` uses that context too and a
  SCATTERED mob must keep the gate: rejecting a 1-voxel tunnel is what it is
  for. So `StampEntities` sets and clears it per entity, the same way it does
  the facing.
- **`worldmap_check` reports the flat-terrain gate**, which is the one
  answerable without a built world and the one most likely to bite: a mob,
  forge, fountain, campfire, signpost, knowledge stone or trap needs its column
  and all eight neighbours at one height, so anything placed on a slope or on
  the lip of a step is dropped. It found five in the default document the day it
  was added. The remaining gates (`minSpacing` against a neighbour, the hazard
  keep-out, the navigation-walkability probe) need voxels and are not checked.

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

**Stamps draw on EVERY view, not only the scene tool's.** A building is a fact
about the ground you need while painting the things that sit beside it — the
same argument climbing routes and spill edges are inked everywhere — and a
footprint you cannot see is one you scatter props into. The one view that
holds anything back is the tunnel cutaway, which shows a stamp only where it
reaches the cut plane — see Carving and building. The composite lives in
the painter's fill pass (`WorldMapState.StampColorAt`) rather than in `SceneView`,
which is now just the plain ground map; the selection highlight comes from
`IWorldMapTool.SelectedPlacement`, which only the scene tool answers, so the plan
stays plain while another tool is active.

The candidate stamps are resolved ONCE per rebuild (`StampsIn`) instead of per
texel. The hit test walks the placement list, and a full rebuild is ~295k texels,
so asking per texel would make drawing the map cost more the more buildings the
document holds. That prefilter is also the thing most able to break the
partial-rebuild invariant, so `worldmap_check` reports
`partial-vs-full disagreements` over chunk-sized rects and it must be 0.

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

**Entity marks draw on EVERY view that shows props**, composited by the painter
(`WorldMapPainter.DrawEntityMarks`) rather than returned by `EntityView`, exactly
as stamps are — so `EntityView` is now just the ground map. A chest or a well is
a fact about the ground you need while placing the things that stand beside it,
and scene placement is the case that makes it urgent: a house dropped on top of
one is the mistake this prevents. The gate is `ESpawnPreview.Props`, the same
flag the scatter dots use, so "wherever props are visible" is one answer rather
than a second list to keep in step.

Two differences from the scatter dots underneath them. They are drawn LAST, over
the step outlines and the dots — a mark you placed outranks a contour line and a
previewed roll — and they are NOT gated on zoom, because a dot is an impression
of a random roll while an entity is one thing you put somewhere and finding it is
the reason you are looking. The pass walks the placement LIST, not the texels:
marks are sparse and one metre each, so it costs the number of entities, where
asking `EntityAt` per texel would walk the whole list ~295k times a rebuild —
the shape that made stamps the slowest thing on the map.

**`EntityTool` is that same interaction with two parts swapped**, which is what
the scene tool was shaped for: the palette is `WorldMapData.entityPalette` and
the hit test is a proximity check, because an entity is a point rather than a
footprint. Everything else — press decides what the stroke is about, drag slides
from where you grabbed, R/F turns the selection, RMB deletes what was under the
press, once — is the same code shape.

**Which mark is which is answered by the cursor and by the palette**, because
every entity draws the same one-metre dot and the map cannot say what one is.
The mark under the cursor GROWS (`entityMarkHighlightRadius`) and the HUD names
its entry, so a grab is aimed rather than guessed at — the hover asks the tool
(`EntityUnder`), which runs the same proximity test the press grabs with, so what
lights up is exactly what a click would pick up. Colour answers the other
question: every placement of the entry the palette has SELECTED is inked as a
match (`entityMatchInk`), so "where are the chests" is answered by choosing the
chest rather than by clicking every dot, and the one placement being edited is
inked over that (`entitySelectedInk`). Matches grow to the same size the hover
and the selection do: at a zoom where the whole world fits on screen a mark is a
few pixels, and a colour difference that small is not an answer. A placement's own FORK still counts as a
match (`EntityPlacement.IsFrom`, off the palette name the fork keeps): a chest
whose text has been edited is still a chest, and it is the one most worth
finding.

None of those three is spatial, which is what the repaint has to respect: a hover
or a selection change repaints the two marks involved and nothing else (this runs
on mouse motion), while changing the palette entry is a whole-map answer and goes
through the deferred `RebuildFull`. Selecting an entity has to repaint the one
that was selected before — it can be anywhere on the map, and the rect under the
cursor says nothing about where. A grown mark also reaches OUTSIDE its own cell,
so `DrawEntityMarks` allows for the growth when rejecting placements against the
rebuild rect, or a highlight is clipped off at a partial rebuild's edge.

The palette is **`SpawnEntryData`, the same entries the scatter layers use**, so
one palette covers props, mobs, chests, loot and NPCs, and a hand-placed chest
spawns through exactly the `TrySpawn` path a scattered one does. A placement
references its entry DIRECTLY rather than by palette index, so reordering the
palette cannot silently turn every chest in the world into a goblin.

**A placed entity's properties ARE its entry's**, edited in the panel top-right
(`WorldMapEntityInspector`) — the text on a signpost, the conditions on a chest,
the descriptor on a mob. There is no parallel set of per-placement overrides,
because a `SpawnEntryData` subclass already exports exactly the fields its entity
type needs; the panel REFLECTS them, so an entry type written tomorrow is
editable the day it is written.

**A flags property is a compact DROPDOWN**, not a row of checkboxes —
`MenuButton` + a checkable `PopupMenu`, mirroring the Godot-side
`addons/data_ed/FlagsPropertyEditor` that `[CompactFlags]` opts into. That one is
an `EditorProperty` behind `#if TOOLS` and cannot be instantiated in the running
game, so the behaviour is mirrored rather than shared, and the rules are ITS
rules: the menu stays open across toggles (the value is a SET), the item id IS
the bit so nothing depends on menu order, and both a zero member (`None`) and any
MULTI-BIT alias (`All`) are skipped — neither is independently togglable, and an
alias item toggles several primaries at once with an ambiguous checked state of
its own. That last rule is what the checkbox version was missing: the knowledge
stone's `ELanguageComponents` has `All = Grammar | Numbers | Vocabulary1 |
Vocabulary2`, and it drew as a checkbox that flipped four bits.

**The panel is pushed on selection CHANGE, not per frame.** It rides `UpdateHud`,
and a click on the map reaches neither on its own — so a selection made by
clicking left the panel showing the entry it was last built for (the previous
signpost's text, or nothing at all for the first selection of a session) until a
tool or option change happened to refresh it. Per frame is not the answer: a
rebuild destroys the widget being typed into.

**The entry is copy-on-write** (`EntityPlacement.EditableEntry`). A placement
starts out pointing at the palette's shared `.tres`, so a chest nobody has
customized keeps tracking whatever that entry is retuned to; the first edit forks
it, and the fork — path cleared, palette file kept as its `resource_name` — saves
into `placements.tres` as a `[sub_resource]` belonging to that placement alone.
Clearing the path is not optional: a duplicate that kept it saves as an
`ext_resource` pointing back at the palette and the fork is silently thrown away
on the next load. `worldmap_check` reports the entity list by entry with a
`(n customized)` count, which is where that failure would show.

**"Is this entry the placement's own copy?" is `SpawnEntryData.IsOwnedCopy`, and
it is TWO shapes.** A fresh fork has no path at all, but one that has been saved
and loaded back carries the sub-resource path Godot gives an embedded resource
(`res://…/placements.tres::Resource_abc`) — which is not empty and is not a
palette file either. Every site that asked `string.IsNullOrEmpty(ResourcePath)`
therefore read a reloaded fork as SHARED: the next edit forked the fork and named
it after the file it was embedded in ("placements"), which took it out of the
panel title, the hover readout, the palette-match highlight and
`worldmap_check`'s by-entry listing, and `PlacementsAspect` stopped capturing its
fields for undo.

**Text applies as it is TYPED**, and the multiline rows are why it cannot be on
Enter: a signpost's text is several lines, so Enter is a NEWLINE there and never a
commit. Committing on Enter-or-focus-exit therefore left clicking away as the
only way to save one — and nothing in the painter takes focus away (the map
canvas is `FOCUS_NONE`, so clicking the map leaves the box focused), so typing and
then clicking the next signpost lost the edit outright.

The undo step is what commit-on-leave was really protecting, and it is kept by
BRACKETING instead: the first keystroke opens one step (`BeforeEdit`, which
snapshots the before state, so it must happen ahead of the first character
reaching the entry) and leaving the field closes it, so a typed sentence is still
one undo. Enter ends the step rather than committing a value that is already in.

The panel is still FLUSHED — `FlushPendingEdit`, which releases focus so each
widget's own path runs, and closes any open bracket — on a canvas press, on
Ctrl+S and whenever the panel switches entities, because the rows that are NOT
text (a `SpinBox` being typed into) still apply on focus-exit. Two rules keep
that safe: rows read and write through the placement they were BUILT for
(`_rowsOwner`), not through the one currently shown, or a write fired while the
panel is already switching lands one signpost's text on the next one selected;
and a write whose value has not moved is dropped, since focus-exit fires for a
box merely clicked into and forking the palette entry for that would silently
stop the placement tracking the palette.

Scalars get an editor — string, number, bool, enum, flags — and so does a
**single resource-typed field**, through a dropdown filled by
`ResourceTypeIndex`. That is what lets an NPC be given its own conversation,
language, recruit template or species without authoring a palette file per
villager, which is the shape `NpcSpawnEntry` asks for in its own class comment
("every placement is its own entity with its own dialogue and stock"). Signposts
and knowledge stones get their language the same way, off the same mechanism.

**A string field with a derivable set of values is a dropdown too.** Not from a
scan — from what the entry itself NAMES, through `SpawnEntryData.NameCandidates`:
`MobSpawnEntry` answers `initialBehavior` with its descriptor's brain nodes
(transitions already reference each other by `BehaviorNode.name`, so that IS the
valid set), and `NpcSpawnEntry` answers `idleAnimation` with the clips in the rig
it is drawn with. Both fail SILENTLY when mistyped — a bad behaviour name falls
through to the species default and a bad clip fails
`ModelAnimator.HasAnimation` — which is the case a free-text box is worst at.

The clips are read off the `PackedScene`'s **`SceneState`**, not by instantiating
it: the rig names its `AnimationLibrary` as a plain `ext_resource`, so the list
is reachable without building a node tree — and without running `_Ready` on
scripts that expect a live `Sim`, which the painter has none of. A rig whose
`AnimationPlayer` sits inside an INSTANCED sub-scene keeps its properties in that
sub-scene's state rather than this one's, so the walk finds nothing, `null` comes
back, and the field stays a text box. Degrading to free text is the required
behaviour for every un-derivable case, and it is why the list is **advisory**:
whatever the entry already holds is offered even when the candidates do not
contain it, marked `(not in this rig)`, so a value authored against another rig
is not silently rewritten by merely selecting the placement.

Ordering is the answer to relevance, not filtering: the human rig carries ~55
clips and about five are rest poses, so `idle*` sorts first and the rest follow
alphabetically. Hiding them would make the list a rule about naming that nothing
else enforces — a pose could reasonably be called `sit`.

**The resource candidates are SCANNED, not authored.** `ResourceTypeIndex` walks
`resources/` once per session and groups every `.tres` by the C# class it
carries, so a conversation written today is pickable today — the same argument
that discovers `.hikescene` stamps on disk rather than through a palette. Two
things it is careful about, both of which would show up as a picker quietly
offering an incomplete list (the worst failure one has, since it reads as "there
are none authored"):

- **Nothing is LOADED to identify it.** A `.tres` names its script as an
  `ext_resource` path, so the class is that script's basename, read off the
  text. Loading a resource to find out what it is pulls in its whole dependency
  graph — for one `WorldGenData` that is most of the game.
- **The header's `script_class` is not enough**, because plenty of files here
  were written without one (`spawn_entries/chest.tres` has a bare
  `[gd_resource type="Resource" format=3]`). The `[resource]` section's own
  `script =` line is the reliable answer, and it has to be that section's — a
  `sub_resource` names a script too, so taking the first one seen types a file
  as whatever it happens to embed.

The field's type comes from **reflection on the entry's C# type**, not from the
property hint: these are C# fields, so reflection is the exact answer while a
hint string is the editor's rendering of one.

**Two things stay read-only**, and neither is an oversight. **Arrays** (an
outfit, a merchant's stock, loyalty gifts) want list editing rather than one
pick. **`PackedScene`** is a rig choice rather than data — an NPC's `scene` has
to gender-match its `outfit`, and offering every scene in the project invites a
mismatch the panel cannot check. A value the scan cannot name (an embedded
`MobPalette` sub-resource) is offered as its own disabled `(embedded)` row, so
leaving it alone is what the row means; dropping it into "none" would read as an
empty field and invite a pick that silently discarded it.

`worldmap_check` reports, per palette entry type, which properties are editable,
which get a picker (with its candidate count), and which stay read-only — using
the panel's OWN classifier (`WorldMapEntityInspector.EditorFor`) rather than a
second copy of the rules. It is also the check on the scan: a picker row showing
0 candidates means the index failed to see that type's files.

Bare-key shortcuts are safe while typing for free — the painter
reads keys in `_UnhandledInput`, and a focused `LineEdit` has already consumed
them.

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

Only ONE voxel is paved, and the kit channel is left alone: the kit says what
the column is made of, which a road laid over it does not change, and the rock
under a road is still the hillside's.

**WHICH voxel is the floor the map is SHOWING** — `CutawayFloor` at the shared
cutaway plane. With the plane parked over the world that is the surface, exactly
as it always was; lower it into a passage, or under an arch you built with the
block tool, and the stroke paves the floor down there instead. So the tool needs
no level of its own: **T/G already aims the cutaway** (and alt+RMB aims it at a
clicked floor), and a level you cannot see is a level you cannot aim. Solid rock
under the plane exposes no floor and takes no paving.

**A road on open ground records the surface SENTINEL, not that Y**, so it keeps
following ground repainted under it — and it resolves against the top SOLID
voxel, so it rides a deck later built over it and drops into a hole later carved
under it. Only a floor with something above it stores an absolute Y, because
nothing about the column describes where that floor is and re-seating would put
the road on the roof. That is the same split `EntityPlacement.floorY` makes, for
the same reason. `worldmap_check` reports the two counts, plus the paving whose
level is no longer a floor at all (**stranded**, and it bakes nothing) — which
only an absolute level can be.

**Erase clears the column, not the level on screen.** There is one paving per
column, so "lift what is here" cannot be ambiguous, and a seat stranded by
terrain repainted under it would otherwise be unreachable from every plane.

A column paved ON ITS SURFACE stamps no detail sprites and is excluded from
`CanSpawnAt` — worldgen's road pass deletes the scatter standing in its tread,
and grass growing through paving is the tell that a road was painted rather than
built. Paving on a floor UNDER the surface does neither: it belongs to that
floor and says nothing about the hillside over it, which is why both gates ask
`SurfacePavingAt` rather than `PavingAt`.

**Paving resolves inside `GroundColorAt`, not in the paving view**, so a road
appears on EVERY view that draws ground — you cannot lay props or mobs sensibly
along a road you cannot see. The colour is the block's own `minimapColor`, since
a block already authors what it looks like from above and a second palette would
only drift from it. Those views show SURFACE paving only: a road under an arch
belongs to the floor it is on, and colouring the hilltop with it would say the
hilltop is paved.

**`CutawayColorAt` resolves it the same way**, at the floor the cut exposes, so a
paved passage reads as paved on every cutting view and not just the paving
tool's — the same argument, and underground it is the one thing telling a
corridor you have finished from one you have not.

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

The bake runs `WorldFinish.StampClimbSurfaces`, which now takes the per-column
answers it cannot look up in a painted world (a route flag instead of a zone's
coverage, the painted water layer instead of a `HeightMap`) plus its wall minimum
and whether to be patchy — worldgen patchy, the painter not. Everything else is
shared: the exposed-face walk, the run heights, the per-block growth table.
Reimplementing that painter-side is exactly how the waterfall shading became two
copies that drifted. A marked column's whole exposed face is dressed, so a route
is currently a plain vertical column of climbable surface.

**Moss comes off the GROUND layer, not the zone layer.** `TerrainKitData.mossCoverage`
says how much of that material's exposed rock and ground wears the moss overlay,
and the bake answers `WorldFinish`'s per-column question with the column's
surface kit and cave kit — exactly the two coverages the pass wants. So painting
a material brings its moss with it: no second brush, and no moss where nothing
was painted.

It is NOT read off the zone, and that is not an oversight. Worldgen keeps moss
density on `ZoneGenData` (there it is a property of the biome being generated),
and the painter cannot reach that: its zone palette is `ZoneData`, which does not
correspond to `WorldGenData.ZoneGens` at all — 15 painted entries against 5 in
the default world, no index mapping, and the back-reference is ambiguous because
a `ZoneData` can be shared by several placements. `WorldFinish.Options.MossCoverageAt`
is the seam, the same shape `StampClimbSurfaces.coverageAt` already has.

The cost of keying per material is that a kit shared by two zones carries one
number: `marsh_kit` is both swamp (0.4) and swamp_fire (0.15) and takes 0.4, and
`cave_limestone_kit` is nearly every zone's cave and takes 0.5. Split the kit if
that ever matters.

Both scalars are deliberately absent from the preset brush: difficulty does not follow
biome, and neither does where the player is MEANT to be able to climb — both are
route-design decisions, so folding them in would tie together the layers that
most want to vary independently.

**Mobs AND forges read it, through two seams on `SpawnContext`.**
`MobSpawnEntry` asks `ComputeMobLevel` and `ForgeSpawnEntry` asks
`ComputeForgeLevel`; both otherwise read zone bands and noise fields that a
`Generate()` run leaves behind and a painted world never produces. Without the
seams a painted mob spawned at its species base and a painted forge baked at
level 0 — no pips, the mildest upgrade, wherever it stood.

The two stay SEPARATE delegates (`MobLevelOverride`, `ForgeLevelOverride`)
because in a generated world they are deliberately independent noise fields — a
zone's forges and its monsters vary apart. A painted world simply feeds both from
the one difficulty layer it has, which is also the scale a forge is authored
against ("a forge sits at the same tier as monsters in its zone").

`worldmap_check` reports the layer as the rounded tiers those seams hand out
(`danger: L0:… L1:…`), because a document that reads all-zero there bakes flat
and nothing says so until you are standing in it.

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
come out of its own palettes — a region's authored `displayName`, a zone's
resource file name — so those layers are painted by NAME rather than by index
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
the HUD toolbar cycle tool (+view) · **Q/E** step the tool's `Cycle`
parameter — the option index on most tools, and the parameter the option row
cannot show on the ones whose row is empty (the tunnel brush's
height) · **R/F** Flatten target level / tunnel floor · **T/G** or
**alt+wheel** cutaway level (**alt+RMB** aims it at a clicked floor) · **W** show/hide
water · **Ctrl+Z** undo, **Ctrl+Shift+Z** / **Ctrl+Y** redo
· **alt+click** pick a height (alt+drag spreads it) · **shift+drag**
constrain to that one height · **ctrl+drag** constrain to that height and above
· **wheel** or
**`[` `]`** brush size (proportional step) ·
**ctrl+wheel** zoom (cursor-anchored) · **middle-drag** pan
· **Ctrl+S** save layers, then bake the `.hike` in the background
· **Esc** pause menu (save / resume / quit to menu).

**The painter binds EDITOR-ONLY actions, never gameplay ones.**
`InputBindings.Apply` remaps `UseItem` / `Interact` / `InteractCancel` /
`Lantern` / `Dash` / `Sneak` at startup, so an editor bound to one of those gets
a different key — and a key that means something else — the moment that set is
edited. Q/E were bound to `UseItem` / `Interact`; Q became
`Lantern` while `UseItem` had moved onto **Ctrl**: Q did nothing here, and every
Ctrl press (the one held for Ctrl+Z and Ctrl+S included) cycled the tool's
parameter. They are `EditorParamLeft` / `EditorParamRight` now, alongside
`EditorUp` / `EditorDown` and `EditorClipUp` / `EditorClipDown` — actions
nothing remaps, which is what makes them rebindable in the input map without
touching this file.

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
  kit palette is authored (`WorldMapData.kitPalette`, a `KitPaletteData`) rather
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
- ~~The bake leaves fog, `EnvTag`, `Interiorness` and the water-current subgrid
  blank.~~ **Fixed.** All four are derived channels the `.hike` serializes and
  nothing recomputes on load, and the bake simply never ran the passes. Both
  producers now end on the one shared list (`WorldFinish.Finish`).
- **Not yet exercised in a bake or in game:** mob sets, climbing routes, paving,
  placements, undo/redo by hand, `worldmap_resize` on a real document, composite
  `SpawnGroupData` entries and chest loot, and the water/waterfall model. The
  last full game load predates all of it. The per-placement property panel is
  verified only as far as `worldmap_check` reaches — the entry types reflect and
  fork correctly and the scene wiring resolves; nobody has typed into it yet.

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

Also: in-world 3D region/zone tint overlay (a `ShaderGlobals` LUT + terrain
shader), and tiling the per-column images per chunk-footprint for streaming-scale
worlds (see `scripts/voxels/CLAUDE.md`).
