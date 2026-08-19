# Voxel Terrain Atlas

Covers surface-texture authoring across `scripts/data/world/BlockSurfaceData.cs`, `resources/data/surfaces/` (the manifest + surface `.tres`), `tools/stitch_voxel_atlas.py`, and `addons/voxel_atlas_stitcher/`.

A **surface** is one baked atlas layer. A **block** (`resources/data/blocks/`) wears up to three of them — top, side, bottom — and owns every per-voxel property. Only `porosity` stays on the surface, because the shader uploads it as `tile_porosity[]` indexed by atlas layer and blends it per fragment. See the root `CLAUDE.md` for the block model.

Each `BlockSurfaceData` carries an `atlasBaseIndex` — a layer index into the two baked `Texture2DArray` strips `assets/textures/terrain/voxel_tiles.png` (color) and `voxel_tiles_nrm_height.png` (RGB normal + A height), which `ChunkMesh` loads and indexes by that id. Surfaces do NOT reference source textures directly.

**Never type an `atlasBaseIndex`** — it is read-only in the inspector. Add the surface to the manifest and rebuild; that mints the next free index for any surface still at `Unassigned` (`-1`) and saves it back into the surface `.tres`. **Headless, that rebuild is the `atlas_rebuild` console command** (`Godot ... --headless -- "atlas_rebuild 1"`), which runs the manifest's real `RebuildAtlas()` — minting included. `tools/stitch_voxel_atlas.py` re-stitches the strips but deliberately will NOT mint, so it hard-errors on an unassigned surface; reach for `atlas_rebuild` when adding a layer and the Python only when repointing art on layers that already have indices. An index already assigned is never touched. `BlockCatalog.ValidateOrLog` errors if a block wears a surface still unassigned (i.e. one never added to the manifest, or added without a rebuild), and `block_check` is the quick way to see that.

The layer→source-texture mapping is owned by a single editor-visible resource: **`resources/data/surfaces/voxel_atlas_manifest.tres`** (`VoxelAtlasManifest`). Open it in the inspector to see every layer (`AtlasLayer`: a `BlockSurfaceData` paired with its color/normal/height `Texture2D` refs, with thumbnails) and press **"Rebuild Atlas"** to re-stitch both strips from the source art under `assets/textures/terrain/`. This manifest is authoring-only — it is never loaded by the running game.

**`atlasBaseIndex` is the only thing that decides which strip row a layer bakes into.** The manifest's `layers` array is an unordered set — reorder or insert entries freely; the bake scatters each one to `layer.surface.atlasBaseIndex` and sizes the strip to the highest index claimed. A duplicate index or a layer with no `surface` aborts the bake. **Never renumber to close a gap** — `atlasBaseIndex` is a wire id, stored in the per-voxel `OverlayId` byte of every `.hike`, so renumbering re-textures saved worlds.

Two kinds of row bake blank (black + flat normal + zero height), and the strip keeps its full height either way so the rows above hold their numbers:

- **No layer claims the index.** Index 5 is currently one of these; the bake warns about it every run.
- **A layer claims it but authors no `color`.** That's how a surface reserves an index while needing no art at all — Water (index 2), which `voxel_water.gdshader` draws without ever sampling the atlas. Nothing else reads those pixels: the minimap indexes *by* `atlasBaseIndex` but paints `BlockData.minimapColor`, and `ChunkMesh.GroundTintFor` only runs for detail scatter on solid natural ground.

The manifest is the single source of truth. The headless mirror `tools/stitch_voxel_atlas.py` parses the `.tres` (not a duplicated list) and resolves the same indices out of each surface `.tres`; the editor plugin `addons/voxel_atlas_stitcher/` just calls the manifest's `RebuildAtlas()`/`IsStale()` (menu item + auto-rebuild on source change). To repoint a block's texture, edit the manifest — not the Python or GDScript. Both bake paths keep `slices/vertical` in the two `.png.import` files in sync with the row count.
