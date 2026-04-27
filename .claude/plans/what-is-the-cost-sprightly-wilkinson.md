# Cost: weather-driven specular (wetness) on voxel terrain

## Context

Goal: when it has rained or been foggy/humid recently, voxel surfaces visibly
wet out — picking up a specular highlight that fades as conditions dry. This
is a visual upgrade only; no gameplay change. The user is asking "what does
this cost?", not "implement it." This document is a cost breakdown plus a
recommended minimum-viable approach.

## TL;DR

**Cheap.** The seams already exist:

- Weather pipeline ([scripts/client/WeatherSimulation.cs](scripts/client/WeatherSimulation.cs), [scripts/client/WeatherDerivation.cs](scripts/client/WeatherDerivation.cs), [scripts/client/SkyController.cs](scripts/client/SkyController.cs)) pushes derived values to shader uniforms every frame. Adding one more float ("wetness") follows the same path as `fog_density`.
- Voxel shader [shaders/voxel_clip.gdshader](shaders/voxel_clip.gdshader) already has a `light()` stage with the sun direction, sampled normal, and a per-voxel **sunlight visibility mask** (`lm.r`, line 437). That mask gives us "is this surface exposed to sky" for free — no new 3D texture needed. The same `NORMAL` gives us slope (water sheds off vertical faces) for free as well.
- Voxel terrain is currently `specular_disabled` (line 2). Switching to a custom specular term in `light()` is ~10 lines.

Estimated effort for the MVP (global wetness, sky-gated, no per-material tuning): **~1 day**, mostly shader iteration.

## Wetness inputs

Three weather signals feed the accumulator, in descending strength:

