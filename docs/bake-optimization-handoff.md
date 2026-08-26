# Optimizing the world-map bake

A bake of `default_world_map.tres` took **~138s** and now takes **~33s**. This is
what was done, how to measure it yourself, and what is left.

## The harness — use this, do not drive the painter

```bash
# Bake headless: no window, no painter. ~33s.
Godot ... --path . --headless -- \
  "worldmap_bake res://resources/data/worldmap/default_world_map.tres user://bake_test.hike" "quit 1"
```

The second argument overrides the document's `outputWorldPath` **in memory only**,
so a test bake cannot overwrite `resources/data/worldmap/painted_world.hike` (230MB
of authored output). Omit it and it writes the real thing — don't.

It prints its own phase breakdown, and two sub-breakdowns under it:

```
[bake] build stages: alloc=190ms columns=4919ms scenes=26ms climb=38ms scatter=446ms entities=3ms finish=18914ms
[sunlight] field=140ms scan=295ms flood=7340ms seeds=23972253
[bake] build=24541ms occluderStamp=51ms relight=7887ms write=306ms
```

Then confirm the result is actually usable — a bake that produces an unlit world
is the failure mode this pipeline has already shipped once:

```bash
Godot ... --headless -- "world_file user://bake_test.hike" "autostart 1" "exec_delay 3" "exec quit"
```

Expect **no** `carries NO baked sunlight` line, and `[Load]   file: header=4ms chunks=336ms`.

## Measured, before and after

`default_world_map.tres` to a 230MB `.hike`, 6,912 chunks (28.3M voxels).
Headless, warm, this machine:

| Phase | Was | Now | Where |
|---|---:|---:|---|
| `BuildWorld` | 52,749ms | 24,541ms | `WorldMapState.BuildWorld` to `WorldFinish.Finish` |
| — `StampColumns` | 10,498ms | 4,919ms | `WorldMapState.StampColumn` |
| — `WorldFinish.Finish` | ~38,000ms | 18,914ms | of which `InteriornessGen` 20,413ms → ~4,500ms |
| occluder stamp | 72ms | 51ms | `FoliageStamper` + `EntityVoxelStamper` |
| relight | 83,225ms | 7,887ms | `LightEngine.Relight` |
| — column scan | ~10,000ms | 295ms | `ScanSunlightColumn`, now parallel by chunk-X |
| — BFS flood | ~70,000ms | 7,340ms | `SpreadSunlight` |
| file write | 1,735ms | 306ms | `WorldFile.Write` to `ChunkSerializer.Write` |
| **total** | **137,783ms** | **32,787ms** | |

Loading that world also got cheaper for free: the chunk read is 336ms where it
was ~3,100ms, because `ChunkSerializer.Read` is the same fix as `.Write`.

## What was done

**`ChunkGrid` (`scripts/voxels/ChunkGrid.cs`) is the mechanism, and new whole-world
voxel passes should use it rather than growing a fourth copy of this idea.** A world
is a bounded box of chunks, so a chunk lookup is an array index, not a
`Dictionary<Vector3I, ChunkState>` hash. A voxel is a PACKED index —
`chunkIndex << 12 | local` — which pays twice: a flood queue is one int per entry
instead of three, and stepping to a neighbour is an add inside a chunk and one
table read across a boundary, with no coordinate arithmetic and no bounds test
either way. `Resolve(dictionary)` flattens a sparse side channel (canopy,
occluders, scratch cost) onto the same indices.

- **`LightEngine`'s sun passes** hold a `SunField`: the grid, every channel they
  read flattened onto it, `exp()` precomputed into two transmittance tables
  (fog 0..255, canopy+shade 0..510), and per-chunk dirty flags flushed ONCE at
  the end instead of two `HashSet.Add` per voxel write. The flood was doing eight
  dictionary lookups and two `Mathf.Exp` calls **per neighbour**.
- **The column scan is parallel by chunk-X slice**, the same disjointness
  `ComputeSkyExposure` is split on: a column writes only into the chunk stack at
  its own `(cx, cz)`. Each worker fills its own seed queue and they concatenate;
  the flood is a monotone max-relaxation, so its fixpoint does not depend on the
  order seeds are popped in.
- **`InteriornessGen`** is the same grid with the same packed indices.
- **`ChunkSerializer.Write`/`.Read`** move a CHANNEL at a time. `byte[,,]` is
  contiguous in exactly the X,Y,Z order the per-voxel loops walked, so a
  `Buffer.BlockCopy` into a scratch buffer plus one call is the identical wire
  format where the loops cost ~57k `BinaryWriter.Write` calls per chunk.
- **`WorldMapState.StampColumn`** resolves its chunk once per 16 voxels of column.
  It was calling world-space setters that hash a `Vector3I` each — and the
  three-argument `SetBlockWorld` hashes three times, because it reads the block
  and the shape before writing.

