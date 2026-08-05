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

### Running Headless (Automated Play)

The full game can run end-to-end with no window (dummy renderer) for smoke-testing and CI:

```bash
Godot ... --path . --headless -- "autostart 1" "autoplay 1"
```

- Every engine arg **after `--`** is run as a console command (`Main._Ready` → `CVarRegistry.ProcessCommand`), so any cvar can be set at launch without a persistent `cvars.txt` — CLI wins over the config file.
- `autostart` skips the main menu and launches a new game (respects `world_file` if set, else the default `WorldGenData`). `autoplay` spawns a `HeadlessBot` ([scripts/client/HeadlessBot.cs](scripts/client/HeadlessBot.cs)) that drives the player via synthesized global `Input` actions (wander + jump/dash/melee).
- The sim is renderer-independent (gameplay reads CPU `WorldState` arrays; volume maps are visual-only and no-op when `RenderingServer.GetRenderingDevice()` is null under the dummy renderer). SubViewport passes render nothing headless but don't crash. `ChunkMesh … GroundTint … tile_array` warnings under headless are **benign** (texture-array CPU readback is unavailable, so authored tints are kept).
- **`--quit-after N` counts FRAMES, not seconds.** Headless spins frames far faster than wall-clock worldgen completes, so a small frame cap quits mid-generation — use a wall-clock timeout (or a large frame budget) when you need the world to finish loading.
- **Don't sit on a fixed multi-minute timeout — kill on the last load marker.** A warm run prints `[Load] Total (to fade start)` at ~21s (`[Load] WorldGen cache HIT`; a MISS regenerates and is where the minutes go). Stream the output and kill on that line instead of waiting out a timeout.

### Checking That Shaders Still Compile

**`shader_check` is the shader loop — a full autostart run is not needed.** It loads every `.gdshader` in `shaders/`, prints `[shader_check] done`, and quits on its own:

```bash
Godot ... --path . --headless -- "shader_check 1"     # ~4s, all 49 shaders
```

Grep the output for `SHADER ERROR` / `Shader compilation failed`; a clean tree prints neither. This beats a gameplay run on coverage as well as speed — shaders reached only by a `GD.Load` in a rare code path (`mesh_outline.gdshader`) may not be touched at all by a short playthrough.

Measured boundaries, so you pick the right run:

| Failure | Cheapest run that reports it |
|---|---|
| Syntax / type error, bad `#include` | `--headless -- "shader_check 1"` (~4s) |
| `global uniform` not registered | **windowed** `-- "autostart 1"` (~10s to the line) |

- **Headless really does run the shader parser** — the dummy renderer's `shader_set_code` compiles the code, so `--headless` is legitimate for everything except the global-uniform check.
- **An unregistered `global uniform` is invisible headless, and it's a WARNING, not an error**: `Shader uses global parameter 'x', but it was removed at some point.` Grep for `global parameter`, windowed, when you touched a `global uniform` or `[shader_globals]`.
- **`--script` mode is useless here** — it never brings the rendering server up, so every shader loads "clean" no matter how broken. It must be a real boot.
- Loading a `Shader` resource does not compile it; the code only reaches the compiler once it's bound to a material (which is what `ShaderCheck.cs` does).

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

- `scenes/gui/main.tscn` - Root scene (Main.cs)
- `scenes/gui/game.tscn` - Game world with camera, lighting, environment
- `scenes/gui/main_menu.tscn` - Title screen
- `scenes/characters/player.tscn` - Player CharacterBody3D
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

Items are customized by **composition, not new `ItemData`** — a spawn source holds an `ItemDescriptor` (`ItemData` + `StatusEffectDescriptor` mods) composed onto the runtime `ItemState`, so **prefer a new mod over a new item variant.** Attacking mobs wield real `WeaponData` weapons (on `SpeciesData.weapons`) through the identical damage + weapon-mod path. See [scripts/data/items/CLAUDE.md](scripts/data/items/CLAUDE.md).

### Rideable Vehicles (`scripts/gameplay/vehicles/`, `scripts/data/vehicles/`)