1. **`rainAmount`** — primary. Fast wetness gain.
2. **Derived `fog`** — moderate. Already computed by [WeatherDerivation.cs:45-62](scripts/client/WeatherDerivation.cs#L45-L62) as `humidity × coolDiurnal`. Heavy fog dampens surfaces.
3. **`humidity`** — mild steady contribution even without fog. A muggy still day should leave things slightly dewy.

All three sum (with separate gain coefficients), saturate to 1, and decay
exponentially when the inputs go away.

## What needs to be built

### 1. Wetness accumulator (CPU, trivial)

`WeatherSimulation` doesn't track history today — only instantaneous values. Add one float to `WorldState` (or a peer of the existing variance channels in [scripts/voxels/WorldState.cs:59-107](scripts/voxels/WorldState.cs#L59-L107)):

```
gain     = rainAmount * rainGain + fog * fogGain + humidity * humidityGain
wetness += gain * dt
wetness *= exp(-decayRate * dt)        // dries out when gain falls
wetness  = clamp(wetness, 0, 1)
```

Tunables: `rainGain` (large), `fogGain` (medium), `humidityGain` (small),
`decayRate`. **Cost: ~20 lines, ~zero CPU.**

### 2. Shader global push (CPU, trivial)

In [scripts/client/SkyController.cs](scripts/client/SkyController.cs) where `fog_density` etc. are pushed each frame, add `RenderingServer.GlobalShaderParameterSet("wetness_level", ...)`. Register it via `ShaderGlobals.RegisterRuntime` in `_Ready` (per [scripts/utils/ShaderGlobals.cs](scripts/utils/ShaderGlobals.cs) conventions documented in CLAUDE.md). **Cost: ~5 lines.**

### 3. Voxel shader specular path (GPU, the real work)

In [shaders/voxel_clip.gdshader](shaders/voxel_clip.gdshader):

- Drop `specular_disabled` from `render_mode` (line 2), or keep it and compose specular manually into `DIFFUSE_LIGHT`.
- In `light()`: compute a Blinn-Phong term `pow(max(dot(N, H), 0), gloss) * sun_color * ATTENUATION`.
- Gate by `sun_mask` (already computed at line 437) so cave/overhang voxels don't fake-shine when it rains.
- Gate by a **slope factor** so water visibly sheds off steep faces: `slope = pow(max(NORMAL.y, 0), k)` (1.0 on flat tops, 0 on vertical walls, smooth between). `k` is a tunable; ~2-4 looks natural.
- Multiply by `wetness_level`.

**Cost: ~10-15 shader lines, a few ALU ops per fragment + one extra dot/pow. Negligible at the resolutions this game targets.** No new texture fetches, no mesh format change.

### 4. (Optional) Per-material wetness response

Without this, every voxel — Stone, Sand, Grass, Wood — wets out identically. With it, sand barely shines, stone shines hard, water is a no-op, etc. Two implementation paths:

- **Vertex attribute** — pack a wetness coefficient into the unused `.w` slot of `CUSTOM2` in [scripts/voxels/ChunkMesherDC.cs](scripts/voxels/ChunkMesherDC.cs) (the mesher already does majority-vote per cell). **Cost: ~30 lines mesher + a few shader lines.** Requires authoring a per-tile or per-`VoxelType` wetness coefficient table.
- **Shader-side lookup table** — small uniform array indexed by `tile_a/b/c` already in `CUSTOM0`. No mesher change. **Cost: ~10 shader lines + table.**

The shader-side LUT is cheaper. Recommended if we go this direction.

### 5. (Optional) Albedo darkening

Wet surfaces look darker as well as shinier. `ALBEDO *= mix(1.0, 0.7, wetness * sun_mask)` is one line and is arguably the single largest visual contributor to "this looks wet." Strictly speaking outside "specular channel" but adjacent and free.

## What we're NOT building

- **No new 3D texture.** The existing `light_map.r` sun-visibility mask covers "is this voxel exposed to sky" — which is what determines wet vs. dry under cover. A dedicated `WetnessMap` like `FogMap` is overkill until we want puddles in depressions.
- **No mesh regeneration.** Wetness is a global float gated by per-fragment sun-mask; existing chunk meshes don't need to rebuild when it starts raining.
- **No PBR rewrite.** Sticking with the current single-channel specular term keeps this shader compatible with everything else.

## Recommended MVP

1. Add `WetnessLevel` accumulator to `WorldState` driven by `rainAmount`, derived `fog`, and `humidity` with per-input gains; exponential decay.
2. Push it as a global shader uniform in `SkyController.Apply()`.
3. Add a Blinn-Phong specular term in `voxel_clip.gdshader` `light()`, gated by `sun_mask` and a `pow(NORMAL.y, k)` slope factor, scaled by `wetness_level`.
4. Add `ALBEDO` darkening on the same `sun_mask × slope × wetness_level` gate (free win; skip if user wants strict "specular only").

That's the floor. Everything in §4 / §5 is incremental.

## Files this would touch

- [scripts/voxels/WorldState.cs](scripts/voxels/WorldState.cs) — wetness accumulator + decay
- [scripts/client/WeatherSimulation.cs](scripts/client/WeatherSimulation.cs) — drive the accumulator from `rainAmount`, derived `fog`, and `humidity`
- [scripts/client/SkyController.cs](scripts/client/SkyController.cs) — push `wetness_level` global each frame
- [scripts/utils/ShaderGlobals.cs](scripts/utils/ShaderGlobals.cs) — register `wetness_level` runtime global
- [shaders/voxel_clip.gdshader](shaders/voxel_clip.gdshader) — add specular term in `light()`, optional albedo darken in `fragment()`

If we add §4 (per-material response):
- [scripts/voxels/ChunkMesherDC.cs](scripts/voxels/ChunkMesherDC.cs) — only if we go vertex-attribute route
- [scripts/voxels/VoxelType.cs](scripts/voxels/VoxelType.cs) — wetness coefficient table

## Verification

- Run game, force `rainAmount = 1.0` via console (CVars / WeatherData override), watch outdoor stone wet out over a few seconds.
- Force `rainAmount = 0` but high `humidity` — surfaces should hold a mild sheen, not fully dry.
- Force everything to 0, watch wetness decay over the configured timescale.
- Walk into a cave or under a roof while wet — those surfaces should stay dry (sun_mask gate).
- Look at a vertical cliff face during heavy rain — should stay near-dry while the flat top of the cliff shines (slope gate).
- Toggle `debug_unlit` and `debug_normals` to verify the specular path doesn't break debug views.
- Check fog-only weather: no rain, high fog → moderate dampness without glaring highlights.
