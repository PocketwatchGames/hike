# Voxel World: File Format & Streaming

Covers `scripts/voxels/io/` (`.hike` file format and disk loading) and the streaming-large-world roadmap.

For high-level voxel architecture (`Sim`, `ChunkManager`, `ChunkState`, `ChunkMesh`, `BlockData`), see the root `CLAUDE.md`.

## World File & Disk Loading (`scripts/voxels/io/`)

The game can load its world from a packed `.hike` file instead of running `WorldGen` at startup. This is the foundation for shipping a large hand-authored world produced by a custom editor.

**Format** (`WorldFile.cs`): single file per world, header + per-chunk index + payload. Each chunk's payload is independently addressable via `(offset, length)` in the index, so a future streaming loader can `Seek` to any chunk without loading or rewriting the file. Header carries world `Min`/`Max`, default `Spawn`, the `SimData` resource path, and **the terrain palette this world was baked against** (one resource path per slot).

That last one is not bookkeeping. `ChunkState.TerrainId` is a byte per voxel holding an index into the terrain palette, so a file's voxels only mean what they meant at bake if that table has not moved. Nothing about the stored bytes looks wrong when it has — they stay valid and simply name a different terrain — which is precisely the failure a version number cannot catch, so the file names its slots and `Main.LoadWorldFromFile` refuses a world whose palette moved, pointing at the slot. Slots APPENDED since the bake are accepted, because appending is the one edit that moves nothing. See `TerrainPaletteData`.

Lighting is **baked into each chunk blob** so the runtime never has to recompute light at load.

**Components**:
- `IChunkSource` — interface (Min/Max/Spawn + `EnumerateChunkCoords` + `TryLoadChunk`). The seam where future streaming and save-delta layers will plug in.
- `WorldFileChunkSource` — `IChunkSource` impl. Opens the file, reads the header + full index up front, then `TryLoadChunk` seeks and decodes a single chunk. Thread-safe via internal lock.
- `ChunkSerializer` — single-chunk encode/decode (voxels + light + entity list).
- `EntitySerializer` — type-tagged binary read/write for `EntitySimState` subclasses. **Type tags are stable wire values — append new ones, never reuse old numbers**, so old world files keep loading after new entity types are added. `PackedScene` and `Resource` references are stored as resource paths.

**Bootstrapping** (`Main.cs`): `StartGame` checks `CVars.worldFile`. If non-empty, it builds a `WorldFileChunkSource`, pulls every chunk into a fresh `WorldState`, and uses the file's `Spawn` as the player position. Otherwise it falls back to `WorldGen.Generate()`. `WorldGen` is kept indefinitely as the editor's "generate basic world" template.

**Producing a world file** (`CVars.worldExport`): with a game running, `world_export <path>` writes the active `WorldState` through `WorldFile.Write`. Used for testing the disk loader against real data before the custom editor exists.

## A `.hikescene` stores placements, not states

**A scene is two files: `house01.hikescene` (the voxel body) and
`house01.entities.tres` (`SubsceneEntities`, beside it by name).** The `.tres`
holds `SceneEntityPlacement`s — a palette entry (`source`), the placement's own
fork once edited (`custom`), a scene-local position and yaw, a seed, and an id.
Move, copy or rename the two together — `resource_check` fails on a scene
without its entities file or an entities file without its scene.

- **Every stamp SPAWNS them** (`SubsceneStamper.StampEntities` → `TrySpawn`,
  `AuthoredPosition`, rolled on the placement's seed), in worldgen, the painter's
  bake and the scene editor alike. So a scene's door tracks `door.tres`, a mob
  is levelled by the destination's context, and what the scene editor showed is
  what every stamp places. Nothing clones states any more.
- **Ids are stable and never reused** (`SubsceneEntities.nextId`). They are what
  a stamp's `SubscenePlacement.entityOverrides` key on — a stamp may fork an
  entity's entry (one house's door gets a lock) but never move or remove it.
  An override of the wrong type is ignored and reported (`worldmap_check`).
