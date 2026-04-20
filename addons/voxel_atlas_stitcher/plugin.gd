@tool
extends EditorPlugin

# Stitches per-tile PNGs into res://assets/textures/voxels/voxel_tiles.png.
# Layer order must match VoxelTypeInfo.TILE_* indices and the slices/vertical
# count in voxel_tiles.png.import. See scripts/voxels/VoxelType.cs.
const SLOT := 128
const SRC_DIR := "res://assets/textures/voxels/"
const OUT_PATH := "res://assets/textures/voxels/voxel_tiles.png"
const MENU_ITEM := "Rebuild Voxel Atlas"

const LAYERS := [
	"stone",         #  0 TILE_STONE
	"dirt",          #  1 TILE_DIRT
	"level1_1",      #  2 TILE_GRASS_TOP band 0 variant 0
	"level1_2",      #  3
	"level1_3",      #  4
	"level1_4",      #  5
	"level2_1",      #  6 band 1
	"level2_2",      #  7
	"level2_3",      #  8
	"level2_4",      #  9
	"level3_1",      # 10 band 2
	"level3_2",      # 11
	"level3_3",      # 12
	"level3_4",      # 13
	"level4_1",      # 14 band 3
	"level4_2",      # 15
	"level4_3",      # 16
	"level4_4",      # 17
	"grass_side",    # 18 TILE_GRASS_SIDE
	"sand",          # 19 TILE_SAND
	"wood_end",      # 20 TILE_WOOD_END
	"wood_side",     # 21 TILE_WOOD_SIDE
	"water",         # 22 TILE_WATER
	"cobblestone1",  # 23 TILE_COBBLESTONE variants
	"cobblestone2",  # 24
	"cobblestone3",  # 25
	"cobblestone4",  # 26
	"dirt1",         # 27 TILE_DIRT_OVERLAY variants
	"dirt2",         # 28
	"dirt3",         # 29
	"dirt4",         # 30
	"field",         # 31 TILE_FIELD_OVERLAY
]

var _rebuilding := false


func _enter_tree() -> void:
	add_tool_menu_item(MENU_ITEM, _rebuild)
	EditorInterface.get_resource_filesystem().filesystem_changed.connect(_on_fs_changed)


func _exit_tree() -> void:
	remove_tool_menu_item(MENU_ITEM)
	var fs := EditorInterface.get_resource_filesystem()
	if fs.filesystem_changed.is_connected(_on_fs_changed):
		fs.filesystem_changed.disconnect(_on_fs_changed)


# Rebuild whenever any source PNG is newer than the atlas. The guard prevents
# the self-triggered scan (after we save voxel_tiles.png) from looping: once
# we've rebuilt, the atlas is newer than every source, so the mtime check fails.
func _on_fs_changed() -> void:
	if _rebuilding:
		return
	if _atlas_is_stale():
		_rebuild()


func _atlas_is_stale() -> bool:
	var atlas_mtime := FileAccess.get_modified_time(OUT_PATH)
	for name: String in LAYERS:
		var src_path: String = SRC_DIR + name + ".png"
		var src_mtime := FileAccess.get_modified_time(src_path)
		if src_mtime == 0:
			# Missing source — skip; _rebuild will error explicitly.
			continue
		if src_mtime > atlas_mtime:
			return true
	return false


func _rebuild() -> void:
	_rebuilding = true
	var strip := Image.create(SLOT, SLOT * LAYERS.size(), false, Image.FORMAT_RGBA8)
	for i in LAYERS.size():
		var name: String = LAYERS[i]
		var src_path := SRC_DIR + name + ".png"
		var img := Image.load_from_file(ProjectSettings.globalize_path(src_path))
		if img == null:
			push_error("Voxel atlas stitcher: failed to load %s" % src_path)
			_rebuilding = false
			return
		if img.get_size() != Vector2i(SLOT, SLOT):
			img.resize(SLOT, SLOT, Image.INTERPOLATE_NEAREST)
		if img.get_format() != Image.FORMAT_RGBA8:
			img.convert(Image.FORMAT_RGBA8)
		strip.blit_rect(img, Rect2i(0, 0, SLOT, SLOT), Vector2i(0, i * SLOT))

	var err := strip.save_png(ProjectSettings.globalize_path(OUT_PATH))
	if err != OK:
		push_error("Voxel atlas stitcher: save_png failed with error %d" % err)
		_rebuilding = false
		return

	print("Voxel atlas stitcher: wrote %d layers to %s" % [LAYERS.size(), OUT_PATH])
	EditorInterface.get_resource_filesystem().scan()
	_rebuilding = false
