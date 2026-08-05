# Frame Time: Where To Look Next

Working note from the 2026-08 profiling pass. Records what was measured, what was
ruled out, and what is left — so the next session starts from evidence rather
than from guesses.

## Read this first: how to measure

- **`frame_ms_avg` in the profiler's frame-coverage block is the ONLY trustworthy
  number.** It is wall-clock window ÷ frames rendered, computed by `Profiler`
  itself.
- **`process_ms` / `physics_process_ms` are NOT usable**, instantaneous *or*
  windowed. Godot's `TIME_PROCESS` monitor can report more time than the frame
  containing it (seen at 45.88 against a 33.33 ms frame, and again windowed at
  48.36 against 42.48). A windowed version was implemented and reverted for
  exactly this reason. An early round of A/B on `process_ms` produced garbage and
  led to a wrong conclusion; don't repeat it.
- **A/B protocol:** stand still in a fixed spot, one full `profile_window` per
  toggle, read `frame_ms_avg`.
- **Toggle deltas are strongly sub-additive.** The four off-screen passes measured
  1.72 / 1.96 / 1.89 / 1.76 individually (7.33 summed) but only 3.70 when all four
  were off. Removing one bottleneck promotes another. Never bank individual
  deltas as independent wins.

## Tools

| tool | what it answers |
|---|---|
| `node_census` | what the resident nodes are, bucketed by subtree / source scene / class, with the columns that cost: `proc`, `phys`, `intl`, `vis`, `col`. Also a table ranked by per-frame ticks. |
| `node_tree <substring>` | the full subtree of the first matching node, per-node cost flags. Census says *which* scene is heavy; this says *what is inside it*. |
| `node_census_delay <sec>` | runs both (plus a profiler dump if `profile 1`) at N seconds — for headless runs, which have no console. |
| `mob_ai`, `mob_visible`, `props_visible`, `details_visible` | content bisection. |
| `cap_mask_pass`, `outline_mask_pass`, `block_light_shadow`, `ground_stain` | off-screen SubViewport pass bisection. All four genuinely set `RenderTargetUpdateMode = Disabled`. |
| `skeleton_internal`, `fx_audio`, `fx_particles` | internal-process bisection (see "ruled out"). |

**`ceiling_cap` is not a pass toggle** — it only hides `_clipCapPlane`. The cap-mask
camera keeps rendering. Use `cap_mask_pass`.

## Measured content bisection (baseline 30.44 ms)

| toggle | frame_ms_avg | saved |
|---|---|---|
| `mob_ai 0` | 22.46 | **−7.98** |
| `props_visible 0` | 24.57 | −5.87 |
| `mob_visible 0` | 26.34 | −4.10 |
| `details_visible 0` | 26.35 | −4.09 |
| all 4 off-screen passes | — | −3.70 |

**Cutting a pass helps only that pass; cutting CONTENT helps all ~5 passes at
once.** That is why `props_visible` alone beat removing every off-screen pass.

## The main open lead

`mob_ai 0` saves ~8 ms, but every `Mob.*` profiler section combined is only
~2.6 ms/frame. **The missing ~5.4 ms is not in mob code — it is the consequence of
mobs moving.** Each mob is ~41 nodes, and every move propagates a transform
notification through that whole subtree, engine-side and unprofileable by any
`Profiler.Sample`. Same mechanism proved on `PixelSnap`, where removing a
redundant `GlobalPosition` write took a section from 0.715 to 0.090 ms/frame.

So **nodes-per-mob is a frame-time multiplier, not just a memory number.**

### What a mob is made of (goblin, 41 nodes after the 2026-08 prune)

- 1 `Mob` root + `MovementCollision` + `HurtBox` (+shape) + `HUDNode`
- **4-deep pure-wrapper chain**: `Visuals` → `MeshContainer` → `GoblinModel` →
  `goblin`. Every one is a transform link that propagates on each move.
- 1 `Skeleton3D`, **8 `BoneAttachment3D`**, 7 `MeshInstance3D`
- **2 `AnimationPlayer`** (`ModelAnimator.player` points at the authored
  `AnimationPlayer2`; the FBX's own `AnimationPlayer` looks unreferenced)
- weapon/torch/item/belt holders, `PixelSnap`, `ModelAnimator`, `HeldItemVisual`,
  `EliteCrown`, an idle-loop `Fx` + its `AudioStreamPlayer3D`

### Recommended next move: replace the placeholder rigs, don't prune them

The expensive structure — the deep wrapper chain, the bone-attachment sockets, the
duplicate `AnimationPlayer`, the many-mesh modular body — is **inherited from the
imported Synty/Infinity-PBR character FBX**, not from anything the game needs. The
current models are placeholders that will be replaced anyway.

Surgically pruning them means authoring changes across 12 character scenes that get
thrown away at art replacement. **If prototyping fps is the problem, swap to a
lighter placeholder rig instead** — one mesh, one `AnimationPlayer`, no cosmetic
bone sockets, a shallow node chain. That attacks the same 5.4 ms without investing
in work that will be discarded, and it sets a node budget the eventual real art has
to meet.

Whatever ships eventually, treat **nodes-per-character as a hard budget** and check
it with `node_tree` when new art lands.

## Ruled out by measurement — do not re-litigate

- **`Skeleton3D` internal processing.** `skeleton_internal 0` → 26.56 vs 26.95
  baseline (−0.39, noise). 119 skeletons tick internally and it does not matter.
- **Fx audio and Fx particles.** Both flat.
- **Loot.** Its whole tick is ~0.1 ms/frame against Mob's 1.28. A dormancy scheme
  was built, measured at nothing, and reverted.
- **Jolt.** `physics_active_objects` and `physics_collision_pairs` both read 0.

## Fixed during the pass (for context on the current baseline)

- `FoliageCluster` authoring nodes freed after their bake (−2545 nodes)
- `MobHUD` pooled behind `MobHudManager` (−2363 nodes, −139 `_Process`)
- Foliage shadow-caster twins skipped for non-fading ground foliage (−694 MMIs)
- Permanently-hidden model meshes freed, and the `BoneAttachment3D` sockets that
  left stranded (goblin 45 → 41 nodes; sockets 378 → 270 world-wide)
- `PixelSnap` centralised + idle-skip: 0.715 → 0.090 ms/frame
- `CullProps` skips static entities unless the clip moved: 0.564 → 0.421 ms/frame
- Outline-mask pass runs only while something is outlined (use `UpdateMode.Once`
  when going idle, never `Disabled` — that freezes a ghost outline in the RT)
- Loot spawn fx no longer replays on every chunk stream-in (was 64 concurrent
  `loot_spawn` Fx = 192 nodes + 64 spurious 3D audio sources)

Node count over the pass: **28,535 → ~22,400**.

## Two smaller threads left dangling

- **`SkyController.Process` max 32.219 ms** against a 413 µs average — a single-frame
  hitch, invisible in averages. Use `hitch_log` + `DiagnosticsOverlay.IsolateNextFrame`.
- **Render submission**: 853 draw calls for 1115 objects is nearly 1:1, i.e. almost
  no batching. Only worth attacking once the CPU side settles.
