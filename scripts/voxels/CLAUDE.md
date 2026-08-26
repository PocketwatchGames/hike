# Voxel World: File Format & Streaming

Covers `scripts/voxels/io/` (`.hike` file format and disk loading) and the streaming-large-world roadmap.

For high-level voxel architecture (`World`, `ChunkManager`, `ChunkState`, `ChunkMesh`, `BlockData`), see the root `CLAUDE.md`.

## World File & Disk Loading (`scripts/voxels/io/`)

The game can load its world from a packed `.hike` file instead of running `WorldGen` at startup. This is the foundation for shipping a large hand-authored world produced by a custom editor.

**Format** (`WorldFile.cs`): single file per world, header + per-chunk index + payload. Each chunk's payload is independently addressable via `(offset, length)` in the index, so a future streaming loader can `Seek` to any chunk without loading or rewriting the file. Header carries world `Min`/`Max`, default `Spawn`, the `SimData` resource path, and **the kit palette this world was baked against** (one resource path per slot).

That last one is not bookkeeping. `ChunkState.TerrainId` is a byte per voxel holding an index into the kit palette, so a file's voxels only mean what they meant at bake if that table has not moved. Nothing about the stored bytes looks wrong when it has — they stay valid and simply name a different kit — which is precisely the failure a version number cannot catch, so the file names its slots and `Main.LoadWorldFromFile` refuses a world whose palette moved, pointing at the slot. Slots APPENDED since the bake are accepted, because appending is the one edit that moves nothing. See `KitPaletteData`.

Lighting is **baked into each chunk blob** so the runtime never has to recompute light at load.

**Components**:
- `IChunkSource` — interface (Min/Max/Spawn + `EnumerateChunkCoords` + `TryLoadChunk`). The seam where future streaming and save-delta layers will plug in.
- `WorldFileChunkSource` — `IChunkSource` impl. Opens the file, reads the header + full index up front, then `TryLoadChunk` seeks and decodes a single chunk. Thread-safe via internal lock.
- `ChunkSerializer` — single-chunk encode/decode (voxels + light + entity list).
- `EntitySerializer` — type-tagged binary read/write for `EntitySimState` subclasses. **Type tags are stable wire values — append new ones, never reuse old numbers**, so old world files keep loading after new entity types are added. `PackedScene` and `Resource` references are stored as resource paths.

**Bootstrapping** (`Main.cs`): `StartGame` checks `CVars.worldFile`. If non-empty, it builds a `WorldFileChunkSource`, pulls every chunk into a fresh `WorldState`, and uses the file's `Spawn` as the player position. Otherwise it falls back to `WorldGen.Generate()`. `WorldGen` is kept indefinitely as the editor's "generate basic world" template.

**Producing a world file** (`CVars.worldExport`): with a game running, `world_export <path>` writes the active `WorldState` through `WorldFile.Write`. Used for testing the disk loader against real data before the custom editor exists.

## WorldGen output cache — bump `WORLDGEN_VERSION` on logic changes

`WorldGenCache` (`scripts/voxels/io/`) memoizes `WorldGen.Generate` output to a `.hike` under `user://` keyed by `seed + size + fingerprint`. **The fingerprint is `WorldGen.WORLDGEN_VERSION` + `WorldFile.VERSION` + `LightEngine.LIGHT_VERSION` + the content-hash of every `.tres`/`.tscn`/`.hikescene` reachable from the `WorldGenData` — it does NOT hash the worldgen C# itself.**

So editing `.tres` data invalidates the cache automatically, but **changing worldgen *logic* (any `.cs` under generation — height, caves, kits, spawns, …) does NOT.** A code-only fix silently loads a stale pre-fix world on the next run; the symptom is "my change had no effect" (and `Generate` never runs, so debug prints inside it never fire). **Bump `WORLDGEN_VERSION` (one int, top of `WorldGen.cs`) whenever you change generation behavior** — it invalidates every cached world. To force a one-off regen without a bump, set `world_cache_enabled false` in the console (or delete the `worldgen_cache` dir). When a worldgen fix "won't take," suspect this first.

## Loading does not relight — a `.hike` carries its light

