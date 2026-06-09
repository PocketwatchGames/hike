# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Hike is an isometric exploration game built with Godot 4.6 (C#/.NET 8.0). It features a voxel-based hand-designed environment with 2D sprite props, dual-stick controls, and pixel art visuals. Indoor and outdoor environments transition seamlessly using a ceiling cutaway effect that handles complex environments.

## Priorities

Always prefer code quality, simplicity, and speed/ease of content authoring over implementation cost. This is a small-team project where authored content (`.tres`/`.tscn` files) is the long-tail bottleneck — a slightly harder implementation that makes authoring faster, less error-prone, or simpler to reason about is worth it.

## Build & Run

```bash
# Build the C# project
dotnet build hike.sln

# Run with Godot (adjust path to your Godot installation)
"C:\Users\andy\source\Godot_v4.6-stable_mono_win64\Godot_v4.6-stable_mono_win64.exe" --path . --verbose
```

- .NET 8.0 SDK required
- Godot.NET.Sdk 4.6.0 (Jolt Physics engine)
- No test framework is configured; testing is done through manual play

### Worktree Setup

Git worktrees don't include gitignored generated files. After creating a new worktree, copy these from the main repo before building:

```bash
cp <main-repo>/scripts/VersionGenerated.g.cs <worktree>/scripts/VersionGenerated.g.cs
```

Then run a headless import to initialize the `.godot/` cache:

```bash
"C:\Users\andy\source\Godot_v4.6-stable_mono_win64\Godot_v4.6-stable_mono_win64.exe" --path . --editor --headless --quit
```

The headless import may not fully populate all imported assets. After the headless import, copy the `.godot/imported/` and `.godot/shader_cache/` directories from the main repo to ensure the worktree has all required cached files:

```bash
cp -r <main-repo>/.godot/imported/ <worktree>/.godot/imported/
cp -r <main-repo>/.godot/shader_cache/ <worktree>/.godot/shader_cache/
```

## External Asset Library

Source textures, models, animations, and some sounds for new assets are pulled from an external Unity asset library at `C:\Users\andy\source\AssetDump\Assets` (outside this repo, not committed). A large bank of stock sound effects lives under `C:\Users\andy\source\AssetDump\Assets\Universal Sound FX`.

**Additional source locations** — first-party Pocketwatch projects on this machine, also browse/source-only and outside this repo. Mine them for audio (and art) the same way as AssetDump:
- `C:\Users\andy\source\Armada` — audio under `Armada\Audio` and `ArmadaContent\audio`.
- `C:\Users\andy\source\house\dev\Assets` — `Art\` and `Audio\` (e.g. character/foley/VO `.wav` under `Audio\Assets`, plus temp/dev sounds under `Audio\Assets\Temp`).
- `C:\Users\andy\source\bowhead\Assets` — `Audio\` (incl. `Audio\Music`), art, shaders, icons.

This is a **browse/source location only** — scan and read it in place when implementing a new asset; do not copy the whole library into the project. Godot imports everything under the project root, so a bulk copy (even gitignored) would generate `.import`/`.godot/imported/` churn for hundreds of unused files.

Workflow when adding an asset:
1. Search/preview the library for what's needed (textures `.png`/`.tga`/`.psd`/`.webp`, models `.fbx`/`.gltf`, audio `.wav`/`.ogg`). Ignore Unity-only files — `.meta`, `.prefab`, `.mat`, `.asset`, `.unity` are useless to Godot.
2. Copy only the chosen source file(s) into the appropriate `res://` subfolder (`assets/textures/...`, `scenes/props/`, etc.). These are **committed**, not gitignored — the game and teammates need them.
3. Wire them up following this repo's conventions (`.import` sidecars, `.tscn`/`.tres`, and the Godot UID Invariants below).

