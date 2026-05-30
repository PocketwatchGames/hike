# Handoff — 3D interactive props + mesh highlight system

Status as of this session. Everything below **builds clean** (`dotnet build` 0 errors), **imports clean**, and **all four scenes load + instantiate headless** with the expected mesh/collider counts. What remains is **in-engine visual verification and transform tuning** — none of it has been seen running in the actual game yet, only validated headless.

## What was requested (5 tasks)
1. **Locked sequoia** for the climbable tree — same shape/size every spawn, 2× height, no height variation.
2. **Spiral ladder** of bright planks (rungs nailed into trunk) around that tree; the ladder is what highlights.
3. **knowledge_stone**: replace sprite with `P_Stone_Statue_Big_A_Sand` on `P_Stone_Statue_Base_A_Sand`; statue uses interactive highlight material, base does not; base sunk so top + statue sit ~0.25m above ground; both get colliders.
4. **signpost**: replace with `SM_Prop_Sign_01`.
5. **chest**: replace with `Treasure_Chest_01` body + `Treasure_Chest_lid_01`; lid opens.

## Architectural decision (approved by user)
The existing interactive highlight (X-ray through walls + yellow selection outline) was **sprite-only**. Built a parallel **mesh** path so solid 3D interactives highlight the same way. This is the first use of it; statue/sign/chest/ladder are all consumers.

---

## NEW FILES (all committed-intent, untracked in git)

### Shaders (`shaders/`)
- `mesh_xray.gdshader` — through-cover silhouette for real geometry (depth-compare vs `DEPTH_TEXTURE`). Mesh analog of `sprite_xray.gdshader`. `instance uniform float xray_amount` (0 = off/early-discard). Wired as a next_pass.
- `mesh_outline.gdshader` — inverted-hull selection outline (cull_front, push along normal by `outline_width`). `instance uniform float selected` gate. Color (1,1,0.8) matches sprite `highlight_outline`.
- `flat_lit.gdshader` — flat painted color, world-lit (fork of `model_lit`). For the ladder planks. `uniform vec3 color`.
- (REMOVED a `model_lit_vcolor.gdshader` mid-session — the chest turned out to be UV-textured, not vertex-colored. Do not recreate it.)

