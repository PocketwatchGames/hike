# Player-Centric Windowed Lightmap (toroidal, all 5 volume maps)

## Context

The game mirrors per-voxel CPU light/environment data into five **full-world** `ImageTexture3D`s that
shaders sample for rendering: `LightMap`, `SkyExposureMap`, `FogMap`, `WindMap`, `WaterCurrentMap`
(all in `scripts/voxels/`). They are near-identical copy-paste classes.

Two problems, both called out in `scripts/voxels/CLAUDE.md` "Streaming a Large World":

1. **Scale:** sizing the texture to the whole world is impossible at the target world size
   (500×500×100 chunks → >100 GB of texture).
2. **Present-day perf:** `ImageTexture3D` has no partial update, so every `Flush` re-uploads the
   *entire* texture. Anything that dirties a chunk each frame (a flickering/moving light) forces a
   full-world re-upload per frame (~10 ms/frame with ~13 flickering lights). Today this is band-aided
   with a 30 Hz flush throttle (`light_flush_hz`) and a flicker distance-gate
   (`SimData.BlockLightFlickerCullDistance`).

**Fix:** replace the full-world textures with a fixed-size, player-centric **toroidal window** that
covers the load radius. A small window makes the full `Update` cheap (kills problem 2) and is
O(load-radius) regardless of world size (kills problem 1).

**Important scoping fact (verified):** these textures are **visual-only**. All gameplay
(perception/stealth in `PlayerPerception.cs`, AI in `MobAI.cs`, ambience in `AmbienceController.cs`,
propagation in `LightEngine.cs`) reads the authoritative CPU arrays via `WorldState.GetSunlightWorld`
/ `GetSkyExposureWorld` / `GetBlockLight` / etc. — never the GPU texture. The only non-shader texture
consumer is the wind GPU-particle attractor (`ChunkManager.cs:259`). So windowing cannot desync any
sim logic; correctness risk is purely cosmetic.

## Design: toroidal (wrap) addressing

Texel for world voxel `V` along each axis is `((V % W) + W) % W`, where `W` is the window width in
voxels — **independent of where the window is centered**. Consequences:

- The shader sampling expression becomes position-independent: `uvw = world_pos / windowWorldSize`
  with a **`repeat`-mode** sampler doing the `fract`/mod automatically. There is no moving origin to
  push to the GPU each frame.
- On recenter, each world voxel keeps its texel forever, so the chunk **entering** the window writes
  into exactly the texels the chunk **leaving** the window vacated (they are `W` voxels apart ≡ same
  texel). Recenter only re-encodes the entering slab — no data movement, no per-crossing full re-encode.
- **No seam**, provided `W` strictly exceeds the resident diameter on that axis: the contiguous
  resident world range maps injectively to texels, and the texel-wrap boundary (texel `W-1`↔`0`)
  always sits between two world-adjacent, both-resident voxels, so `filter_linear` across it is exact.
  Undersizing `W` (resident wider than window) is the only failure mode → two world voxels alias one
  texel. Sizing rule below guarantees the margin.

### Window size

Derive from the existing load radius in `ChunkManager` (`MAX_LOAD_DISTANCE = 10` chunks):

```
windowChunks (per axis) = MAX_LOAD_DISTANCE*2 + 1 + 2*WINDOW_MARGIN_CHUNKS   // 23 chunks @ margin 1
W (voxels)              = windowChunks * ChunkState.SIZE                       // 368 voxels
```

