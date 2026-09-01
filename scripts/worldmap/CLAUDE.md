# World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`, `scripts/data/world_editor/`)

The **first step in the world-authoring chain**: a broad-brush, in-game paint
program that authors a layered raster *document* and bakes it into a real
`WorldState` / `.hike`. The downstream `WorldEditor` does fine per-voxel detail;
the game loads the baked `.hike`.

## Where the detail lives

This file is the map of the painter: the model, the class split, the tool/view
contract and how to verify a change. Everything below it is reference for one
part, kept out of here because it is ~1,800 lines that a task touching one tool
does not need. **Read the linked file before changing that part** - each one
carries invariants that are not repeated here.

| Topic | File |
|---|---|
| `WorldMapState` - the mutable runtime document, its layer queries (`TerrainHeight`, `WaterSurface`, `Underwater`, `SolidAt`, `SurfaceBelow`...) and the bake | [docs/state-and-bake.md](docs/state-and-bake.md) |
| The elevation model - signed around sea level, snapped to the interior lattice | [docs/elevation.md](docs/elevation.md) |
| Painted water - a brush and not a fill, and why there is no waterline | [docs/water.md](docs/water.md) |
| How the map is drawn (relief + step outlines), and what a rebuild costs | [docs/drawing.md](docs/drawing.md) |
| Resizing: `worldmap_resize` (rescale) vs `worldmap_canvas` (extend) | [docs/resize.md](docs/resize.md) |
| The host `WorldMapPainter` - palette families, placements, the entity inspector panel, subscene stamps, entity marks, paving | [docs/host.md](docs/host.md) |
| Carving and building (`VoxelEditTool`) | [tools/CLAUDE.md](tools/CLAUDE.md) |
| Undo / redo | [undo/CLAUDE.md](undo/CLAUDE.md) |
| Ink and brush tuning (`WorldMapInkData`, `WorldMapBrush`) | [../data/world_editor/CLAUDE.md](../data/world_editor/CLAUDE.md) |

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
## The classes: the map, the ink, the bake, the view, and two caches


**One model, two consumers, and neither consumer is reachable from the model.**
`WorldMapState` was 3,058 lines doing all three jobs, and that is what let a
display value become a dependency of a headless check.

| Class | Is | Holds |
|---|---|---|
| `WorldMapState` | the document — layer images, placements, tunnels, load/save/mutate, and every query derived from them | the layers |
| `WorldMapInk` | how it is DRAWN | the only `WorldMapInkData` in the painter |
| `WorldMapBake` | how it becomes a `WorldState` / `.hike` | the `WorldState` under construction, the kit-slot binding, the four-stage driver |

The boundary is enforced by what each file compiles against, and it is worth
checking after any change here: **`WorldMapState` and `WorldMapBake` do not
name `WorldMapInkData` at all.** A bake pass therefore cannot read a display
value even by accident.

**Scatter resolution belongs to the MODEL, not to the bake** — `TreeAt`,
`GrassAt`, `ChunkScatterCells`, `AreaRoll` and the salts. They answer "what does
this document say stands at this column", which is a property of the map. The
bake PLACES what the model resolves and the map preview DRAWS what the model
resolves, which is exactly why the two cannot disagree; filing the resolution
under the bake is what made that look like shared mutable state needing a
redesign. Same argument for stamp plans: `Plan`'s per-voxel colours come from
`BlockData.minimapColor` — the subscene and the block catalog, not the painter's
palette — so plans stay on the model and only the compositing is `WorldMapInk`'s.

The bake's whole external surface is four entry points (`Bake`, and the
`BakeBuild` / `BakeStampOccluders` / `BakeRelightAndWrite` split a worker thread
drives); `BuildWorld`, `StampColumns` and `RescatterColumns` have no callers
outside it. Members the bake reaches on the model are `internal` rather than
public: they are the bake's seam, not part of the map's surface.

**Two of the model's parts are caches, not queries, and each is its own type**
(`WorldMapState.Field` / `.Edits`) — because a cache's real contract is its
INVALIDATION, and as loose arrays on the state that contract had nowhere to
live. A path that edits the underlying layer without telling them gets a
silently wrong answer rather than an error:

| Type | Derived from | Owed |
|---|---|---|
| `TerrainField` | elevation + water + roughness layers | `Invalidate(rect)` per edit, `InvalidateAll()` on a whole-layer rewrite (resize, reload, undo restore) |
| `VoxelEditOverlay` | the tunnel mask | `Note(px, pz, wy, edit)` per written voxel, `InvalidateAll()` on a wholesale rewrite |

`WorldMapState` keeps `TerrainHeight` / `WaterSurface` / `InvalidateHeights` /
`InvalidateVoxelEdits` as forwarders, so the call surface the tools and the bake
use did not move.

**Session state is `WorldMapView`, and it is neither the document's nor the
renderer's.** The cutaway plane and the water toggle sat on `WorldMapState`,
where the bake, undo, resize and `worldmap_check` could all see them and all had
to ignore them — and the model never read either, which is the tell that they
were only parked there.

They cannot move to `WorldMapInk` either, and the reason is worth keeping: **the
cutaway plane is an input to PAINTING**, not just to drawing — a carve, a paving
level and an entity's seat are all taken at it. Handing a renderer to `Paint` to
reach it would make the write path depend on the draw path and put the palette
back within reach of an edit, which is the exact inversion this split removed.

So the painter owns a `WorldMapView`, `WorldMapInk` holds one (every view reads
`ink.View`), and the four tool operations that genuinely depend on the working
plane take it explicitly — `Paint`, `BeginStroke`, `LevelText`, `StatusText`.
Model queries still never read it: they take a `clipY` argument, which is what
lets one view cut while another does not.

`TerrainField` is re-entrant through the model by design and must stay so: its
`Caps` pass asks `Map.StandHeight`, which is `max(RawHeight, WaterSurface)` and
so re-enters `Field.Water` mid-rebuild. That is safe only because `EnsureHeights`
clears the dirty rect BEFORE rebuilding, so the re-entrant call reads the
partially-built array instead of recursing. Do not "tidy" that ordering.

**Verifying a change to any of this: bake and compare the `.hike` byte for
byte.** The bake is deterministic, so a refactor that preserves behaviour
produces an identical file — that is what proved this whole split
(`worldmap_bake <doc.tres> <out.hike>`, ~35s, then `sha256sum` against a
baseline). Append `quit` or expect an orphaned process: `worldmap_bake` does not
self-quit, which is the trap the top of this file warns about.
## Tools + views (the extensible part)


Each tool is an **`IWorldMapTool`** and carries its own variables (brush
`Radius`, op, active elevation, cross-section, index...) and its own
**`IWorldMapView`** (`ColorAt(ctx, px, pz)`). The active tool decides BOTH what a
stroke does AND how the 2D map is coloured — switch tool, switch view.

| Tool | Paints | Extra vars | View |
|------|--------|-----------|------|
| `ElevationTool` | elevation (raise/lower/flatten/flatten-soft/smooth/lift/smear) **and** cliff weathering (roughen) | `Op`, `VoxelsPerStroke`, `TargetVoxels`, `RoughenStopIndex`; `AdjustLevel` steps whichever number the op uses | one band per lattice step, eroded heights, water overlaid when `ShowWater` (**W**) |
| `WaterTool` | each painted column's water surface AND its water type (RMB removes) | `SurfaceVoxels` (R/F, signed; alt+click samples), type (1-9 / Q/E), `ReplaceOnly` (**X**) | water shaded by depth, dry land dimmed — **cuts away** (T/G), so water can be painted inside a passage |
| `TunnelTool` | LMB carves the box UP from `PaintY`; RMB erases the whole exposed passage | `PaintY` (R/F), `Height` (Q/E) | `CutawayElevationView` — the elevation map cut at `view.CutawayY` (T/G): the highest floor under the cut in its own band, dithered where seen through rock |
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
  per square metre. Everything is an inverted rate (`squareMetersPerSpawn`) or
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
