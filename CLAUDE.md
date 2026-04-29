# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Hike is an isometric exploration game built with Godot 4.6 (C#/.NET 8.0). It features a voxel-based hand-designed environment with 2D sprite props, dual-stick controls, and pixel art visuals. Indoor and outdoor environments transition seamlessly using a ceiling cutaway effect that handles complex environments.

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

- `EffectOneShot` - One-shot particle effect lifecycle manager
- `LinqExtensions` - MaxBy, MinBy, RemoveAtSwap helpers

### World (`scripts/World.cs`) and Voxel System (`scripts/voxels/`)

`World` (Node3D) is the central hub that all world simulation entities (Player, Mob, Loot, Door, Torch, Chest) reference. It owns entity dictionaries for loaded props, mobs, and interactives, manages entity spawning/cleanup, world boundary walls, and delegates voxel operations to `ChunkManager`. `ChunkManager` (Node3D, child of World) handles streaming chunk mesh loading/unloading with frustum culling, the mesh rebuild queue, and the `LightMap`. It notifies World via `OnChunkLoaded`/`OnChunkUnloaded` callbacks so World can spawn or clean up entities for each chunk. `ChunkState` stores a 16x16x16 voxel array per chunk. `ChunkMesh` generates a culled mesh via `SurfaceTool` with per-vertex colors and trimesh collision. `VoxelType` enum defines voxel types (Air, Stone, Grass, Dirt, Sand). Player spawning is deferred until the spawn chunk's collision is ready.

## Documentation

### World File & Disk Loading (`scripts/voxels/io/`)

The game can load its world from a packed `.hike` file instead of running `WorldGen` at startup. This is the foundation for shipping a large hand-authored world produced by a custom editor.

**Format** (`WorldFile.cs`): single file per world, header + per-chunk index + payload. Each chunk's payload is independently addressable via `(offset, length)` in the index, so a future streaming loader can `Seek` to any chunk without loading or rewriting the file. Header carries world `Min`/`Max`, default `Spawn`, and the `SimData` resource path. Lighting is **baked into each chunk blob** so the runtime never has to recompute light at load.

**Components**:
- `IChunkSource` — interface (Min/Max/Spawn + `EnumerateChunkCoords` + `TryLoadChunk`). The seam where future streaming and save-delta layers will plug in.
- `WorldFileChunkSource` — `IChunkSource` impl. Opens the file, reads the header + full index up front, then `TryLoadChunk` seeks and decodes a single chunk. Thread-safe via internal lock.
- `ChunkSerializer` — single-chunk encode/decode (voxels + light + entity list).
- `EntitySerializer` — type-tagged binary read/write for `EntitySimState` subclasses. **Type tags are stable wire values — append new ones, never reuse old numbers**, so old world files keep loading after new entity types are added. `PackedScene` and `Resource` references are stored as resource paths.

**Bootstrapping** (`Main.cs`): `StartGame` checks `CVars.worldFile`. If non-empty, it builds a `WorldFileChunkSource`, pulls every chunk into a fresh `WorldState`, and uses the file's `Spawn` as the player position. Otherwise it falls back to `WorldGen.Generate()`. `WorldGen` is kept indefinitely as the editor's "generate basic world" template.

**Producing a world file** (`CVars.worldExport`): with a game running, `world_export <path>` writes the active `WorldState` through `WorldFile.Write`. Used for testing the disk loader against real data before the custom editor exists.

### Streaming a Large World (future)

The target is a hand-authored world of roughly **500×500×100 chunks (~8km × 8km × 1.6km of voxels)**, of which only ~1 in 20 chunks contains meaningful data. Procedural generation will not produce this; it will come from a custom editor that writes `.hike` files directly.

The current disk-loading path still loads **every** chunk into memory at boot. The streaming work will replace that without changing the file format — all the seams are already in place:

- **Async chunk loader** behind a worker thread, calling `IChunkSource.TryLoadChunk` for chunks the player approaches. Mesh generation in `ChunkMesh.Create` can be split into off-thread (`SurfaceTool` build) + main-thread (mesh upload + collision).
- **`WorldState` becomes a bounded cache.** `_chunks` is populated/evicted by `ChunkManager` as the player moves. Cross-chunk accessors (`GetVoxelWorld`, `GetSunlightWorld`, etc.) already return defaults for missing chunks, which is the correct behavior for unloaded neighbors.
- **`Min`/`Max` go away** (they make the world feel finite and break a sliding `LightMap`). `World.CreateWorldBoundary` and the current `LightMap` constructor depend on them; both need updating. A small world manifest file may take over for spawn / extent if walls are still wanted.
- **Sliding-window `LightMap`.** The current map is sized to the entire world bounds — at the target scale that's >100 GB of texture. Replace with a player-centric window covering the streaming radius (re-centered as the player crosses chunk boundaries). Since light is baked into each chunk blob, populating the window is just a copy — no propagation pass on load.
- **Save model is deferred.** When player mutations need persistence, the answer will be either a delta layer over the read-only authored data or copy-on-first-load into a save slot. Either way it's a second `IChunkSource` implementation (likely a `LayeredChunkSource`) — no change to anything that consumes chunks.
- **Entity sync-back.** Mobs walk; chests change state. Before a chunk is evicted, its live `Node3D` entities must flush their mutable state back to `EntitySimState`. The `IWorldEntity` interface is the right place for a `SyncToSimState()` hook.

**What not to break when working in this area**: keep chunk payloads independently addressable, keep `IChunkSource` as the only thing that touches the file format, keep entity type tags stable, and don't add any code path that iterates "every chunk in the world" — the design must remain compatible with worlds where most chunks are not resident.

### Save/Load System (`scripts/SaveGame.cs`)

Binary format with a version header. Currently stubbed -- writes/reads the header but no game state yet.

### CVars (`scripts/console/`, `scripts/CVars.cs`)

Runtime configuration variables with an in-game console. Add new CVars as `public static` fields in `scripts/CVars.cs` using typed subclasses (`CVarBool`, `CVarInt`, `CVarFloat`, `CVarString`). The constructor auto-registers them. Read values via `.Value` (e.g., `CVars.language.Value`). Set via in-game console, `cvars.txt` config file (project root, runs at startup), or `.Value =` / `.Set()` in code. Action CVars (type `None`) take a callback instead of storing a value.

### Localization (`scripts/localization/`, `resources/localization/`)

`Loc.Get(Loc.Keys.key)` and `Loc.Format(Loc.Keys.key, args...)` with `%0`/`%1` placeholders. Per-language TSV files (`resources/localization/english.tsv`) with `key\tvalue` columns. `Loc.Keys` enum is auto-generated on build from `english.tsv` via `tools/loc_generator`. Language controlled by `CVars.language`; changing it reloads strings and fires `Loc.OnLanguageChanged`.

**Adding strings:** add `snake_case` key to `english.tsv`, use `Loc.Get`/`Loc.Format`. Search for unlocalised strings via `.Text =` with `$"..."` or string literals.

### Mob AI System (`scripts/gameplay/MobAI.cs`, `scripts/data/behaviors/`, `scripts/gameplay/behaviors/`)

Per-mob hierarchical state machine driven by polymorphic Resource data.

**Data model (authored in `.tres`):**
- `BrainData` (`scripts/data/BrainData.cs`) — `idleBehavior` (StringName) + `Array<BehaviorNode> behaviors`. One brain per mob type, referenced from `MobData.brain`.
- `BehaviorNode` — `name` (StringName, per-brain instance id), `data` (`BehaviorData` subclass), `Array<BehaviorNodeTransition> transitions`.
- `BehaviorData` (base, `scripts/data/BehaviorData.cs`) — abstract per-behavior tuning. Subclasses live in `scripts/data/behaviors/` (e.g. `IdleBehaviorData`, `AttackBehaviorData`). Override `CreateRuntime()` to return a fresh `BehaviorBase` instance bound to this data.
- `BehaviorNodeTransition` — `condition` (`BehaviorTransitionData` subclass) + `destination` (StringName naming a sibling node).
- `BehaviorTransitionData` (base, `scripts/data/BehaviorTransitionData.cs`) — abstract transition predicate. Subclasses live in `scripts/data/behaviors/conditions/` (e.g. `AggroAcquiredCondition`). Override `Evaluate(Mob, ref PerceptionState)`.