**Sunlight is baked into the chunk blob and is TRUSTED on load.** `Main.StartGame`'s
disk path (worldgen cache and `world_file` alike) does not re-propagate it: a
full-world `LightEngine.Relight` is ~13s at the default 18x16 size and used to be
the entire "Generating world" phase, on the main thread, for every load of an
already-built world. What that path still does, because the file does not carry it:

- **`FoliageStamper.Stamp` + `EntityVoxelStamper.Stamp`** (~50ms). The canopy /
  `SunOpaque` occluder fields are deliberately not serialized — their effect is
  already in the sun bytes — but they must be live so a later voxel edit
  re-propagates against foliage and roofs.
- **`ComputeSkyExposure`** (off the main thread, one worker per chunk-X slice —
  a column writes only into the chunk stack at its own `(cx, cz)`, and that
  disjointness is the whole reason the split is by chunk x). Derived, not stored.

**A change to the light pipeline is caught by `LightEngine.LIGHT_VERSION`**, which
rides in the worldgen-cache fingerprint: bump it and every cached world gets a new
cache path and regenerates. That covers the cache, which is disposable. It does
**not** cover a hand-authored `.hike`, which keeps whatever light it was baked
with — re-bake it, or run **`relight`** in the console (full recompute + re-mesh,
seconds).

## Whole-world voxel passes use `ChunkGrid`, not the chunk dictionary

**A pass that walks every voxel must not hash a `Vector3I` to find its chunk.**
`ChunkGrid` (`scripts/voxels/ChunkGrid.cs`) is the flat, chunk-indexed view of a
`WorldState`'s chunk dictionary: a world is a bounded box of chunks, so the lookup
is an array index. A voxel is addressed as a PACKED index, `chunkIndex << 12 |
local`, which pays twice — a flood queue holds one int per entry instead of three,
and `Step(packed, dir)` is an add inside a chunk (14 times out of 16 per axis) and
one table read across a chunk boundary, with no coordinate arithmetic and no
bounds test either way. `Resolve(dictionary)` flattens a sparse per-chunk side
channel (canopy, occluders, scratch cost) onto the same indices.

The sun flood and the interiorness flood were each paying ~48 hashes per popped
voxel, and that was most of what a bake cost: 83s of relight became 7.9s and 20s
of `InteriornessGen` became 4.5s, with byte-identical output. **Build one per pass
and drop it** — it caches chunk references, so anything that adds, removes or
replaces a chunk invalidates it.

**A `WorldState.Get*World` accessor is not always a raw array read**, and inlining
one that isn't is a silent bug. `GetFogWorld` goes through `ChunkState.GetFog`,
which floors air voxels at an interior class's `dustFloor * Interiorness`; reading
`FogDensity` directly instead made every building and cave bake a level or two
brighter, in 21 scattered chunks, with nothing visibly wrong. `GetBlockWorld`,
`GetSunlightWorld`, `GetCanopyAttenuationWorld`, `GetCanopyShadeWorld` and
`GetSunOpaqueWorld` are raw; check before you assume the next one is.

See [docs/bake-optimization-handoff.md](../../docs/bake-optimization-handoff.md)
for the measurements and the in-process reference-diff harness that caught it.

## Building a chunk: the pure half and the main-thread half

**A chunk build is ~98% pure CPU geometry and ~2% rendering server**, so it is
split in two and the initial world fill runs the first half for every chunk at
once. Measured over the 535-chunk fill of the default world: the DC mesher 6.3s,
the ledge barriers 1.8s, the water mesher 0.8s, the detail scatter 0.2s — against
0.14s of ArrayMesh commit and trimesh collision. The fill went 9.4s → 1.5s.

| | |
|---|---|
| `ChunkMesh.BuildGeometry` | PURE. No Node, no Resource, no RID, no shared mutable state. Returns a `ChunkGeometry`. Runs on a worker. |
| `ChunkMesh.Realize` | MAIN THREAD. Meshes, collision bodies, nodes, the detail-scatter post. |
| `ChunkMesh.Create` | both, in order — what a single-chunk caller on the main thread still uses (runtime streaming, the mesh rebuild queue). |

- **`MeshBuffer` is what makes the first half pure.** The meshers used to write
  into a `SurfaceTool`, which is a native object; now they fill plain C# lists and
  `ToArrayMesh` (main thread) hands the renderer one array per channel. Note it
  bought no measurable time on its own — the marshaling was not the bottleneck,
  the mesher's arithmetic is — it is there for the thread boundary.