- **In the scene editor the placements ARE the document; states are only the
  preview** (`SceneEntityDocument`). Every tool — place, aim, move, duplicate,
  delete, roof retune, pasting a scene in — edits placements and then syncs,
  which respawns exactly the placements whose spawn no longer matches. Nothing
  reads a placement back off a state; the one reverse lookup (`OwnerOf`) is for
  picking a clicked node. Undo snapshots the placements
  (`ScenePlacementsAspect`) and re-syncs. The scene editor edits scenes only — a
  `.hike` is the game's output, and a world's states are never turned back into
  a scene.
- **Deleting an entity a painter stamp has edited warns at save.** The scene
  editor compares the ids on disk with the ids it is writing and names any
  painter document that overrides a removed one; undoing the delete restores the
  same id, so undo-and-save keeps the edit. `worldmap_check` reports any such
  override that slipped through as an ERROR.
- **A scene entity's properties are edited in the painter's panel**
  (`EntityInspector`, shown for a single selection in Select mode). The panel
  forks the placement's entry and edits the fork in place, so both the preview
  sync (`SceneEntityDocument.Matches`) and undo (`ScenePlacementsAspect`)
  compare the fork's VALUES (`ResourceValues`), not just its reference. An
  entry missing a per-placement value (a book with no inscription) is kept and
  previewed bare — the editor's context sets `PlacesIncomplete` — and the stamp
  picks it in the painter; a bake refuses one nobody picked.
- **Roofs are `RoofSpawnEntry` placements with no `source`**: the roof tool
  authors the whole entry, since a footprint and pitch are the whole of a roof.
- **The format is exact-version** (v21), like `.hike`. All earlier versions
  stored serialized states; the eight scenes were converted once and the old
  reader deleted, along with every old-scene branch in `EntitySerializer`.

## WorldGen output cache — bump `WORLDGEN_VERSION` on logic changes

