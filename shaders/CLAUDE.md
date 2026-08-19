# Shaders

Conventions and gotchas for `.gdshader` / `.gdshaderinc` files here and the C# that feeds them (`scripts/utils/ShaderGlobals.cs`).

## Shader Global Uniforms (`scripts/utils/ShaderGlobals.cs`)

Every `global uniform` declared in a `.gdshader` MUST be initialized from C# at startup, in the `_Ready` of whatever owns its per-frame `Set` calls (e.g. `SkyController`, `ChunkManager`). Use one of two methods depending on where the global lives:

- **`ShaderGlobals.Register(name, type, defaultValue)`** — for globals also declared in `project.godot`'s `[shader_globals]` section. The engine creates the variable at startup; this call seeds the C# default value before the first material that uses it compiles. Use for scalar/vector globals with sensible static defaults that you also want visible in the editor's Project Settings UI.
- **`ShaderGlobals.RegisterRuntime(name, type, value)`** — for globals NOT in `project.godot`. Calls `RenderingServer.GlobalShaderParameterAdd` directly. Useful for any global whose only meaningful value is computed at runtime and which does not need to exist in the editor. Note: if shaders that reference the global are ever opened in the editor's script editor or re-imported, the editor will fail to compile them — in that case, declare the global in `project.godot` with a placeholder and use `Register` instead (see `light_map` / `light_map_placeholder.tres`).

The runtime `RenderingServer.GlobalShaderParameterGet`/`GetList` APIs are editor-only, so we can't auto-detect whether a name is already declared in `project.godot`. The caller knows; pick the right method. Both must run before the first material that uses the global compiles — standalone launches (VS Code → `Godot.exe`) compile shaders very early. A global that hasn't been seeded by then either fails with `Global uniform '<name>' does not exist` (runtime-only globals) or compiles with stale values (project.godot-declared globals).

`SkyController` is `[Tool]`, so its `Apply()` runs in the editor too. Any global it `Set`s must be created in both editor and runtime — keep the `RegisterRuntime` calls outside the `Engine.IsEditorHint()` gate.

**`material_storage.cpp:1677 - "!global_shader_uniforms.variables.has(p_name)"` spam diagnosis** — fires whenever `RenderingServer.GlobalShaderParameterSet(name, ...)` runs for a `name` not in the engine's shader-globals dictionary. Common root causes, in rough order of frequency:
1. **`Register` used for a global that is NOT declared in `project.godot`.** `Register` calls `Set` internally, which errors. Fix: switch to `RegisterRuntime` (creates the global), or add the `project.godot` declaration.
2. **`mat4` global declared in `project.godot`'s `[shader_globals]` section.** The parser accepts the declaration well enough for shader compile to succeed, but the global never makes it into `RenderingServer.global_shader_uniforms.variables`, so every per-frame `Set` errors. Decompose into supported scalar/vector types (e.g. `vec3 origin`, `vec3 right`, `vec3 up`, `float size`) and reconstruct in the shader. Other untested types (`mat2`, `mat3`, `ivec*`, `uvec*`, `bvec*`) may have the same issue — stick to `bool`/`int`/`float`/`vec*`/`sampler*`.
3. **`[Tool]`-script `Apply()` runs in editor and pushes globals only registered behind `if (!Engine.IsEditorHint())`.** Move the global *creation* outside the editor-hint gate; the gated block can keep non-shader work.
4. **Stack-trace it.** Run `Godot.exe --path . --verbose` in a terminal that surfaces C# backtraces — the trace points at the exact `Set`/`Register` callsite and its global name. Vastly faster than guessing.

**Sampler globals trap:** do NOT put a sampler global in `project.godot` with `value: null` — Godot will try to load `res://<null>` as a resource on startup. Either give it a real texture path (use a `PlaceholderTexture*` `.tres` if the runtime value is computed — `Register` will swap it in), or skip the `project.godot` declaration entirely and create the global at runtime via `RegisterRuntime`.

**Unshaded fragment output formula:** Godot 4 outputs `ALBEDO + EMISSION` for materials with `render_mode unshaded`. If your shader writes only `EMISSION = composited`, `ALBEDO` defaults to white and saturates the surface. Either explicitly `ALBEDO = vec3(0.0)` or write the composite to `ALBEDO` and zero `EMISSION`. The same applies to other render-mode-stripped paths — be deliberate about which output channel carries the color and zero the other.

## Shared shading paths (`water_shading.gdshaderinc`)

Where two shaders draw the SAME material, they share the code rather than each keeping a copy. `voxel_water` (pools, rivers) and `waterfall` (falling sheets) both call `water_shading.gdshaderinc` — it owns the lightmap / cloud-shadow / block-light globals, the per-zone `water_absorption` + `water_scatter_color` + `foam_*` set, and `water_light` / `water_in_scatter` / `water_compose` / `water_adapt`.

**A consumer must not redeclare or re-include anything the shared file owns** — that includes `sky_common`, `cloud_shadow`, `block_light_shadow` and `eye_adaptation`, which it pulls in itself. Duplicating a `global uniform` is a compile error, so `shader_check` catches it in ~4s; a *missing* one is a windowed-only warning (see above), so run windowed once after moving any global between files.

The copy-per-shader version of this lasted one afternoon: the waterfall's copy silently omitted the sky reflection, and since water's look is mostly fresnel-weighted sky, the top of every cascade stopped reading as water at all.
