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
const CAPTURE_MENU_ITEM := "Capture Player Animation Events"

var _manifest: Resource


func _enter_tree() -> void:
	add_tool_menu_item(MENU_ITEM, _rebuild)
	add_tool_menu_item(CAPTURE_MENU_ITEM, _capture_events)


func _exit_tree() -> void:
	remove_tool_menu_item(MENU_ITEM)
	remove_tool_menu_item(CAPTURE_MENU_ITEM)


func _load_manifest() -> Resource:
	if _manifest == null:
		_manifest = load(MANIFEST_PATH)
	if _manifest == null:
		push_error("Anim Library Builder: cannot load %s (build the C# project first)" % MANIFEST_PATH)
	return _manifest


func _rebuild(_arg = null) -> void:
	var manifest := _load_manifest()
	if manifest != null:
		manifest.RebuildLibrary()


# Pull method-track keys tuned in the AnimationPlayer dock back into the manifest
# so they're re-baked on every future rebuild instead of lost on FBX re-import.
func _capture_events(_arg = null) -> void:
	var manifest := _load_manifest()
	if manifest != null:
		manifest.CaptureEventsFromLibrary()
