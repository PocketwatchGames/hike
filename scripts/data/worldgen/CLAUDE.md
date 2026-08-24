# World Generation: Terrain Approaches

Covers the authored worldgen resources in this folder and the pluggable terrain
approaches they select. The algorithms themselves live in
`scripts/voxels/terrain/`; the approach-agnostic passes (kits, roads, props,
spawns, fog, lighting) live in `scripts/voxels/WorldGen.cs`.

For the `.hike` file format and the streaming roadmap, see
[scripts/voxels/CLAUDE.md](../../voxels/CLAUDE.md).

## The split

Terrain shape is **not** in `WorldGen.cs`. An approach is two halves:

| Half | Lives in | Is |
|---|---|---|
| `TerrainGenData` subclass | `scripts/data/worldgen/` | authored, immutable, shared tuning |
| `ITerrainGenerator` impl | `scripts/voxels/terrain/` | per-run object owning this world's noise channels |

`WorldGenData.terrain` holds the subclass, and **that choice is the algorithm
selection** — there is no mode enum. `WorldGen.Generate` calls
`terrain.CreateGenerator(genData, worldSeed)` once and drives the result through
three hooks:

- `BuildHeightMap(ws)` — once, before any chunk exists.
- `IsCarvedAt(wx, wy, wz, columnSolidHeight)` — per solid voxel during chunk
  fill, for approaches that hollow terrain as they generate it. Must be a pure
  function: fill order isn't guaranteed and the same voxel is queried again as a
  neighbour when the mesher decides surface shapes.
- `IsSealedFromWaterAt(wx, wy, wz)` — of a voxel `IsCarvedAt` already claimed:
  must it stay air even at or below its column's waterline? Chunk fill floods
  carved voxels under the waterline, which is right for the channel under a
  bridge deck and wrong for an enclosed cave. Without it, any passage descending
  past sea level fills to its ceiling. Pure, for the same reason.
- `CarveVolumes(ws)` — after every chunk is filled, for volumes that need the
  finished grid rather than one column.
- `GetNamedFeatures()` — landforms the approach placed and named, registered
  into `WorldState.PointsOfInterest` so roads route to them and POI placements
  can name them. Stable internal identifiers, never shown to the player.
- `DumpDiagnostics(dir)` — the approach's own debug output, written alongside
  the shared images. It exists because the shared dump is a HEIGHTFIELD view: a
  hillshade of a world with caves under it is identical to one without.

Per-zone tuning mirrors this one level down: `ZoneGenData.terrain` holds a
`ZoneTerrainData` subclass.

## Adding an approach

Four things, and nothing else should need to change:

1. **`FooTerrainData : TerrainGenData`** in this folder — its knobs, plus a
   one-line `CreateGenerator` returning your generator. Keep the body that thin;
   no generation logic belongs on a `Resource`.
2. **`FooZoneTerrainData : ZoneTerrainData`** in this folder, if the approach
   wants per-zone knobs beyond the inherited elevation / flatten contract.
3. **`FooTerrainGen : ITerrainGenerator`** in `scripts/voxels/terrain/`.
4. **A `.tres`** for the world (`resources/data/world_gen/`) plus a
   `ZoneTerrainData` sub-resource on each zone the world uses.

Then bump `WorldGen.WORLDGEN_VERSION` — see the cache warning below.

**Do not add terrain fields to `WorldGenData` or `ZoneGenData`.** Both used to
carry them, and a second approach's knobs ended up interleaved with the first's
with nothing marking which belonged to which. A field belongs on the shared
class only if worldgen *outside* the approaches reads it — today that is the
vertical-extent trio and `maxGradeStep` on `TerrainGenData`, and nothing on
`ZoneGenData` at all.

**Blending per-zone scalars:** `WorldGen.SampleBlendedZoneGen` has a
weights-out overload that hands back the kernel weights, so an approach folds
its own per-zone knobs from the same weight solve. Use it rather than adding a
field to `BlendedZoneGen` — that struct deliberately does not grow per approach.
A zone carrying another approach's resource should contribute *defaults*, not
drop out of the sum, or it silently skews its neighbours' share.

