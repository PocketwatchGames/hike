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
| Resizing and rotating: `worldmap_resize` (rescale) vs `worldmap_canvas` (extend), and `worldmap_rotate` | [docs/resize.md](docs/resize.md) |
| The host `WorldMapPainter` - palette families, placements, the entity inspector panel, subscene stamps, entity marks, paving | [docs/host.md](docs/host.md) |
| Painted prop regions - the packed fill over measured collision, interior clearings, slope seating, no-spawn | [docs/prop-fill.md](docs/prop-fill.md) |
| Carving and building (`VoxelEditTool`) | [tools/CLAUDE.md](tools/CLAUDE.md) |
| Undo / redo | [undo/CLAUDE.md](undo/CLAUDE.md) |
| Ink and brush tuning (`WorldMapInkData`, `WorldMapBrush`) | [../data/world_editor/CLAUDE.md](../data/world_editor/CLAUDE.md) |

## Model: document + bake (not direct-voxel paint)


The authored source of truth is **`WorldMapData`** (`scripts/data/worldmap/`,
a plain `Resource` — deliberately NOT `[Tool]`, or the editor strips every
typed reference it holds on save; see the note on the class) — bake settings
(world extent in chunks, default sea level, height scale). The layers are NOT
referenced: they are ONE binary file, `layers.bin`, at a fixed name beside the
`.tres` with `placements.tres` and `palettes.tres` (`WorldMapData.LAYER_FILES`),
so a document is its folder and a copied folder is an independent world.
`layers.bin` holds every raster layer and the voxel layer as ONE independently
compressed section per chunk column — everything the document says about those
16 x 16 columns — so an edit with any tool changes only the sections of the
chunk columns it touched and git deltas the rest; ~110 KB for antica, ~140 ms to
save, ~110 ms to load. Exact-version: a file of another
version, or with a layer missing or unknown, is refused by name. See
`WorldMapData.LoadLayers`. The painter opens one through a file dialog, never a
folder search — see [docs/host.md](docs/host.md). It mirrors the `VoxelAtlasManifest` convention: one editor-visible
resource of record + a re-runnable bake (`BakeToWorldFile`, pure C# so it runs
headless).

Layers:
- **Elevation** — `Rf`, per voxel **column**, in **voxels relative to
  `seaLevel`, signed** (negative digs seabed).
- **Water** — PER VOXEL, in the voxel layer (bits 16-23): 0 none,
  1 the zone's water, n the water-type slot n - 2. **The layer is the whole answer
  — there is no waterline rule folded in.** A new document is prefilled with the
  zone's water up to `seaLevel`; land hides the water it stands in, and the bake
  fills a voxel with water only where it comes out empty. Per voxel, so a column
  can hold several bodies (a pool in a cave under a hill, a lake over a dry cave),
  each with its own level and type. See [docs/water.md](docs/water.md).
- **Region** / **Zone** — `R8`, per **chunk** (index → `ChunkState.RegionIndex` / `ZoneIndex`).
- **Habitat** — `R8`, per **chunk** (slot + 1; 0 = none →
  `ChunkState.HabitatIndex`, same encoding). What LIVES in a place — fairy
  chance and breakable/creature loot — painted apart from the zone so one look
  can host different wildlife. Per chunk because that is what the world
  records; a passage shares the habitat of the chunk column it is carved in.
- **Wind** — `Rgba8`, per **chunk**: R = compass angle over a full turn,
  G = strength, with **0 reserved for UNPAINTED**. Per chunk because that is the
  granularity the bake seeds `ChunkState`'s wind-velocity subgrid at.
- **Props — one layer**, `props_blocking`, `R8`, per column (prop
  list index + 1; 0 = none), indexing the `props` palette. No density
  channel and no spacing: the bake FILLS a painted region until every column of
  it is inside some prop's collision, because a painted region is a barrier and
  anything thinner leaves lanes through it. There used to be a second
  ("breakable") layer over the same palette; whether a barrier can be CLEARED is
  decided by what the scenes in the list are built out of, so the two rasters
  could not state a difference and the pair is now one.
