# World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The **first step in the world-authoring chain**: a broad-brush, in-game paint
program that authors a layered raster *document* and bakes it into a real
`WorldState` / `.hike`. The downstream `WorldEditor` does fine per-voxel detail;
the game loads the baked `.hike`.

## Model: document + bake (not direct-voxel paint)

The authored source of truth is **`WorldMapData`** (`scripts/data/worldmap/`,
a `[Tool] Resource`) — bake settings (`GenData`, world extent in chunks, default
sea level, height scale) plus references to the **layer files** (openable
directly). It mirrors the `VoxelAtlasManifest` convention: one editor-visible
resource of record + a re-runnable bake (`[ExportToolButton]` "Bake to .hike",
runs headless since the bake is pure C#).

Layers:
- **Elevation** — `.exr` `Rf`, per voxel **column** (normalized height).
- **Water** — `.exr` `Rf`, per column (painted water-surface height; separate
  from the global ocean elevation).
- **Region** / **Zone** — `.png` `R8`, per **chunk** (index → `ChunkState.RegionIndex` / `ZoneIndex`).
- **Scatter** — `.png` `Rgba8`, per column (R = kind id, G = density); the bake
  places `EntitySimState` props/interactives, serialized into the `.hike`.
- **Tunnels** — `.bin`, per-voxel carve mask (`byte[px,ly,pz]`), too 3D to be a
  useful image; the carved result is captured in the baked `.hike`.

## Runtime + bake (`WorldMapState`)

The mutable runtime document: owns every layer's data, the queries the
tools/views read (`TerrainHeight`, `WaterSurface`, `Underwater`, `Ocean`,
`SolidAt`, `IsTunnel`, `ColumnHeight` against the live `SeaLevel`), and the
deterministic `BuildWorld` bake. **The painter edits only the 2D layer images —
no live voxel `World` is kept.** The `WorldState` is materialized on demand:
`BuildWorld` creates every chunk, stamps regions/zones, stamps all columns,
scatters entities, and propagates sunlight, and is run only at bake/save time
(`Save` → `WorldFile.Write`, and `WorldMapData`'s headless "Bake to .hike"
button). `StampColumns` stamps each column: tunnel-carve → `Air`, else `Terrain`
up to `TerrainHeight`, else `Water` up to `WaterSurface`, else `Air`. The
elevation+water images REPLACE WorldGen's noise height/water; WorldGen's other
per-column logic (ramps, shore, kit blending) is out of scope — a clean focused
stamp, not a fork. Also holds the shared view palette (`Hypsometric`,
`RegionColor`, `ZoneColor`).

## Tools + views (the extensible part)

Each tool is an **`IWorldMapTool`** and carries its own variables (brush
`Radius`, op, active elevation, cross-section, index...) and its own
**`IWorldMapView`** (`ColorAt(ctx, px, pz)`). The active tool decides BOTH what a
stroke does AND how the 2D map is coloured — switch tool, switch view.

| Tool | Paints | Extra vars | View |
|------|--------|-----------|------|
| `ElevationTool` | elevation (raise/lower/flatten/smooth) | `Op`, `StrengthPerStep`; `AdjustLevel` sets ocean Y | hypsometric ramp, **underwater tinted blue** |
| `WaterTool` | per-column water surface | `ActiveLevel` (surface Y) | water blue by depth, dry land grey |
| `TunnelTool` | carve at a cross-section | `CrossSectionY`, `CarveHeight` | white=land at slice, grey=2 below, blue=2 above, red=existing carve |
| `RegionTool` | per-chunk region index | `RegionIndex` | region colours, **50% darker in ocean** |
| `ZoneTool` | per-chunk zone index | `ZoneIndex` | zone colours, **brightness by elevation** |
| `ScatterTool` | density of props/interactives | `Kind`, `Density` | dim terrain + scatter coverage by kind |

`ScatterTool` paints a per-column `(kind, density)` raster; `WorldMapState.RescatterColumns`
deterministically (hash-seeded) places one `EntitySimState` per column with
probability == density on dry land, resolving the kind via `ScatterFactory`
(props from the zone `SurfaceKit` scene lists, interactives from the zone spawn
lists — the same resolution as `WorldEditor`). This runs during the `BuildWorld`
bake; the scatter brush itself only writes the per-column `(kind, density)`
raster.

`WorldMapBrush` (`Resource`) is the shared, layer-agnostic stamp engine
(falloff/flow/noise + `Stamp(center, radius, w, h, apply)` callback); each tool
supplies its own radius and per-texel write. Add a new tool by implementing the
two interfaces and appending it to `WorldMapPainter._tools`.

## Host (`WorldMapPainter : Node3D`)

A **pure 2D in-game program** — no live `World`, no `GameCamera`, no chunk
meshes. Launched from the main menu (`GuiMainMenu.OnStartPainter` →
`Main.StartPainter`), which just instantiates + `Init()`s the scene, so it opens
instantly. Holds the tool list + a colourised `Rgba8` display image fed to
`WorldMapCanvas` (a dumb viewer: fits the image, draws the cursor, reports texel
strokes via `OnPaint` and hover via `OnHover`). Each tool view reads the layer
images directly, so nothing here needs the voxel world.

Keys: LMB paint / RMB erase · **Tab** cycle tool (+view) · **Q/E** cycle the
tool's param · **R/F** active elevation / cross-section · **`[` `]`** brush size
· **Ctrl+S** save layers + bake `.hike`.

## Not yet (future steps)

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
