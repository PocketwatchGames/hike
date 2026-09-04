# Painted prop regions: the size-ordered fill

An author paints a REGION on one of two prop layers; the bake furnishes it with
props. The point of painting props is to say **where the player cannot walk**, so
the region's EDGE is sealed — but only its edge. Behind that band nobody can
reach the ground, so what happens there is a question about how the region looks
from a camera that can see over it, and nothing more.

This file is the whole of it: the data, the algorithm, what is measured, what is
guaranteed, and what was tried and rejected. `../CLAUDE.md` carries the summary.

## Where everything is

| Thing | File |
|---|---|
| `PropListData` — a list of scenes + the knobs that shape the passes | `scripts/data/worldmap/PropListData.cs` |
| The fill, the caches, the seat, the queries | `scripts/worldmap/WorldMapState.cs` (the `--- Props: a size-ordered fill ---` section, and the nested `FillWork`) |
| Placement into the world | `scripts/worldmap/WorldMapBake.cs` (`RescatterColumn` / `ScatterProps` / `PlacePaintedProp`) |
| The two tools + their shared view | `scripts/worldmap/tools/PropPaintTool.cs` |
| Map dots | `scripts/worldmap/WorldMapPainter.cs` (`DrawPropDots`), inks on `WorldMapInkData` |
| Measured shapes (shared with the minimap) | `scripts/gameplay/minimap/PropFootprint.cs` |
| Authored lists | `resources/data/world_authoring/prop_lists/*.tres` (9 today) |
| Reporting | `scripts/worldmap/WorldMapCheck.cs` — the `props:` line |

Two layers, two `R8` rasters, one shared palette:

- `map/props_blocking.png` — collidable. **A no-spawn region** (see below).
- `map/props_breakable.png` — destructible. Passable by construction.
- Both store `prop list index + 1`; 0 = unpainted. The palette is
  `WorldMapPaletteSource.PropLists`, discovered from `prop_lists/`, ledgered in
  `map/palettes.tres` like every other indexed palette.
- **No density channel and no spacing.** The raster says only *which list covers
  this column*; everything else is a property of the list.

## The contract

Given a painted column that `CanPlacePropAt` accepts, exactly one of these is
true after a bake, and `worldmap_check` counts all four:

| Outcome | Meaning |
|---|---|
| **covered** | inside some prop's collision |
| **interior** | past the edge band — furnished to taste, not to coverage |
| **too tight for the list** | nothing in the list fits here without reaching outside the painted region — the author's to fix |
| **uncovered** | **must be 0** for a column in the edge band. A hole in the barrier; a bug |

`CanPlacePropAt` is `CanSpawnAt` minus the grade clause and minus the
blocking-region test. It refuses water, a carve or build over the surface,
paving, and a placement's reserved footprint — "there is no ground here" or
"something else owns this column". It deliberately does **not** refuse a graded
slope: that clause is a scatter pass's taste (no lone trees down a generated
hillside) and it punched a hole through every barrier crossing a slope — 102 of
the first 413 painted columns, measured.

## The fill

Per CHUNK, seeded by the chunk. `WorldMapState.BuildFill(destructible, cx, cz)`
builds a `FillWork` and runs one **spacing pass per size class, largest first**,
then one **seal pass**.

That ordering is the whole shape of the result:

- The big pass runs first and everywhere, so the trees go down before anything
  else has taken the ground — and they are spaced by their **canopies**, not
  their trunks. A trunk-sized exclusion cannot express "room to breathe": two
  oaks a metre apart have not touched trunks and still read as one smeared tree.
- Each later pass spaces only against **its own class**, so a bush may stand at a
  tree's foot. The skirt of undergrowth around a trunk falls out of the ordering
  instead of being authored into the tree.
- The seal pass then covers the edge band, ignoring spacing entirely, choosing
  the **widest collision that fits**.

### Two storeys, two reservation models

A prop fills the **CANOPY** or the **UNDERSTORY** (`PropListEntry.tier`, or by
measured drawn height for a row left on `Auto`). Every canopy class places before
any understory class: the trees go in first and everywhere, then the low stuff
fills beneath and between them, largest down to smallest.