**Wiring a Synty FBX model (material override + scale):**
- Synty FBX embed a texture path that doesn't exist in the project (e.g. `PolygonNatureBiomes_Texture_01_Tom.png`), so import logs a harmless `Resource file not found: res://`. Override the material in the `.fbx.import` `_subresources.materials` block (`use_external`), pointing at a `model_lit` `.tres`. See `signpost.tscn` / `scenes/interactives/campfire.tscn` / `well.tscn` for the canonical wiring.
- **The override key is the material name *Godot assigns*, which is NOT always the raw name in the FBX** — the importer can append a digit (`Nature_Base_Mat` → `Nature_Base_Mat8`, and it differs per file). If the key is wrong the override silently no-ops and the model renders **untextured** (the broken embedded material is kept). Don't guess from FBX strings; introspect the imported scene to get the exact name.
- **Headless introspection** (no editor): a throwaway `SceneTree` GDScript that `load()`s + `instantiate()`s the `.fbx` and walks the tree, run via `Godot.exe --path . --headless --script <file>.gd`, prints the real surface material names, mesh node paths, and **AABB**. Read the AABB to set scale + ground offset — Synty per-asset scale varies wildly (the campfire was ~0.9m needing 2.25×; the well was ~3m at 1.0×). Delete the throwaway script (and its `.uid`) when done.
- **Reuse a pack's atlas if it's already imported** — the PolygonNatureBiomes meadow atlas lives at `assets/models/signpost/PolygonNatureBiomes_Meadow_Texture_01.png`; point new meadow-pack materials at that existing copy instead of re-importing, to avoid texture churn. Prefer models from packs whose atlas is already in-project.
- Canonical template for an interactive 3D prop: instanced FBX under a `Model` Node3D, external material via `.fbx.import`, `InteractiveMeshHighlight` with `_collectFrom` → the model node for the selection outline (3D analog of the sprite X-ray). `GameClient.ApplyHighlight` auto-falls back to the mesh-highlight path when an interactive has no sprite.
- A single FBX surface = one material; you can't glow/recolor part of a mesh from the material alone. To vary appearance across a composite prop (e.g. glowing logs but not the stone ring), keep the parts as **separate FBX instances** and assign/swap per-instance materials.

## Architecture

### Entry Point (`scripts/Main.cs`)

`Main` is the root node. It initializes CVars, localization, and the in-game console, then manages screen transitions between the main menu and the game scene.

### Client Layer (`scripts/client/`)

`GameClient` (Node3D) is the game scene root. It handles player spawning, camera control (third-person isometric), movement input (camera-relative), physics (gravity + MoveAndSlide), and the pause system.

### GUI (`scripts/gui/`)

- `GuiMainMenu` - Main menu with new game/load game signals
- `PauseMenu` - Resume/save/quit buttons, visibility tied to pause state
- `Hud` - HUD container (CanvasLayer)
- `HudText` - Floating world-space text with fade animation (factory: `HudText.Create()`)
- `ConsoleUI` - In-game debug console (toggle with backtick)

### Scenes

- `scenes/main.tscn` - Root scene (Main.cs)
- `scenes/screens/game.tscn` - Game world with camera, lighting, environment
- `scenes/screens/main_menu.tscn` - Title screen
- `scenes/game/player.tscn` - Player CharacterBody3D
- `scenes/gui/` - HUD, pause menu, floating text scenes

### Utilities (`scripts/utils/`)

- `Fx` - One-shot particle effect lifecycle manager
- `LinqExtensions` - MaxBy, MinBy, RemoveAtSwap helpers

### World (`scripts/World.cs`) and Voxel System (`scripts/voxels/`)

`World` (Node3D) is the central hub that all world simulation entities (Player, Mob, Loot, Door, Torch, Chest) reference. It owns entity dictionaries for loaded props, mobs, and interactives, manages entity spawning/cleanup, world boundary walls, and delegates voxel operations to `ChunkManager`. `ChunkManager` (Node3D, child of World) handles streaming chunk mesh loading/unloading with frustum culling, the mesh rebuild queue, and the `LightMap`. It notifies World via `OnChunkLoaded`/`OnChunkUnloaded` callbacks so World can spawn or clean up entities for each chunk. `ChunkState` stores a 16x16x16 voxel array per chunk. `ChunkMesh` generates a culled mesh via `SurfaceTool` with per-vertex colors and trimesh collision. `VoxelType` enum defines voxel types (Air, Stone, Grass, Dirt, Sand). Player spawning is deferred until the spawn chunk's collision is ready.

