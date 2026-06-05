@tool
extends EditorPlugin

# Editor convenience wrapper around PlayerAnimManifest (the C# resource that owns
# the actual merge). Adds "Project > Tools > Rebuild Player Animations", which
# scans the manifest's source folder of animation FBXs and merges each clip into
# human_anims.res.
#
# Unlike the voxel atlas stitcher this does NOT auto-rebuild on filesystem
# change: the animation library is partly hand-curated (clips with no source FBX
# yet), so the merge is explicit to avoid surprise overwrites. The merge logic
# lives ONLY on res://assets/models/characters/polysplit/player_anim_manifest.tres — this
# plugin just loads it and calls RebuildLibrary(). See PlayerAnimManifest.cs.
const MANIFEST_PATH := "res://assets/models/characters/polysplit/player_anim_manifest.tres"
const MENU_ITEM := "Rebuild Player Animations"

var _manifest: Resource


func _enter_tree() -> void:
	add_tool_menu_item(MENU_ITEM, _rebuild)


func _exit_tree() -> void:
	remove_tool_menu_item(MENU_ITEM)


func _rebuild(_arg = null) -> void:
	if _manifest == null:
		_manifest = load(MANIFEST_PATH)
	if _manifest == null:
		push_error("Anim Library Builder: cannot load %s (build the C# project first)" % MANIFEST_PATH)
		return
	_manifest.RebuildLibrary()