- **Mobs** — `Rgba8`, per column (R = set index + 1, G = density
  multiplier), indexing `mobSets`. Still a rate: a mob set is a
  `SpawnScatterData` over a `SpawnListData` whose rows carry their own
  square-metres-per-spawn.
- **Ground** — the same split: the `ground` layer (`R8`, terrain kit + 1; 0 =
  `defaultGround`) for a column's surface, and a per-voxel kit override (voxel
  layer bits 24-31) on a floor under something. The bake carries a floor's kit
  down through the solid run beneath it, so a cliff under a painted floor matches.
- **Paving** — two kinds, split the way an entity's `floorY` is. A road on a
  column's SURFACE is the `paving` layer (`R8`, block + 1) and follows the ground as it
  is reshaped; paving on a floor with something over it is a REPAVED voxel
  (`EditRepave` in the voxel layer) at that height. One surface road plus any
  number of repaved floors per column.
- **Scalars** — `Rgba8`, per column: R = mob level, G = climb route flag,
  B = cliff roughness.
- **Placements** — `.tres` (`WorldMapPlacements`): the subscene stamps (each
  with its own forks of its scene's entities, `entityOverrides`), the
  hand-placed entities, and the player spawn. Not a raster: a per-column byte
  cannot hold "this scene, facing that way", nor two of them overlapping, nor a
  footprint that moves as one thing.
- **Voxel layer** — a per-voxel `uint[px,ly,pz]`, each column run-length
  encoded inside its chunk column's section. Low 16 bits: the EDIT in the low two (0 untouched,
  1 carved away, 2 added, 3 repaved) and, on a carve, the **passage** it belongs
  to — its danger level, scatter set and density — or, on an add or a repave, the
  **block** (a `BuildingBlocks` slot + 1; 0 on an add = the column's ground
  terrain). High 16 bits, independent of the edit: the voxel's **water** (16-23)
  and **terrain kit override** (24-31). See "Passages" below.
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
file with nothing checking they agreed — and only the terrain palette had a backstop
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
| `WorldMapBake` | how it becomes a `WorldState` / `.hike` | the `WorldState` under construction, the terrain-slot binding, the four-stage driver |

The boundary is enforced by what each file compiles against, and it is worth
checking after any change here: **`WorldMapState` and `WorldMapBake` do not
name `WorldMapInkData` at all.** A bake pass therefore cannot read a display
value even by accident.

**Spawn resolution belongs to the MODEL, not to the bake** — `PropOriginAt`,
`PropCoversAt`, `PreviewMobAt`, `AreaRoll` and the salts. They answer "what does
this document say stands at this column", which is a property of the map. The
bake PLACES what the model resolves and the map preview DRAWS what the model
resolves, which is exactly why the two cannot disagree; filing the resolution
under the bake is what made that look like shared mutable state needing a
redesign. Same argument for stamp plans: `Plan`'s per-voxel colours come from
`BlockData.minimapColor` — the subscene and the block catalog, not the painter's
palette — so plans stay on the model and only the compositing is `WorldMapInk`'s.