**The two reserve room from their own kind ONLY.** A bush does not push a tree
away and a tree does not push a bush away — that is what an understory *is*, and
it is why one shared spacing rule could never express both: it had to either
forbid undergrowth beneath trees or let a redwood stand inside a maple's canopy.
An earlier `understoryRatio` tried to draw that line numerically off the size gap
and could not, because a sequoia's canopy (2.25 m) sits closer to a maple's
(4.12 m) than to a bush's.

| Storey | Reserves | Why |
|---|---|---|
| **Understory** | its own **collision radius**, measured, no knob | two bushes reserving that stand exactly touching, which is what a thicket is. The collider already says how big a low prop is |
| **Canopy** | drawn radius × `canopySpacing`, capped at `canopyMaxReservation` | a crown is many times wider than the trunk holding it up, so nothing about the collider says how much room it wants |

Within a storey, two props stand at least the **sum of their reservations**
apart, plus the jitter both may spend walking toward each other. A single radius
cannot express that: "no two of these within R" puts big trees and small ones the
same distance apart, and it is what had birches 1.5 m apart while an oak claimed
11 m.

**Height, not collider width, decides the storey** for an `Auto` row. A pine's
trunk collider is as wide as its crown and a willow's is a tenth of it, so width
says nothing — but nothing low is a tree and nothing tall is undergrowth. Classifying
on the width ratio put pines and fat-collidered trees in with the bushes.

**A row may fill BOTH** (`EPropTier.Both`), appearing as one class in each
storey. That is for a tree whose branches reach the ground and so reads as
undergrowth as well as canopy — the pines, and only the pines.

The one rule that spans both storeys is **no collision overlap**: a prop's
footprint may not cover a column another prop's footprint already covers. Two
solid volumes sharing ground is a bush growing through a trunk, and no amount of
spacing tuning hides it. It is deliberately about COLLISION and not the drawn
radius, which is exactly what lets a bush sit at a trunk's foot.

Both models come off in the seal pass's last resort. How a prop reads under a
canopy is taste, and taste does not get to open a lane through a barrier.

### Density: rounds, not relaxation

Each class is thrown at the region `spacingRounds` times, with a different salt
each round and every round refused by what the earlier ones reserved. One round
is a maximal independent set, which settles at roughly two thirds of the props
the same minimum separation would allow; the later rounds are dart throws into
the gaps it left.

**A spring relaxation was considered and is the wrong fit here**, however well it
suits this problem in general. It would have to MOVE props, iteratively, so a
position could no longer be re-derived per chunk from its column — the fill would
have to be stored, which is the one property this whole model is built on — and
two chunks would converge differently along their shared seam. Rounds get most of
the density for none of that: nothing moves, and every round is as order-free as
the first.

### Size classes are read off the list, not authored

`FillWork.Bucket` sorts the list's scenes and starts a new class wherever the
radius falls off a step (`ClassSizeStep`, 0.6). So the passes follow the sizes an
author actually put in the list, and adding a bigger bush adds a pass rather than
needing one declared.

It buckets **within a storey**, on the EFFECTIVE reservation rather than the raw
drawn radius — that was a bug worth remembering. On the raw radius an oak
(4.62 m) and a birch (2.56 m) landed in different classes while
`canopyMaxReservation` had already made their reservations identical, so they ran
as separate passes competing for the same ground. The oak pass took every slot
and **no birch was ever placed** in the wood.

### The seal is undergrowth's job, and reservations are not its business

The seal buckets separately by **collision** radius, widest first, over
**understory scenes only** (a list with no understory falls back to everything).
Three things about that, each of which was a bug:

- **Widest first.** A tree is the largest thing in a forest list and seals one
  column, where a bush half its size seals seven. Taking the smallest that fits
  is what ringed every region in pebbles.
- **Understory only.** A barrier is made of the low storey and trees stand IN it.
  Letting the seal reach for a tree stood a pine's crown against a maple, since a
  pine has the widest collision in the list.
