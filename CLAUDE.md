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

### Verification Cost Discipline

Measured warm on this machine, so the choice is by cost rather than by feel:

| Step | Cost |
|---|---|
| `dotnet build hike.sln` (no-op or small edit) | ~8s |
| Godot headless **boot floor** (`--quit-after 1`) | ~3.5s |
| `block_check` / `shader_check` (boot included) | ~3s |
| `spawn_check` (boot + every spawn list resolved) | ~4s |
| `resource_check` (boot + all 776 `.tres` loaded) | ~7s |
| `autostart` to `[Load] Total (to fade start)`, worldgen cache HIT | ~4.5s (world load ~0.9s, chunk-mesh fill ~1.5s, entity spawn ~1.2s) |
| a full self-quitting headless smoke run (`exec ...; quit`), cache HIT | ~10s wall |
| A full `.hike` bake of the painted world (`worldmap_bake`, 6.9k chunks) | ~33s |
| Worldgen cache MISS | minutes |

**The boot is not the expensive part — building a world is.** So:

- **`dotnet build` is the default verification.** Run the game only for a failure the compiler cannot report. A rename, a signature change, a refactor that compiles is verified when it compiles.
- **Prefer a self-quitting check to a gameplay run.** When iterating on a subsystem that has none, add one — `ShaderCheck` / `BlockCheck` are the template (a `CVarBool`, an early return in `Main._Ready`, `GetTree().Quit()`), and it pays for itself on the second run.
- **One world-building run per task, at the end** — not per iteration. In particular, do NOT spend a run reproducing a bug that is already localized: when the exception names a file and line, fix it and verify once. An A/B "prove it was broken before" run is for a diagnosis that is still a guess.
- **A run over ~30s is announced and backgrounded**, never silently blocking. Say what it costs and why it is needed, then keep working while it runs.
- **Temporary instrumentation beats a bigger run.** A throwaway hook plus `GD.Print` reaches a code path in one 12s boot where driving the real UI to it costs minutes — just remove it in the same session.
- **Never pipe Godot's stdout into `grep` — redirect to a file and grep that.** Under Git Bash the pipeline returns as soon as grep is satisfied, Godot does not die of SIGPIPE, and you are left with an ORPHANED process. It holds `painted_world.hike` open, so every later `worldmap_bake` writes nothing while still reporting a plausible elapsed time, and you debug a "stale" blob that is simply the last one that landed. **Check the `.hike`'s mtime after a bake**, and `Get-Process | ? ProcessName -match Godot` if one seems to do nothing. This cost six 30s cycles once. (`worldmap_bake` now reports the real outcome — `BakeToWorldFile` returns a bool — but the orphan itself is still the trap.)
- **`1 resources still in use at exit` + `ObjectDB instances leaked` is INTERMITTENT and means nothing.** Roughly 1 run in 3–5, always paired, on `block_check` and `spawn_check` alike. Two separate investigations blamed a static holding a `Resource` and a mid-rewrite resource tree; both were disproved by re-running (a static would fire every time; a settled tree still leaks). Nondeterministic shutdown ordering, not attributable to any change. Exit code stays 0. Do not chase it, and do not re-derive it.

### Running Headless (Automated Play)

The full game can run end-to-end with no window (dummy renderer) for smoke-testing and CI:

```bash
Godot ... --path . --headless -- "autostart 1" "autoplay 1"
```

- Every engine arg **after `--`** is run as a console command (`Main._Ready` → `CVarRegistry.ProcessCommand`), so any cvar can be set at launch without a persistent `cvars.txt` — CLI wins over the config file.
- `autostart` skips the main menu and launches a new game (respects `world_file` if set, else the default `WorldGenData`). `autoplay` spawns a `HeadlessBot` ([scripts/client/HeadlessBot.cs](scripts/client/HeadlessBot.cs)) that drives the player via synthesized global `Input` actions (wander + dash/melee).
- The sim is renderer-independent (gameplay reads CPU `WorldState` arrays; volume maps are visual-only and no-op when `RenderingServer.GetRenderingDevice()` is null under the dummy renderer). SubViewport passes render nothing headless but don't crash. The one `ChunkMesh … GroundTint … tile_array` warning under headless is **benign** (texture-array CPU readback is unavailable, so authored tints are kept). It is one line per run because `TryGetLayerAverageLinear` memoizes FAILURES as well as successes — it is called per scattered detail sprite, not per world load, so without the negative cache every sprite in every chunk re-probed the atlas and re-warned with a full stack trace: 135s of a 148s headless load and ~375k log lines. If that flood ever returns, the negative cache is what broke.
- **`--quit-after N` counts FRAMES, not seconds.** Headless spins frames far faster than wall-clock worldgen completes, so a small frame cap quits mid-generation — use a wall-clock timeout (or a large frame budget) when you need the world to finish loading.
- **End the run with `quit` — don't let it ride the timeout.** `exec` accepts a `;`-separated chain, so `-- "autostart 1" "exec_delay 3" "exec tp ?; quit"` finishes in ~31s wall with exit 0. Without a terminating command the game has no reason to stop, the run burns the whole `timeout`, and wall clock tells you nothing about what it cost. Piping into `awk .. exit` / `grep -m1` does NOT stop it either — Godot does not die of SIGPIPE under Git Bash, so the pipeline returns while the process keeps running. A cache MISS regenerates and is still where the minutes go (~55s of worldgen on top).

### Reaching a Test Condition

**The console can put the game into a condition; do not walk there.** Most of the
~230 cvars either observe the running game (`debug_*`, `*_probe`) or set global
state (`time_of_day`, `weather`) — these four place the player instead, and they
exist because time-to-condition was the dominant cost in every check, manual or
automated:

| Verb | Does |
|---|---|
| `tp <poi>` / `tp <x> <y> <z>` | move the living party; `tp ?` lists the world's points of interest |
| `spawn <species> [count] [level]` | ring of **transient** mobs around the player; `spawn ?` lists species |
| `give <item> [count]` | drop an item at the player's feet; `give ?` lists items |
| `setup <name>` | run an authored scenario's command list; `setup ?` lists them |