## Documentation

### Voxel World File & Streaming (`scripts/voxels/`)

The game loads its world from a packed `.hike` file (`WorldFile` / `WorldFileChunkSource`) when `CVars.worldFile` is set, falling back to `WorldGen.Generate()` otherwise — chunk payloads are independently addressable through the `IChunkSource` interface, with lighting baked into each chunk blob and entity state serialized via stable `EntitySerializer` type tags. This is the seam for future async streaming, a sliding-window `LightMap`, and save-delta layers. See [scripts/voxels/CLAUDE.md](scripts/voxels/CLAUDE.md).

### Voxel Terrain Atlas (`scripts/data/BlockData.cs`, `resources/data/blocks/`, `tools/stitch_voxel_atlas.py`)

Each `BlockData` carries an `AtlasBaseIndex` — a layer index into the two baked `Texture2DArray` strips `assets/textures/voxels/voxel_tiles.png` (color) and `voxel_tiles_nrm_height.png` (RGB normal + A height), which `ChunkMesh` loads and indexes by that id. Blocks do NOT reference source textures directly.

The layer→source-texture mapping is owned by a single editor-visible resource: **`resources/data/blocks/voxel_atlas_manifest.tres`** (`VoxelAtlasManifest`). Open it in the inspector to see every layer (`AtlasLayer`: a `BlockData` paired with its color/normal/height `Texture2D` refs, with thumbnails) and press **"Rebuild Atlas"** to re-stitch both strips from the source art under `assets/textures/terrain/`. This manifest is authoring-only — it is never loaded by the running game.

The manifest is the single source of truth. The headless mirror `tools/stitch_voxel_atlas.py` parses the `.tres` (not a duplicated list), and the editor plugin `addons/voxel_atlas_stitcher/` just calls the manifest's `RebuildAtlas()`/`IsStale()` (menu item + auto-rebuild on source change). To repoint a block's texture, edit the manifest — not the Python or GDScript. Layer order must match each block's `AtlasBaseIndex` (the manifest validates this) and the `slices/vertical` count in both `.png.import` files.

### Save/Load System (`scripts/SaveGame.cs`)

Binary format with a version header. Currently stubbed -- writes/reads the header but no game state yet.

### CVars (`scripts/console/`, `scripts/CVars.cs`)

Runtime configuration variables with an in-game console. Add new CVars as `public static` fields in `scripts/CVars.cs` using typed subclasses (`CVarBool`, `CVarInt`, `CVarFloat`, `CVarString`). The constructor auto-registers them. Read values via `.Value` (e.g., `CVars.language.Value`). Set via in-game console, `cvars.txt` config file (project root, runs at startup), or `.Value =` / `.Set()` in code. Action CVars (type `None`) take a callback instead of storing a value.

### Localization (`scripts/localization/`, `resources/localization/`)

`Loc.Get(Loc.Keys.key)` and `Loc.Format(Loc.Keys.key, args...)` with `%0`/`%1` placeholders. Per-language TSV files (`resources/localization/english.tsv`) with `key\tvalue` columns. `Loc.Keys` enum is auto-generated on build from `english.tsv` via `tools/loc_generator`. Language controlled by `CVars.language`; changing it reloads strings and fires `Loc.OnLanguageChanged`.

**Adding strings:** add `snake_case` key to `english.tsv`, use `Loc.Get`/`Loc.Format`. Search for unlocalised strings via `.Text =` with `$"..."` or string literals.

### Mob AI System (`scripts/gameplay/MobAI.cs`, `scripts/data/behaviors/`, `scripts/gameplay/behaviors/`)

Per-mob hierarchical state machine driven by polymorphic Resource data — `BrainData` holds `BehaviorNode`s, each pairing a `BehaviorData` subclass (authored tuning) with `BehaviorNodeTransition`s gated by `BehaviorTransitionData` predicates; `BehaviorBase` runtime instances tick at 60Hz against `PerceptionState[]` slots in `MobSimState` (perception accumulates at ~10Hz and latches `triggered` when crossing `PerceptionThresholdAlert`). See [scripts/gameplay/behaviors/CLAUDE.md](scripts/gameplay/behaviors/CLAUDE.md).

