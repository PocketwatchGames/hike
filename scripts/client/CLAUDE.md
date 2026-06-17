# Ground Stains / Decals

Covers `scripts/client/GroundStainProjector.cs`, `shaders/ground_stain.gdshaderinc`, and the dynamic-mark batcher `scripts/gameplay/effects/FootprintScatter.cs`.

## Why not `Decal` nodes

Flat ground marks — scorch, footprints, blood, worn paths — are **not** Godot `Decal` nodes. A `Decal` only modifies `ALBEDO`, but the terrain/sprite shaders (`voxel_clip`, `detail_sprite`) run `ambient_light_disabled` and route most surface brightness through `EMISSION` (ambient sky-bounce + block/torch light computed from `base`). So a `Decal` only tints the direct-sun fraction and **washes out wherever EMISSION dominates** — shade, dark terrain (swamp), and right next to a light (a fire trap). Decals are visible only on bright, sunlit ground; do not use them for ground marks.

## The ground-stain layer

A sibling of the `BlockLightShadowProjector` pattern:
- **`GroundStainProjector`** (in `game.tscn` under `SceneViewport`) is a top-down orthographic `SubViewport` camera over the player that renders stain proxies on **visual layer 5** (`STAIN_PROXY_LAYER_MASK = 1u << 4`, value `16`) into `ground_stain_tex`, publishing `ground_stain_origin/right/up/size/strength` globals each frame. `MainCamera`'s `cull_mask` excludes layer 5 so proxies never draw to the screen directly — only into the projector.
- **`voxel_clip.gdshader`** samples it via `apply_ground_stain(base, world_vertex)` (from `ground_stain.gdshaderinc`) **immediately after `base` is computed, BEFORE the ALBEDO/EMISSION split** — so a stain darkens/tints both channels and reads in every lighting condition. The RT is a transparent `SubViewport` (premultiplied color), so the composite is `base*(1 - a*strength) + premult_rgb*strength`, not a plain `mix`.
- The shader hook is a strict **no-op when unstained** (`ground_stain_enabled` false, or the fragment is outside the projector frustum / has zero coverage) → terrain is byte-identical to pre-feature. **Do not "fix" decal visibility by restructuring the ALBEDO/EMISSION lighting** (e.g. moving ambient/block light into `light()` so a `Decal` can darken it) — that path silently darkens all terrain and was reverted; the stain layer exists precisely to avoid touching the lighting model.

Per-mark intensity comes from the mark's own texture/tint alpha; `GroundStainProjector.strength` (and the `ground_stain` CVar) is the shared master. New globals follow the `ShaderGlobals` rules in the root CLAUDE.md (declared in `project.godot` + the texture seeded via `Register`).

## Adding a new stain type

Give the source a flat quad proxy — a `MeshInstance3D` with a `PlaneMesh` on **layer 5** and an **unshaded, alpha-blended** material showing the mark texture (see `resources/materials/scorch_stain.tres`). The shader composites whatever the quad renders.

- **Static mark** (e.g. scorch on a fire trap): author the quad + material directly in the prop scene (`fire_trap.tscn` → `ScorchStain`).
- **Dynamic / high-volume mark** (e.g. footprints): batch into a MultiMesh rather than spawning a node + unique material per mark. `FootprintScatter` (`scripts/gameplay/effects/`, owned by `World`, created in `World.Initialize` next to `WorldPropScatter`) keeps one `MultiMesh` bucket per actor footprint **texture** (a MultiMesh = one mesh + one material = one albedo texture) on layer 5, with per-instance transform (pos/yaw/XZ-scale) and per-instance `INSTANCE_COLOR` carrying tint + animated alpha (template material `resources/materials/footprint_multimesh.tres` is unshaded, alpha-blended, `vertex_color_use_as_albedo`, `.Duplicate()`-d per texture with the actor texture bound). The scatter owns the per-mark lifetime fade and the mob-print discovery gate (ported off the old per-print `Discoverable` node) in its `_Process` — there is no per-mark Node. Tunings (material, per-ground tints, duration, discovery prominence/threshold/fade) live on `SimData`. The ground shader already light-matches the mark, so **don't pre-dim the mark by perceived light** — a print on dark ground reads dark because the ground is dark.
