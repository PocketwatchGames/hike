# Sourcing External Assets

Read this when adding a new texture, model, animation, or sound to the project from an external library. It is not loaded by default — CLAUDE.md links here.

## Source Locations

All of these are **browse/source-only**, outside this repo, and not committed. Scan and read them in place; never bulk-copy into the project (Godot imports everything under the project root, so a bulk copy — even gitignored — generates `.import`/`.godot/imported/` churn for hundreds of unused files).

- `C:\Users\andy\source\AssetDump\Assets` — external Unity asset library (textures, models, animations, some sounds). Stock SFX bank under `Universal Sound FX`.
- `C:\Users\andy\source\Armada` — audio under `Armada\Audio` and `ArmadaContent\audio`.
- `C:\Users\andy\source\house\dev\Assets` — `Art\` and `Audio\` (character/foley/VO `.wav` under `Audio\Assets`, temp/dev sounds under `Audio\Assets\Temp`).
- `C:\Users\andy\source\bowhead\Assets` — `Audio\` (incl. `Audio\Music`), art, shaders, icons.

## Workflow

1. Search/preview the library for what's needed (textures `.png`/`.tga`/`.psd`/`.webp`, models `.fbx`/`.gltf`, audio `.wav`/`.ogg`). Ignore Unity-only files — `.meta`, `.prefab`, `.mat`, `.asset`, `.unity` are useless to Godot.
2. Copy only the chosen source file(s) into the appropriate `res://` subfolder (`assets/textures/...`, `scenes/props/`, etc.). These are **committed**, not gitignored — the game and teammates need them.
3. Wire them up following the repo conventions (`.import` sidecars, `.tscn`/`.tres`, and the Godot UID Invariants in CLAUDE.md).

## Search by the material's ROLE, not by the name in the request

A request names a *look* ("an ivy texture"); the library files are named for what they **are**
in a terrain set. Searching the literal word finds mesh atlases and alpha cards — `Generic_Ivy`
is a UV atlas for the ivy models, `Vines_01` a hanging card — and concluding "the library has
no ivy, I'll generate one" from that is wrong twice: it is, and the real art was better.

The tiling terrain art lives in per-pack `Terrain/` folders under compound material names —
`Rock_Moss`, `Moss_Rock`, `Rock_Rough_Moss_Red`. Enumerate those folders before deciding
something does not exist. Note what a compound name is telling you: `Rock_Moss` is growth ON
rock, which is a different (and usually more useful) tile than a plain `Moss` carpet, because
the rock showing through is what makes an overlay read as growth rather than as paint.

Two standing caveats for that art: Synty terrain packs ship **colour + normal only, no height**,
so anything needing displacement has to derive one; and a colour variant usually **reuses its
base tile's normal map** (`Rock_Rough_Moss_Red` has no normal of its own).

## Deriving a reversed / held / in-place animation clip

A character clip that is another clip **played backwards**, **one pose held**, or the same
clip with its **root motion stripped** has no import-time expression — Godot's scene importer
can trim, slice and retime, but not reverse or de-root. Bake it as its own FBX with
`tools/derive_anim_clips.py` (Blender, headless) and let the `PlayerAnimManifest` merge it
like any other clip, rather than reversing, freezing or cancelling motion at runtime. The
three climb clips are derived from one source this way; the recipe and its two gotchas (a
held pose needs `animation/remove_immutable_tracks=false`; root motion sits on the armature
OBJECT, not a bone) are in `assets/models/characters/polysplit/anims/README.md`.

## Wiring a Synty FBX model (material override + scale)

- Synty FBX embed a texture path that doesn't exist in the project (e.g. `PolygonNatureBiomes_Texture_01_Tom.png`), so import logs a harmless `Resource file not found: res://`. Override the material in the `.fbx.import` `_subresources.materials` block (`use_external`), pointing at a `model_lit` `.tres`. See `signpost.tscn` / `scenes/interactives/campfire.tscn` / `well.tscn` for the canonical wiring.
- **The override key is the material name *Godot assigns*, which is NOT always the raw name in the FBX** — the importer can append a digit (`Nature_Base_Mat` → `Nature_Base_Mat8`, and it differs per file). If the key is wrong the override silently no-ops and the model renders **untextured** (the broken embedded material is kept). Don't guess from FBX strings; introspect the imported scene to get the exact name.
- **Headless introspection** (no editor): a throwaway `SceneTree` GDScript that `load()`s + `instantiate()`s the `.fbx` and walks the tree, run via `Godot.exe --path . --headless --script <file>.gd`, prints the real surface material names, mesh node paths, and **AABB**. Read the AABB to set scale + ground offset — Synty per-asset scale varies wildly (the campfire was ~0.9m needing 2.25×; the well was ~3m at 1.0×). Delete the throwaway script (and its `.uid`) when done.
- **Reuse a pack's atlas if it's already imported** — the PolygonNatureBiomes meadow atlas lives at `assets/models/signpost/PolygonNatureBiomes_Meadow_Texture_01.png`; point new meadow-pack materials at that existing copy instead of re-importing, to avoid texture churn. Prefer models from packs whose atlas is already in-project.
- Canonical template for an interactive 3D prop: instanced FBX under a `Model` Node3D, external material via `.fbx.import`, `InteractiveMeshHighlight` with `_collectFrom` → the model node for the selection outline (3D analog of the sprite X-ray). `GameClient.ApplyHighlight` auto-falls back to the mesh-highlight path when an interactive has no sprite.
- A single FBX surface = one material; you can't glow/recolor part of a mesh from the material alone. To vary appearance across a composite prop (e.g. glowing logs but not the stone ring), keep the parts as **separate FBX instances** and assign/swap per-instance materials.