- **Reservations off from the start.** They are the FURNISHING model; the seal
  answers to coverage, and collision no-overlap is its only geometric rule. While
  the seal honoured reservations, everything wide was refused and the last-resort
  tier — least-intrusive-first — became the main path: **872 pebbles and 451 of
  the smallest bush out of 1404 props**, with every larger bush placed 0 or 1
  times. Turning them off took it to 1147 props with the bushes actually used.

**A bucket is entered at a hashed offset, not at [0].** The sweep takes the first
member that fits, so a fixed start has one scene of each bucket doing all the
sealing — six bushes of identical width came out 240 / 51 / 54 / 54 / 51 / 49,
and 72 / 85 / 74 / 69 / 83 / 89 once rotated.

### Footprints are measured, never authored

`PropFootprint.Measure` rasterizes a scene's static collision **once**, into a
reusable `PropFootprint.Shape` in the scene's own space; `Shape.Rasterize(pose)`
then answers for any yaw and any sub-metre offset without re-instantiating.
Splitting those two is what makes position jitter free — the old cache was per
`(scene, yaw)` and an offset would have multiplied it.

The shape also carries two reaches:

- **`CollisionRadius`** — what blocks. What a barrier is made of.
- **`VisualRadius`** — what is drawn. Read off the authored `FoliageCluster`
  nodes rather than off the `MultiMesh` they bake into, because that bake is
  rebuilt at runtime and the copy in the `.tscn` is only the editor's last
  preview: a bush whose clusters were widened measures at its old size until
  someone opens it.

Measured today (forest list): oak **visual 4.62 / collision 0.42**, maple
4.12 / 0.49, birch 2.56 / 0.35, pine 2.25 / 2.12, bush_05 2.4 / 1.65,
bush_01 1.30 / 1.20, rock6 0.63 / 0.63. The gap between the two columns on a tree
is the entire reason there are two.

- `PropFootprint.Collect` (the minimap's path) still gathers and rasterizes in
  one go against a shared buffer, so a chunk load allocates nothing.
- **`Node3D.GlobalTransform` is only maintained inside the tree** and silently
  reads as the local transform outside it, which measured every prop at its own
  origin (everything came back a 2×2 block). `Measure` threads the transform down
  the hierarchy instead.

### Jitter and scale

- **Position** — up to `positionJitter` metres off the column centre, and the
  footprint is rasterized AT that pose, so the coverage claim stays exact.
- **Scale** — uniform, and **upward only** (`1 .. 1 + scaleJitter`). The footprint
  is measured at 1, so growing a prop can only make it cover *more* than was
  claimed, and over-covering is the harmless direction for a barrier; shrinking
  would let a column the map calls blocked come back open. Carried on
  `EntitySimState.Scale` (`WorldFile` v53) and applied as a **multiplier** on the
  scene's own root scale, since an imported FBX is authored at 0.01.
- **Yaw** — free, in radians. It was quantized to eight steps only so a footprint
  could be cached per `(scene, yaw)`; a measured `Shape` rasterizes at any pose,
  so there is nothing left to quantize for.

### Density, and not making the band obvious

`DensityAt` is 1 inside `barrierDepthMeters` and falls to `interiorDensity` over
`densityRampMeters`, smoothstepped. The ramp is the point: a hard switch draws a
line around every region at exactly the band depth, which is the tell that gives a
painted wood away.

**`maxSpacingMeters` caps what the widest prop may reserve, and it is a cap
rather than a taste knob.** Without one the biggest thing in a list monopolises
the region: at `canopySpacing` 1.2 an oak reserved 5.5 m and pushed every birch
8.6 m away, so eight oaks blanketed a 360 m² wood and nothing else fitted in it.
Capping compresses the range instead of flattening it — everything under the cap
still scales with what it draws.

**The cap is also the tree-count dial, and the arithmetic is unforgiving.** A
reservation of `r` puts trees `2r` apart, which is about one per `π r²` of
region: 3.0 m gives one tree per ~31 m², 2.5 m one per ~22 m², 2.0 m one per
~14 m². A 360 m² painted wood holds eleven trees at 3.0 and twenty-six at 2.0.
"Not enough trees" and "trees too close together" are the same dial read from
opposite ends.

**`minSpacingMeters` is a floor under every class's spacing.** Without it the
smallest things in a list space themselves at under a metre, which is no spacing
at all on a 1 m grid — every pass then lays one pebble per free column. Measured:
it took the small rocks in the test region from 63 to 45 with coverage unchanged.
It does not trade against sealing, because the seal pass runs afterwards and
ignores spacing.

### Variety

`ChooseInClass` weights each scene by `weight / (1 + varietyPressure × usedInThisChunk)`.
Without it a patch small enough to take in at a glance is mostly whichever scene
the weights favour, however many the author put in the list. Measured on the test
map: **all 21 scenes of the forest list placed**, none dominating.

### Nothing reaches outside the painted region

Every column a footprint covers must be painted on that layer, or a rock ends up
in the road beside the wood. Painted, not placeable — a lake or a paved square
inside the region is still inside the region the author drew, and refusing to
reach across one would open a lane at every puddle. `PaintedPropIndex` treats
out-of-bounds as unpainted; clamping let a footprint "fit" by reaching off the map
onto a copy of the border column.

**Refusing a placement is not the same as marking a column `NoFit`.** The seal
pass tries a weighted pick per size bucket first, and only if every one of those
is refused does it sweep EVERY scene, widest first, un-jittered and with the
taste rules off. Without that second sweep a column can come back "too tight"
while something in the list would have sat in it — one pick per bucket samples a
handful of a list, and a jitter is exactly what pushes a rim footprint over the
region's edge. Measured on the merged test map: 95 such columns, then 0.

## Seating on slopes

`PropSeatY(px, pz)`. A flat column's drawn top is half a voxel above the surface
voxel's top face (the mesher's shallow-Y smoothing) — that is `PropSurfaceLift`
1.5. On a grade the mesher averages the cell's edge crossings instead, so the
surface runs as a plane through the column and the flat anchor floats a prop off
its downhill side.