Clamp each axis to the world extent (`world.Max-world.Min`); where the world is smaller than the
window, use the world size on that axis (degenerate to today's behavior, still exact). `WINDOW_MARGIN_CHUNKS`
is a small const (≥1) next to `MAX_LOAD_DISTANCE` — sizing is correctness-critical (it backs the
texture buffer), so it stays a const, not an `[Export]`. Wind/water maps use the same window but at
their coarser cell granularity (`ChunkState.ENV_VOXELS_PER_CELL`), so `W_cells = W / CELL`.

**Known limitation (documented, not fixed):** bird's-eye overlook streams visual-only backdrop chunks
out to 48 chunks (`OVERLOOK_LOAD_DISTANCE_MAX`), far beyond the window. Those backdrop chunks will
sample wrapped (aliased) lighting. They are distant, top-down, mostly flat sunlight, and hidden behind
the overlook fog curtain, so this is expected to be invisible. If it ever shows, the fallback is to
special-case overlook (skip the windowed sample / force full-bright on visual-only chunks) — out of
scope here.

## Implementation

### 1. New shared base: `scripts/voxels/WindowedVolumeMap.cs`

Abstract base that owns all the shared machinery (the five maps are currently duplicates of it):

- Fields: window dims in texels (`_w/_h/_d`), per-axis cell scale, `_slicePixels`/`_slices`/`_imageList`/
  `_texture`, `_dirtyChunks`, current window chunk-coord bounds.
- `Origin => Vector3.Zero` and `InvSize => Vector3.One / windowWorldSize` (constant after construct).
- Helpers used by encode to map a world voxel/cell to a wrapped texel: slice index `((vz % _d)+_d)%_d`,
  row/pixel offset from wrapped `vx`,`vy`.
- `MarkChunkDirty(coord)`, `Flush(world)` (encode dirty chunks that fall in the current window and have
  a resident `ChunkState`, then one `_texture.Update`), and **`Recenter(playerChunk)`** — recompute
  window bounds; mark every chunk now in-window that wasn't before as dirty.
- Abstract members the subclass supplies: `Image.Format Format`, `int BytesPerPixel`,
  `int TexelScale` (1 for voxel maps, `ENV_VOXELS_PER_CELL` for wind/water), and
  `void EncodeChunk(WorldState, ChunkState, Vector3I coord)` — the per-map channel packing, which calls
  the base wrap-offset helpers to find where to write. (Keeps each map's RGBA/R8 packing local while
  centralizing the wrap math.)

Constructor encodes only chunks within the initial window (centered on spawn), then `Upload(initialCreate)`.

### 2. Convert the five maps to subclasses

`LightMap`, `SkyExposureMap`, `FogMap`, `WindMap`, `WaterCurrentMap` shrink to: format/bpp/scale +
`EncodeChunk` body (lift the per-voxel packing out of each current `EncodeChunkIfPresent`, writing via
the base's wrapped offsets instead of `coord*SIZE - origin`). Delete the duplicated dirty/flush/upload
plumbing.

### 3. `scripts/voxels/ChunkManager.cs`

- Construct the five maps with the window config (pass `MAX_LOAD_DISTANCE`/margin + spawn chunk; world
  for extent clamp).
- In `_Process`, when `_lastPlayerChunkCoord` changes, call `Recenter(newChunk)` on all five maps
  (before their `Flush`). The existing `Drain*ChunkDirty` + `Flush` calls stay.
- Globals: register `*_origin = Vector3.Zero` and `*_inv_size = map.InvSize` once at init; these are now
  constant (no per-frame updates — there were none before either). Same for fog material params
  (`fog_map_origin`/`fog_map_inv_size`).
- **Wind attractor** (`_windAttractor`, lines ~257-264): its AABB currently spans the full world. Make
  it window-sized and reposition it on recenter to the window's tile origin
  (`floor(playerWorld / windowWorldSize) * windowWorldSize`) so its linear AABB→uv mapping matches the
  shader's `world/W`. A one-frame wind discontinuity when the player crosses a `W` boundary is
  acceptable (note in code).

### 4. Shaders — add `repeat` to the sampler declarations

The sampling expression `(world_pos - <map>_origin) * <map>_inv_size` is unchanged (origin is now 0).
The only edit is making each of the five `sampler3D` global/uniform declarations wrap. Pattern, applied
everywhere a map is declared:

```
global uniform sampler3D light_map : filter_linear;            // before
global uniform sampler3D light_map : filter_linear, repeat_enable;   // after
```

`light_map` is declared in the two shared includes (`shaders/model_lit_body.gdshaderinc`,
`shaders/sprite_lit_common.gdshaderinc`) and ~15 standalone shaders (`voxel_clip`, `voxel_water`,
`water_clip_cap`, `fog_volumetric`, `detail_sprite`, `flat_lit`, `mote`, `particle_lit`, `rain_drop`,
`tree_lit`, `tree_cards_lit`, `tree_detail`, `tree_twigs`, `sprite_prop_multimesh`,
`sprite_prop_reflection_multimesh`, `sprite_reflection`). The other four maps are declared in their
smaller consumer sets (`fog_map` in `fog_volumetric`; `sky_exposure_map` in `fog_volumetric`/`rain_drop`;
`wind_map`/`water_current_map` in the wind/water consumers). All declarations were verified to use the
identical `(world_pos - origin)*inv_size` form, so no expression edits are needed. The
`length(<map>_inv_size) > 1e-6` editor-preview fallback still works (inv_size stays nonzero at runtime).

The `fog_map` is currently `repeat_disable` (`fog_volumetric.gdshader:29`) — flip it to `repeat_enable`.

### 5. `project.godot` placeholder declarations

The `[shader_globals]` placeholder `sampler3D`s exist so shaders compile in-editor. No type change
needed (still Sampler3D); confirm nothing there pins an address mode.

## Out of scope (explicitly deferred)

- Async streaming / chunk eviction (`WorldState` as bounded cache) — windowing here assumes the current
  all-resident model; it just stops mirroring the whole world to the GPU. It is forward-compatible with
  streaming (entering chunks already re-encode on recenter).
- Removing `World.Min/Max` and `CreateWorldBoundary` changes.
- The overlook backdrop lighting special-case (see limitation above).
- Relaxing the `light_flush_hz` throttle / flicker distance-gate. Once the window is small the full
  upload is cheap and these *could* be relaxed, but leave them as-is in this pass and re-tune after
  measuring.

## Verification

1. `dotnet build hike.sln` — clean.
2. `dotnet run --project tools/validate_uids` — new `WindowedVolumeMap.cs` needs a `.cs.uid` sidecar
   (mint with `--fix`); confirm no UID drift.
3. Run the game (`Godot ... --path . --verbose`):
   - No `Global uniform '<map>' does not exist` errors at startup (global registration order preserved).
   - Lighting, sun/cave shading, fog, water, trees, sprites, rain look correct while **walking long
     distances** in all directions — specifically watch the region where the texel-wrap boundary passes
     through view every `W` voxels for any seam/aliasing line (should be none with the margin).
   - Place/flicker a torch and move it: shading updates; verify no full-world hitch (the win) via the
     in-game profiler — `ChunkManager.LightFlush` cost should drop sharply vs today, and
     `light_chunk_uploads` behavior unchanged.
   - Trigger bird's-eye overlook: confirm backdrop is acceptable (documented aliasing only, fogged).
   - GPU-particle wind (embers/dust/rain drift) still responds to wind near the player.
4. Profiler A/B: compare `ChunkManager.LightFlush` / frame time with several flickering lights before
   vs after — expect the ~10 ms/frame full-upload cost to collapse to the window-sized upload.