The bake's whole external surface is `Bake`, run on the main thread of its
OWN process: the painter's Ctrl+S launches a headless `worldmap_bake`
(`WorldMapBakeProcess`) that outlives the window, and a re-save kills and
restarts it — see [docs/state-and-bake.md](docs/state-and-bake.md).
`BuildWorld`, `StampColumns` and `RescatterColumns` have no callers outside it. Members the bake reaches on the model are `internal` rather than
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
| `WaterTool` | each painted column's water surface AND its water type (RMB removes) | `SurfaceVoxels` (R/F, signed; alt+click samples), type (option row), `ReplaceOnly` (**X**) | water shaded by depth, dry land dimmed — **cuts away** (Q/E), so water can be painted inside a passage |
| `TunnelTool` | LMB carves the box ABOVE `PaintY`, which is the floor left behind; RMB erases the carve inside the same box | `PaintY` (R/F, the floor), `Height` (T/G, the ceiling) | `CutawayElevationView` — the elevation map cut at `view.CutawayY` (Q/E): the highest floor under the cut in its own band, dithered where seen through rock |
| `BlockTool` | the same box, LMB filling it DOWN to `PaintY`, the new surface — or, in wall mode, `Height` voxels stood on each column's ground | the same, plus the block (option row; "Ground" = the column's terrain) and `WallMode` (**X**) | the same view, built blocks in their `minimapColor` |
| `RegionTool` | per-chunk region index | `RegionIndex`, named in the option row | region colours, **50% darker under water** |
| `HabitatTool` | per-chunk habitat (RMB clears to none) | `HabitatIndex`, named in the option row | habitat colours, unpainted grey, **50% darker under water** |
| `ZoneTool` | per-chunk zone index | `ZoneIndex`, named in the option row | zone colours, **brightness by elevation** |
| `WindTool` | per-chunk wind direction + strength (RMB clears back to the zone's) | `Mode` (Stroke / Inward / Outward), `AdjustLevel` = strength in m/s; alt+click samples | hue = compass angle, a sawtooth ramp ALONG the flow, unpainted chunks flat grey |
| `PaveTool` | a block on the floor the map is SHOWING — the surface, or a passage's floor under the cut | `BlockIndex` | `CutawayGroundView` — the ground map, **cutting away** (Q/E) once the plane comes down |
| `PropPaintTool` ("Blocking") | which `PropListData` fills a column (RMB clears, alt+click samples) | `ListIndex` | ground colour, a dot per painted column in that list's own `mapColor` — black for a barrier, mid-grey for a breakable one |
| `ClimbTool` | climbing route on a column's walls | none | `CutawayElevationView`, routed edges inked magenta — **cuts away** (Q/E), so a route can be painted on a passage's walls |
| `SceneTool` | `.hikescene` stamps — place / select / move / rotate / delete; seat follows the ground or is pinned (`fixedY`: X, shift+R/F, alt+click) | `SceneIndex`, `Selected` | `CutawayGroundView` — **cuts away** (Q/E), so a stamp can be placed in a passage (the stamps themselves draw on EVERY view) |
| `MobLevelTool` | per-column danger level; with the plane lowered, the level of the PASSAGE under the cut | `Level` | terrain recoloured, one shade per level — **cuts away** (Q/E) to shade passages |
| `MobTool` | which `SpawnScatterData` supplies a column's wildlife + density; with the plane lowered, the PASSAGE's scatter; the inspector lists the set's entries at `n / km²` | `SetIndex`, `Density` | ground colour + a dot per mob spawn, the SELECTED set full weight and the rest dimmed — **cuts away** (Q/E), dotting the passage scatter |
| `EntityTool` | individual entities, their per-placement properties, and the player spawn; a STAMPED SCENE's entities are selectable too and edited for that stamp alone, never moved or deleted | `PaletteIndex`, `Selected`, `SelectedScene` | the ground map (the marks themselves draw on EVERY view that shows props, but only on the slice their floor is on; a scene's entities in `sceneEntityInk`) |
| `TriggerTool` | trigger boxes (`TriggerSpawnEntry`, own list `WorldMapPlacements.triggers`): drag to draw, drag inside to move, drag the border ring to resize, R/F height, shift+R/F depth, RMB delete (the box gestures are `BoxPlacementTool`'s, shared with bomb walls) | `Selected` | `CutawayGroundView`; every box draws as an outline on EVERY view (`triggerInk`), full strength under this tool |
| `BombWallTool` | bomb walls (`BombWallSpawnEntry`) as voxel boxes — ordinary ENTITY placements of the palette's `bomb_wall`, so the panel, hooks and the entity tool's selection apply. Same box gestures; R/F moves the top, shift+R/F the bottom; a new box stands on the floor shown under the press (or hangs from the plane over rock). The box is DERIVED from `cells` (`WorldMapState.BombWallBox`); `floorY` is always absolute, facing Deg0. The entity tool selects/deletes a wall but never moves, aims or point-places one. `worldmap_check` ERRORs a wall with no exposed rock, one in/beside water, and two sharing a cell | the palette's bomb-wall entry | `CutawayGroundView`; boxes on EVERY view (`bombWallInk`), drawn while any of the box is at or below the cut |

A spawn brush writes only its raster; `RescatterColumns` resolves it during the
bake. The two resolutions are not the same shape and should not be made to
match:

- **Props are placed SIZE-ORDERED over their own measured collision** — one pass
  per size class, largest first (`PropFootprint.Shape`, shared with the minimap),
  then a seal pass covers the region's edge band with the widest COLLISION that
  fits. Coverage is the BAND's contract and only the band's — `worldmap_check`
  reports `uncovered (must be 0)` — while the interior is furnished to
  `interiorDensity` and no more, because nobody can reach it. **Two props stand
  at least the SUM of their reservations apart** (drawn radius × `canopySpacing`),
  with `understoryRatio` waiving that for something far smaller so undergrowth is
  still possible; a single radius cannot express this and put big trees and small
  ones the same distance apart. Both WHERE a prop stands and what has reserved
  ground near it are decided ORDER-FREE over a padded halo — checking against a
  chunk's own placements is blind at its seams, which is where 46 of 46 remaining
  violations were. Nothing about the fill is stored — it is derived at export
  from the lists and the collision as they are on disk, so a widened trunk
  reaches the world by re-saving the map. Read
  [docs/prop-fill.md](docs/prop-fill.md) before changing any of it; it carries
  the invariants, the measured numbers and the rejected alternatives.
- **Mobs still run worldgen's own rate math** (`SpawnScatterData` rows at their
  authored area rates), gated by `CanSpawnAt` — which now also refuses any column
  inside a painted BLOCKING region, so nothing is spawned walled in behind a
  barrier. Every decision is a hash of the column rather than a running `Random`,
  which is what lets the map preview reach the same answer without replaying the
  pass.