The seat is the mean of the facing surfaces inside the grade window (a neighbour
outside it is a wall and is left out, or a clifftop prop is dragged down the
drop), clamped twice:

- **Never above the flat anchor** — rising ground buries a prop rather than
  leaving a gap under it.
- **Never more than `PropMaxEmbed` (0.5) below it** — `floor(Y)` is the cell
  `PathBlockerRasterizer` marks blocked, so half a voxel lower is the last seat
  that still marks the AIR cell a mob walks through. Deeper marks the solid voxel
  and the barrier stops blocking anything.

## No-spawn

`CanSpawnAt` refuses any column in a painted BLOCKING region (`InBlockingRegion`).
One gate covers the painted mob layer, spawn entries' own column probe
(`SpawnContextForBake.IsValidColumn`) and the map's mob dots.

- The WHOLE region, not just columns a prop stands in: the interior is sparse
  *because* nobody can reach it, and a mob spawned there is walled in for the
  life of the world.
- The breakable layer does not count — counting it would sterilize every meadow.
- Nothing is needed at runtime: `NightMobSpawner` and `FairySpawner` both pick
  from `NavigationGoals.CollectReachableStandableCells`, and props block the nav
  grid through `PropSimState.GetPathBlockerCells`, so a sealed interior is
  already unreachable.

## Export, caches, determinism

**Nothing about the fill is stored.** The raster holds only which list covers a
column; which props that takes is derived afresh every time it is asked. So
changing a prop list or a collider needs no edit to the document — it needs the
answer recomputed, and re-saving the map is the whole workflow.

- `WorldMapState.RefreshPropAssets()` re-reads the lists and their scenes with
  `ResourceLoader.CacheMode.Replace` and drops the caches. A plain `Load` hands
  back the copy already in memory and re-measures the old collider just as
  faithfully. Called by `SaveAndBake`, by `WorldMapData.BakeToWorldFile`, and on
  opening the painter.
- `_propFills` is dropped wholesale by `InvalidatePropFill()`, called from
  `InvalidateHeights` / `InvalidateAllHeights` / `InvalidateVoxelEdits` — the
  fill reads the rasters, the terrain and the placement list, so almost any edit
  can change it. It is locked: the bake runs on a worker thread while the painter
  draws on the main one.