**Runtime:**
- `BehaviorBase` (base, `scripts/gameplay/BehaviorBase.cs`) — runtime instance per mob. Subclasses live in `scripts/gameplay/behaviors/` (e.g. `BehaviorIdle`, `BehaviorAttack`). Override `Run(Mob, time, ref PerceptionState, ref AIOutput)`. Use `TryTransitions(...)` to evaluate the node's transitions; on a hit return `new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination)`. Otherwise write to `AIOutput` and return `Running`. Per-instance state (timers, sub-state) lives on the runtime instance — never on the shared data Resource.
- `Mob.InitBehaviors()` walks `mobData.brain`, instantiates each `BehaviorData.CreateRuntime()`, calls `Init(node)`, populates `_behaviors` (Dictionary<StringName, BehaviorBase>), validates transition destinations, sets `_curBehavior = brain.idleBehavior`.
- `Mob.TickAI(deltaTime, out AIOutput)` runs in `_PhysicsProcess` at 60Hz. Picks the highest-perception triggered slot from `_simState.PerceptionTargets`, then runs the current behavior; behavior output drives actuation (`Mob._PhysicsProcess` reads `AIOutput.pathTarget` and applies impulses, with damping toggling for braking).

**Perception:**
- `MobSimState.PerceptionTargets[]` — one `PerceptionState` slot per potential target (currently sized 1 for the player; preserved as an array for future multiplayer). Each slot has `perception` (slow-accumulating awareness), `triggered` (latched binary; sets when perception hits `MobData.PerceptionThresholdAlert`, clears at 0), `aggro`, `canSee`, `lastKnownPosition`, and the target reference.
- `Mob.UpdatePerception()` is throttled via `MobSimState.PerceptionTickAccumulator` / `PerceptionTickInterval` (~10Hz, jittered per-mob at construction so raycasts don't clump on the same frame). Behaviors stay at 60Hz so combat reactions are responsive.

**Adding a new behavior:**
1. Create `FooBehaviorData : BehaviorData` in `scripts/data/behaviors/` with `[Export]` tuning fields and `CreateRuntime() => new BehaviorFoo(this)`.
2. Create `BehaviorFoo : BehaviorBase` in `scripts/gameplay/behaviors/`. Constructor takes the data; `Run` calls `TryTransitions` first, then writes to `AIOutput`.
3. Add a `BehaviorNode` to the brain `.tres` with a unique `name`, the new data subclass, and any transitions.

**Adding a new transition condition:**
1. Create `FooCondition : BehaviorTransitionData` in `scripts/data/behaviors/conditions/` overriding `Evaluate`.
2. Wire it as the `condition` of a `BehaviorNodeTransition` sub-resource in the brain `.tres`.

Both base classes are non-abstract (`virtual` with `GD.PushError` fallback) so `[GlobalClass]` plays nicely with Godot's editor picker. Subclasses must be tagged `[GlobalClass]` to surface in the inspector.

### Shader Global Uniforms (`scripts/utils/ShaderGlobals.cs`)

Every `global uniform` declared in a `.gdshader` MUST be initialized from C# at startup, in the `_Ready` of whatever owns its per-frame `Set` calls (e.g. `SkyController`, `ChunkManager`). Use one of two methods depending on where the global lives:

- **`ShaderGlobals.Register(name, type, defaultValue)`** — for globals also declared in `project.godot`'s `[shader_globals]` section. The engine creates the variable at startup; this call seeds the C# default value before the first material that uses it compiles. Use for scalar/vector globals with sensible static defaults that you also want visible in the editor's Project Settings UI.
- **`ShaderGlobals.RegisterRuntime(name, type, value)`** — for globals NOT in `project.godot`. Calls `RenderingServer.GlobalShaderParameterAdd` directly. Useful for any global whose only meaningful value is computed at runtime and which does not need to exist in the editor. Note: if shaders that reference the global are ever opened in the editor's script editor or re-imported, the editor will fail to compile them — in that case, declare the global in `project.godot` with a placeholder and use `Register` instead (see `light_map` / `light_map_placeholder.tres`).

**Why both:** the runtime `RenderingServer.GlobalShaderParameterGet` and `GetList` APIs are editor-only, so we can't auto-detect at runtime whether a name is already declared in `project.godot`. The caller knows; pick the right method.

**Why initialize from C# at all:** a standalone launch (e.g. via VS Code → `Godot.exe`) compiles shaders very early and a global that hasn't been seeded yet either fails with `Global uniform '<name>' does not exist` (for runtime-only globals) or compiles with stale values (for project.godot-declared globals). Both methods must run before the first material that uses the global compiles.

**Sampler globals gotcha:** do NOT put a sampler global in `project.godot` with `value: null` — Godot will try to load `res://<null>` as a resource on startup. Sampler globals either need a real texture path in `project.godot` (use a `PlaceholderTexture*` `.tres` if the real value is runtime-constructed — `Register` will swap the value in at runtime), or they should be added at runtime via `RegisterRuntime`.

**`material_storage.cpp:1677 - "!global_shader_uniforms.variables.has(p_name)"` spam diagnosis:** this fires whenever `RenderingServer.GlobalShaderParameterSet(name, ...)` runs for a `name` that is not in the engine's shader-globals dictionary. Common root causes, in rough order of frequency:
1. **`Register` used for a global that is NOT declared in `project.godot`.** `Register` calls `Set` internally, which errors. Fix: switch to `RegisterRuntime` (creates the global), or add the project.godot declaration.
2. **`mat4` global declared in `project.godot`'s `[shader_globals]` section.** Godot 4.6's project-settings parser accepts the declaration well enough for shader compile to succeed, but the global never makes it into `RenderingServer.global_shader_uniforms.variables`, so every per-frame `Set` errors. Decompose into supported scalar/vector types (e.g. `vec3 origin`, `vec3 right`, `vec3 up`, `float size`) and reconstruct in the shader. Other untested types (mat2, mat3, ivec*, uvec*, bvec*) may have the same issue — stick to bool/int/float/vec*/sampler*.
3. **`[Tool]`-script `Apply()` runs in editor and pushes globals that are only registered behind `if (!Engine.IsEditorHint())`.** SkyController is `[Tool]`; its per-frame Set calls fire in the editor too. Move `RegisterRuntime` for any global Apply() pushes outside the editor-hint gate so the global exists in both modes (the gated block can keep allocations + non-shader work, but ANY global the per-frame pusher touches must be created in both editor and runtime).
4. **Stack-trace it.** Run `Godot.exe --path . --verbose` in a terminal that surfaces C# backtraces — the trace points at the exact `Set`/`Register` callsite and its global name. Vastly faster than guessing.

**Unshaded fragment output formula:** Godot 4 outputs `ALBEDO + EMISSION` for materials with `render_mode unshaded`. If your shader writes only `EMISSION = composited`, ALBEDO defaults to white and saturates the surface. Either explicitly `ALBEDO = vec3(0.0)` or write the composite to ALBEDO and zero EMISSION. The same applies to other render-mode-stripped paths — be deliberate about which output channel carries the color and zero the other.

### Build-Time Code Generation (`hike.csproj`)

Two MSBuild targets run before compilation:
- `GenerateVersion` - writes `scripts/VersionGenerated.g.cs` with git hash and build number
- `GenerateLocKeys` - runs `tools/loc_generator` to generate `scripts/localization/LocKeys.g.cs` from `english.tsv`

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