Board-and-ride vehicles (`Boat` now, mounts later) via the interactive system; `RideableVehicle` carries shared plumbing, subclasses supply physics. See [scripts/gameplay/vehicles/CLAUDE.md](scripts/gameplay/vehicles/CLAUDE.md).

### Audio-Visual Effects (`scripts/utils/Fx.cs`)

**`Fx` scenes are the canonical way to author particles, sounds, camera shake, and screen flashes** — build a `.tscn` on `Fx.cs` and spawn it with `Fx.Create` rather than creating `GpuParticles3D` / `AudioStreamPlayer3D` in code (a raw one under a non-`Fx` root never starts). Purely audio-visual, distinct from the state-mutating `ItemEffect` hierarchy. See [scripts/utils/CLAUDE.md](scripts/utils/CLAUDE.md) (also covers the general `AddChild`/`_ExitTree` node-lifecycle rules).

### Shader Global Uniforms (`scripts/utils/ShaderGlobals.cs`)

Every `global uniform` in a `.gdshader` MUST be seeded from C# at startup before the first material using it compiles — via `ShaderGlobals.Register` (globals also in `project.godot`'s `[shader_globals]`) or `ShaderGlobals.RegisterRuntime` (runtime-only globals). Miss it and you get `Global uniform '<name>' does not exist` or stale values. See [shaders/CLAUDE.md](shaders/CLAUDE.md).

### Minimap (`scripts/gameplay/minimap/`, `shaders/minimap.gdshader`)

Two parallel renderers behind one HUD widget — a global outdoor heightmap and a sparse per-slice indoor atlas — composited via an A/B crossfade so mode and slice-level changes glide. Exploration is a separate per-renderer R8 mask. See [scripts/gameplay/minimap/CLAUDE.md](scripts/gameplay/minimap/CLAUDE.md).

### World Editor Undo / Redo (`scripts/editor/undo/`)

Snapshot-on-touch: a tool opens an `EditorEdit` off `EditorHistory`, declares what it is **about to** change (`TouchVoxel` / `TouchEntityChunk` / `TouchSpawn`) before writing, then commits — the edit captures "before" at touch and "after" at commit and drops itself if nothing moved. **Any new editor tool gets undo by touching what it writes; no tool ever writes undo logic.** A whole drag is one edit (Begin on press, Commit on release). Making a new *kind* of state undoable means one new `IEditorEditAspect`, not a case in a switch. `EditorRefresh` is the shared "what the live scene must redo" batch (relight + re-mesh changed chunks, respawn changed entity buckets) — brush strokes, subscene stamps and undo/redo all go through it.

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

- **Every `.cs` file under `scripts/`, `addons/`, `tools/` MUST have a matching `.cs.uid` sidecar** containing exactly one line of the form `uid://[a-z0-9]+`. When creating a new C# script, also create the sidecar — **mint its UID with `dotnet run --project tools/validate_uids --fix`** (or let the Godot editor create it), never by typing one out.
- **Never invent, copy, or hand-type `uid://` values.** A UID must be a random string like the tool/editor emits (length varies — 11 to 18 characters occur in this repo, so length proves nothing). A "memorable" or mnemonic value satisfies the `uid://[a-z0-9]+` format but is still invented — e.g. `uid://b2sdxefdesc4mqn` (≈ "sd-ef-desc") and `uid://bs1owm0t10nfx7` (≈ "slowmotion") are wrong, however valid they look. Generating a fresh UID with the tool for a brand-new file is fine; reusing one from another file is not.
- **Every `[ext_resource ...]` line must carry a `uid=` attribute, not just `path=`.** Godot backfills the missing attribute the next time it saves that file, so an omitted `uid=` becomes an unrelated diff in a later commit. The validator flags these and `--fix` inserts them.
- **A script's `.cs.uid` and every reference to that script must carry the identical UID.** The same value appears in the sidecar and in every `[ext_resource ...]` / `metadata/_custom_type_script` across all `.tscn`/`.tres`. When they drift (commonly: a hand-typed vanity sidecar vs. the editor-assigned value the content uses), `validate_uids` flags it — converge on the value the references unanimously use, not the lone outlier sidecar. Run the validator after any UID edit.
- **When moving or renaming a `.cs` file**, move its `.cs.uid` sidecar with it AND update every `[ext_resource ... path="res://..." ...]` reference in `.tscn`/`.tres` files to the new path. The `uid` in the reference stays the same; only the path changes.
- **When moving or renaming a `.tscn`/`.tres`**, the file's own `[gd_scene uid=...]` / `[gd_resource uid=...]` value stays the same. Update `path=` references in any other scene that points at it.
- **Never edit anything under `.godot/`.** That's the editor's cache, regenerated from sidecars and project files.

