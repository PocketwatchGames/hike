@tool
extends EditorPlugin

# Editor convenience wrapper around VoxelAtlasManifest (the C# resource that owns
# the actual stitch). Adds "Project > Tools > Rebuild Voxel Atlas" and auto-
# rebuilds whenever a source terrain map changes on disk.
#
# The layer list / stitch logic lives ONLY on
# res://resources/data/blocks/voxel_atlas_manifest.tres now — this plugin just
# loads it and calls RebuildAtlas()/IsStale(). See VoxelAtlasManifest.cs and the
# headless mirror tools/stitch_voxel_atlas.py.
const MANIFEST_PATH := "res://resources/data/blocks/voxel_atlas_manifest.tres"
const MENU_ITEM := "Rebuild Voxel Atlas"

var _manifest: Resource
var _rebuilding := false


func _enter_tree() -> void:
	_manifest = load(MANIFEST_PATH)
	add_tool_menu_item(MENU_ITEM, _rebuild)
	EditorInterface.get_resource_filesystem().filesystem_changed.connect(_on_fs_changed)


func _exit_tree() -> void:
	remove_tool_menu_item(MENU_ITEM)
	var fs := EditorInterface.get_resource_filesystem()
	if fs.filesystem_changed.is_connected(_on_fs_changed):
		fs.filesystem_changed.disconnect(_on_fs_changed)


# Rebuild whenever a source map is newer than the baked atlas. The guard prevents
# the self-triggered scan (after the manifest saves the atlases) from looping.
func _on_fs_changed() -> void:
	if _rebuilding or _manifest == null:
		return
	if _manifest.IsStale():
		_rebuild()


func _rebuild(_arg = null) -> void:
	if _manifest == null:
		_manifest = load(MANIFEST_PATH)
	if _manifest == null:
		push_error("Voxel Atlas Stitcher: cannot load %s (build the C# project first)" % MANIFEST_PATH)
		return
	_rebuilding = true
	_manifest.RebuildAtlas()
	_rebuilding = false