## The kit palette is a WIRE FORMAT (`KitPaletteData`)

`WorldGenData.kitPalette` is the slot table `ChunkState.TerrainId` indexes — one
byte per voxel, in memory and in every `.hike`. Three rules follow, and none of
them is a style preference:

- **APPEND ONLY.** Insert, remove or reorder a slot and every world already baked
  comes back re-textured. Nothing about the stored bytes looks wrong when that
  happens — they stay valid and simply name a different kit. `WorldFile` v47
  records the slot paths (and the detail palette's, which is derived from the
  kits' `defaultDetail` and can move on its own) and `Main.LoadWorldFromFile`
  refuses a world whose palette moved, naming the slot.
- **It is authored, not derived.** It used to be built by walking `zones` and
  collecting each zone's four kit slots in declaration order, which made the wire
  format a side effect of zone *placement* — adding a zone re-textured every
  baked world — and gave no slot at all to a kit no zone referenced, so anything
  naming one silently fell back to slot 0.
- **It belongs to the world.** `WorldState.Kits` (a `KitPalette`) resolves it to
  the flat slot→block / slot→purpose tables the per-voxel loops read. It is not
  process state: two worlds can exist at once (the map painter bakes one on a
  background thread while another is live), and it outlives generation.

`EKitPurpose` stays DERIVED from the zones, because nothing outside worldgen
reads it — it answers "is this voxel the zone's surface ground?" for the scatter
and overlay passes, and a painted world that places no zones simply has none.

**`block_check` dumps the resolved palette** (`--headless -- "block_check 1"`,
~3s), which is how you prove an edit only appended: diff the slot list.

## The HeightMap contract

Everything downstream reads `HeightMap` and nothing else about the approach, so
these are the invariants a new approach owes its consumers:

- **`Height`** — what chunk fill fills solid up to.
- **`Surface`** — where the ground actually ended up after carving. Seeded equal
  to `Height`; `DeriveSurface` re-derives it. Placement passes anchor here.
- **`Plateau`** — the flat-ground reference. `IsFlatDryGrassAt` tests
  `Height == Plateau`, so an approach with no ramp concept sets `Plateau =
  Height`, which reduces that test to "above water" and lets scatter cover
  hillsides. Spawns needing genuinely level ground use `IsFlatTerrainAt`'s
  8-neighbour equality instead, which is geometric and always correct.
- **`LevelStep`** — the world's vertical lattice for **enclosed** space:
  building floors now, cave and tunnel ceilings when carving returns. Every
  interior ceiling must sit on a shared Y grid or the camera cutaway slices
  rooms at arbitrary heights. The open-air surface may ignore it.
- **`Water`** — the per-column INLAND water surface (river channel or lake), or
  `HeightMap.NoWater`. **Optional**: an approach that makes no inland water
  passes nothing and the array is null, which `GetWaterY` answers as `NoWater`
  everywhere. Two invariants a producer owes its consumers: the surface sits on
  the world's terrace lattice (so water reads as flat pools stepping down in
  whole cascades, never as a slope), and it is at or above `Height` for the same
  column (water is the fill between `Height + 1` and here). Sea columns stay at
  `NoWater` — the global waterline already covers them, and stamping the sea
  level into the channel would make every consumer's `max()` a no-op that hides
  bugs.

  **Consumers must go through `WorldGen.WaterYAt(heightMap, wx, wz)`**, which is
  `max(WATER_LEVEL, GetWaterY(...))`. Comparing against `WATER_LEVEL` alone is
  the bug this channel exists to fix: it says a lake floor 8 voxels above sea
  level is dry land. Chunk fill, the shore-kit bands above and below the
  waterline, `IsFlatDryGrassAt` and road passability all read it. Passes that
  test the VOXELS instead (`GetVoxelWorld(...) == VoxelType.Water` — the
  submerged-kit tagging, water-entity spawns, fog's open-to-sky scan) were
  already correct and needed no change.
- **Waterfalls are NOT a `HeightMap` channel.** A terrain approach reports no
  cascades at all. They are found after the fact by `WaterfallFinder`, off the
  finished voxels, under one rule: wherever a water voxel sits beside an air
  voxel, the topmost air voxel of that span with water beside it is a lip, and
  the sheet runs from there to the floor of the span.

  It used to be a channel, walked down a scratch copy of the water field by a
  `StandWaterfalls` pass. That only ever saw falls the river pass itself had a
  notion of — nothing made by a carve, a stamped scene or a hand edit — and the
  painter had to grow a second implementation off its painted layers for the
  same reason. Both are gone; adding a terrain approach adds no waterfall code.

  **The drop is left as AIR.** `StandWaterfalls` used to fill it with water so a
  fall didn't read as two pools with bare rock between them; a waterfall effect
  draws that now, and the voxels were actively harmful — a column of water is
  indistinguishable from a deep pool, so buoyancy floated the player back *up* the
  cascade. Air makes falling through work with no special case anywhere: the
  water-state checks, the swim gate and the nav sampler all do the right thing
  because there is simply nothing there. A dedicated voxel type was tried and
  removed; every gate it needed turned out to be reproducing what air does free.

- **`Current`** — which way the inland water in each column is MOVING, as a
  world-XZ vector in the normalized `[-1, 1]` units `ChunkState.SetCurrent`
  stores; zero on a still or dry column, and **optional** in the same way
  `Water` is. `WorldGen.StampRiverCurrents` averages it into the env-cell
  current subgrid the water shader advects its ripples along.

  **Only the approach that routed the water can supply this.** It comes off the
  drainage tree that produced `Water`, and nothing downstream can re-derive it:
  the surface is deliberately FLAT along a reach, so the finished heightfield
  has no gradient left to read a direction from. An approach that leaves it null
  gets the ambient wind-driven drift everywhere, which is what the sea gets.

- **`NoSpawn` / `FixtureGround`** — reserved ground; the approach just allocates
  them.

`maxGradeStep` (on `TerrainGenData`) is the mesher's discriminator: adjacent
columns within it mesh smooth as a grade, beyond it mesh crisp as a wall. An
approach that authors grades steeper than this hardens them into visible stairs.

## Existing approaches

**`PlateauTerrainGen`** — the original. Height noise quantized to `plateauStep`
bands with ramps painted back in; tunnel slabs at band boundaries; swiss-cheese
caves whose ceilings snap to the next band. Known shape problems, which is why
it isn't the only one: quantization caps cliff height at one band, the tunnel
slabs leave one-voxel roofs floating over open air, and cave ceilings breach the
surface as open pits.

**`OrganicTerrainGen`** — continuous domain-warped surface; cliffs emerge from
steepness rather than from a lattice. Carves nothing. See
`OrganicTerrainData` for the per-stage reasoning.

**`CellularTerrainGen`** — the world is partitioned into irregular cells and each
takes ONE flat top at the median of the ground under it, quantized to
`quantizeStep`; every cell border is therefore a wall. Cells subdivide themselves
wherever one flat top would misstate the ground it covers, so relief drives cell
SIZE rather than cell height. A spanning corridor network over the cell graph
guarantees every cell is reachable, and refines the cells along each route so it
climbs in short steps. Ringed by ocean on all sides (island falloff). This is the
approach the default world runs; see `CellularTerrainData`.

Beyond the cells it places **landforms**, cuts **escarpments** and **carves**.
Split across three files, one partial class: `CellularTerrainGen.cs` (pipeline),
`CellularTerrainFeatures.cs` (landforms), `CellularTerrainCaves.cs` (carving).

**Landforms** — mesas, quarries, craters and terraced stair flights — are ONE
pass over the cell graph: pick N cells matching a topological rule, apply an
effect, claim them. Always by COUNT, never by a per-cell chance, so an author can
say "three mesas in this world" and get it. They run after the LAST
`RelaxCellWalls`, which is the only reason they survive — relax lowers any cell
standing more than `maxCellStep` above a neighbour, which is precisely what a
mesa does on purpose. No landform may leave a wall taller than `maxCellStep`; a
candidate that would is passed over rather than clamped.

Pinning is asymmetric and non-obvious. `Pinned` reaches `BuildWaterways`, where
it means "no lake may flood this and no breach may cut it" — right for a mesa,
which is a local maximum and can never be a sink, and wrong for anything
pit-shaped. A pinned quarry or crater is a sink the breach pass cannot drain, so
the fill never settles and the river above it dead-ends. Those are left unpinned
and the water pass does the right thing with them on its own.

**Land bridges** are not cell features: a ribbon narrow enough to read as a
bridge would be folded away by `MergeSlivers`, and `RelaxCellWalls` would drag
the deck down into the gap it spans. They are a direct per-column height write
after both passes — the ordering trick `CutRamps` already relies on — plus a
carve for the air underneath, since the heightfield is single-valued and only the
deck can live in `Height`. `DeriveSurface` then makes the deck the surface, so
placement passes put props on top of the bridge rather than in the void under it.
Ribbons refuse inland-water columns (a deck over one leaves `Water` below
`Height`, which breaks that channel's invariant); the SEA is fine and a bridge
over open water is the intended shape.

**A pair of cell centroids picks a candidate; it is not the bridge.** The
segment between them is trimmed to the gap it actually crosses (`TrimToGap`) and
the abutments are the columns either side of that gap, so `bridgeSpanMin/Max`
measure the SPAN and not the two cells' own ground. Measuring centroid to
centroid — which is what it used to do — rejects a short crossing between two
large cells and passes a long one between two small ones, because most of the
distance between two cells is solid earth.

The deck is **arched**, written in the terrain's own vocabulary: flat treads on
the `quantizeStep` lattice joined by risers. **Every riser chooses for itself**
(`bridgeArchSlopeChance`) whether to stand as a crisp 2 m step or spread into a
short 1-voxel-per-column grade, so one arch carries both — grade onto the deck,
step to the crown, grade down, step off. Rolling it per DECK instead gives
bridges that are uniformly one or the other, which is a duller shape and reads as
two templates rather than as one kind of thing.

Geometry has the veto, not the roll: a riser spreads only where the treads either
side can spare the columns (`bridgeArchSlopeTread`). The shortest treads in an
arch are the two at the abutments, so that knob really decides whether a deck may
grade onto its abutment or must step onto it — at 4 the abutment risers are
always steps and short bridges never grade at all, at 3 the split comes out even.
A bridge short enough to hold only one riser each side is a single hump whatever
the chance says, which is right.

Crown height is a FRACTION of the span (`bridgeArchGrade`): a fixed rise that
reads as gentle over 30 m is a hillock over 12 m — measured, an absolute rise
left every bridge in the world all-steps, because no tread had room to grade. A
graded column moves exactly one voxel because that is `maxGradeStep`; spread any
wider and the mesher hardens it back into the stairs the grade was meant to
replace, which is also the smooth arch's admission test (a sine climbs fastest at
its abutments, and one too steep there is built stepped instead). The pass logs
its smooth/stepped split and its graded-column count; all of one and none of the
other means the grade or the tread threshold is off.

**Cliff erosion** works per cliff-lip column off THREE independent noise fields:
how far back to cut, whether that lip becomes a step or a slope, and (for a step)
how low the flat sits. Independent because sharing one field correlates them —
every deep cut would also be a slope and every step would bench at the same
height, which reads as a pattern rather than as erosion. A cut-back of ZERO is a
legal roll, and those columns are what leave the cliff its **fingers**, so the
un-cut stretches come from the same field as the cuts rather than from a separate
coverage test.

A cut takes one of TWO shapes, and there is deliberately no sloped one. A slope
across a cut one to three columns wide resolves to a run of single-voxel steps,
which read as near-invisible ledges rather than as a grade — the exact thing the
slope was meant to avoid. Every shape here is either full-height or flat.

A **pure cut** drops the band straight to the cliff base, so the edge retreats in
plan and the wall keeps its whole height, introducing NO horizontal surface. A
**ledge** drops the band to one flat height, kept at least
`cliffLedgeTopClearance` below the top so the cliff still has a real face above
it. Which shape a lip gets is a gameplay decision before a visual one: any
horizontal surface part-way down a short wall is something to climb, and a short
wall exists to stop the player — so a wall with no room for a ledge under the
clearance can only be cut.

The cut DEPTH follows how far under the erosion threshold the lip's own noise
sample fell, so depth varies along a face rather than alternating; the ledge
HEIGHT is rolled over the band between the base and the clearance, so a tall
cliff has real choice and a short one has exactly one. Lips whose noise sample
clears the threshold are left alone entirely, and those un-cut stretches standing
proud between the cut ones are the FINGERS — as much the effect as the cuts.

Nothing this pass writes leaves the `quantizeStep` lattice, and it counts to
prove it. One case had to be excluded to make that true: a RAMP beside a cliff is
a cutting rather than the cliff's base, and taking one as the base put a pure cut
off the lattice with it.

Its other half, **talus** — scattered single columns raised one step at the foot
of a face — runs AFTER the water pass and skips wet columns, because it RAISES
ground and a block dropped into a channel the flood already routed would dam it.
Same per-column-write reasoning as bridges. Measured on the default world:
12-voxel walls 783 -> 93, nothing above `maxCellStep`, and open ground in runs
over 64 columns unchanged at ~34%.

**Offshore islands and sea stacks** are injected into the CONTINENT MASK before
the cells see it, so the partition, the medians, the quantization, the coastal
taper and the sliver merge all treat an island as ordinary land and it terraces
like the mainland for free; a post-hoc height stamp would sit outside all of that
and read as a foreign object. A stack is the same mechanism with a small radius
and the coastal relief taper skipped. Two traps, both measured: the island term
is a mask TARGET rather than an added amount (an additive one large enough for
the deepest legal site overshoots `coastalPlainBand` at every shallower one, so
the taper never applies), and a stack's height must be capped, because
`FitVerticalExtent` sizes the world to its tallest column and one untapered stack
lifted the world's ceiling — which then silently licensed taller mesas too.

**Caves** are a small COUNT (0–5) of short, LOCAL systems: one arched **porch**
cut through a cell wall, and behind it a bounded flood at a SINGLE lattice level.
Systems are never joined to each other, and they may overlap — the only thing
that has to hold is that nothing opens to the sky, and that is verified over the
finished claim regardless of who claimed what.

That shape replaced independently-placed chambers linked by A*-routed tunnels,
and the simplification fixed three things at once that tuning could not. A routed
tunnel spans whatever distance separates two unrelated points, so it is long; it
must change level on the way, and fitting those changes to a route's length is
what kept putting floors off the lattice; and a path widened by a fixed disc is
only ever as wide as the disc, which the enclosure check then trims further — so
tunnels came out narrow however large the disc was set. A flood at one level has
no level arithmetic to get wrong and takes every column the rock allows, so a
passage is as wide as the ground it runs through.

**A mouth is only accepted on a terrace already on the interior lattice.** This
is the load-bearing line for "cave floors are at 4 m elevations": a system takes
its level from its mouth and a mouth takes its floor from the terrace outside,
which sits on `quantizeStep` — so half of them are 2 voxels off the interior
grid, which put every column of those systems off-lattice (527 of 568 in one
run). Terraces on 4 are half of all terraces, so the rule costs candidate mouths
and buys the invariant outright, with nothing downstream having to snap or
compensate. The pass counts stray off-lattice floors, and it must read 0.

The one level change a system may have is a short **entry ramp** just inside the
porch, tried deepest-first and falling back to flat — a step in the middle of a
cave is one the player meets with no warning, while a ramp at the mouth reads as
the way down. **Both floors and ceilings sit on
`HeightMap.LevelStep`** — the 4 m lattice building floors use, so the camera
cutaway slices a cave and a room at the same heights. That forces the gap to a
multiple of 4, and the smallest one clearing `caveClearance` is 8, so headroom is
a consistent 7 m. Exactly three kinds of ground leave the lattice, and the pass
counts each so a fourth cannot appear unnoticed: the **porch** (which meets the
terrain's own 2-lattice), a **ramp** at a level change (spent one voxel at a
time, because a 4 m step is a wall inside a corridor), and the **arch** over a
mouth. Four voxels of headroom everywhere, including at the arch jambs.
Stalagmites come from a position hash, not stored state.

Systems are small on purpose and small in fact: bounded by `caveReach` from the
mouth and by a column budget, and in practice bounded before either by how much
ground stands `caveRoofRock` above the ceiling — which in this shallow world is
patchy. Read the width histogram the pass logs (distance to the nearest wall,
doubled) rather than assuming the authored width was achieved.

The earlier version grew ONE flood outward from a mouth and enforced enclosure
with a per-column PREDICATE judging each candidate against a PREDICTED level for
its neighbours. Both halves were wrong — a prediction is not the geometry that
gets built, and the mouth exemption applied to a radius rather than to an
identified doorway — so wherever the flood ran along a narrow spine it opened to
the air on both sides and left the rock above standing as a slab. **The fix is to
stop predicting and start VERIFYING**: everything is claimed first, then a
fixpoint deletes any column with a carved voxel facing open air unless it belongs
to a doorway, repeating because deleting a column exposes its neighbour. A flood
from the entrances then throws away whatever they cannot reach.

Four things that each cost a debugging round, all of them ordering or scope traps
rather than tuning:

- **A porch's depth cannot be authored.** It has to run inward until the rock
  closes in, because a fixed depth leaves the column behind it facing daylight
  wherever the cliff face is not straight — the seal then deletes that column,
  correctly, and severs the cave from its own entrance.
- **The porch and the tunnel router must use the SAME wall radius**, or the porch
  stops one column short of anywhere a tunnel may legally start.
- **Selection and validation must be one loop.** Choosing `caveSystemCount`
  candidates by spacing and testing them afterwards meant four tries out of a
  hundred, and when all four happened to be slots the world got no caves while
  the log insisted none were possible.
- **A column holds one air span**, so two spaces meeting in it must resolve to
  one — and `min(floor), max(ceiling)` is the wrong resolution: a mouth at y=15
  sharing columns with a tunnel at y=-15 fused into a 31-voxel shaft. First claim
  wins, and the router refuses columns it cannot walk into.

Tunnels leave their start at a fixed GRADE rather than interpolating across the
span. While a tunnel is still at mouth level the ground beside it is the very
drop the mouth was cut into, so it is not walled and has nowhere to go —
measured, with a linear floor NEITHER entrance could route anywhere. Descending
also puts caves in the rock below sea level, which is most of the rock this world
has, and combined with `IsSealedFromWaterAt` they stay dry however deep they go.

All of it is decided in `BuildHeightMap` and written into ONE bitset that
`IsCarvedAt` reads and nothing ever mutates afterwards — which is how the hook's
purity contract is met for shapes ("this cave reaches daylight", "this roof is
thick enough") that are properties of a whole system rather than of one voxel.

**Rivers and lakes** are another pass that carves. Rain weighted by each zone's
authored humidity (`ZoneData.weather.humidity` — the biome-local channel; the
`rainAmount` beside it is imported weather and says nothing about the climate
here) is routed over the FINISHED terraces by a priority flood from the sea, and
the same fill that gives the catchment gives, per column, the level water would
stand at. Ground already at that level becomes a CHANNEL — dug `riverDepth`
under it, water flush with the terrace it crosses. Ground below it is a POOL.
Neither ever slopes: the surface is always a lattice multiple, so a river is a
chain of flat reaches that drop a whole step at each wall, and the column at the
foot of a drop is filled to the pool above it so the fall is a vertical sheet of
water rather than a bare rock face.

That last rule cost three tries and is worth reading before touching. It must
reach **dry** columns, not just ones already holding water — where a channel
runs off a lip there is no channel below it to raise, so a wet-only rule left
the river as a lip of water overhanging bare rock. It must **walk the whole way
down**, because a wall here is rarely one drop (the relaxation caps each cell
step and the coast terraces repeatedly), so a one-column hop reaches the first
tread and stops. And it must **re-visit a column whose level improves**: the
walk can reach a tread before the taller neighbour above it has been raised, so
a round-based sweep carried the wrong surface down and the curtain broke a
couple of voxels below the lip. What keeps it from flooding the world is that it
only ever moves onto STRICTLY LOWER ground — water never spreads along a
terrace, only down a face — plus `riverFallReach` bounding the one case that
rule doesn't cover, a long gentle descent.

Channel **width** is the flow curve times a noise multiplier (`riverWidthNoise`).
The noise is not decoration: flow only ever grows downstream, so the curve alone
gives a ribbon that widens monotonically from source to mouth and reads as a road
rather than as water. The noise is what puts pools and narrows along one reach.
Two things constrain it. Its **wavelength must be long** relative to
`riverHalfWidthMax`, because the channel is stamped as a disc per column and the
discs union — a narrow between two wide columns is simply filled in by its
neighbours' discs, so above roughly `0.06` it averages straight back out to the
plain flow width. And the half-width is floored at **0.5**, which is what
guarantees the channel's own column is always stamped; under it the disc covers
nothing, the river comes out with holes, and nothing downstream re-checks that
the network still reaches the sea (`stats.txt`'s connected-body count is the
check — it must not rise when this is retuned).

`WIDTH_NOISE_GAIN` exists because **Perlin does not reach ±1**. Measured on the
default world, the raw channel spanned only `-0.38..0.51`, so an authored 0.5
delivered a ±0.25 wobble and the rivers were barely distinguishable from the pure
curve. The gain is a bit over the reciprocal of that span, clamped, so the
authored number is reachable; the clamp flat-tops the extremes, which is the
right shape for water anyway. The pass logs both the achieved noise span and the
achieved half-width range — read those, not the knob, when retuning.

**Current** comes off the same fill: each column's direction is the step toward
its receiver in the drainage tree, its speed follows the same log-flow curve the
width does, and lakes keep only `lakeCurrentScale` of it. Direction and speed are
smoothed SEPARATELY over the wet neighbours and recombined, and that separation is
the non-obvious part — the tree is four-connected, so a raw direction is one of
four axis vectors and a diagonal reach is a zigzag; blurring the vector alone
loses speed wherever the zigzag cancels (averaging `(1,0)` with `(0,1)` is 0.7
long), so every bend came out 30% slower than the straights either side of it.

A sink that fails the lake tests is **breached, not abandoned** — its rim is
notched down until it drains. Water entering a sink has nowhere else to go, so
skipping one leaves the river dead-ending at its rim; the whole network came out
in disconnected fragments before this existed. The rim to cut is read straight
off the fill (each column records the constriction holding it back) rather than
searched for afterwards, which cannot tell a true saddle from an interior bump
of the same height. Breaching changes the ground, so the fill re-runs until it
settles — a handful of passes.

Two collisions worth knowing. **This world is a bowl**: its central plain is one
6752-column sink, uniformly two voxels deep, with the village in it — flooding it
puts 10% of the world under water, so it is breached to the sea instead, and it
is the reason a lake cap exists at all. And a **channel may cross pinned ground
(the village) while a lake may never flood it**: a stream through a settlement is
where settlements are, standing water over one drowns it. Both are logged.

**It contains no slope at all, by design** — every column is a multiple of
`quantizeStep` and every adjacent pair is either equal or a wall of at least one
whole step. Rivers keep that invariant: a bed is cut a whole number of steps
down, and the water surface is on the same lattice. Nothing is interpolated: the coast and the flatten zones are folded
into the field *before* the medians, so they terrace like everything else.
Anything sloped in a dump of this world came from a later pass (the road grader),
not from here.

Two things to know when reading its dump. The **sustained-grade line is
mis-calibrated for it**: that metric calls a rise of 2 over 3 columns an
over-steep grade and only treats 3+ as a wall, so with `quantizeStep = 2` it
counts every short cell wall as slope — read the transition histogram (which
should contain only multiples of the step) and the hillshade instead. And a
**2-voxel step is mantleable**, so the smallest cell walls break ground up rather
than acting as barriers; the walls that constrain movement are the 4+ ones.

**Short walls do not get the wall tile.** `voxel_clip.gdshader` picks flat vs
wall per fragment from `shaded_normal.y` against `TerrainData.wallBand` (authored
at 0.3–0.4), and `ChunkMesherDC` sets the `sharpness` that would give it a true
face normal only for `SharpAxes.All` (architecture) — natural ground is
`SharpAxes.Y`, so the shader always shades it from the SMOOTHED DC normal. Over a
2-voxel step that normal never drops into the band. Raise `quantizeStep`, or
raise `wallBand` on the terrain `.tres` files (safe here precisely because this
approach leaves no real slope to mis-paint).

Terrain *playability* rules that outlived all three (walls always 4–12 m,
sustained grade capped, slope always intermixed with cliffs and plateaus) are
documented on `OrganicTerrainData`'s fields — worth reading before designing a
fourth, since they are gameplay constraints rather than aesthetic ones and a new
approach inherits them.

## Verifying an approach

**Do not boot the game to judge terrain.** `worldgen_debug_dump` runs the whole
generate headless and quits, in ~35 s:

```bash
Godot ... --path . --headless -- "worldgen_debug_dump user://wg_test"
```

It writes `stats.txt` plus `hillshade.ppm` (relief-shaded, red = walls, cyan =
inland water painted OVER the wall overlay since a gorge is a wall by every
geometric test — the image to read for shape) and the banded
`height`/`plateau`/`ramp` PPMs. `stats.txt` reports, in order: which
`TerrainGenData` ran, sustained-grade violations, per-zone elevation,
unbroken-traverse run lengths (how far you can walk before a wall stops you —
the transition histogram cannot see this), the adjacent-delta histogram, which
is the wall-height distribution, and the water section: coverage, connected-body
sizes (the check for one-column puddles, which no coverage figure shows), the
surface-level histogram (**every level must be a lattice multiple** — an odd one
is a bug in the approach, not a tuning problem) and the depth histogram.

**A heightfield dump cannot show carving.** `worldgen_terrain_dump` is the fast
(~5 s) loop for tuning surface shape and is blind to caves by construction. For
anything hollowed, run the full `worldgen_debug_dump`: the cellular approach's
`DumpDiagnostics` writes `carve_slices.txt` (vertical slices through each cave
mouth and bridge, as text, because "is this ceiling a multiple of 4" is a
question about digits) and its passes log their own invariants — headroom, roof
rock, deepest floor, and a post-fill check that every carved voxel actually came
out as air rather than flooded. Read those numbers before reading the images.

`height.bin` / `plateau.bin` / `water.bin` are the same fields as raw int16 for
numerical analysis; `water.bin` writes `short.MinValue` for a dry column. Layout
is documented in `stats.txt`. **Reach the numbers, not the eye** — connectivity
of the river network to the sea is a five-line flood fill over `water.bin`, and
it caught three separate breaks that the hillshade made look plausible.

The dump path uses `Main.defaultWorldGenData` only. To exercise a different
world, point that `.tres` at your terrain resource temporarily, or boot with
`autostart 1` + `world_gen_index N`.

## Cache gotcha

`WorldGenCache` fingerprints `WORLDGEN_VERSION` + `WorldFile.VERSION` + the
content hash of every reachable `.tres` — **not** the worldgen C#. Editing a
`.tres` invalidates automatically; changing generator *code* does not, so a
code-only fix silently loads a stale world and `Generate` never runs. Bump
`WORLDGEN_VERSION` whenever generation behaviour changes. When a worldgen fix
"won't take", suspect this first.
