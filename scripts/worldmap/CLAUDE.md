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
- **Tunnels** — `.bin`, per-voxel carve mask (`byte[px,ly,pz]`), too 3D to be a
  useful image; the carved result is captured in the baked `.hike`.

## Runtime + bake (`WorldMapState`)

The mutable runtime document: owns every layer's data, the baked `WorldState` +
live `World` preview, the queries the tools/views read (`TerrainHeight`,
`WaterSurface`, `Underwater`, `Ocean`, `SolidAt`, `IsTunnel`, `ColumnHeight`
against the live `SeaLevel`), and the incremental re-bake. `StampColumns` stamps
each column: tunnel-carve → `Air`, else `Terrain` up to `TerrainHeight`, else
`Water` up to `WaterSurface`, else `Air`. The elevation+water images REPLACE
WorldGen's noise height/water; WorldGen's other per-column logic (ramps, shore,
kit blending) is out of scope — a clean focused stamp, not a fork. Same write
seam as `WorldEditor`: `SetVoxelWorld` into pre-existing chunks → `Commit` runs
`World.UpdateLighting` + `RebuildNearbyChunkMeshes`. Also holds the shared view
palette (`Hypsometric`, `RegionColor`, `ZoneColor`).

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

`WorldMapBrush` (`Resource`) is the shared, layer-agnostic stamp engine
(falloff/flow/noise + `Stamp(center, radius, w, h, apply)` callback); each tool
supplies its own radius and per-texel write. Add a new tool by implementing the
two interfaces and appending it to `WorldMapPainter._tools`.

## Host (`WorldMapPainter : Node3D`)

In-game mode (mirrors `WorldEditor`: Node3D + `GameCamera` + live `World` in
editor mode). Launched from the main menu (`GuiMainMenu.OnStartPainter` →
`Main.StartPainter`, which binds palettes + `ChunkMesh.SetTerrains/SetDetailGroups`
before the preview builds meshes — main-thread, same as `StartGame`). Holds the
tool list + a colourised `Rgba8` display image fed to `WorldMapCanvas` (a dumb
viewer: fits the image, draws the cursor, reports texel strokes via `OnPaint`).

Keys: LMB paint / RMB erase · **Tab** cycle tool (+view) · **Q/E** cycle the
tool's param · **R/F** active elevation / cross-section · **Space** 2D map ↔ 3D
fly-over · **`[` `]`** brush size · **Ctrl+S** save layers + bake `.hike`.

## Not yet (future steps)

In-world 3D region/zone tint overlay (a `ShaderGlobals` LUT + terrain shader),
prop/interactive scatter brushes, and tiling the per-column images per
chunk-footprint for streaming-scale worlds (see `scripts/voxels/CLAUDE.md`).
The clip plane is parked far above the world (no player to occlude around).
