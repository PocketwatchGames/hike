# Runtime document + bake (`WorldMapState`)

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