**When something is broken (UID errors at editor load, "missing dependency", etc.):**

Run `dotnet run --project tools/validate_uids` to scan for missing `.cs.uid` sidecars, duplicate UIDs, stale `path=` references, and uid/path mismatches. It resolves a target's genuine UID from its `.uid` sidecar, its `.import` file (for textures/models/audio), or its own `[gd_resource]`/`[gd_scene]` header, so resource-to-resource references are checked too — not just references to scripts.

`--fix` auto-creates missing `.cs.uid` sidecars, inserts absent `uid=` attributes, and reconciles sidecars/references to a strict reference majority. Two classes are deliberately **reported but never auto-fixed**:

- **A reference disagreeing with the target's own header uid.** A header can itself be fabricated (several in this repo spell out their filename), so neither side is reliably genuine. Only the editor knows which UID it has registered — open the project in Godot, let it re-save, and commit that as its own commit.
- **A tied reference vote.** Needs a human.

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
- **Per-tick reads: cost lives at the managed↔native boundary.** Anything read every tick, for every entity, should stay on the managed side. Three sources of boundary crossings, and only the first two are the problem:
  - **`Godot.Collections.Array` / `Dictionary` element access — and `.Count`.** These aren't C# containers; they're handles over a native Variant container, so every index/lookup marshals a Variant (plus an instance-binding lookup when the element is `Resource`-derived). `.Count` is itself a native call, so `for (i = 0; i < arr.Count; i++)` crosses twice per element — hoist it into a local. **When such a collection on a `*Data` resource is read per-tick, give it a lazily-built managed mirror ON THAT RESOURCE and have hot paths read the mirror** (`MobData.ModifiersFlat`, `MobData.AnimationsFlat`; build via `StatModifierUtil.Flatten`). Put the mirror on the resource, never on the consuming instance — 139 mobs sharing five `MobData` assets should share five flattened arrays, not allocate 139. This needs no invalidation *because* `*Data` is immutable after load; if something must mutate at runtime it belongs on `*State` instead. Cold collections (read at spawn / load / UI-open) stay as they are — this is triggered by access frequency, not by type.
  - **Godot engine properties and methods on nodes/resources** — `GlobalPosition`, `Visible`, `LinearVelocity`, `AnimationPlayer.HasAnimation()`. Resolve once per tick into a local and reuse; don't let two call sites each walk the same transform.
  - **NOT plain `[Export]` fields on your own C# `Node`/`Resource` subclasses.** `[Export] public float visionRange` is an ordinary managed field — `[Export]` only affects the editor and serializer. Reading it is free, and the `[Export]`-everything conventions above stand unchanged.