- **The listing argument is `?`, never a bare verb.** `CVarRegistry.ProcessCommand`
  answers a value-less cvar with its current value and never invokes the
  callback, so a bare `tp` prints `tp = ` and does nothing. (Same reason these
  are `CVarString` and not action cvars: an action cvar's argument is DISCARDED.)
- **`spawn` is transient by design** (`Sim.SpawnMobTransient`). A debug spawn
  recorded in `WorldState` would persist into the worldgen cache and
  re-materialize on every later run of that world.
- **`give` drops rather than filling the backpack.** `Inventory.TryAdd` refuses
  non-materials, and potions / scrolls / fairy corpses do their real work in the
  world-pickup path — dropping exercises what the player actually does.
- **A scenario is just a command list** (`TestScenarioData` on
  `SimData.testScenarios`), so authoring one costs a resource and no code, and it
  picks up any cvar added later. **Author one per feature as you build it**; they
  accumulate into the test bed.
- **`tp` needs POIs, which are baked into the world file** (`WorldFile` v49).
  Worldgen resolves them from authored zone data and nothing recomputes them on
  load, so before v49 every POI was lost through a `.hike` or worldgen-cache
  round trip — which is every run but a cache MISS.
- **A launch line cannot call these directly.** CLI cvar args are processed in
  `Main._Ready`, before a world exists. Use the delayed driver:
  `-- "autostart 1" "exec_delay 8" "exec tp lake; spawn drake_mountain 2"`
  (`exec` is a `;`-separated command line, run `exec_delay` seconds after the
  game scene comes up).

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

### Checking That the Data Still Holds Together

**`resource_check` is the data loop** — `--headless -- "resource_check 1"`,
self-quitting, no world and no renderer. Two passes, because the failure it
chases is invisible from either side alone:

- **`[Tool]` closure**, read off the C# type graph: a C#-scripted `Resource`
  reachable from a typed `[Export]` on a `[Tool]` class must itself be `[Tool]`
  (see Key Conventions). It reports the gap **before** any data is lost, which is
  the only useful time — the bug is editor-only, so a runtime playthrough can
  never find it, and by the time a `.tres` comes back missing a reference the
  loss has already happened. Built-in engine types (`PackedScene`, `Texture2D`,
  `Curve`, `AudioStream`) are deliberately NOT flagged: they carry no script for
  `[Tool]` to gate. Neither is a field typed as bare `Godot.Resource` (no cast to
  fail), nor a gap under a parent that is itself not `[Tool]` — `WorldMapData` is
  deliberately un-`[Tool]` and correctly reports nothing.
- **Load sweep**: every `.tres` under `resources/` loads, and the script the file
  names is the script the loaded object ended up with. Catches a renamed or moved
  class, a broken dependency, and a parse error.

Grep the output for `FAIL`; a clean tree prints `[resource_check] ok`.

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

The runtime node/scene skeleton (`Main` → `GameClient` → `Sim` → `ChunkManager`) — where things live and connect. See Subsystems for per-feature detail.

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

### Sim (`scripts/Sim.*.cs`) and Voxel System (`scripts/voxels/`)

`Sim` (Node3D) is the central hub that all world simulation entities (Player, Mob, Loot, Door, Torch, Chest) reference. It is split across partial files by concern (`Sim.cs` lifecycle, `Sim.EntityStreaming.cs`, `Sim.SpawnLifecycle.cs`, `Sim.Spawning.cs`, `Sim.Environment.cs`, `Sim.Chunks.cs`), owns entity dictionaries for loaded props, mobs, and interactives, manages entity spawning/cleanup, the boundary walls (the static `WorldBoundary` helper), and delegates voxel operations to `ChunkManager`. `ChunkManager` (Node3D, child of `Sim`) handles streaming chunk mesh loading/unloading with frustum culling, the mesh rebuild queue, and the `LightMap`. It notifies `Sim` via `OnChunkLoaded`/`OnChunkUnloaded` callbacks so `Sim` can spawn or clean up entities for each chunk. `ChunkState` stores a 16x16x16 array of **block ids** per chunk (one byte, a `BlockData.blockId`). `ChunkMesh` generates a culled mesh via `SurfaceTool` with per-vertex colors and trimesh collision. Player spawning is deferred until the spawn chunk's collision is ready.

## Subsystems

Per-feature summaries, most linking to a nested `CLAUDE.md` with the full detail.

### Voxel World File & Streaming (`scripts/voxels/`)

The world loads from a packed `.hike` file (`WorldFile` / `WorldFileChunkSource`) when `CVars.worldFile` is set, else `WorldGen.Generate()`; chunks are independently addressable via `IChunkSource`, with lighting and entity state baked into each blob. The seam for future async streaming and save-delta layers. See [scripts/voxels/CLAUDE.md](scripts/voxels/CLAUDE.md).

### Building a world: WorldGen vs the painter, and what they share

**`WorldState` is the only output, and after it is handed off nothing reads the
generator.** Two things produce one: `WorldGen` and the world-map painter's bake.
The boundary is enforced by where code lives, and there are four files to know:

| File | Is | Rule |
|---|---|---|
| `WorldGen` | ONE RUN of the generator — a normal class you instantiate, not a static bag | Owns `ZoneField`, the level-noise fields, the road columns, the path hints, the height map. Nothing outside a run can see any of it. |
| `WorldFinish` | the passes a finished world derives from its OWN voxels | **Both producers end on `WorldFinish.Finish`.** Grades, detail scatter, moss, the roof/sky/classify air pipeline, fog, wind, water currents, cascades. |
| `TerrainMath` | pure world-shape math, no state at all | `SEA_LEVEL`, `DeriveSeed`, `MakePerlin`, `StampGradeShapes`, `FootprintPlateauY`, the voxel predicates. |
| `ZoneField` | where THIS world's zones are and how they blend | Per-run object. Handed to the terrain approach; never a global. |

Four rules fall out, each of which was a real bug:

- **A derived channel goes in `WorldFinish`, never on `WorldGen`.** Several of the
  channels `.hike` SERIALIZES are derived at the end from the finished geometry —
  `FogDensity`, `EnvTag`, `Interiorness`, `CurrentX/Z`, the shape channel, the
  detail scatter. `Main` relights a loaded world but does not RECLASSIFY it, so a
  channel the painter didn't know to call baked as zeros and stayed zero: painted
  worlds had no fog anywhere, read `Outdoor` inside every building and tunnel, and
  had no water currents. Nothing errored. Put a new pass in the `Finish` list and
  both kinds of world get it.
- **Per-run state is a FIELD on the run, never a static.** The statics this
  replaced outlived their run, were never cleared, and were readable from the
  painter's background bake — which meant a bake could silently read whichever
  world a previous `Generate` had left behind.
- **A spawn entry asks its `SpawnContext`, not a generator.** `SpawnContext.MobLevel`
  / `ForgeLevel` are the seam: worldgen installs its zone-band sampler on the
  contexts it builds, the painter installs its painted difficulty layer. Reaching a
  static sampler instead is how painted mobs came out at their species base.
- **A pass shared by both producers takes its per-column answer as a DELEGATE.**
  The pass is shared; the answer usually is not. `StampClimbSurfaces` takes
  `coverageAt` — worldgen answers with a zone's `climbCoverage`, the painter with
  its authored route flag. `StampMossPatches` takes `MossCoverageAt` — worldgen
  answers from `ZoneGenData` (moss density is a property of the biome it is
  generating), the painter from the kits its ground layer paints (there it is a
  property of the material the author put down). Do NOT try to force one shared
  source: the painter's zone palette is `ZoneData` and does not correspond to
  `WorldGenData.ZoneGens` at all — 15 entries against 5 in the default world, no
  index mapping and no reliable back-reference. Where a value genuinely IS the
  same question for both (fog's humidity), `ws.Zones[i].Data` is the one table
  both producers fill.
- **Nothing outside generation may assume where water is.** `TerrainMath.SEA_LEVEL`
  is the waterline a GENERATED world is built around, not a claim about the world:
  a painted lake sits where it was painted and a carve can open a dry cavern below
  sea level. Ask `WaterSurface`, which reads the voxels — it is also the one
  implementation of that query, after four call sites had grown their own.

What a run STARTS with — quests, party, initial knowledge — rides on `WorldState`
(`BindStartContent`), not on the `WorldGenData` passed to the client. A `.hike`
records the authoring resource's path in its header and re-resolves them on load;
before that a loaded world took all three from whichever template the menu had
selected.

### Where authored data lives (`worlds/`, `world_authoring/`, `world_gen/`)

**Three trees, split by REUSE. Dependencies run left to right and never back:**

| Tree | Holds | Test |
|---|---|---|
| `worlds/<name>/` | ONE world, whichever producer builds it — its `WorldGenData` **or** its painted `map/`, plus that world's own `WorldStartData` / `WorldFinishData` / `WorldScriptData`. A painted world links its start content through `WorldMapData.startContent` / `.finish`; the bake records the `WorldStartData`'s path in the `.hike` header and `WorldFileChunkSource` re-resolves it on load | is this world the only thing that wants it? |
| `worlds/shared/` | the GAME's fiction, used by every world: `npcs/` (conversations, appearances), `party/`, `quests/`, `script_variables/`, `regions/`, `languages/`, `buried/`, and the spawn entries / lists / groups that NAME a character, language or story beat | a proper noun, but not one world's |
| `world_authoring/` | the reusable authoring kit: `kits/`, `zones/`, `presets/`, `ground_sets/`, `prop_sets/`, `mob_sets/`, `spawn_entries/`, `spawn_lists/`, `spawn_groups/`, `mob_descriptors/`, `subscenes/`, `details/`, `roofs/`, `props/`, `editor/` | a type, kit or style — no proper nouns |
| `world_gen/` | the generator's reusable vocabulary: `ZoneGenData`, `RegionGenData`, `TerrainGenData` | nothing but the generator reads it |

**`ZoneData` is a theme, `RegionData` is a place** — which is why they sit in
different trees despite looking alike. A zone carries a palette (sky, water
colour, ambience) and no display name; a region carries nothing BUT a display
name ("The Crowns", "Mire of the Lost"). Themes are reusable, named places are
not.

Three rules, each of which was a real bug:

- **A leaf `SpawnEntryData` is a *what* and belongs in a `spawn_entries/`; only a
  placement rule is a *how*.** `world_gen/fixtures/` used to hold both, so the
  painter had to reach into the generator's folder for the forge and forked its
  own copies of the fountains. "Fixture" is a property of a placement
  (`EntitySimState.PlacedAsFixture`), never a kind of entity — don't name a
  folder after it.
- **Never embed a leaf as a `[sub_resource]` if a palette might offer it.** The
  painter's `entityPalette` is a `SpawnEntryData[]` and needs a FILE, so an
  embedded leaf is unreachable and the author has no choice but to write a second
  one. That is how three Muddish knowledge stones and two signposts with
  conflicting texts happened. (Forks the painter itself makes are the exception —
  see the next rule.)
- **A per-placement property lives on the PLACEMENT, and the shared entry carries
  NOTHING of it — not even a default.** A signpost's text is the case: worldgen
  embeds a per-POI copy in the containing `PoiPlacement`, the painter forks the
  entry into the placement on first edit (`EntityPlacement.EditableEntry`,
  copy-on-write), and `signpost.tres` itself holds only the scene. A "generic
  default" was tried and is the wrong shape twice over: whatever it says gets
  stamped at every placement nobody edited, and the language it is written in is
  a proper noun parked in reusable vocabulary. Language travels WITH the text,
  because an inscription is written in something and the two are one decision.
  An entry that cannot function without the placement's value refuses loudly
  (`SignpostSpawnEntry` errors and does not place) rather than drawing a blank.

**A baked `.hike` is not self-contained, and what it may reference is exactly
what a build SHIPS.** Its header stores `res://` paths and re-resolves them on
load — `SimData`, `StartContentPath`, and every `ZoneData` / `RegionData` — and
entity payloads reference resources through a table of `res://` paths. A
`<file>::<id>` sub-resource is fine there as long as `<file>` ships (a
`MobDescriptor`'s status effect, an NPC appearance's palette); `GD.Load` resolves
that form only from the resource cache, which is why `EntitySerializer.LoadRef`
loads the outer document first.

**The one document that does NOT ship is the painter's own `placements.tres`**,
and a reference into it is therefore stored BY VALUE instead — a ref-table slot
holding the resource's type and its stored properties (`WorldFile` v51). A
placement's forked entry can author its children inline (a merchant's
`LoyaltyGift`), and those had a path only the painter could resolve: cold, the
load failed loudly and `ReadPathRef`'s `as T` then handed back null, so the
merchant spawned minus its gift. Loud error, silent data loss. `WorldState
.AuthoringDocument` is what names the document; only flat records are stored this
way, and a non-empty collection on one is refused with an error naming the field
rather than half-written — a resource with structure of its own has earned a
`.tres` of its own.

So a shipped build needs the world's start content, zones and regions. It does
NOT need anything else under `map/`: the raster layers (`*.png`, `*.exr`,
`tunnels.bin` — 27 MB on its own), the `WorldMapData`, or `placements.tres`.

### World Map Painting Tool (`scripts/worldmap/`, `scripts/data/worldmap/`)

The first step in the world-authoring chain: a broad-brush, in-game paint program that authors a layered raster *document* and bakes it into a real `WorldState` / `.hike` (the downstream `WorldEditor` does fine per-voxel detail; the game loads the baked `.hike`). See [scripts/worldmap/CLAUDE.md](scripts/worldmap/CLAUDE.md).

### Blocks — the voxel material model (`scripts/data/world/`, `resources/data/voxels/blocks/`)

**The per-voxel byte is a `BlockData.blockId`.** There is no `VoxelType` enum — it used to carry physics AND appearance, and both moved:

| Concept | Lives on | Is |
|---|---|---|
| `BlockSurfaceData` | `resources/data/voxels/surfaces/` | one baked atlas layer + `porosity` (the only genuinely per-TEXTURE property) |
| `BlockData` | `resources/data/voxels/blocks/` | `top`/`side`/`bottom` surfaces, `wallBand`, `climbGrowthSurface`, sim flags, and every per-VOXEL material property |
| `BlockCatalog` | `resources/data/voxels/blocks/block_catalog.tres` | global id→block registry; ids are the wire format |
| `WaterFilmData` | `resources/data/voxels/films/` | the scum/algae layer a water block floats (`BlockData.waterFilm`) |
| `Blocks` | `scripts/voxels/Blocks.cs` | flattened `bool[]`/`int[]` tables for the hot paths |

Rules:
- **Hot-path physics reads `Blocks.IsSolid(id)` etc., never `BlockCatalog.GetById(id).solid`** — the tables exist because those loops run per voxel per chunk build.
- **A block's face is picked per FRAGMENT by slope**, not by the mesher: `block_faces[]` / `block_bands[]` uniforms, smoothstepped on `|normal.y|`. There is no auto/literal split — a block repeating one surface just blends between identical tiles.
- **Tile class (ground vs cliff) comes from the face slot**, top/bottom = ground, side = cliff. One texture can therefore be ground on a floor and cliff on the wall below it.
- **What grows on climbable rock is a per-BLOCK choice, not a per-zone one.** `BlockData.climbGrowthSurface` feeds BOTH halves of the climb affordance through **one mechanism**: worldgen paints it down tall cliff faces (`StampClimbSurfaces`, stored in `OverlayId`), and `ChunkMesherDC` dresses every mantleable lip voxel in the same overlay at mesh time (appearance only — never written to `WorldState`, so it cannot make a two-voxel step climbable). A lip matches the wall it caps by construction, not by keeping two blend paths in sync. Keying it per zone cannot express the cases that matter: `CaveSandstone` and `CaveLimestone` are one zone's caves and everyone else's, and `ChunkState.ZoneIndex` is per CHUNK, so a zone-keyed mark would step along 16 m boundaries instead of following the terrain. It is not on `BlockSurfaceData` either — wall surfaces are shared far too widely to carry it (`surface_stone` is the side of Grass, MarshGround and both cave blocks). `ZoneGenData.climbCoverage` stays per-zone because how MUCH rock is dressed is genuinely a zone call; only WHAT grows comes from the block.
- **There is no `Blocks.WaterId`, and reintroducing one is a bug.** Water is SEVERAL blocks — `Water` (the standard, turbidity delta 0), `WaterClear`, `WaterMurky` and the scum types, with ice to come — so an equality test against one id silently stops being "is this water" the moment a body is anything but standard: you would not swim in it, boats would ignore it, nav would read a hole, the waterfall finder would skip it. Ask **`Blocks.IsWater(id)`**, which is fed by `render == EBlockRender.Water` and so covers every type including ones added later. **`Blocks.DefaultWaterId` is for WRITES only** — the block to lay down when something fills a column and has no reason to pick a type — and is deliberately named so it cannot be mistaken for a test. The migration off the old symbol was compile-driven (delete it, fix the 66 errors); that is the only safe way to do it again, because every one of those sites still compiles when it is wrong.
- **What a water body IS comes from the block; what floats ON it comes from the block's `waterFilm`.** The zone still owns the water's hue and baseline clarity (`ZoneData.waterColor` / `waterOpacity`) — that is how a region defines its palette — and a block only says how this body DIFFERS from it, via `waterTurbidityDelta` applied as a push toward the endpoint rather than a clamped addition. An absolute would mean authoring one scum block per zone. A film's colour comes from its own texture and `WaterFilmData.tint`, which is what lets one baked atlas row serve green and red scum.
- **Several water types mesh as ONE volume.** `WaterMesher` culls against `Blocks.IsWater`, so a clear tarn and the scummy shallows at its edge share a surface with no seam, and the per-fragment block id in `CUSTOM0.x` is what makes them look different. A SHELL cell (the one-voxel dilation into the shore) is solid, so it borrows its block from the water it shells — the same move `ShelledWaterIsOpen` makes, and without it every scummy pond gets a ring of ordinary water.
- **`Blocks.IsEmpty(id)` is not `id == AirId`** — an Opening is empty in every sense except the ceiling cutaway's.
- **Appearance is never resolved at draw time.** Re-texturing a stamped scene rewrites block ids at stamp time, which is why a block can own physics and appearance together without a wall going soft in another biome. `SubsceneStamper` decides per voxel with **`ws.Kits.IsKitGround`**: a block that is some kit's ground is a biome statement, and a scene has no biome, so it adopts the kit it lands in — while anything else (a stone wall, a plank floor, cobbles, a dirt path) is a deliberate material that survives the journey unchanged. Block ids are global, so a grass voxel authored in one world is recognisable as kit ground in another even though the SLOT it sat in is not. Two traps, both hit here: inheriting only the `TerrainId` byte changes nothing visible now that appearance lives on the block (a scene authored on forest soil kept it in a desert), and `BlockData.naturalGround` is the wrong test for "is this biome ground" — it answers "may the road pass grade across this?" and is true of Road and Dirt.
- **`block_check`** (`--headless -- "block_check 1"`, ~3s) validates the catalog and dumps the resolved table — the data twin of `shader_check`.

Worldgen still keeps a parallel **kit** channel (`TerrainId`): `ws.Kits.BlockFor(id)` maps a slot to its block, and the channel survives generation only because `EKitPurpose` and the per-kit scatter tunings need it. Appearance is not among its jobs.

**The kit palette belongs to the WORLD, not to the process** — `WorldState.Kits`, a `KitPalette` built from the authored `KitPaletteData` on `WorldGenData.kitPalette`. Two rules follow from what it is:

- **It is the `.hike`'s wire format, and it is APPEND-ONLY.** `TerrainId` is one byte per voxel holding an index into it. Insert, remove or reorder a slot and every world already baked comes back re-textured, with the stored bytes still perfectly valid — they just mean a different kit. `WorldFile` v46 records the slot paths and `Main.LoadWorldFromFile` refuses a world whose palette moved, naming the slot; `block_check` dumps the resolved table so a diff proves an edit only appended.
- **It is AUTHORED, not derived.** It used to be built by walking `WorldGenData.zones` and collecting each zone's four kit slots in declaration order, which made the wire format a side effect of zone *placement*: adding a zone re-textured every baked world, and a kit no zone referenced had no slot at all, so anything naming it silently fell back to slot 0.

It was a set of `WorldGen` statics (`ActiveKitPalette`, `_kitIndex`, `_kitPurposes`, plus a `KitBlocks` table) bound by whichever of six call sites ran last. That was world state parked in the generator: it outlives generation, it is read by code with nothing to do with generation (the mesher, `SubsceneStamper`, the editor, the map painter), and the painter's bake wrote it from a background thread while the painter was live — which is why "one bake at a time" had to be a rule. Nothing was lost by moving it: every hot-path reader already had the `WorldState` in hand.

See [resources/data/voxels/surfaces/CLAUDE.md](resources/data/voxels/surfaces/CLAUDE.md) for the atlas-baking half.

### Spawn Entries & Lists (`scripts/data/spawn/`, `resources/data/world_authoring/spawn_entries/`)

**What a thing is and how a list uses it are separate resources.** A
`SpawnEntryData` (`MobSpawnEntry`, `ForageSpawnEntry`, `ChestSpawnEntry`, …) is a
SHARED asset in `resources/data/world_authoring/spawn_entries/` holding only what is true of the
thing wherever it appears — its descriptor or item, its scene, its placement
gates. A **row** names one of those and adds what THIS container says about it,
so a zone's entity list reads as a list of named files with a number each:

| Row type | Container | Adds |
|---|---|---|
| `SpawnRow` (base) | — | `entry`, `spawnConditions` |
| `SpawnListRow` | `SpawnListData.rows` | `squareMetersPerSpawn` (per-column area rate) |
| `SpawnGroupRow` | `SpawnGroupData.rows` | `countMin`/`countMax`, `placeAtAnchor` |

The two containers ask different questions — a list wants a rate per area, a
cluster wants a count and a position within itself — and neither can act on the
other's answer, so they are separate types. One shared row type carrying all of
it put three dead fields on every list row, and a field that cannot affect its
container is worse than a missing one: it invites tuning that does nothing.

- **Per-container values must not migrate onto the entry.** They genuinely differ
  per list, which is the whole reason for the split: the mountain goblin is
  night-only on the surface and any-time in a cave, and a camp holds 2–3 of a
  goblin the surface scan places one of. Authored the other way round, each list
  had to embed its own copy of every entry and one well was re-authored in three
  files. `spawnConditions` is on the shared base because it is the one question
  BOTH containers ask.
- **`spawnConditions` reaches `Spawn` on the `SpawnContext`**, stamped by the row
  immediately before each spawn. `Spawn` is overridden by ~20 entry types and only
  three (mob, npc, chest) have a sim state that can defer on a condition, so
  widening every signature for it is the worse trade. Nothing clears the field —
  every caller sets it for the row it is about to place.
- **An entry stays embedded when it is genuinely one-of-a-kind**: the villagers in
  a house list (`NpcSpawnEntry` — each carries its own conversation, outfit and
  palette), a campfire fixture with authored text. Hoisting a single-use entry
  into a shared file buys nothing.
- **`spawn_check`** (`--headless -- "spawn_check 1"`, ~4s) dumps every list's
  resolved rows with every stored property. There is no runtime error mode here —
  a dropped rate or condition just quietly stops placing something — so a diff of
  this output is how an edit to these files is proved to have changed nothing else.

### Save/Load System (`scripts/SaveGame.cs`)

Binary format with a version header. Currently stubbed -- writes/reads the header but the player status-effect buildup section (v2) and the scripting-variable bank (v3) round-trip.

### Scripting Variables — Quest Flags / World State (`scripts/data/scripting/`, `scripts/gameplay/scripting/`, `resources/data/worlds/shared/script_variables/`)

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

### Waterfalls (`scripts/gameplay/entities/Waterfall*.cs`, `shaders/waterfall.gdshader`)

A cascade's drop is **air** in the voxel grid and stays air (standing water there floated the player back up it), so the fall is drawn by an entity instead.

**Where the falls are is read off the FINISHED VOXELS, under one rule** (`WaterfallFinder`, called by `WorldFinish.PlaceWaterfalls`): wherever a water voxel sits beside an air voxel, the topmost air voxel of that air span with water beside it is a **lip**, and the sheet runs from there down to the floor of the span. Lips group 8-connected and by the level they pour from — a five-wide sheet is one cascade wanting one effect, an outside corner turns through a diagonal so its two perpendicular strips must reach the same entity, and two pools at different heights spilling past each other stay two falls. Nothing shorter than the smallest authored tier becomes an entity. There were two derivations of this before, neither reading the world: worldgen walked a scratch water surface down its own staircase and the map painter tested its painted height/water layers, so each saw only the falls its own pass had a notion of and neither saw one made by a carve, a stamped scene or a hand edit. That is the whole reason falls went missing.

**The sheet is a jet swept off that edge, not the water that would stand in the drop.** `WaterfallMeshBuilder` traces the ballistic profile from an authored exit SPEED (`pourSpeed`) — the water leaves horizontally and gravity bends it over, so the throw at the foot is `v·√(2·drop/g)` and GROWS with the drop; it lands `landingDepth` inside the pool below, and closes the wedge between two perpendicular strips fed by the same pool column with a quarter-turn corner skirt. Skinning the voxel columns the fall passes through was tried first and is wrong twice over: those columns are a *staircase* walked out from the lip, so they draw a slab of standing water reaching out over the terrain, notched wherever the staircase stepped — and their top faces lay flat water on the rock shoulders beside the drop. Samples are bunched toward the lip (`shoulderBias`): the profile's tangent is horizontal at the very top, and evenly spaced ones put the first polygon past the turn, so the sheet reads as leaving the pool at an angle. The reach was authored as a flat distance in metres first, which inverts the physics: every fall threw its water the same distance out whatever its height, so a 1 m weir arced as far off its lip as a 12 m cascade and read as a chute.

**Both waters run the SAME shading code**, not parallel copies: `shaders/water_shading.gdshaderinc` owns the lightmap + cloud-shadow + block-light globals, the per-zone colour, `water_light` / `water_compose` / `water_adapt`, and `voxel_water` and `waterfall` both call it. It was written as a parallel copy first and drifted immediately — the copy's missing reflection term alone stopped the top of a fall reading as water. **A fall then lerps between the two behaviours by the Y of its own normal** (`surfaceness`): thickness, reflection, foam, face shade and which side the lightmap is read from are all one blend on it, so the shoulder rolling off the lip IS the pool surface and becomes a falling sheet exactly as the geometry turns. Two things the fall still handles itself:
- **Thickness.** A pool reads its depth off the depth buffer; a curtain in open air would measure the far side of the valley. So thickness is authored per size tier, modulated by the flow streaks (thin streaks are the gaps you see through) and multiplied up by `surface_thickness_scale` where the sheet is still acting as a surface.
- **Where the light is sampled.** A pool reads the lightmap half a voxel BACK, inside its own cell. A sheet must read FORWARD into the air it hangs in — backwards is the cliff, and the sample comes back black.
- **Sparkle.** Falling water tears into droplets rather than presenting one surface, so per-pixel jitter is added to the reflection normal — one independent value per snapped cell (sampling `vnoise` on integer coordinates lands on its lattice, so the interpolation weights vanish and the raw hash comes back, which is what separates glitter from a wobble). It rides on the up-tilted normal below, never the raw one, or it re-triggers the cloud-plane degeneracy.
- **Which normal the reflection is taken off.** NOT the sheet's own: the geometry swings from flat-up to vertical within a few centimetres of the lip, so a ray reflected off it sweeps through the horizon in a handful of pixels — and `sample_sky` projects clouds onto their plane at `t = (altitude − y)/max(dir.y, 0.05)`, so the sample point runs away by hundreds of metres per pixel and the cloud texture comes back as crawling speckle over the top of every fall. The reflection normal is lerped to +Y by `surfaceness`, so wherever the term has weight the shoulder reflects as the pool it continues.

**The cutaway is decided at the LANDING, not at the entity's origin.** `GameClient` hides a whole entity node once its `GlobalPosition` is above the clip height, which assumes the origin sits at the bottom of what it draws — and a waterfall's node is on its *lip*, because that is the chunk it files into. So a fall whose top poked above the cut vanished entirely, taking the part still under the ceiling with it. `Waterfall` implements `IClipAnchored` and names its base instead; the sheet itself was always cutting correctly per fragment in-shader. (Residual: in the straddling case the lip spray `Fx` draws above the cut, since particles carry no clip of their own.)

**The fall BLENDS; the pool composites by hand — and mixing that up is a real bug.** `voxel_water` samples `screen_tex` to get the ground under it, which works because the ground is opaque. The cascade cannot do the same: **Godot captures the screen copy once, before the transparent pass**, so a transparent object never sees another transparent object in it *whatever their render priorities say*. The pool is transparent (priority -3), so the sheet's `behind` sample returned the opaque scene behind the pool — bare rock — and a fall hanging in front of bright water drew the grey cliff over it. No ordering fixes that, because the copy predates both draws. The sheet therefore uses `blend_premul_alpha` and lets the hardware blend read the real framebuffer, emitting its own light in `ALBEDO` and putting only "how much of the background is hidden" in `ALPHA` — which is exactly the split `water_compose` already returns. The costs are explicit: screen-space refraction is impossible (it needs the copy), and alpha is scalar, so the tint absorption would give the background is lost.

**Extinction through the sheet is TWO terms, and only one of them is the pool's.** `water_absorption` is clear water swallowing light wavelength by wavelength, and it is *slow* — SkyController derives it at ~0.08/m, so a one-metre sheet stops about 8% of what is behind it and no authored thickness reaches opaque (that would take tens of metres). What makes a real fall opaque is the AIR in it: a bubble cloud scattering strongly and with no colour preference, which is why whitewater is white and not blue. So the falling half adds a second, grey `aeration_density` term. Miss this and the curtain is a ~95% transparent window onto whatever is behind it, every gap opened in the streaks comes back dark, and no amount of tuning the streaks or the foam fixes it because the water is barely optically present. Its counterweight is `falling_ambient` and it is not optional: making the sheet opaque takes away the background it was borrowing brightness from, so a fall in shadow gets *darker* the more solid it becomes, and `WATER_MIN_AMBIENT` is 0.

**The fall is TWO layers of water, composited as optical depth** — a front veil and a denser, slower back curtain starting `back_start_depth` below the lip, whose thicknesses ADD because two layers of water along one ray is one longer path through water. This is what lets the streaks be genuinely gappy: a gap in a single sheet exposes the shadowed rock behind it, so every notch of extra noise made the fall darker, whereas a gap in the front layer now exposes more water. Real geometry cannot do it here — the sheet is `depth_draw_never` and composites by hand against `screen_tex`, which Godot captures BEFORE the object sampling it, so a second surface only shows through the front's gaps as a separate draw at a lower priority (a second full-screen copy, plus another slot in the `-3/-2/-1/0` band); both layers in one mesh and the front's gaps still sample the cliff.

**Whitewater on the falling half is a FLECK field, not a gradient**, built the way shoreline surf is: the sheet position is snapped to a pixel grid *before* the noise is read and cut with a narrow threshold band, so a speck is opaque whitewater or it is absent. How much foam a fragment carries — the tier's `foam`, the plunge ramp boiling on the churn, break-up along the streak ropes — pulls that threshold DOWN rather than scaling the result, so a quiet fall gets *fewer* specks instead of greyer ones. Two things it must not be: the noise cell is deliberately anisotropic (a fleck is a vertical dash of falling water, and an isotropic cell fine enough to read as a speck decorrelates every frame at `flow_speed` and strobes like static), and the thresholds are calibrated against the field's measured distribution — `vnoise` is bell-shaped about 0.5, not uniform, so a threshold picked as though it were flat lands almost entirely on or off. Flecks also thicken the sheet under themselves (`fleck_opacity_boost`): aerated water hides what is behind it, and without that the specks hover over a curtain you can still see straight through.

**The shoreline surf is shared too** (`water_shore_foam`), and it has to be: the shoulder is drawn OVER the pool's last half-metre, so a band the pool paints and the sheet does not reads as a hole punched in the surf exactly at the lip. Each caller supplies only how its own water is running out — a pool measures that off its depth; the shoulder is water about to leave the ground and passes full shallowness. It lived in `voxel_water`, and that hole is what it cost.

Size is one authored axis: `SimData.waterfalls` (a `WaterfallData`) holds four `WaterfallTierData` tiers picked by fall height, each carrying its own looping base ambience, lip/base spray `Fx` scenes, sheet thickness and foam. Spray emitters are **spaced** along the lip and landing lines rather than counted, so a one-column trickle gets one plume and a wide curtain mists along its whole width. A fall shorter than the first tier's `minFallHeight` draws nothing, and that threshold is the ONLY thing deciding how small a drop is worth drawing — the finder reports every lip it sees. The floor is 1 m (the `Rapids` tier): a one-voxel weir in a river is visible from the ground and reads as broken without a sheet, so "too small to be a waterfall" is not the same as "too small to draw".

### Water Types — clear / murky / scum, and ice later (`scripts/data/world/`, `WorldFinish.StampWaterTypes`)

**Water is SEVERAL blocks, not one.** `Water` (the standard), `WaterClear`, `WaterMurky`, the scum types, and ice when it lands. A block says what a body of water IS; the zone still says what the region's water LOOKS like. Three places, and the split is the whole design:

| What | Lives on | Because |
|---|---|---|
| the region's water hue + baseline clarity | `ZoneData.waterColor` / `waterOpacity` | this is how a zone defines its palette, and it must stay independent of `dustColor`, which is the region's AIR (fog is `dustColor` outright) |
| how far THIS body sits from that | `BlockData.waterTurbidityDelta` | one scum block then works in every zone instead of one per zone |
| what floats on it | `BlockData.waterFilm` (a `WaterFilmData`) | the film's colour is its own texture × `tint`, so it owes the zone nothing |
| WHICH body is which | the world-map painter's per-column water-type layer | authored, never derived — see below |

- **The delta is a PUSH TOWARD AN ENDPOINT, not a clamped addition**: `d > 0 ? mix(zone, 1, d) : mix(zone, 0, -d)`. Clamped addition saturates uselessly — `+0.3` does nothing in a swamp already at 0.9. 0 is the exact identity, which is why the standard block leaves every already-baked world unchanged.
- **The optics are derived TWICE and the two are checked against each other.** `SkyController` pushes the endpoints (`water_hue`, `water_sediment`, the absorb/albedo pairs, `water_zone_muddiness`) and `water_shading.gdshaderinc`'s `water_optics()` redoes the zone's own derivation per fragment. `SkyController.VerifyWaterOpticsParity` asserts the shader's delta-0 answer equals the C# one and errors on drift — deriving one model in two languages is otherwise how they part company silently.
- **The zone's FINISHED globals stay.** Puddles (`voxel_clip`), both sprite-reflection shaders and `water_clip_cap` are made of water but carry no block id to look a type up with, so `water_absorption` / `water_scatter_color` / `water_muddiness` remain and are the right answer for them.
- **A film's colour is its texture × `WaterFilmData.tint`**, which is what lets ONE baked atlas row serve green and red scum. The tint multiplies, so values above 1 are legitimate — pushing an olive tile to rust needs them. **The silhouette comes from the tile's own HEIGHT map**, thresholded by `breakup` (the fraction cut away). That height is baked RANK-NORMALIZED — uniformly distributed — which is what makes `breakup` linear: 0.25 really removes a quarter. Derive one for colour-only art rather than doing without; the procedural fallback thresholds a PIXEL-SNAPPED noise, so its boundary follows the snap grid and reads as an obvious border rather than a ragged edge of weed. `shape` blends between the two and is 0 only for a layer whose height row bakes blank.
- **Water type is PAINTED, never derived, and a per-zone rule was REMOVED to make that true.** `ZoneData` briefly carried a `ZoneWaterData` that dressed a fraction of each zone's water in scum from a noise field. It worked, but the painter did not draw it, so the map and the baked world disagreed and the only way to notice was to go and stand in it. Closing that would have meant the painter reimplementing the rule — which is how the waterfall shading became two copies that drifted — so the rule went instead. A generated world now keeps standard water everywhere; `WorldFinish.StampWaterTypes` is a no-op without the painter's delegate.
- **`water_type_probe`** answers the four indistinguishable reasons for "I see no scum": the zone authors none, the gates excluded everything, it is real but far away, or the shader is at fault. It reports free-surface voxels per block with their Y range, the nearest non-standard water, what the zone under the player authors, and it reads CUSTOM0 back out of the REAL mesher — the one check that covers the hand-packed vertex channel and `ToArrayMesh`'s format flags. **`water_film_force <blockId>`** draws every water surface as one block so a film can be judged without regenerating a world; `water_debug 21` shows the film mask; `surface_reload` re-pushes the tables live.
- **The chunk water material is SHARED, and must stay that way.** It used to be `WaterMaterial.Duplicate()` per chunk (alone among the materials here — the backface beside it was always shared), which froze every parameter at build time: every runtime knob reached the original and nothing on screen.

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
- **Every `Resource` type reachable from a typed `[Export]` field on a `[Tool]` class must itself be `[Tool]`.** The editor can only instantiate a C# scripted resource as its real type if the class is `[Tool]`; without it the editor materializes it as a base `Godot.Resource`, the `[Tool]` parent's strongly-typed setter throws `InvalidCastException` ("Unable to cast object of type 'Godot.Resource' to type 'X'"), and the field reads **empty in the inspector**. Three things make this nastier than it sounds:
  - **It is not limited to embedded `[sub_resource]`s.** A plain `[ext_resource]` pointing at another `.tres` fails identically — `AtlasLayer.surface` (a `BlockSurfaceData`, which was not `[Tool]`) silently blanked and had to be restored twice before anyone spotted the cause.
  - **It is data loss, not just a display bug.** The field reads empty, so the next time the editor saves that resource it writes the file back *without* the reference. You lose authored data to a diff you didn't make, and only in-editor — runtime has no `[Tool]` gate, so the game keeps working and hides it.
  - **It cascades.** Tagging `X` makes `X`'s own typed fields subject to the same rule, so the real cost is the transitive closure (subclasses + everything reachable through `[Export]`s), not one attribute. Measure that closure before starting: it is 5 classes for `ZoneData` but ~90 for `ItemData` and ~174 for `WorldGenData`. A half-applied sweep leaves the bug in place.

  Match the parent: if it's `[Tool]`, everything it can reach is too, and say so in a comment on each so nobody strips it later. `StatusEffectData`'s payloads (`WeaponModData`, `DamageOverTimeData`, …) and the `SkyController`/`ZoneData` ambience graph are `[Tool]` for exactly this reason. Known remaining gaps: `ItemEvent.reagent` / `.concept` / `.minionData`.
- No namespaces; all classes are global scope.
- Event communication uses C# `Action` delegates and Godot `[Signal]` attributes.
- Factory methods (`Create()`) for instantiating scene-backed objects.
- `[Export]` attributes for wiring node references. Never look up child nodes by iterating children or using `GetNode`/`GetChild` in `_Ready` — instead, declare an `[Export]` field and assign the node path in the `.tscn` file.
- Godot resources (materials, shaders, meshes, etc.) should not be created programmatically at runtime. Instead, create them as `.tres`/`.tscn` files in the Godot editor and wire them into scripts via `[Export]` variables.
- Static user-defined data belongs in Godot `Resource` subclasses named `[Object]Data` (e.g., `WeaponData`). Dynamic runtime state belongs in classes named `[Object]State` (e.g., `WeaponState`). Never use "Data" to refer to dynamic/mutable properties.
- **Never hardcode resource paths in C#.** Do not call `GD.Load<T>("res://...")` with a literal path from gameplay or worldgen code. Add an `[Export]` field of the appropriate `Data` type (typically on `WorldGenData`, `SimData`, or the nearest owning `*Data` resource) and wire the `.tres` reference in the editor. The exception is generic infrastructure that genuinely has no upstream owner (e.g. `EntitySerializer` reloading by serialized path); gameplay placement code is never that case.
- **Avoid hardcoded user-facing strings in C# too.** Sign text, dialogue, item names, conversation prompts, etc. live on a `*Data` resource (or in `english.tsv` for localized UI strings). Reach for a literal only when the string is a stable internal identifier (StringName key, action name, scene path) that is never shown to the player and never authored.
- **Tunable values are `[Export]` properties, not `const`s.** Any number, color, or duration that an author/designer might want to adjust for feel — speeds, radii, fade times, thresholds, intensities, BPMs, dB levels — belongs on an `[Export]` field (with a `PropertyHint.Range` where helpful) so it's editable in the inspector without a recompile. Do NOT bury these in `private const`. Reserve `const` for genuine non-tunables: stable internal identifiers (audio bus names, action/StringName keys, scene paths), array capacities / buffer sizes that the code's correctness depends on, and pure mathematical constants. When extracting a subsystem into its own node class, move its tuning `const`s out as `[Export]`s on that class so each component owns its own inspector-visible tuning. See the `[Export]`-precision note below for sub-0.01 values.
- **An unset `[Export] StringName` is NULL, not empty.** `StringName` is a class, so a field the `.tres` never assigns arrives as `null` and `name.IsEmpty` throws — the idiom is `name is null || name.IsEmpty`. It bites precisely where a blank is the intended authoring (leave `baseBlockName` unset to mean "the standard one"), and it does not surface as a validation message: it takes down whatever pass dereferenced it. One unset name in one zone resource threw inside `WorldFinish.Finish`, which `BakeBuild` catches and reports only as "Bake failed - see console", so it read as the world-map bake producing no light.

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
- **Timing: sim clock vs wall clock.** Pick the clock by whether a timer is *gameplay-authoritative*, NOT by which callback it sits in (`_Process` vs `_PhysicsProcess`). Anything that decides *when something happens in the world* — a despawn, a damage tick, becoming interactable, a telegraph firing, a cook job finishing — belongs on the **sim clock**: prefer a `GameTimeMs` deadline (`expireMs = Sim.GameTimeMs + seconds * 1000`, compare each tick), the pattern cooldowns / AI timers / traps already use. `GameTimeMs` advances in `Sim.Tick`, so it slows uniformly under slow-mo, is frame-rate independent, and survives save/load. Purely *presentational* timing — fades, bobs, spins, the death/HUD screens — stays on wall-clock `_Process` `delta` so slow-mo doesn't drag it and it stays smooth at render fps. Avoid accumulating a gameplay duration as `_ageSeconds += (float)delta` (especially on `_Process`); that's neither slowable nor frame-rate-independent. `Discoverable` is the reference split (perception on the sim side, sprite fade on `_Process`).