### Materials (`resources/materials/`)
Chain pattern: base shader → `next_pass = mesh_xray.tres` → `next_pass = mesh_outline.tres`.
- `mesh_xray.tres`, `mesh_outline.tres` — shared highlight passes.
- `plank_yellow.tres`, `plank_pine.tres`, `plank_orange.tres` — flat_lit, the 3 ladder colors, each chaining to mesh_xray.
- `statue_interactive.tres` (model_lit + statue TGA + highlight chain) — for the **statue body**.
- `statue_base.tres` (model_lit + statue TGA, **no** highlight chain) — for the **base** (per requirement: base doesn't highlight).
- `sign_interactive.tres` (model_lit + sign PNG + highlight chain).
- `chest_interactive.tres` (model_lit + `Atlas_Props_01.png` + highlight chain) — used by both chest body and lid.

### Scripts (`scripts/gameplay/entities/`)
- `InteractiveMeshHighlight.cs` (+`.cs.uid`) — mesh analog of `InteractiveXray`. Child of the IInteractive root. `_meshes` (explicit) and/or `_collectFrom` (recursively gathers MeshInstance3D — used for generated ladder planks + instanced FBX). Self-drives `xray_amount` off the same proximity+LOS probe; `SetSelected(bool)` toggles outline. Lazy-collects so runtime-generated meshes are picked up.
- `LadderPlanks.cs` (+`.cs.uid`) — runtime generator. Spawns `PlankCount` box MeshInstance3D rungs in a helix (HeightStep up, AngleStepDeg around), inner end embedded in trunk (Embed), cycling `PlankMaterials`. Children un-owned (not saved to .tscn).

### Models (`assets/models/`) — copied from AssetDump, committed-intent
- `statue/SM_Stone_Statue_Big_A.fbx`, `SM_Stone_Statue_Base_A.fbx`, `T_StoneStatue_Big_A_AlbedoTransparency.tga`
- `signpost/SM_Prop_Sign_01.fbx`, `PolygonNatureBiomes_Meadow_Texture_01.png`
- `chest/Treasure_Chest_Base_01.fbx`, `Treasure_Chest_lid_01.fbx`, `Atlas_Props_01.png`
  - **Chest texture note**: MD2 pack ships PSDs only. Converted `Textures/Atlas_Props_01.psd` → PNG with ImageMagick. The chest material name in the FBX is `Chest_01_Mat`; mapped via `.import` to `chest_interactive.tres`. (Atlas may need UV/region sanity-check in engine — picked Atlas_Props_01 as the most likely atlas; not visually confirmed it's the right one for the chest UVs.)

## MODIFIED FILES
- `scripts/gameplay/trees/TreeTrunk.cs` — added `[Export] bool LockSeed`. When true, branch-structure hash seeds from `Vector3.Zero` instead of world position → identical geometry every spawn. World-Y math still uses real origin. Other trees unaffected (default false).
- `scripts/client/GameClient.cs` — `ApplyHighlight`/`RemoveHighlight`: when `FindChildSprite` returns null, fall back to new `FindMeshHighlight(node)` → `SetSelected(true/false)`. Added `_meshHighlight` field + `FindMeshHighlight` helper.
- `scripts/gameplay/entities/Chest.cs` — added `_lidHinge` / `_lidOpenAngleDeg` / `_lidOpenSeconds` exports. `UpdateVisuals(bool animateLid)`: tweens lid hinge rotation.x open (Back/Out) on open, snaps on spawn-as-open. `_animator` now null-safe (3D chest has no sprite animator).

## REBUILT SCENES
- `scenes/interactives/climbable_tree.tscn` — root still `ClimbableTree` (required by `Instantiate<ClimbableTree>`). TreeTrunk (TrunkHeight=18, HeightVariation=0, LockSeed=true) + Foliage (3 clusters at Y=18/16/13 — lowest cluster removed per spec) + Body cylinder collider. LadderPlanks child (30 planks, 0.2m step, 30°). InteractiveMeshHighlight `_collectFrom=Trunk/LadderPlanks`. **Headless: 34 meshes, 1 body.**
- `scenes/interactives/knowledge_stone.tscn` — BaseModel (sunk, MeshAutoCollider) + StatueModel (MeshAutoCollider) + InteractiveMeshHighlight `_collectFrom=StatueModel` (so only statue highlights, not base). **User has been live-editing transforms in the editor** (base scaled 0.5 @ y=1.13, statue scaled 0.3) — leave those. **Headless: 7 meshes, 7 bodies.**
- `scenes/interactives/signpost.tscn` — SignModel (MeshAutoCollider) + sign FBX + InteractiveMeshHighlight + MinimapStamp. **Headless: 1 mesh, 1 body.**
- `scenes/interactives/chest.tscn` — ChestVisual{Body(MeshAutoCollider)+FBX, LidHinge+lid FBX}, HurtBox, Discoverable, InteractiveMeshHighlight `_collectFrom=ChestVisual`, `_lidHinge=ChestVisual/LidHinge`. **Headless: 2 meshes, 1 body.**

---

## OPEN / NEEDS IN-ENGINE VERIFICATION (pick up here)
None of these block build; all are "looks right in game?" items.

1. **Run the game and look at each prop.** Nothing has been seen rendered. Spawn a climbable tree, knowledge stone, signpost, chest.
2. **Transforms are first-guess and likely need tuning** (do in editor, like the user already started on knowledge_stone):
   - Ladder: `AttachRadius=0.22`, `Embed=0.12`, `StartHeight=1.0` vs the actual locked-trunk radius/taper. Planks may float or sink. The trunk tapers (BottomRadius 0.25 → TopRadius 0.05) so a fixed AttachRadius won't hug the trunk over 6m of height — may want AttachRadius to shrink with height, or just eyeball.
   - Statue base sink: requirement is "top of base + statue 0.25m above ground." Current base Y offset was set before the user rescaled; **re-derive after their scaling settles.** Need the base FBX AABB at their chosen scale to place it so its top is at 0.25.
   - Chest `LidHinge` pivot transform (currently `(0, 0.42, 0.26)`) and the lid's counter-offset are **guesses** — almost certainly need adjusting to the real lid mesh dimensions so it hinges on the back top edge.
   - Chest `BoxShape` interact/hurt sizes vs actual chest size.
3. **Chest atlas correctness** — confirm `Atlas_Props_01.png` is the atlas the chest UVs reference (vs Atlas_Furniture/Props_02). If chest looks miscolored, try the other atlases (all in AssetDump MD2 `Textures/*.psd`, convert same way).
4. **Highlight visual tuning** — `mesh_outline` `outline_width=0.03` and `mesh_xray` `xray_alpha=0.22` are ports of sprite values; check they read well on these mesh scales. The big statue may want a smaller relative outline.
5. **MeshAutoCollider** generates **trimesh** colliders at runtime (concave, static). Fine for static props. If the chest needs to be hit/broken, confirm the HurtBox covers it.
6. **Signpost text** — `Signpost.cs` reads `_text`; placeholder set. Real sign copy is content.
7. **Spawn lists** — these scenes are referenced by spawn entries elsewhere; verify nothing else hardcoded the old sprite scenes' node structure (e.g. expecting a `Sprite` child).

## GOTCHAS LEARNED (so you don't re-hit them)
- **FBX material remap**: don't author MaterialOverride in the .tscn — set it in the FBX's `.import` under `_subresources/materials/<MaterialName>/use_external`. Material names: statue=`T_Statue_Moss`, sign=`RopeBridge`, chest=`Chest_01_Mat`. Re-run `--import` after editing.
- **ImageMagick + spaces + `[0]`**: `magick "path with spaces.psd[0]"` fails. Copy PSD to a space-free temp path first, then convert.
- **validate_uids**: `dotnet run --project tools/validate_uids` (`--fix` creates missing .cs.uid). Pre-existing unrelated failure: `ScrollData.cs ambiguous uid` (NOT mine, ignore).
- Headless import: `Godot.exe --path . --import --headless`. Editor re-saves scenes with real uids on `path=`-only ext_resources (harmless, expected).

## NOT MINE in git status (pre-existing/other work — don't touch)
`marsh_kit.tres` (M), `tree_willow*.tscn`, `tree_bark_willow.tres` (untracked).

## Memory written
`project-mesh-interactive-highlight` and `project-fbx-prop-pipeline` in the agent memory dir capture the reusable patterns.