`WorldGenCache` (`scripts/voxels/io/`) memoizes `WorldGen.Generate` output to a `.hike` under `user://` keyed by `seed + size + fingerprint`. **The fingerprint is `WorldGen.WORLDGEN_VERSION` + `WorldFile.VERSION` + `LightEngine.LIGHT_VERSION` + the content-hash of every `.tres`/`.tscn`/`.hikescene` reachable from the `WorldGenData` (a scene's `.entities.tres` included — it is found by name, not by reference, so `WorldGenCache` enqueues it beside each scene) — it does NOT hash the worldgen C# itself.**

So editing `.tres` data invalidates the cache automatically, but **changing worldgen *logic* (any `.cs` under generation — height, caves, terrains, spawns, …) does NOT.** A code-only fix silently loads a stale pre-fix world on the next run; the symptom is "my change had no effect" (and `Generate` never runs, so debug prints inside it never fire). **Bump `WORLDGEN_VERSION` (one int, top of `WorldGen.cs`) whenever you change generation behavior** — it invalidates every cached world. To force a one-off regen without a bump, set `world_cache_enabled false` in the console (or delete the `worldgen_cache` dir). When a worldgen fix "won't take," suspect this first.

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
| `ChunkMesh.Create` | both, in order — what the mesh rebuild queue still uses. |

- **`MeshBuffer` is what makes the first half pure.** The meshers used to write
  into a `SurfaceTool`, which is a native object; now they fill plain C# lists and
  `ToArrayMesh` (main thread) hands the renderer one array per channel. Note it
  bought no measurable time on its own — the marshaling was not the bottleneck,
  the mesher's arithmetic is — it is there for the thread boundary.
- **The initial fill and wakes run every build at once** (`ChunkManager.FillSphere`,
  `debug_chunk_parallel_fill`) and block on them, behind a loading or black screen.
  It loads the SPHERE set only; pulling the frustum-extension chunks forward would
  change what is resident at spawn, not just how fast it got there.
- **Runtime streaming builds on the thread pool too** (`ChunkManager.StartBuild` /
  `RealizeFinishedBuilds`): up to `MAX_BUILDS_IN_FLIGHT` at once, realized on the
  main thread within `REALIZE_BUDGET_MS`, on-screen chunks first, then nearest. A
  synchronous load is ~50ms, so even one per frame was a 20fps stretch after every
  chunk crossing. Only the 3x3x3 box round the player still loads synchronously
  (waiting on an in-flight build if there is one), because collision must exist
  before the player can step there.
- **Workers read voxels the main thread may be writing.** Nothing adds or removes
  a `WorldState` chunk during play, so the dictionary is safe; a torn voxel read
  can only make a wrong mesh. Every edit that remeshes goes through
  `RebuildChunkMeshes`, which marks an in-flight build `Stale` so its result is
  discarded and rebuilt. **A new path that changes mesh-visible voxels without
  calling it** leaves a streamed-in chunk showing pre-edit geometry. A build that
  throws is rebuilt on the main thread, where a real bug surfaces with a stack.
- **A re-mesh of a loaded chunk builds on the same pool** (`RebuildChunkMeshes` →
  `DispatchRemeshes` → `RealizeRemesh`), ahead of streaming loads. The pending
  set is a SET, so a brush drag naming the same chunks every stamp costs one
  rebuild each. The old mesh and its collision stay up until the new one lands,
  a few frames later. A re-mesh that went stale mid-build is swapped in anyway
  and queued again, unlike a stale load: during a continuous drag every result
  is stale by the time it lands, and discarding them would show nothing until
  the drag stopped.
- **An edit re-meshes exactly the chunks that READ it** (`Sim.RemeshVoxels`,
  `Sim.RemeshRegion` → `ChunkMeshReach`), never a fixed box. A build reads past
  its own 16³ — 7 voxels below and 9 above for the DC sun/openness march, plus
  ledge-barrier drops and detail-scatter edge walks sized by data — and each
  mesher declares its own reach beside the constants that set it
  (`ChunkMesherDC.READ_BELOW/ABOVE`, `LedgeBarrierMesher.ReadReach`,
  `ChunkDetailScatter.ReadReach`, the water meshers' `READ_REACH`). **A mesher
  change that reads further must widen its declaration**, or an edit near a seam
  leaves the neighbour's mesh stale with nothing reporting it; `debug_mesh_reach`
  (needs a running world, ~40s) rebuilds every loaded chunk through recording
  accessors and prints `[mesh_reach] ok` or FAIL. The one unbounded read, the
  water mesher climbing a water run to see whether it is roofed, is covered by
  adding the run under each edited cell. Sunlight is NOT a mesh input for
  in-window chunks — terrain multiplies the live volume by a baked openness term
  — so a relight never re-meshes; SunOpaque cover is, hence `RemeshRegion` for
  roofs.
- **`debug_chunk_stream_log 1|2`** reports streaming: pending and starved
  on-screen chunks, fps, worker build vs main-thread realize cost, and (at 2) one
  line per load.
- **Do not call `Profiler.Sample` inside `BuildGeometry`.** Its section stack is
  main-thread state. To profile the fill, `debug_chunk_parallel_fill 0` puts the whole
  build back on the main thread through `Create`.
- **Anything added to the mesher must stay pure** — no `GD.Load`, no Godot object
  construction, and above all no static counters. A `public static long`
  incremented from inside the mesher for diagnostics is a straight data race.

## Streaming a Large World (future)

The target is a hand-authored world of roughly **500×500×100 chunks (~8km × 8km × 1.6km of voxels)**, of which only ~1 in 20 chunks contains meaningful data. Procedural generation will not produce this; it will come from a custom editor that writes `.hike` files directly.

The current disk-loading path still loads **every** chunk into memory at boot. The streaming work will replace that without changing the file format — all the seams are already in place:

- **Async chunk loader** behind a worker thread, calling `IChunkSource.TryLoadChunk` for chunks the player approaches. The mesh half of this is done — runtime streaming already builds on the thread pool, see above; what remains is loading the chunk DATA the same way.
- **`WorldState` becomes a bounded cache.** `_chunks` is populated/evicted by `ChunkManager` as the player moves. Cross-chunk accessors (`GetVoxelWorld`, `GetSunlightWorld`, etc.) already return defaults for missing chunks, which is the correct behavior for unloaded neighbors.
- **`Min`/`Max` are a decision, not a given.** They still come off the `.hike` header, and the volume maps no longer need them (`LightMap` is a `WindowedVolumeMap` now). What still reads them whole-world: `WorldBoundary.Create` (the boxing walls), `MinimapTextures` (one full-extent image, sized once at construction — 500x500 chunks would be a 4000x4000 RGBA8 + two R8 masks, ~96MB), and `LightEngine.ComputeSunlight`'s column scan. For a hand-authored world with real extents, keeping them as authored bounds is cheaper than a sliding origin; drop them only if the world is meant to feel unbounded.
- **Player-centric windowed volume maps (`WindowedVolumeMap.cs`).** The five volume textures (`LightMap`, `SkyExposureMap`, `FogMap`, `WindMap`, `WaterCurrentMap`) share a toroidal window covering the load radius rather than spanning the whole world. `ChunkManager` recenters them on chunk crossings; they are visual-only (gameplay reads the CPU `WorldState` arrays). Each is a RenderingDevice 3D texture wrapped in a `Texture3Drd` (so it still binds as a `sampler3D` global with `repeat_enable`/`filter_linear`), updated **per dirty chunk**: encode into a reusable cells³ staging texture, then `texture_copy` that block into the volume — `ImageTexture3D` has no partial update, so the old path re-uploaded the whole window (every Z-slice marshaled) on any change, which was the dominant per-frame cost while carried torches re-dirtied chunks. See `WindowedVolumeMap.cs` for the toroidal addressing, recenter, no-seam reasoning, partial-upload mechanics, and the overlook-backdrop caveat; `MovingLight.BlockLightMovingReshadeCullDistance` gates the per-frame reshade that drove that dirtying. RD ops run on the main thread against the global device — move to `RenderingServer.CallOnRenderThread` if a driver races.
- **Save model is deferred.** When player mutations need persistence, the answer will be either a delta layer over the read-only authored data or copy-on-first-load into a save slot. Either way it's a second `IChunkSource` implementation (likely a `LayeredChunkSource`) — no change to anything that consumes chunks.
- **Entity sync-back — done for NODES, not for CHUNK DATA.** Entity *nodes* already stream on their own radius (`Sim.EntityStreaming.cs`) and flush on despawn: `Mob` writes back from `TreeExiting`, interactives mutate their `EntitySimState` in place (`Chest.Complete`). What that flush writes into is `WorldState._entities`, a per-chunk bucket — so the moment chunk DATA is evicted too, evicting a bucket throws that mutation away (a killed mob comes back). A bounded cache therefore can't ship without the save model above, at minimum an in-memory dirty-chunk layer that eviction writes to. `IWorldEntity` carries only `OnSpawned`; a uniform `SyncToSimState()` hook there would make the flush enforceable rather than per-class convention.

- **The whole-world walks that already exist**, all of which a bounded cache turns from "slow" into "wrong": `Main.HasAnyBakedSunlight` and `Minimap.Initialize`'s catch-up loop (both at load), `WorldState.ClearSunlightAll` / `ClearSkyExposureAll` (every relight), `GetHighestSolidVoxelY` and `ComputeRegionCentroidsXZ` (one-off, cacheable into the file header), `SubsceneBuilder` (editor-only). None is hot; each needs either a baked side-table in the header or a resident-only contract stated out loud.

**What not to break when working in this area**: keep chunk payloads independently addressable, keep `IChunkSource` as the only thing that touches the file format, keep entity type tags stable, and don't add any code path that iterates "every chunk in the world" — the design must remain compatible with worlds where most chunks are not resident.