- The fill is a pure function of (rasters, terrain, placements, lists, measured
  collision, chunk coords). Same inputs, same world.

## What the map draws

**The painted raster, not the resolved fill.** One semi-transparent dot per
painted column, a little smaller than the metre cell so the ground shows between
the marks: black 0.8 at 0.8 of the cell for blocking, white 0.5 at 0.65 for
breakable, all on `WorldMapInkData`.

Drawing the fill live was tried and reverted twice over. A wash plus a firmer
mark per prop read as a patchwork of one blob per entity; and drawing coverage at
all made a stroke come back patchy where the fill had thinned the interior,
answering a question the author had not asked while painting. What is authored is
the REGION. How many props that becomes is a number, and `worldmap_check` is
where a number belongs.

This also means the prop layers deliberately break the *preview == bake* invariant
the mob layer still holds — see `../CLAUDE.md`'s verification section.

## Verifying

`--headless -- "worldmap_check res://.../world_map.tres"`, ~9 s, self-quitting.
The `props:` line:

```
props: blocking 362 columns painted, 345 blocked by 185 props, 8 interior,
0 too tight for the list, 0 uncovered (must be 0); breakable 130 painted,
10 blocked by 7 props
```

Where that came from on the test map, at each step of the rewrite:

| | props | trees | small rocks |
|---|---|---|---|
| the old packed fill | 231 | — | — |
| size-ordered passes, greedy | 238 | 76 | 63 |
| + `minSpacingMeters` floor | 227 | 76 | 45 |
| + order-free placement | 195 | 48 | 37 |
| + the authored large bushes | **185** | 48 | 37 |

Then the breakable layer was merged into the blocking one (1467 columns painted,
four lists), and the two overlap rules went in:

| | props | too tight | uncovered |
|---|---|---|---|
| overlap allowed | 1041 | 0 | 0 |
| + no collision overlap | 1178 | 0 | 0 |
| + `understoryRatio` | 1151 | **45** | 0 |
| + taste off in the seal's last resort | 1192 | 0 | 0 |
| + sum-of-reservations, cross-seam | 1223 | 0 | 0 |

Then the tree count itself, which that spacing had cut to 30 in 1467 m² — with
`tree_birch` never placed at all:

| | trees | 1 tree per |
|---|---|---|
| sum-of-reservations as first written | 30 | 48.9 m² |
| + canopies never bucketed with masses, + `spacingRounds` | 40 | 36.7 m² |
| + `maxSpacingMeters` cap | 73 | 20.1 m² |
| + buckets keyed on the capped reservation | **73**, every species placed | 20.1 m² |

The 45 is the lesson: an aesthetic rule that can refuse a placement must be
switched off wherever coverage is the contract, or it punches holes in barriers.

At the end of that: **0 pairs of trees anywhere on the map closer than their
reservations**, and 16 pairs of anything-against-a-tree, all of them last-resort
seal placements inside the barrier band. The band is packed on purpose — that is
what a barrier is — so props there do stand closer than they reserve.

Resident node count is the game's known FPS ceiling (`node_census`), so those
numbers are the budget this system spends.

## Open

1. **The seal pass is still greedy and chunk-local.** Its props are small and its
   seam artifacts are minor, but it is the last part of the fill that a chunk
   boundary can change.
2. **Coverage is column-centre, disc-free.** Three mutually adjacent covered
   columns still leave a curved gap between the actual colliders. The player
   capsule is 0.25 m in radius, so a 0.5 m diagonal seam is passable in
   principle; a mob's own radius covers for it today.
3. **`NoFit` is still a cliff.** A region too narrow for anything in its list
   comes back empty there rather than degrading.
4. **No clustering.** Species are picked per column with a variety bias, so a
   stand of one species does not happen. A low-frequency patch hash over a
   sub-palette is the shape of the fix.

Anything that changes the fill must keep: the per-chunk replayability, the pure
hash (no sequential `Random`), the order-free spacing test, the
fit-inside-the-region rule, the `uncovered == 0` invariant for the edge band, and
the "nothing is stored, it is derived at export" property.
