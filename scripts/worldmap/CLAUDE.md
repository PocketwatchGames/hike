# World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The **first step in the world-authoring chain**: a broad-brush, in-game paint
program that authors a layered raster *document* and bakes it into a real
`WorldState` / `.hike`. The downstream `WorldEditor` does fine per-voxel detail;
the game loads the baked `.hike`.

## Model: document + bake (not direct-voxel paint)

The authored source of truth is **`WorldMapData`** (`scripts/data/worldmap/`,
a `[Tool] Resource`) — bake settings (`GenData`, world extent in chunks, sea
level, height scale) plus references to the **layer images** (the layers ARE
images, persisted as external files you can open):

- **Elevation** — `.exr`, `Image.Format.Rf`, one texel per voxel **column**;
  R = normalized height 0..1, mapped to world voxels by `ColumnHeight()`.
- **Region** — `.png`, `Image.Format.R8`, one texel per **chunk**; R*255 = the
  region index baked into `ChunkState.RegionIndex`.

This mirrors the `VoxelAtlasManifest` convention: one editor-visible resource of
record + a re-runnable bake. `WorldMapData` carries an `[ExportToolButton]`
"Bake to .hike" that runs headless (no game) since the bake is pure C#.

## Bake (`WorldMapBake`, static)

`Build(data, elevation, region)` → `WorldState`: creates every chunk, stamps
`RegionIndex` from the region image, then `StampColumns` stamps `Terrain` up to
`ColumnHeight`, `Water` to `SeaLevel`, else `Air`, and runs
`LightEngine.ComputeSunlight`. The elevation image **replaces WorldGen's
noise-derived height field**; WorldGen's other per-column logic (ramps, shore,
tunnels, kit blending) is intentionally out of v1 scope — this is a clean
focused stamp, not a fork of the 3100-line `WorldGen`. Surface kit = palette
index 0 (the per-voxel `TerrainId` default), so the stamp never sets `TerrainId`.

`RebakeElevation` / `RebakeRegion` re-stamp just a painted rect. The write seam
is the same one `WorldEditor` uses: `SetVoxelWorld` (no-ops on a missing chunk,
so chunks must pre-exist) → caller drives `World.UpdateLighting` +
`RebuildNearbyChunkMeshes`.

## Host (`WorldMapPainter : Node3D`)

In-game mode, structured like `WorldEditor` (Node3D + `GameCamera` + a live
`World` preview in editor mode). Launched from the main menu
(`GuiMainMenu.OnStartPainter` → `Main.StartPainter`, which binds palettes +
`ChunkMesh.SetTerrains/SetDetailGroups` before the preview builds meshes —
main-thread, same as `StartGame`).

- Owns the layer `Image`s (truth) + a colourised `Rgba8` **display** image fed to
  the 2D canvas. Painting → `WorldMapBrush.Stamp` into the layer → incremental
  re-bake → live 3D update + recolour the painted rect of the display.
- **Two views, toggled with Space:** 2D map (the paint surface) and 3D fly-over
  preview (WASD over the baked terrain). **Tab** cycles the active layer;
  **Q/E** cycles the tool (brush op in Elevation, region index in Region);
  **`[` `]`** size; **Ctrl+S** saves the layer images + bakes the `.hike`.

`WorldMapCanvas : Control` is a dumb viewer — fits the display image, draws the
brush cursor, and reports texel-space strokes via `OnPaint`. `WorldMapBrush`
(`Resource`) is the shared, layer-agnostic stamp (radius/falloff/flow + Raise/
Lower/Flatten/Smooth + noise); future zone/prop/tunnel brushes reuse it.

## Not yet (future steps)

In-world 3D region tint (a `ShaderGlobals` region LUT + terrain-shader overlay),
zone painting, prop/interactive scatter brushes, and section-plane tunnel
carving (reusing the camera clip + inside-solid raycast from `WorldEditor`).
Large worlds will need the elevation image tiled per chunk-footprint to stay
streaming-compatible (see `scripts/voxels/CLAUDE.md`).