### Triggers: boxes that fire script events

**A trigger is an entity placement in its OWN list** (`WorldMapPlacements.triggers`),
so the entity tool, its marks, its palette and its hit test never see one and
only the sites that must (bake, undo, rotate, resize, check, drawing) do. Like
the scene editor's roofs it has no palette file: the tool authors the whole
`TriggerSpawnEntry` per placement (`source` null, `custom` the box).

- **`anchorXZ` is the box's MIN CORNER**; the size is on the entry. The bake
  seats it on the floor under its CENTRE (`SeatCell`) and spawns it AT the centre,
  which is where `TriggerSpawnEntry` recovers the box from and what makes the
  entity stream with the chunk most of the box is in.
- **It fires on OCCUPANCY**: enter when the first admitted actor steps in, exit
  when the last leaves. `actors` admits party kinds and mob TEAMS
  (`Mob.ActorTeam`; the tamed companion is only ever `Companion`). Mobs are
  tested every `SimData.triggerMobScanSeconds`, and only while a resident trigger
  admits them; the party every tick (`Sim.TickTriggers`).
- **Effects**: set a Bool variable, run `ConversationAction` files (picked, like
  conversation cells), and — for a NAMED box — `WorldScriptData.OnTriggerEntered /
  OnTriggerExited`. `Once` state (`EnterFired`/`ExitFired`) is saved through the
  chunk content diff; who is inside is runtime only, so an actor already standing
  in a box when it streams in counts as entering.
- `worldmap_check` lists every box and warns about one that admits nobody or does
  nothing with no name for a script.

### Placement hooks: what an entity runs when it raises an event