## The trap this hit, and it will hit you too

**A `WorldState.Get*World` accessor is not always a raw array read.** Replacing
`GetFogWorld` with `chunk.FogDensity[...]` looked obviously equivalent and was
not: it goes through `ChunkState.GetFog(simData, …)`, which floors air voxels at
an interior class's `dustFloor * Interiorness`. Dropping that made every
building, cave and tunnel bake one or two levels BRIGHTER — 3,216 voxels across
21 chunks, scattered, with nothing about it visible in a screenshot. `SunField`
now materializes the effective fog per chunk **by calling `GetFog` itself**, never
a second copy of the rule, and aliases the raw channel where a chunk carries no
dust.

Check every accessor you inline: `GetBlockWorld`, `GetSunlightWorld`,
`GetCanopyAttenuationWorld`, `GetCanopyShadeWorld` and `GetSunOpaqueWorld` are
raw; `GetFogWorld` is not.

## The verification pattern that worked — use it

That bug was found by the pattern this file already recommended, and would not
have been found any other way:

- **A temporary reference implementation, behind a cvar, diffed in-process.**
  The pre-rewrite dictionary flood was pasted back in as `sun_verify 1`, run over
  the same world right after the new one, and compared voxel by voxel. It went
  `mismatched=3216 maxDelta=10` → fix → `mismatched=0 maxDelta=0` over 28.3M
  voxels. Deleted in the same session; re-add it the same way if you touch the
  flood.
- **Byte-compare two `.hike` files.** The bake is deterministic, so an
  output-preserving change produces an identical file. Keep a copy of the last
  good bake and `cmp` against it. When it differs, the chunk index (`coord`,
  `offset`, `length` triples after the header) maps a byte offset to a chunk and
  a channel, which localizes the change immediately.
- **Beware of comparing across a document change.** The first comparison here
  looked like a lighting regression and was partly another session saving a new
  `water_type.png` layer between the two runs. Re-bake the baseline if anything
  under `resources/data/worldmap/` has moved.

## What is left

Ranked, on the current 33s:

1. **`WorldFinish.Finish` — 18.9s, still the biggest single item.** `InteriornessGen`
   is now ~4.5s of it; nobody has broken down the other ~14s. The obvious first
   step is per-pass timers in `WorldFinish.Finish`'s ordered list. **That file was
   contended when this work was done** (another session was adding a per-column
   water-type layer), which is why it was left alone.
2. **The BFS flood — 7.3s.** Down from ~70s, so the remaining lever is
   parallelism: flood per chunk, exchange border values, iterate to a fixpoint.
   Probably not worth the complexity now.
3. **`StampColumns` — 4.9s.** Columns are independent, so it parallelizes the same
   way the sky/sun scans do, if the per-column readers it calls are thread-safe.

## Constraints that will bite you

- **The bake runs on a worker thread** (`WorldMapPainter.RunBake`). Everything in
  `BuildWorld` / `WorldFinish` / `LightEngine` must stay pure C#: no Godot object
  construction, no `GD.Load`, and **no static mutable counters**.
- **The canopy stamp is the one main-thread step**, marshalled out of the bake worker
  by a deferred `Callable` with a 60s timeout. It has to be: `FoliageStamper`
  instantiates each tree scene to read its `FoliageCluster` transforms.
- **`Profiler.Sample` is main-thread only** — its section stack is not thread-safe.
  Do not add it inside worker code.
- **`WorldFinish.Finish` is shared by BOTH producers** — worldgen and the painter.
  Anything you change there changes worldgen too.
- **A `ChunkGrid` caches chunk references**, so anything that adds, removes or
  replaces a chunk invalidates it. Build one per pass and drop it.
- **Bump `LightEngine.LIGHT_VERSION`** if you change the sunlight maths. It is in the
  worldgen-cache fingerprint, and a disk-loaded world's sunlight is now *trusted* and
  never re-propagated. (This work did NOT bump it: the output is bit-identical,
  proven both by `sun_verify` and by the file comparison.)
- **Bump `WorldGen.WORLDGEN_VERSION`** if generation output changes.

## Already done — do not redo

- Load no longer relights; a `.hike` carries baked sunlight, guarded by
  `LIGHT_VERSION` and a "world carries no baked sunlight" safety net.
- `ComputeSkyExposure`: chunk-at-a-time walk plus parallel by chunk-X. 1,371 to 91ms.
- Chunk build split into pure (`ChunkMesh.BuildGeometry`) and main-thread
  (`ChunkMesh.Realize`); initial world fill parallelized. 9,394 to 1,520ms.
- Meshers write `MeshBuffer` instead of `SurfaceTool` — this bought no measurable
  time on its own; it is what makes the mesher thread-safe.
- Whole load: 27.4s to 4.5s on the default world; 84s to 4.6s on the painted world.
- Everything under "What was done" above.
