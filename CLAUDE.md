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

New textures, models, animations, and sounds are sourced from browse-only external libraries on this machine (Unity AssetDump + first-party Pocketwatch projects), copied into `res://` per-file, and wired to repo conventions. **When adding an asset, read [docs/asset-sourcing.md](docs/asset-sourcing.md)** for the source locations, the copy-don't-bulk-import rule, and the Synty FBX wiring recipe (material override + scale).

## Architecture

The runtime node/scene skeleton (`Main` → `GameClient` → `World` → `ChunkManager`) — where things live and connect. See Subsystems for per-feature detail.

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

- `LinqExtensions` - MaxBy, MinBy, RemoveAtSwap helpers
- `Fx` - audio-visual effect lifecycle manager (full write-up under Subsystems → Audio-Visual Effects)

### World (`scripts/World.cs`) and Voxel System (`scripts/voxels/`)

`World` (Node3D) is the central hub that all world simulation entities (Player, Mob, Loot, Door, Torch, Chest) reference. It owns entity dictionaries for loaded props, mobs, and interactives, manages entity spawning/cleanup, world boundary walls, and delegates voxel operations to `ChunkManager`. `ChunkManager` (Node3D, child of World) handles streaming chunk mesh loading/unloading with frustum culling, the mesh rebuild queue, and the `LightMap`. It notifies World via `OnChunkLoaded`/`OnChunkUnloaded` callbacks so World can spawn or clean up entities for each chunk. `ChunkState` stores a 16x16x16 voxel array per chunk. `ChunkMesh` generates a culled mesh via `SurfaceTool` with per-vertex colors and trimesh collision. `VoxelType` enum defines voxel types (Air, Stone, Grass, Dirt, Sand). Player spawning is deferred until the spawn chunk's collision is ready.

## Subsystems

Per-feature summaries, most linking to a nested `CLAUDE.md` with the full detail.

### Voxel World File & Streaming (`scripts/voxels/`)

The world loads from a packed `.hike` file (`WorldFile` / `WorldFileChunkSource`) when `CVars.worldFile` is set, else `WorldGen.Generate()`; chunks are independently addressable via `IChunkSource`, with lighting and entity state baked into each blob. The seam for future async streaming and save-delta layers. See [scripts/voxels/CLAUDE.md](scripts/voxels/CLAUDE.md).

### World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The first step in the world-authoring chain: a broad-brush, in-game paint program that authors a layered raster *document* and bakes it into a real `WorldState` / `.hike` (the downstream `WorldEditor` does fine per-voxel detail; the game loads the baked `.hike`). See [scripts/worldmap/CLAUDE.md](scripts/worldmap/CLAUDE.md).

### Voxel Terrain Atlas (`scripts/data/BlockData.cs`, `resources/data/blocks/`, `tools/stitch_voxel_atlas.py`)

Each `BlockData` carries an `AtlasBaseIndex` into two baked `Texture2DArray` strips (color + normal/height) that `ChunkMesh` indexes by id; blocks never reference source textures directly. The layer→texture mapping is owned by one authoring-only resource, `resources/data/blocks/voxel_atlas_manifest.tres` (the single source of truth — edit it, not the Python/GDScript mirrors, to repoint a block). See [resources/data/blocks/CLAUDE.md](resources/data/blocks/CLAUDE.md).

### Save/Load System (`scripts/SaveGame.cs`)

Binary format with a version header. Currently stubbed -- writes/reads the header but the player status-effect buildup section (v2) and the scripting-variable bank (v3) round-trip.

### Scripting Variables — Quest Flags / World State (`scripts/data/scripting/`, `scripts/gameplay/scripting/`, `resources/data/script_variables/`)

A save-persisted bank of named `Bool`/`Int` variables that conditions/actions read and write **by name** to branch mob conversations and behaviors (quest flags, world state, counters). Read via `ScriptVarCondition`/`ScriptVarTransition`, write via `SetScriptVarAction`. See [scripts/gameplay/scripting/CLAUDE.md](scripts/gameplay/scripting/CLAUDE.md).

### CVars (`scripts/console/`, `scripts/CVars.cs`)

Runtime configuration variables with an in-game console. Add new CVars as `public static` fields in `scripts/CVars.cs` using typed subclasses (`CVarBool`, `CVarInt`, `CVarFloat`, `CVarString`). The constructor auto-registers them. Read values via `.Value` (e.g., `CVars.language.Value`). Set via in-game console, `cvars.txt` config file (project root, runs at startup), or `.Value =` / `.Set()` in code. Action CVars (type `None`) take a callback instead of storing a value.

### Localization (`scripts/localization/`, `resources/localization/`)

`Loc.Get(Loc.Keys.key)` and `Loc.Format(Loc.Keys.key, args...)` with `%0`/`%1` placeholders. Per-language TSV files (`resources/localization/english.tsv`) with `key\tvalue` columns. `Loc.Keys` enum is auto-generated on build from `english.tsv` via `tools/loc_generator`. Language controlled by `CVars.language`; changing it reloads strings and fires `Loc.OnLanguageChanged`.

**Adding strings:** add `snake_case` key to `english.tsv`, use `Loc.Get`/`Loc.Format`. Search for unlocalised strings via `.Text =` with `$"..."` or string literals.

### Mob AI System (`scripts/gameplay/MobAI.cs`, `scripts/data/behaviors/`, `scripts/gameplay/behaviors/`)