**A hook is per-placement typed text**: `SpawnEntryData.hooks`, a list of
`EntityHook` (`hookEvent`, `commands`, `once`), authored on a placement's fork —
or on a stamped scene's override of one of its entities, which is how one
bridge's portcullis sets a flag and the others do not. `commands` is the
conversation sheet's action-cell grammar (`ActionCommand`):
`var:antica_bridge_opened=true; start_quest:find_her; some_action_file`.

- **An entry offers hooks only if it raises something** (`HookEvents`; doors:
  Open, Close, Unlock), and the panel's event dropdown lists only those
  (`NarrowEnumHint`). Raising one is `Sim.RaiseEntityEvent` from the entity's own
  code path — `SetDoorOpen` (player or script) and `Door.Unlock` today.
- **Commands are resolved AT SPAWN to paths and ids** (`EntityHookCommands`),
  and that resolved form is what the state carries and the `.hike` stores in the
  common entity trailer, with each hook's fired flag. Nothing scans for a name
  after the bake.
- **A placement has no speaker**, so `npcvar:`, `spawn_loot:`, `behavior:`,
  `reappear:` and any action file whose `ConversationAction.NeedsSpeaker` is true
  are refused; `teach:` is refused because a lesson's identity is its source FILE
  and a hook is not one. A refused command is dropped at bake with an error; the
  panel shows the first problem under the field as it is typed
  (`IValidatedRecord`), and `worldmap_check` lists every hook and ERRORs on each.

### Passages: danger and scatter underground

**A passage's danger and wildlife live ON THE CARVE, not in a per-column
layer**, because two passages stacked in one column must be able to differ. Each
carved voxel packs, beside its edit bits (`WorldMapState.Tunnels`):

| Bits | Field | 0 means |
|---|---|---|
| 2–5 | level + 1 | the surface's level at this column |
| 6–11 | scatter slot + 1 (the same ledger slot the `mobs` layer stores) | nothing scatters here |
| 12–15 | density, 15 steps | — |

So `mobLevelCount` is capped at 15 and a passage can name scatter slots 0..62.
Read and write through the accessors (`PassageLevelAt`, `PassageScatterAt`,
`SetPassageLevel`, `SetPassageScatter`); the packing is private.

- **Painted with the Danger and Mobs tools, under a lowered cutaway.** Each
  paints what the map shows: a column whose exposed floor has carve over it
  (`CutawayPassage`) sets that whole carved run; anywhere else is the surface
  layer as usual. The plane picks between stacked passages. A floor seen through
  rock IS paintable — unlike the erase, this destroys nothing. Passage levels
  are whole and hard-edged, never a soft gradient.
- **A fresh carve takes a carved neighbour's passage** (`SetVoxelEdit`), so
  extending a tunnel extends what lives in it; re-carving keeps it. Undo, resize
  and RMB-erase move whole cells, so the passage travels with its voxels.
- **Scatter**: the bake visits every passage floor in a column
  (`PassageFloorBelow`, a solid voxel with carve over it) and rolls that
  passage's rows on `PassageHash`, salted with the floor's Y so stacked floors
  roll independently. `PassageSpawnSetAt` is the passage's half of `CanSpawnAt`
  — dry, two metres of headroom, unpaved, clear of a stamp's volume — and the one
  gate the bake and the preview share. The surface pass never reaches a passage:
  `CanSpawnAt` wants the column's top to be the painted ground.
- **Danger at bake**: `MobLevelAtWorld(pos)` reads the passage of the voxel a
  spawn stands in, else the surface field — so scattered mobs, hand-placed mobs
  and forges in a passage all get its level.
- `worldmap_check` reports carved voxels by passage level and set, and passage
  floors as total / with a scatter / spawnable / previewing a spawn. A big gap
  between "with a scatter" and "spawnable" is floors the gate refuses — flooded,
  crawlways, paved, under a stamp.

