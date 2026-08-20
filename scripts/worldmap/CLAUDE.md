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
- **Water** — `.exr` `Rf`, per column, same encoding but never negative
  (0 = unpainted = plain ocean). Since terrain is signed, **sea is made by
  lowering the ground, not by painting water** — this layer is only for water
  held above the waterline, such as a highland lake.
- **Region** / **Zone** — `.png` `R8`, per **chunk** (index → `ChunkState.RegionIndex` / `ZoneIndex`).
- **Props** / **Mobs** — `.png` `Rgba8`, per column (R = set index + 1,
  G = density multiplier), indexing `propSets` / `mobSets`.
- **Ground** — `.png` `R8`, per column (ground set + 1; 0 = `defaultGround`).
- **Scalars** — `.png` `Rgba8`, per column: R = mob level, G = climb route flag.
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
writing into. One bake at a time (`WorldFile`'s shared path table and the
`Blocks`/`KitBlocks` bind are process-global).

**The bake does NOT light the world.** Every consumer of a `.hike` relights on
open — `Main` on both load branches, `WorldEditor` on both its open paths —
because baked light is only as good as the pipeline was at save time, and
`SkyExposure` is not serialized at all, so the format assumes the pass happens on
load. Lighting was ~19s of a ~22s bake and was discarded every time. (A consumer
that ever loads a `.hike` without relighting would get a black world and should
relight, not move the pass back here.) `LightEngine.Relight` keeps its optional
progress callback, which the editor and any future long relight can use. `StampColumns` stamps each column: tunnel-carve → `Air`, else `Terrain`
up to `TerrainHeight`, else `Water` up to `WaterSurface`, else `Air`.

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

**Detail sprites come from the ground too.** The top solid voxel of every column
is stamped with its kit's `defaultDetail` and a strength ramped off
`detailNoise`, the same math as `WorldGen.StampDetailScatter`. They belong to the
ground layer rather than to props because they are part of what the material
looks like up close, not something standing on it — which is also why they live
on `TerrainKitData` and not in a `SpawnSetData`.

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
  white by a fraction of each channel's own headroom — `base + (1 - base) *
  within / N`. The authored colour is the band's BASE, its lowest metre, so it is
  authored at part value: a fully saturated base has no headroom and its metres
  would be indistinguishable. Every step is therefore visible on its own terms —
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
- **alt+drag** — smear: the picked height spread wherever you drag, hard-edged
  under Flatten and feathered under FlattenSoft.

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

**Smooth is not part of that pair.** It blurs toward the neighbourhood average
and has no target at all, so it is elevation-independent: it takes roughness out
of whatever is already there rather than moving it toward a height you chose.

Brush strength is in **voxels per stroke event** (`VoxelsPerStroke`, 0.5) rather
than a fraction of the full range — a motion event used to move ~1.3 voxels, so
a flick crossed tens of voxels. Several events per visible step is the point.

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

## Tools + views (the extensible part)

Each tool is an **`IWorldMapTool`** and carries its own variables (brush
`Radius`, op, active elevation, cross-section, index...) and its own
**`IWorldMapView`** (`ColorAt(ctx, px, pz)`). The active tool decides BOTH what a
stroke does AND how the 2D map is coloured — switch tool, switch view.

| Tool | Paints | Extra vars | View |
|------|--------|-----------|------|
| `ElevationTool` | elevation (raise/lower/flatten/flatten-soft/smooth/lift) | `Op`, `VoxelsPerStroke`, `TargetVoxels`; `AdjustLevel` steps the Flatten target | one band per lattice step, water overlaid when `ShowWater` (**W**) |
| `WaterTool` | per-column water surface ABOVE sea level | `SurfaceVoxels` (0 = shore, never negative) | water blue by depth, dry land dimmed |
| `TunnelTool` | carve at a cross-section | `CrossSectionY`, `CarveHeight` | white=land at slice, grey=2 below, blue=2 above, red=existing carve |
| `RegionTool` | per-chunk region index | `RegionIndex`, named in the option row | region colours, **50% darker in ocean** |
| `ZoneTool` | per-chunk zone index | `ZoneIndex`, named in the option row | zone colours, **brightness by elevation** |
| `ScatterTool` | which `SpawnSetData` covers a column + density | `SetIndex`, `Density` | ground colour + a dot per prop spawn |
| `MobTool` | the same, on the mob layer | `SetIndex`, `Density` | ground colour + a dot per mob spawn |
| `MobLevelTool` | per-column danger level | `Level` | terrain recoloured, one shade per level |
| `ClimbTool` | climbing route on a column's walls | none | the elevation map, routed edges inked magenta |

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

**Climbing routes are the second scalar, and they are AUTHORED, not covered.**
`ZoneGenData.climbCoverage` asks "how much of this zone's rock is climbable" and
worldgen answers it with cellular patches; the painter asks "where is the way
up", which is a route-design question with a specific answer. A coverage field
was built first and is the wrong shape for it: a fraction cannot say *this* wall,
and a patchy face is not a route. So the layer is a per-column FLAG.

The tool paints over the **elevation view, unchanged** — that is the map the
decision is read from — and a routed wall is drawn in `climbInk` (magenta)
**instead of its height ink**, so the tall edge you clicked is recoloured rather
than covered by a mark floating above it. Only columns that own a wall of at
least `climbRouteMinWallVoxels` take the flag, which is the same set of edges the
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
water · **alt+click** pick a height (alt+drag smears it) · **shift+drag**
constrain to that one height · **ctrl+drag** constrain to that height and above
· **wheel** or
**`[` `]`** brush size (proportional step) ·
**ctrl+wheel** zoom (cursor-anchored) · **middle-drag** pan
· **Ctrl+S** save layers, then bake the `.hike` in the background.

## Not yet (future steps)

Current state and the ordered to-do list live in [HANDOFF.md](HANDOFF.md) —
delete that file once its list is empty.


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
