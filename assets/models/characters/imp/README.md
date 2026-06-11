# Imp minion model (staged for re-skin)

`imp.fbx` (Magic Pig Games "IMP_LP") + `imp_red.png` are the source art for the
summoner's minion. They are **not yet wired** — the shipped minion currently
reuses the goblin rig so the summoner works end-to-end headless. Re-skin to the
Imp in the Godot **editor** (FBX animation baking is editor-only):

1. Import `imp.fbx`. In `imp.fbx.import`, override the embedded material via
   `_subresources.materials` → `use_external` pointing at a new `model_lit`
   `.tres` bound to `imp_red.png`. Introspect the imported scene headlessly
   (throwaway `SceneTree` GDScript) to get the **exact Godot-assigned material
   name** (the importer may append a digit) and the **AABB** for scale/offset —
   see the Synty wiring notes in the root `CLAUDE.md`.
2. Bake an `AnimationLibrary` `.res` with at least `idle`, `run`, `dead`
   (the names `minion.tres`'s `animations` dict references). The Imp FBX bundles
   its clips; retarget/rename them to those keys.
3. Create `scenes/characters/minion.tscn` (duplicate `goblin.tscn`, swap the FBX
   ext_resource, the `AnimationLibrary`, and the materials for the Imp's), then
   repoint `resources/data/characters/minion/minion.tres` `MobScene` at it.
4. Adjust `MeshContainer` scale + ground offset from the AABB; verify idle/run/
   death animations play on a summoned minion.
