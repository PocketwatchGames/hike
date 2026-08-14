# Voxel World: File Format & Streaming

Covers `scripts/voxels/io/` (`.hike` file format and disk loading) and the streaming-large-world roadmap.

For high-level voxel architecture (`World`, `ChunkManager`, `ChunkState`, `ChunkMesh`, `BlockData`), see the root `CLAUDE.md`.

## World File & Disk Loading (`scripts/voxels/io/`)

The game can load its world from a packed `.hike` file instead of running `WorldGen` at startup. This is the foundation for shipping a large hand-authored world produced by a custom editor.

**Format** (`WorldFile.cs`): single file per world, header + per-chunk index + payload. Each chunk's payload is independently addressable via `(offset, length)` in the index, so a future streaming loader can `Seek` to any chunk without loading or rewriting the file. Header carries world `Min`/`Max`, default `Spawn`, and the `SimData` resource path. Lighting is **baked into each chunk blob** so the runtime never has to recompute light at load.

**Components**:
- `IChunkSource` — interface (Min/Max/Spawn + `EnumerateChunkCoords` + `TryLoadChunk`). The seam where future streaming and save-delta layers will plug in.
- `WorldFileChunkSource` — `IChunkSource` impl. Opens the file, reads the header + full index up front, then `TryLoadChunk` seeks and decodes a single chunk. Thread-safe via internal lock.
- `ChunkSerializer` — single-chunk encode/decode (voxels + light + entity list).
- `EntitySerializer` — type-tagged binary read/write for `EntitySimState` subclasses. **Type tags are stable wire values — append new ones, never reuse old numbers**, so old world files keep loading after new entity types are added. `PackedScene` and `Resource` references are stored as resource paths.

**Bootstrapping** (`Main.cs`): `StartGame` checks `CVars.worldFile`. If non-empty, it builds a `WorldFileChunkSource`, pulls every chunk into a fresh `WorldState`, and uses the file's `Spawn` as the player position. Otherwise it falls back to `WorldGen.Generate()`. `WorldGen` is kept indefinitely as the editor's "generate basic world" template.

**Producing a world file** (`CVars.worldExport`): with a game running, `world_export <path>` writes the active `WorldState` through `WorldFile.Write`. Used for testing the disk loader against real data before the custom editor exists.

## WorldGen output cache — bump `WORLDGEN_VERSION` on logic changes

`WorldGenCache` (`scripts/voxels/io/`) memoizes `WorldGen.Generate` output to a `.hike` under `user://` keyed by `seed + size + fingerprint`. **The fingerprint is `WorldGen.WORLDGEN_VERSION` + `WorldFile.VERSION` + the content-hash of every `.tres`/`.tscn`/`.hikescene` reachable from the `WorldGenData` — it does NOT hash the worldgen C# itself.**

So editing `.tres` data invalidates the cache automatically, but **changing worldgen *logic* (any `.cs` under generation — height, caves, kits, spawns, …) does NOT.** A code-only fix silently loads a stale pre-fix world on the next run; the symptom is "my change had no effect" (and `Generate` never runs, so debug prints inside it never fire). **Bump `WORLDGEN_VERSION` (one int, top of `WorldGen.cs`) whenever you change generation behavior** — it invalidates every cached world. To force a one-off regen without a bump, set `world_cache_enabled false` in the console (or delete the `worldgen_cache` dir). When a worldgen fix "won't take," suspect this first.

## Streaming a Large World (future)

The target is a hand-authored world of roughly **500×500×100 chunks (~8km × 8km × 1.6km of voxels)**, of which only ~1 in 20 chunks contains meaningful data. Procedural generation will not produce this; it will come from a custom editor that writes `.hike` files directly.

The current disk-loading path still loads **every** chunk into memory at boot. The streaming work will replace that without changing the file format — all the seams are already in place:

- **Async chunk loader** behind a worker thread, calling `IChunkSource.TryLoadChunk` for chunks the player approaches. Mesh generation in `ChunkMesh.Create` can be split into off-thread (`SurfaceTool` build) + main-thread (mesh upload + collision).
- **`WorldState` becomes a bounded cache.** `_chunks` is populated/evicted by `ChunkManager` as the player moves. Cross-chunk accessors (`GetVoxelWorld`, `GetSunlightWorld`, etc.) already return defaults for missing chunks, which is the correct behavior for unloaded neighbors.
- **`Min`/`Max` go away** (they make the world feel finite and break a sliding `LightMap`). `World.CreateWorldBoundary` and the current `LightMap` constructor depend on them; both need updating. A small world manifest file may take over for spawn / extent if walls are still wanted.
- **Player-centric windowed volume maps (`WindowedVolumeMap.cs`).** The five volume textures (`LightMap`, `SkyExposureMap`, `FogMap`, `WindMap`, `WaterCurrentMap`) share a toroidal window covering the load radius rather than spanning the whole world. `ChunkManager` recenters them on chunk crossings; they are visual-only (gameplay reads the CPU `WorldState` arrays). Each is a RenderingDevice 3D texture wrapped in a `Texture3Drd` (so it still binds as a `sampler3D` global with `repeat_enable`/`filter_linear`), updated **per dirty chunk**: encode into a reusable cells³ staging texture, then `texture_copy` that block into the volume — `ImageTexture3D` has no partial update, so the old path re-uploaded the whole window (every Z-slice marshaled) on any change, which was the dominant per-frame cost while carried torches re-dirtied chunks. See `WindowedVolumeMap.cs` for the toroidal addressing, recenter, no-seam reasoning, partial-upload mechanics, and the overlook-backdrop caveat; `MovingLight.BlockLightMovingReshadeCullDistance` gates the per-frame reshade that drove that dirtying. RD ops run on the main thread against the global device — move to `RenderingServer.CallOnRenderThread` if a driver races.
- **Save model is deferred.** When player mutations need persistence, the answer will be either a delta layer over the read-only authored data or copy-on-first-load into a save slot. Either way it's a second `IChunkSource` implementation (likely a `LayeredChunkSource`) — no change to anything that consumes chunks.
- **Entity sync-back.** Mobs walk; chests change state. Before a chunk is evicted, its live `Node3D` entities must flush their mutable state back to `EntitySimState`. The `IWorldEntity` interface is the right place for a `SyncToSimState()` hook.

**What not to break when working in this area**: keep chunk payloads independently addressable, keep `IChunkSource` as the only thing that touches the file format, keep entity type tags stable, and don't add any code path that iterates "every chunk in the world" — the design must remain compatible with worlds where most chunks are not resident.