**Bucket fill is the HOST's, the region rule is the tool's.** Holding **B** at the
press (`WorldMapCanvas.FillKey` → `EStrokeMods.Fill`) makes it a fill. With LMB it
paints, and with RMB it erases. The press ends there and never becomes a stroke.
A tool answers `IWorldMapTool.Fill` with a `WorldMapFill`: `KeyAt` (4-connected
cells with the seed's key are one region), `Write` (one cell) and `CellSize`
(`ChunkState.SIZE` for the per-chunk region/zone/habitat layers). The painter
floods, snapshots the bounds as one undo step and repaints, so a tool cannot get
connectivity or undo wrong. **The flood is gathered in full before anything is
written**, because `Write` changes the keys it compares. Tools with a fill:
region, zone, habitat, blocking, paving (key = floor Y + paving there + ground
kit), danger and mobs (where a passage under the cut is a different region from
the surface beside it), and elevation in Flatten / FlattenSoft only (key = the
snapped height that shift-constrain compares). Any other tool, or op, returns null
and the press does nothing.

`WorldMapBrush` (`Resource`) is the shared, layer-agnostic stamp engine
(falloff/flow/noise + `Stamp(center, radius, w, h, apply)` callback); each tool
supplies its own radius and per-texel write. Add a new tool by implementing the
two interfaces and appending it to `WorldMapPainter._tools`; if it needs a
palette, declare it in `AuthoringPaletteSource.Table` (see below) rather than
adding an array to `WorldMapData`.

## Verifying a change to the painter


**`worldmap_check` is the loop** — `--headless -- "worldmap_check
res://path/to/world_map.tres"`, ~5 s, self-quitting. It opens a document's layer
images (no world built, no `.hike` written) and reports what the bake would make
of its water and which cascades it would file. Extend it rather than reaching for
a bake: every question about a painted document that does not involve voxels can
be answered here.

Four invariants are worth re-checking after any change to placement or to how the
map is drawn. Each was a real bug, and none of them was visible by reading:

- **Preview == bake, for the MOB layer.** Count columns where
  `PreviewMobAt(px, pz) >= 0` and compare against the columns that actually
  received entities after `BuildWorld`. It must be **zero disagreement, not
  merely equal totals** — the totals matched while 665 columns disagreed. This is
  what `CanSpawnAt` being the single gate for both the dots and the scatter buys,
  so a new eligibility rule goes THERE and nowhere else.
- **The PROP layers deliberately do not work that way.** Their dots are the
  painted raster, not the resolved fill: what an author paints is the region, and
  which props that becomes is worked out at export. Drawing the fill live made a
  stroke come back patchy — interior clearings and columns nothing fit — and made
  every frame build per-chunk fills to answer a question nobody had asked. The
  resolved numbers are `worldmap_check`'s job.
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
## Palettes: a directory, not a list


**Nothing is registered by hand. A resource becomes paintable by existing in the
right directory.** `AuthoringPaletteSource.Table` is the one place a palette is
declared — the directories it is scanned from (or the block-catalog filter it
uses) — and it is the only file to touch when the painter should offer a new
KIND of thing.

| Palette | Comes from |
|---|---|
| Zones | `world_authoring/zones/` |
| Regions | `worlds/shared/regions/` |
| Habitats | `resources/data/habitats/` — runtime data, read by path from the baked world |
| Ground | `world_authoring/terrain_kits/` |
| Props | `world_authoring/props/` |
| Mobs | `world_authoring/spawn_scatters/` |
| Presets | `world_authoring/presets/` |
| Entities | `world_authoring/spawn_entries/` (recursive — the folder is the section: `mobs/goblin/`, `props/furniture/`) and `worlds/shared/spawn_entries/npcs/` — one row per FILE; what a buried spot holds, what a stone teaches, what a chest contains is still set on the placement (see [docs/host.md](docs/host.md)) |
| Water | the block catalog, every block whose `render` is `Water` |
| Blocks (paving AND block edits — one slot numbering, stored by both the `paving` layer and the voxel layer) | the block catalog, every solid block with a top surface |

Four rules, three of which were real bugs:

- **A palette whose index is painted is a WIRE FORMAT, and its slots live in a
  LEDGER.** The `zone`, `region`, `habitat`, `ground`, `props_blocking`, `mobs`
  and `paving` layers and the voxel layer's water and kit bytes all store a slot number, so a slot that moves
  silently re-zones or re-textures every world already baked — with the stored
  bytes still perfectly valid. Discovery therefore only ever APPENDS to
  `WorldMapPalettes` (`map/palettes.tres`, beside `layers.bin`), never
  reorders, and a slot whose file is gone keeps its index as a named dead slot.
  Same rule and same reason as `TerrainPaletteData`.
- **A FREE palette has no ledger at all.** Nothing stores an index for entities
  (an `EntityPlacement` holds its entry by reference) or presets (a composite
  brush stroke that is never written down), so those are simply what is on disk
  in name order.
- **The ledger is its own file because the painter WRITES it**, like
  `WorldMapPlacements` and for the same reason: `WorldMapData` is a resource the
  Godot editor may have open, and rewriting it from a running game is how
  `genData` got stripped twice.
- **The entity palette is one row per entry FILE, and its folder is its
  section.** Its root is scanned recursively (`PaletteRoot.Recursive`):
  `spawn_entries/mobs/goblin/goblin_swamp.tres` lists under Mobs ▸ Goblin, a file
  directly in `spawn_entries/` under Interactives. There are no families — a
  swamp goblin and a crate are each their own file, picked BEFORE placing, and
  what an entry IS (`scene`, `species`) cannot be changed on a placement
  (`SpawnEntryData.IsIdentityProperty`). A prop is a three-line
  `PropSpawnEntry` (scene + prop type) under `props/<category>/`, and
  `worldmap_check` reports any prop scene that has none. The same files are what
  the generator's spawn lists and the scatter sets name, so a palette row and a
  spawn row are one thing. The painter shows the palette as a collapsible tree
  with a text filter (`WorldMapHud.optionTree` / `optionFilter`); Escape clears
  the filter, then gives the keys back, and a press on the map does the same.

`worldmap_check` prints every palette with its slot numbers. That readout is the
only thing that can catch a ledger whose order has moved: a document whose zone 4
stopped being the hub does not error, it bakes a different world.

## Known gaps


- ~~A terrain kit may only name terrains some `genData` zone names.~~ **Fixed.** The
  terrain palette is authored (`WorldMapData.kitPalette`, a `TerrainPaletteData`) rather
  than derived from the zone list, so a terrain kit may name any terrain the palette
  carries whether a zone places it or not. `swamp_highlands`, `swamp_mud` and
  `swamp_village` were APPENDED to it — appending is the one safe edit, since it
  moves no existing slot and therefore re-textures nothing already baked. A terrain
  still absent from the palette bakes as slot 0 and `SlotOf` warns by name; the
  fix for that is to append it, never to add a zone for it.
- **Subscene stamps fill no marker pools.** Worldgen pulls a scene's marker
  placements out before stamping and then fills each `SubscenePlacement.variants`
  pool; the painter stamps them straight through, so a scene authored with a
  pool bakes with its markers still in it and nobody standing on them. Markers
  and path hints only spawn under the world editor, so they are inert in game —
  the loss is the variants, not the stray entities.
- **Path hints register no POIs in a painted world**, and there is no road pass
  for them to be endpoints of — paving is a hand-painted material here, not a
  routed, graded corridor. A painted world's POIs are its NAMED entity
  placements (see [docs/host.md](docs/host.md)).
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
  Selecting a stamped scene's entity and editing it in the panel is likewise
  unexercised by hand: the override is verified to reach the bake (one door of
  one stamp locked, the rest and the shared entry untouched, through a rotation),
  and `worldmap_check` lists every override.
## Not yet (future steps)


**Prop-piece walls** — a VOXEL wall is the block tool in wall mode; this is walls
built from tileable scenes. These ARE a raster: a per-column mask plus a type
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