### Action System: Weapons, Consumables, Interactives (`scripts/data/actions/`, `scripts/gameplay/actions/`)

A single per-actor `ActionRunner` drives all timeline-based player and mob actions via two authored data shapes — `ItemActionProfile` (charge-tier-based `ItemAction`s, used for weapons / consumables / mob attacks, dispatched by `EActionVerb`) and `InteractiveAction` (single-phase wait used for chests / doors / torches / loot via `IInteractive`); both consume `ItemEvent` timelines whose `type` is an `EItemEventType` flag bitmask (Melee, Hitscan, UseAmmo, ApplyEffect, DecrementStack, ToggleMovingLight, PlayAnim, PlaySound, OpenInteractive, ConsumeFromInventory). See [scripts/gameplay/actions/CLAUDE.md](scripts/gameplay/actions/CLAUDE.md).

### Rideable Vehicles (`scripts/gameplay/vehicles/`, `scripts/data/vehicles/`)

The player can board and ride vehicles (`Boat` now, mounts later) through a common interface. Boarding reuses the interactive system — a `RideableVehicle` is an `IInteractive` whose `Mount`-verb `InteractiveAction` calls `Player.Mount`, which parents the player under the vehicle's `SeatAnchor` and suspends on-foot locomotion. The vehicle owns its own physics in `_PhysicsProcess` and reads the rider's steering via `Player.MountMoveInput`; the rider just loops the vehicle's `IdleAnim` / `MoveAnim`. `RideableVehicle` carries the shared plumbing; concrete subclasses (`Boat`) supply only their physics + `GetDismountPosition`. See [scripts/gameplay/vehicles/CLAUDE.md](scripts/gameplay/vehicles/CLAUDE.md).

### Audio-Visual Effects (`scripts/utils/Fx.cs`)

`Fx` is the lifecycle-managed scene wrapper for audio + particles (replaces the older `EffectOneShot`). One-shot mode (`_loop = false`, default) auto-frees once every child `GpuParticles3D` has stopped emitting AND every child `AudioStreamPlayer3D` has finished playing. Loop mode (`_loop = true`) re-plays randomized audio on each `Finished` and frees only after `Stop()` plus a particle-lifetime grace window. Distinct from the gameplay-state `ItemEffect` / `HealEffect` hierarchy — `Fx` is purely audio-visual; `ItemEffect` mutates actor state.

**`Fx.Create(scene, parent, position)`** is the factory. It has a `CallDeferred` fallback for the `AddChild` "data.blocked > 0" case; the caller-side pattern (`_Ready` → `CallDeferred(MethodName.Activate)`) is the cleaner fix and is used by `MovingLight`. Never call `Fx.Create` from `_ExitTree` — `MovingLight` shows the split: private `Cleanup()` (no fx) shared by `Deactivate` (player-initiated, fires the off-cue) and `_ExitTree` (silent).

**General Node lifecycle rules these Fx patterns embody** (apply to any factory or scene-spawning code, not just Fx):
- **`AddChild` rejected with `"data.blocked > 0"`** — Godot guards against re-entrant tree mutation. If `AddChild` runs inside the `_Ready` of a parent that is itself still mid-`AddChild`, the inner call fails. Two-part fix: (1) factory falls back to `parent.CallDeferred(Node.MethodName.AddChild, child)`; (2) caller defers the spawning work via `_Ready` → `CallDeferred(MethodName.Activate)`. The caller-side defer is the cleaner fix because Godot still prints the inner error from `AddChild` even when the factory's fallback succeeds — only deferring the caller silences the log.
- **Never spawn nodes (or fx, or audio one-shots) from `_ExitTree`** or anything it calls — the parent is being torn down; the new child either fails outright or leaks. If a "shutdown" effect feels like it belongs in `_ExitTree` (off-cue, death rattle), fire it from the player-initiated teardown path instead and treat `_ExitTree` as silent cleanup. The split-method pattern (`Cleanup()` for shared teardown + `Deactivate()` for the player path that adds the fx) is the standard shape.