- **A read that costs something is a method, not a property.** A C# property reads like a field at the call site, so anything doing real work behind one — folding modifier lists, resolving a node transform, a line-of-sight query — gets sprinkled into per-tick code with no visible cost. Give those a verb name (`ComposeStat()`, `ShowsHudFeedbackAt()`) and reserve properties for genuine field forwarding (`health`, `alive`, `Level`). Related: **C# evaluates arguments eagerly**, so a cheap-looking call whose callee early-outs still pays for building its arguments — when the callee usually bails, expose the bail condition as a predicate the caller checks first (`DotHudAccumulator.WantsTick`).
- **Per-mob work picks a tick band.** `Mob._PhysicsProcess` runs at 60 Hz for *every* resident mob, so anything added there is multiplied by the whole population (139 at spawn in the default world) and again by the physics-steps-per-rendered-frame ratio (3–4× at low fps). Adding a subsystem means choosing a band, not defaulting into the hot one: **hot** for anything that must stay frame-accurate or is visible at range (animation, steering, perception, the action runner); **cold** — inside the `if (runCold)` gate — for rate-based upkeep that only has to integrate correctly (wetness, sunburn, status/DoT timers, terrain speed). Cold subsystems receive `coldDt`, the accumulated delta since that mob's last cold tick, so totals are unchanged and only granularity coarsens; the gate is distance-based (`SimData.mobColdTickDistance`) with a per-mob phase offset so the work spreads across frames. `mob_cold_tick 0` disables it for A/B. Self-throttling accumulators (`PerceptionTickInterval`, `LightSampleInterval`) and the `SuspendAITimeMs` AI LOD are the same idea applied per-subsystem — either is fine, silently running at full rate for all mobs is not.
- **Resident node count is a budget, and it is invisible to the C# Profiler.** Godot walks the tree to dispatch notifications and walks every `VisualInstance3D` to cull it (once per camera — this project runs several `SubViewport` passes), all *outside* any `Profiler.Sample` scope, so it lands in `unaccounted_ms_avg` and no section-level tuning touches it. **`node_census` in the console** (or `node_census_delay <sec>` for a headless run) dumps the whole tree bucketed by subtree / source scene / class, with the columns that actually cost: nodes in the process lists, `VisualInstance3D`s, and `CollisionObject3D`s. Read it before optimizing anything scene-shaped — a bucket with a big `total` and zeroes across those columns is cheap; a small bucket with a big `proc` is not. Two recurring shapes to avoid:
  - **Authoring-data nodes must not outlive their bake.** A `Node3D` that only carries `[Export]`s for a build step (`FoliageCluster`) is inert once consumed — free it at runtime (editor-gated, since that's where it's authored). It was 2545 nodes.
  - **`IsProcessing()` is not the whole story — check `intl`.** Godot runs `AnimationPlayer`, `Skeleton3D`, `AudioStreamPlayer3D`, `GpuParticles3D` and friends on a separate *internal* process channel that `IsProcessing()` does not report and no `Profiler.Sample` can wrap (there is no C# frame to wrap). That work lands in `process_ms` as pure `unaccounted_ms_avg`. The census's `intl` column is the only way to see it, and the only way to SIZE it is to switch it off and read the delta — hence the `fx_audio` / `fx_particles` / `skeleton_internal` bisection cvars. Don't conclude a class is free because `proc` is 0.
  - **Shared per-frame inputs are resolved once, not per node.** `PixelSnap` read the camera's viewport, size, projection and basis *per instance* — identical values for all 141 of them, and every one a native crossing. Centralising the resolve and skipping instances whose input hasn't moved took it from the most expensive `_Process` section in the game to a rounding error. When N nodes all read the same camera/player/world value, one driver should read it and hand it down.
  - **Per-entity UI is pooled, not owned.** Giving every mob its own HUD subtree + `_Process` costs the full population to draw the handful on screen. `MobHudManager` is the pattern: one node ticks a managed loop over the entities, applies a cheap "would this draw anything" gate, and leases a pooled widget to the few that pass — order the gate so the expensive terms (anything folding stat modifiers, any transform read) run only for candidates that already passed the free ones.
- **Timing: sim clock vs wall clock.** Pick the clock by whether a timer is *gameplay-authoritative*, NOT by which callback it sits in (`_Process` vs `_PhysicsProcess`). Anything that decides *when something happens in the world* — a despawn, a damage tick, becoming interactable, a telegraph firing, a cook job finishing — belongs on the **sim clock**: prefer a `GameTimeMs` deadline (`expireMs = World.GameTimeMs + seconds * 1000`, compare each tick), the pattern cooldowns / AI timers / traps already use. `GameTimeMs` advances in `World.Tick`, so it slows uniformly under slow-mo, is frame-rate independent, and survives save/load. Purely *presentational* timing — fades, bobs, spins, the death/HUD screens — stays on wall-clock `_Process` `delta` so slow-mo doesn't drag it and it stays smooth at render fps. Avoid accumulating a gameplay duration as `_ageSeconds += (float)delta` (especially on `_Process`); that's neither slowable nor frame-rate-independent. `Discoverable` is the reference split (perception on the sim side, sprite fade on `_Process`).
