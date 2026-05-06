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

- `Fx` - One-shot particle effect lifecycle manager
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

### Action System: Weapons, Consumables, Interactives (`scripts/data/actions/`, `scripts/gameplay/actions/`)

A single `ActionRunner` (one per actor) drives all timeline-based player and mob actions. Two distinct authored data shapes feed it.

**Slot-driven actions (weapons, consumables, mob attacks)** — pressed via input or AI request:
- `ItemActionProfile` (`scripts/data/actions/ItemActionProfile.cs`): one profile per slot. Holds an `Array<ItemAction> chargedActions` (the tiers), profile-level `chargeEvents` / `chargeEndEvents` / `abortEvents`, and behavioral flags (`autoActivateAtMax`, `locksMovement`, `interruptOnDamage`, `queueable`/`queueWindowSeconds`).
- `ItemAction` (`scripts/data/actions/ItemAction.cs`): one charge tier within a profile. Carries `chargeTime`, `activeDurationSeconds`, `cooldownSeconds`, `events` (Active timeline), `readyEvents` (announce reaching this tier), combo position (`comboIndex` / `comboWindowMs`), `requirements`, abort/interrupt policy (`canAbort` / `canInterrupt`), per-tier charge curves, and per-tier charge fx (`chargeStartEffect`, `chargeLoopEffect`, `chargeCancelEffect`, `releaseEffect`).
- Profiles are referenced from `WeaponData.actionProfile`, `ConsumableData.actionProfile`, and `AttackBehaviorData.actionProfile`.

**Interactive actions (chest, door, torch, loot)** — pressed via Interact:
- `InteractiveAction` (`scripts/data/actions/InteractiveAction.cs`): one verb's behavior on an interactive. Self-describes its `verb` (EActionVerb) + `displayName` (StringName, used by future radial UI). Carries `interactEvents` (timeline during the wait), `completionEvents` (fired as a batch at `durationSeconds` — this is where `OpenInteractive` lives, so authors don't have to align an event time to the duration), `requirements`, `locksMovement`, `interruptOnDamage`. No charging, queueing, combo, or auto-activate — interactives have one phase that runs to completion.
- `IInteractive` (`scripts/gameplay/IInteractive.cs`): exposes `Array<InteractiveAction> GetActions(Player)` and `Complete(int actionIndex)`. The first entry is the default action; future radial UI iterates the array reading `displayName` for each entry. The `ActionRunner` calls `interactive.Complete(context.interactiveActionIndex)` from the `OpenInteractive` event handler.

**`ActionRunner` (`scripts/gameplay/actions/ActionRunner.cs`)**: single-action runner with one in-flight `PlayerAction` plus an optional queued action. Two `TryStart` overloads (one per data shape) — `ItemActionProfile` enters Charging, walks tier selection, fires `readyEvents` on tier promotion, transitions to Active on release / `autoActivateAtMax`; `InteractiveAction` enters Active immediately, walks `interactEvents` over `durationSeconds`, fires `completionEvents` from `EndActive`. Aborts (player-initiated `TryAbort`, damage-driven `TryInterrupt`) skip `completionEvents` for interactives; weapons consult per-tier `canAbort` / `canInterrupt`.

**`ItemEvent` (`scripts/data/actions/ItemEvent.cs`)**: shared timeline event used by both shapes. `type` is a bitmask of `EItemEventType` flags (Melee, Hitscan, UseAmmo, ApplyEffect, DecrementStack, ToggleMovingLight, PlayAnim, PlaySound, OpenInteractive, ConsumeFromInventory) — a single event can fire several handlers at once (e.g. a healing potion's release tick is `ApplyEffect | DecrementStack`). Per-flag fields are unioned on the resource; the inspector hides fields whose owning flag isn't selected (`_ValidateProperty`), but storage is preserved so toggling a flag off and back on doesn't lose values. **Wire values are stable: append new bits, never reassign existing ones**, so existing `.tres` files keep loading. The `fx` field is the per-event audiovisual cue (e.g. the chest-creak puff lives on the `OpenInteractive` event in chest.tscn, not on the chest's C# class).

**Player flow**:
- Press Interact: `Player.TryStartInteractiveAction(highlight)` calls `runner.TryStart(actions[0], context)` and stashes `(_curInteractive, _curInteractiveActionIndex)` so the existing movement-lock and Interacting-anim checks (`_curInteractive != null`) keep working unchanged.
- After `_runner.Tick()`: if `_curInteractive != null` and the runner is no longer busy, the interactive completed naturally — clear `_curInteractive` and `_highlightInteractive`.
- Cancel (Jump / Sneak / repeat-Interact press): `CancelInteract` calls `_runner.TryAbort()` if mid-interactive, then clears `_curInteractive`.

**Adding a new interactive**: implement `IInteractive`, expose `[Export] Array<InteractiveAction> _actions`, return it from `GetActions`, branch on `actionIndex` (or `_actions[actionIndex].verb`) inside `Complete`. Author the `.tscn` with one or more inline `InteractiveAction` sub-resources, each carrying `verb`, `durationSeconds`, `interactEvents`, and `completionEvents` (typically `[OpenInteractive]`).

**Adding a new weapon / consumable verb**: extend `EActionVerb`, author a new `ItemAction` tier on the profile with the new verb tag, wire its `events` and per-tier fx. The runner's tier-selection loop picks it up via `comboIndex` / `chargeTime`.

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