### Shader Global Uniforms (`scripts/utils/ShaderGlobals.cs`)

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

### Minimap (`scripts/gameplay/minimap/`, `shaders/minimap.gdshader`)

Two parallel renderers behind one HUD widget — a global outdoor heightmap (2m/voxel, one height per XZ column) and a sparse per-slice indoor atlas (1m/voxel, slice = `floor(Y / PlateauHeight)`), composited via a state-A/state-B crossfade so mode toggles and slice-level crossings glide. Surface texels pack height + tile id (`MinimapTileColors` LUT) + foliage id (`MinimapFoliageColors` LUT); exploration is a separate R8 mask per renderer, revealed via soft-edged disk writes scaled by `WorldState.GetPerceivedLightWorld` for slices. See [scripts/gameplay/minimap/CLAUDE.md](scripts/gameplay/minimap/CLAUDE.md).

### Ground Stains / Decals (`scripts/client/GroundStainProjector.cs`, `shaders/ground_stain.gdshaderinc`)

Flat ground marks — scorch, footprints, blood, worn paths — are **not** Godot `Decal` nodes. A `Decal` only modifies `ALBEDO`, but the terrain/sprite shaders (`voxel_clip`, `detail_sprite`) run `ambient_light_disabled` and route most surface brightness through `EMISSION` (ambient sky-bounce + block/torch light computed from `base`). So a `Decal` only tints the direct-sun fraction and **washes out wherever EMISSION dominates** — shade, dark terrain (swamp), and right next to a light (a fire trap). Decals are visible only on bright, sunlit ground; do not use them for ground marks.

Instead they go through the **ground-stain layer**, a sibling of the `BlockLightShadowProjector` pattern:
- **`GroundStainProjector`** (in `game.tscn` under `SceneViewport`) is a top-down orthographic `SubViewport` camera over the player that renders stain proxies on **visual layer 5** (`STAIN_PROXY_LAYER_MASK = 1u << 4`, value `16`) into `ground_stain_tex`, publishing `ground_stain_origin/right/up/size/strength` globals each frame. `MainCamera`'s `cull_mask` excludes layer 5 so proxies never draw to the screen directly — only into the projector.
- **`voxel_clip.gdshader`** samples it via `apply_ground_stain(base, world_vertex)` (from `ground_stain.gdshaderinc`) **immediately after `base` is computed, BEFORE the ALBEDO/EMISSION split** — so a stain darkens/tints both channels and reads in every lighting condition. The RT is a transparent `SubViewport` (premultiplied color), so the composite is `base*(1 - a*strength) + premult_rgb*strength`, not a plain `mix`.
- The shader hook is a strict **no-op when unstained** (`ground_stain_enabled` false, or the fragment is outside the projector frustum / has zero coverage) → terrain is byte-identical to pre-feature. **Do not "fix" decal visibility by restructuring the ALBEDO/EMISSION lighting** (e.g. moving ambient/block light into `light()` so a `Decal` can darken it) — that path silently darkens all terrain and was reverted; the stain layer exists precisely to avoid touching the lighting model.

**Adding a new stain type:** give the source a flat quad proxy — a `MeshInstance3D` with a `PlaneMesh` on **layer 5** and an **unshaded, alpha-blended** material showing the mark texture (see `resources/materials/scorch_stain.tres`). The shader composites whatever the quad renders.
- **Static mark** (e.g. scorch on a fire trap): author the quad + material directly in the prop scene (`fire_trap.tscn` → `ScorchStain`).
- **Dynamic / high-volume mark** (e.g. footprints): batch into a MultiMesh rather than spawning a node + unique material per mark. `FootprintScatter` (`scripts/gameplay/effects/`, owned by `World`, created in `World.Initialize` next to `WorldPropScatter`) keeps one `MultiMesh` bucket per actor footprint **texture** (a MultiMesh = one mesh + one material = one albedo texture) on layer 5, with per-instance transform (pos/yaw/XZ-scale) and per-instance `INSTANCE_COLOR` carrying tint + animated alpha (template material `resources/materials/footprint_multimesh.tres` is unshaded, alpha-blended, `vertex_color_use_as_albedo`, `.Duplicate()`-d per texture with the actor texture bound). The scatter owns the per-mark lifetime fade and the mob-print discovery gate (ported off the old per-print `Discoverable` node) in its `_Process` — there is no per-mark Node. Tunings (material, per-ground tints, duration, discovery prominence/threshold/fade) live on `SimData`. The ground shader already light-matches the mark, so **don't pre-dim the mark by perceived light** — a print on dark ground reads dark because the ground is dark.