Per-mob hierarchical state machine driven by polymorphic Resource data — `BrainData` holds `BehaviorNode`s (a `BehaviorData` tuning subclass + gated transitions), and `BehaviorBase` runtime instances tick at 60Hz against perception slots in `MobSimState`. See [scripts/gameplay/behaviors/CLAUDE.md](scripts/gameplay/behaviors/CLAUDE.md).

### Action System: Weapons, Consumables, Interactives (`scripts/data/actions/`, `scripts/gameplay/actions/`)

A single per-actor `ActionRunner` drives all timeline-based player and mob actions from two authored shapes — `ItemActionProfile` (charge-tier actions for weapons / consumables / mob attacks) and `InteractiveAction` (chests / doors / torches / loot) — both consuming `ItemEvent` timelines flagged by an `EItemEventType` bitmask. See [scripts/gameplay/actions/CLAUDE.md](scripts/gameplay/actions/CLAUDE.md).

### Item Composition & Weapon Status-Effect Mods (`scripts/data/items/`)

Items are customized by **composition, not new `ItemData`** — a spawn source holds an `ItemDescriptor` (`ItemData` + `StatusEffectDescriptor` mods) composed onto the runtime `ItemState`, so **prefer a new mod over a new item variant.** Attacking mobs wield real `WeaponData` weapons (on `MobData.weapons`) through the identical damage + weapon-mod path. See [scripts/data/items/CLAUDE.md](scripts/data/items/CLAUDE.md).

### Rideable Vehicles (`scripts/gameplay/vehicles/`, `scripts/data/vehicles/`)

Board-and-ride vehicles (`Boat` now, mounts later) via the interactive system; `RideableVehicle` carries shared plumbing, subclasses supply physics. See [scripts/gameplay/vehicles/CLAUDE.md](scripts/gameplay/vehicles/CLAUDE.md).

### Audio-Visual Effects (`scripts/utils/Fx.cs`)

**`Fx` scenes are the canonical way to author particles, sounds, camera shake, and screen flashes** — build a `.tscn` on `Fx.cs` and spawn it with `Fx.Create` rather than creating `GpuParticles3D` / `AudioStreamPlayer3D` in code (a raw one under a non-`Fx` root never starts). Purely audio-visual, distinct from the state-mutating `ItemEffect` hierarchy. See [scripts/utils/CLAUDE.md](scripts/utils/CLAUDE.md) (also covers the general `AddChild`/`_ExitTree` node-lifecycle rules).

### Shader Global Uniforms (`scripts/utils/ShaderGlobals.cs`)

Every `global uniform` in a `.gdshader` MUST be seeded from C# at startup before the first material using it compiles — via `ShaderGlobals.Register` (globals also in `project.godot`'s `[shader_globals]`) or `ShaderGlobals.RegisterRuntime` (runtime-only globals). Miss it and you get `Global uniform '<name>' does not exist` or stale values. See [shaders/CLAUDE.md](shaders/CLAUDE.md).

### Minimap (`scripts/gameplay/minimap/`, `shaders/minimap.gdshader`)

Two parallel renderers behind one HUD widget — a global outdoor heightmap and a sparse per-slice indoor atlas — composited via an A/B crossfade so mode and slice-level changes glide. Exploration is a separate per-renderer R8 mask. See [scripts/gameplay/minimap/CLAUDE.md](scripts/gameplay/minimap/CLAUDE.md).

### Ground Stains / Decals (`scripts/client/GroundStainProjector.cs`, `shaders/ground_stain.gdshaderinc`)

Flat ground marks (scorch, footprints, blood) are **not** Godot `Decal`s (decals wash out wherever the terrain shader's `EMISSION` dominates). Instead a top-down `GroundStainProjector` renders proxy quads on visual layer 5 into `ground_stain_tex`, which the terrain shader composites into `base`; add a stain via a flat unshaded layer-5 quad (static in the prop scene, or batched via `FootprintScatter`). See [scripts/client/CLAUDE.md](scripts/client/CLAUDE.md).

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
- **Comment what the code can't say, and keep it brief.** A field or section usually needs one sentence. Do NOT narrate how the code evolved, restate what a name/signature already conveys, or justify design decisions that are already settled — code changes and that history reads as noise. Spend words only on what isn't visible in the code: non-obvious engine behavior, ordering/lifetime constraints, units, and gotchas we keep hitting (a genuine "don't do X, it breaks Y" worth a future warning). When unsure, cut.

## Key Conventions

- Any class derived from a Godot Node (or Resource) must be tagged with `[GlobalClass]`.
- **A `Resource` subclass embedded as a sub-resource (typed `[Export]` field) under a `[Tool]` parent must itself be `[Tool]`.** The editor can only instantiate a C# scripted resource as its real type if the class is `[Tool]`; without it, the editor loads the `[sub_resource]` as a base `Godot.Resource`, and the `[Tool]` parent's strongly-typed setter throws `InvalidCastException` ("Unable to cast object of type 'Godot.Resource' to type 'X'") and leaves the field **empty in the inspector** — silently, since it only happens in-editor (runtime has no `[Tool]` gate, so the game still works). This bit the `StatusEffectData` payloads (`WeaponModData`, `DamageOverTimeData`, etc.), which are `[Tool]` for exactly this reason. Match the parent: if it's `[Tool]`, its `*Data` sub-resources are too.
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