- **The parallel fill is the INITIAL fill only** (`ChunkManager.FillInParallel`,
  `chunk_parallel_fill`). It is safe there because the player does not exist yet:
  nothing is editing voxels or touching the chunk dictionary while the workers
  read it. Per-frame streaming loads stay synchronous, and they are 1–4 chunks.
- **It loads the SPHERE set only**, exactly what the uncapped initial pass loaded.
  Pulling the frustum-extension chunks forward would change what is resident at
  spawn, not just how fast it got there.
- **Do not call `Profiler.Sample` inside `BuildGeometry`.** Its section stack is
  main-thread state. To profile the fill, `chunk_parallel_fill 0` puts the whole
  build back on the main thread through `Create`.
- **Anything added to the mesher must stay pure** — no `GD.Load`, no Godot object
  construction, and above all no static counters. A `public static long`
  incremented from inside the mesher for diagnostics is a straight data race.

## Streaming a Large World (future)

The target is a hand-authored world of roughly **500×500×100 chunks (~8km × 8km × 1.6km of voxels)**, of which only ~1 in 20 chunks contains meaningful data. Procedural generation will not produce this; it will come from a custom editor that writes `.hike` files directly.

The current disk-loading path still loads **every** chunk into memory at boot. The streaming work will replace that without changing the file format — all the seams are already in place:

- **Async chunk loader** behind a worker thread, calling `IChunkSource.TryLoadChunk` for chunks the player approaches. The mesh half of this is done — `ChunkMesh.BuildGeometry` / `Realize`, see above — but only the initial fill uses it; extending it to per-frame streaming means handling voxel edits landing while a build is in flight.
- **`WorldState` becomes a bounded cache.** `_chunks` is populated/evicted by `ChunkManager` as the player moves. Cross-chunk accessors (`GetVoxelWorld`, `GetSunlightWorld`, etc.) already return defaults for missing chunks, which is the correct behavior for unloaded neighbors.
- **`Min`/`Max` go away** (they make the world feel finite and break a sliding `LightMap`). `World.CreateWorldBoundary` and the current `LightMap` constructor depend on them; both need updating. A small world manifest file may take over for spawn / extent if walls are still wanted.
- **Player-centric windowed volume maps (`WindowedVolumeMap.cs`).** The five volume textures (`LightMap`, `SkyExposureMap`, `FogMap`, `WindMap`, `WaterCurrentMap`) share a toroidal window covering the load radius rather than spanning the whole world. `ChunkManager` recenters them on chunk crossings; they are visual-only (gameplay reads the CPU `WorldState` arrays). Each is a RenderingDevice 3D texture wrapped in a `Texture3Drd` (so it still binds as a `sampler3D` global with `repeat_enable`/`filter_linear`), updated **per dirty chunk**: encode into a reusable cells³ staging texture, then `texture_copy` that block into the volume — `ImageTexture3D` has no partial update, so the old path re-uploaded the whole window (every Z-slice marshaled) on any change, which was the dominant per-frame cost while carried torches re-dirtied chunks. See `WindowedVolumeMap.cs` for the toroidal addressing, recenter, no-seam reasoning, partial-upload mechanics, and the overlook-backdrop caveat; `MovingLight.BlockLightMovingReshadeCullDistance` gates the per-frame reshade that drove that dirtying. RD ops run on the main thread against the global device — move to `RenderingServer.CallOnRenderThread` if a driver races.
- **Save model is deferred.** When player mutations need persistence, the answer will be either a delta layer over the read-only authored data or copy-on-first-load into a save slot. Either way it's a second `IChunkSource` implementation (likely a `LayeredChunkSource`) — no change to anything that consumes chunks.
- **Entity sync-back.** Mobs walk; chests change state. Before a chunk is evicted, its live `Node3D` entities must flush their mutable state back to `EntitySimState`. The `IWorldEntity` interface is the right place for a `SyncToSimState()` hook.

**What not to break when working in this area**: keep chunk payloads independently addressable, keep `IChunkSource` as the only thing that touches the file format, keep entity type tags stable, and don't add any code path that iterates "every chunk in the world" — the design must remain compatible with worlds where most chunks are not resident.