Per-mark intensity comes from the mark's own texture/tint alpha; `GroundStainProjector.strength` (and the `ground_stain` CVar) is the shared master. New globals follow the `ShaderGlobals` rules below (declared in `project.godot` + the texture seeded via `Register`).

### Visual Render Layers (`VisualInstance3D.Layers` / `Camera3D.CullMask`)

The game runs several off-screen `SubViewport` cameras alongside the main one, each culling to a **dedicated 3D-render layer bit** so it sees only its own geometry. These bits are a **shared global namespace** — every `GeometryInstance3D.Layers` value and every `Camera3D.CullMask` in the project draws from the same 20 bits — so a duplicate claim silently cross-feeds one system's meshes into another's projector. (This bit us once: the selection outline and the ground-stain projector both used bit 4, so highlighting a prop rendered its model into `ground_stain_tex` and smeared its color up nearby walls.)

The allocation is the single source of truth — keep these in sync (C# const is authoritative; the `project.godot` name is the editor label; **editor "Layer N" = bit N-1**):

| Bit | Value | C# constant | `project.godot` name | Culled by |
|-----|-------|-------------|----------------------|-----------|
| 0 | 1 | `GameCamera.MainSceneLayer` | `MainScene` | main camera |
| 1 | 2 | `GameCamera.CapMaskLayer` | `CapMask` | cap-mask camera (ceiling cutaway) |
| 2 | 4 | `GameCamera.OutlineMaskLayer` | `OutlineMask` | selection-outline mask camera |
| 3 | 8 | `BlockLightShadowProjector.SHADOW_PROXY_LAYER_MASK` | `ShadowProxy` | block-light shadow projector |
| 4 | 16 | `GroundStainProjector.STAIN_PROXY_LAYER_MASK` | `StainProxy` | ground-stain projector |

When adding a new off-screen pass: pick the **next free bit**, add a `1u << N` const, name it under `[layer_names]`'s `3d_render/layer_(N+1)` in `project.godot`, and add a row here. The names make a collision visible in the editor's Layers/Cull Mask dropdowns, but they do **not** enforce uniqueness — this table is the actual guard.

### Build-Time Code Generation (`hike.csproj`)

Two MSBuild targets run before compilation:
- `GenerateVersion` - writes `scripts/VersionGenerated.g.cs` with git hash and build number
- `GenerateLocKeys` - runs `tools/loc_generator` to generate `scripts/localization/LocKeys.g.cs` from `english.tsv`

### Godot UID Invariants (especially for headless agents)

Godot 4 tracks every importable file by a stable `uid://...` value. Scenes (`.tscn`) and resources (`.tres`) reference dependencies by `uid` AND by `res://` path; both must agree. The Godot editor maintains these automatically — agents running headless (mobile / remote Claude, CI worktrees) do not, so corruption is the failure mode to avoid.

**Rules when editing without Godot:**

- **Every `.cs` file under `scripts/`, `addons/`, `tools/` MUST have a matching `.cs.uid` sidecar** containing exactly one line of the form `uid://[a-z0-9]+`. When creating a new C# script, also create the sidecar with a freshly generated UID.
- **Never invent or copy `uid://` values** by hand for scenes, resources, or scripts you didn't author. Generating a fresh UID for a brand-new file is fine; reusing one from another file is not.
- **When moving or renaming a `.cs` file**, move its `.cs.uid` sidecar with it AND update every `[ext_resource ... path="res://..." ...]` reference in `.tscn`/`.tres` files to the new path. The `uid` in the reference stays the same; only the path changes.
- **When moving or renaming a `.tscn`/`.tres`**, the file's own `[gd_scene uid=...]` / `[gd_resource uid=...]` value stays the same. Update `path=` references in any other scene that points at it.
- **Never edit anything under `.godot/`.** That's the editor's cache, regenerated from sidecars and project files.

**When something is broken (UID errors at editor load, "missing dependency", etc.):**

Run `dotnet run --project tools/validate_uids` to scan for missing `.cs.uid` sidecars, duplicate UIDs, stale `path=` references, and uid/path mismatches. Add `--fix` to auto-create missing `.cs.uid` sidecars with fresh UIDs (other classes of issue are reported but not auto-fixed — they require knowing where things moved).

## Code Style

- Always use explicit curly braces for control flow statements (`if`, `else`, `for`, `foreach`, `while`, etc.), even for single-line bodies.
- Place opening and closing curly braces on their own lines (Allman style).
- Use descriptive consts instead of magic numbers.

## Key Conventions

- Any class derived from a Godot Node (or Resource) must be tagged with `[GlobalClass]`.
- No namespaces; all classes are global scope.
- Event communication uses C# `Action` delegates and Godot `[Signal]` attributes.
- Factory methods (`Create()`) for instantiating scene-backed objects.
- `[Export]` attributes for wiring node references. Never look up child nodes by iterating children or using `GetNode`/`GetChild` in `_Ready` — instead, declare an `[Export]` field and assign the node path in the `.tscn` file.
- Godot resources (materials, shaders, meshes, etc.) should not be created programmatically at runtime. Instead, create them as `.tres`/`.tscn` files in the Godot editor and wire them into scripts via `[Export]` variables.
- Static user-defined data belongs in Godot `Resource` subclasses named `[Object]Data` (e.g., `WeaponData`). Dynamic runtime state belongs in classes named `[Object]State` (e.g., `WeaponState`). Never use "Data" to refer to dynamic/mutable properties.
- **Never hardcode resource paths in C#.** Do not call `GD.Load<T>("res://...")` with a literal path from gameplay or worldgen code. Add an `[Export]` field of the appropriate `Data` type (typically on `WorldGenData`, `SimData`, or the nearest owning `*Data` resource) and wire the `.tres` reference in the editor. The exception is generic infrastructure that genuinely has no upstream owner (e.g. `EntitySerializer` reloading by serialized path); gameplay placement code is never that case.
- **Avoid hardcoded user-facing strings in C# too.** Sign text, dialogue, item names, conversation prompts, etc. live on a `*Data` resource (or in `english.tsv` for localized UI strings). Reach for a literal only when the string is a stable internal identifier (StringName key, action name, scene path) that is never shown to the player and never authored.
- **Tunable values are `[Export]` properties, not `const`s.** Any number, color, or duration that an author/designer might want to adjust for feel — speeds, radii, fade times, thresholds, intensities, BPMs, dB levels — belongs on an `[Export]` field (with a `PropertyHint.Range` where helpful) so it's editable in the inspector without a recompile. Do NOT bury these in `private const`. Reserve `const` for genuine non-tunables: stable internal identifiers (audio bus names, action/StringName keys, scene paths), array capacities / buffer sizes that the code's correctness depends on, and pure mathematical constants. When extracting a subsystem into its own node class, move its tuning `const`s out as `[Export]`s on that class so each component owns its own inspector-visible tuning. See the `[Export]`-precision note below for sub-0.01 values.
- **Exported floats authored at sub-0.01 magnitudes need explicit precision.** Godot's default `[Export] float` spinbox step is 0.001 — typed values finer than that snap to the nearest step (often to zero). The snap happens on UI input, not on save, so the .tres looks fine in the editor while the underlying value is wrong. Two fixes, pick by which one keeps authoring more honest: (1) invert the unit when the field is a density or rate so values land in the friendly 10..10000 range (`SquareMetersPerSpawn = 1000` instead of `Chance = 0.001`; "seconds to fully dry" instead of a per-second drying coefficient); or (2) if the small fraction IS the natural unit (a threshold against a normalized signal, a noise frequency), set `[Export(PropertyHint.Range, "0,1,0.0001")]` (or finer) so the spinbox respects the precision. Either way, never leave an unhinted `[Export] float` whose typical authored values approach 0.001.
