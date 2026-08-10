# Voxel Terrain Atlas

Covers block texture authoring across `scripts/data/BlockData.cs`, `resources/data/blocks/` (the manifest + block `.tres`), `tools/stitch_voxel_atlas.py`, and `addons/voxel_atlas_stitcher/`.

Each `BlockData` carries an `AtlasBaseIndex` — a layer index into the two baked `Texture2DArray` strips `assets/textures/terrain/voxel_tiles.png` (color) and `voxel_tiles_nrm_height.png` (RGB normal + A height), which `ChunkMesh` loads and indexes by that id. Blocks do NOT reference source textures directly.

The layer→source-texture mapping is owned by a single editor-visible resource: **`resources/data/blocks/voxel_atlas_manifest.tres`** (`VoxelAtlasManifest`). Open it in the inspector to see every layer (`AtlasLayer`: a `BlockData` paired with its color/normal/height `Texture2D` refs, with thumbnails) and press **"Rebuild Atlas"** to re-stitch both strips from the source art under `assets/textures/terrain/`. This manifest is authoring-only — it is never loaded by the running game.

The manifest is the single source of truth. The headless mirror `tools/stitch_voxel_atlas.py` parses the `.tres` (not a duplicated list), and the editor plugin `addons/voxel_atlas_stitcher/` just calls the manifest's `RebuildAtlas()`/`IsStale()` (menu item + auto-rebuild on source change). To repoint a block's texture, edit the manifest — not the Python or GDScript. Layer order must match each block's `AtlasBaseIndex` (the manifest validates this) and the `slices/vertical` count in both `.png.import` files.
