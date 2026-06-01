@tool
extends EditorPlugin

# Stitches the PBR terrain material sets in res://assets/textures/terrain/ into
# two Texture2DArray strips:
#   voxel_tiles.png            - base color (sRGB)
#   voxel_tiles_nrm_height.png - RGB = tangent normal, A = height/displacement
# Layer order must match the AtlasBaseIndex on each BlockData and the
# slices/vertical count in both .import files. See scripts/voxels/VoxelType.cs
# and tools/stitch_voxel_atlas.py (the headless mirror of this plugin).
const SLOT := 256
const TERRAIN_DIR := "res://assets/textures/terrain/"
const VOXEL_DIR := "res://assets/textures/voxels/"
const COLOR_OUT := "res://assets/textures/voxels/voxel_tiles.png"
const NH_OUT := "res://assets/textures/voxels/voxel_tiles_nrm_height.png"
const MENU_ITEM := "Rebuild Voxel Atlas"

# Flat tangent normal (0.5,0.5,1.0) for the water placeholder slot.
const FLAT_NORMAL := Color(0.5, 0.5, 1.0, 0.0)

# One dict per atlas layer, in AtlasBaseIndex order. `color`/`normal`/`height`
# are res:// paths; null normal/height yields a flat-normal / zero-height slot.
const LAYERS := [
	{  # 0 Stone -> stylized rocks (general cliff faces)
		"color": TERRAIN_DIR + "Stylized_Rocks_002_4K/Stylized_Rocks_002_basecolor.png",
		"normal": TERRAIN_DIR + "Stylized_Rocks_002_4K/Stylized_Rocks_002_normal.png",
		"height": TERRAIN_DIR + "Stylized_Rocks_002_4K/Stylized_Rocks_002_height.png",
	},
	{  # 1 GrassTop -> forest/mountain ground
		"color": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_basecolor.jpg",
		"normal": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_normal.jpg",
		"height": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_height.png",
	},
	{  # 2 Water (placeholder; rendered by water shader)
		"color": VOXEL_DIR + "water.png", "normal": null, "height": null,
	},
	{  # 3 Cobblestone
		"color": TERRAIN_DIR + "Cobblestone_Irregular_Floor_001/Cobblestone_Irregular_Floor_001_basecolor.png",
		"normal": TERRAIN_DIR + "Cobblestone_Irregular_Floor_001/Cobblestone_Irregular_Floor_001_normal.png",
		"height": TERRAIN_DIR + "Cobblestone_Irregular_Floor_001/Cobblestone_Irregular_Floor_001_height.png",
	},
	{  # 4 DirtOverlay -> dry mud
		"color": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_basecolor.jpg",
		"normal": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_normal.jpg",
		"height": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_height.png",
	},
	{  # 5 FieldOverlay -> same stylized grass as the base ground (matches mountain)
		"color": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_basecolor.jpg",
		"normal": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_normal.jpg",
		"height": TERRAIN_DIR + "Stylized_Grass_002_SD/Stylized_Grass_002_height.png",
	},
	{  # 6 DesertTop -> stylized sand
		"color": TERRAIN_DIR + "Stylized_Sand_002_SD/Stylized_Sand_002_basecolor.png",
		"normal": TERRAIN_DIR + "Stylized_Sand_002_SD/Stylized_Sand_002_normal.png",
		"height": TERRAIN_DIR + "Stylized_Sand_002_SD/Stylized_Sand_002_height.png",
	},
	{  # 7 DesertSand -> realistic sand
		"color": TERRAIN_DIR + "Sand 001/Sand_001_COLOR.png",
		"normal": TERRAIN_DIR + "Sand 001/Sand_001_NRM.png",
		"height": TERRAIN_DIR + "Sand 001/Sand_001_DISP.png",
	},
	{  # 8 DesertWall -> stylized cliff rock (desert cliffs)
		"color": TERRAIN_DIR + "Stylized_Cliff_Rock_006_SD/Stylized_Cliff_Rock_006_basecolor.png",
		"normal": TERRAIN_DIR + "Stylized_Cliff_Rock_006_SD/Stylized_Cliff_Rock_006_normal.png",
		"height": TERRAIN_DIR + "Stylized_Cliff_Rock_006_SD/Stylized_Cliff_Rock_006_height.png",
	},
	{  # 9 DesertCave -> dry mud (sandstone cave floor)
		"color": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_basecolor.jpg",
		"normal": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_normal.jpg",
		"height": TERRAIN_DIR + "Stylized_Dry_Mud_001_SD/Stylized_Dry_Mud_001_height.png",
	},
	{  # 10 Marsh -> wet ground (swamp)
		"color": TERRAIN_DIR + "Ground_Wet_002_SD/Ground_Wet_002_basecolor.jpg",
		"normal": TERRAIN_DIR + "Ground_Wet_002_SD/Ground_Wet_002_normal.jpg",
		"height": TERRAIN_DIR + "Ground_Wet_002_SD/Ground_Wet_002_height.png",
	},
	{  # 11 CaveFloor -> stylized stone floor (limestone cave floor)
		"color": TERRAIN_DIR + "Stylized_Stone_Floor_002_4K/Stylized_Stone_Floor_002_basecolor.png",
		"normal": TERRAIN_DIR + "Stylized_Stone_Floor_002_4K/Stylized_Stone_Floor_002_normal.png",
		"height": TERRAIN_DIR + "Stylized_Stone_Floor_002_4K/Stylized_Stone_Floor_002_height.png",
	},
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


# Rebuild whenever any source map is newer than the color atlas. The guard
# prevents the self-triggered scan (after we save the atlases) from looping.
func _on_fs_changed() -> void:
	if _rebuilding:
		return
	if _atlas_is_stale():
		_rebuild()


func _atlas_is_stale() -> bool:
	var atlas_mtime := FileAccess.get_modified_time(COLOR_OUT)
	for layer: Dictionary in LAYERS:
		for key in ["color", "normal", "height"]:
			var src: Variant = layer[key]
			if src == null:
				continue
			var src_mtime := FileAccess.get_modified_time(src)
			if src_mtime == 0:
				continue  # Missing source — _rebuild errors explicitly.
			if src_mtime > atlas_mtime:
				return true
	return false


func _load_slot(path: String, format: int) -> Image:
	var img := Image.load_from_file(ProjectSettings.globalize_path(path))
	if img == null:
		return null
	if img.get_size() != Vector2i(SLOT, SLOT):
		img.resize(SLOT, SLOT, Image.INTERPOLATE_LANCZOS)
	if img.get_format() != format:
		img.convert(format)
	return img


func _rebuild() -> void:
	_rebuilding = true
	var color_strip := Image.create(SLOT, SLOT * LAYERS.size(), false, Image.FORMAT_RGB8)
	var nh_strip := Image.create(SLOT, SLOT * LAYERS.size(), false, Image.FORMAT_RGBA8)

	for i in LAYERS.size():
		var layer: Dictionary = LAYERS[i]
		var color := _load_slot(layer["color"], Image.FORMAT_RGB8)
		if color == null:
			push_error("Voxel atlas stitcher: failed to load %s" % layer["color"])
			_rebuilding = false
			return
		color_strip.blit_rect(color, Rect2i(0, 0, SLOT, SLOT), Vector2i(0, i * SLOT))

		var nrm: Image
		if layer["normal"] != null:
			nrm = _load_slot(layer["normal"], Image.FORMAT_RGB8)
		var hgt: Image
		if layer["height"] != null:
			hgt = _load_slot(layer["height"], Image.FORMAT_L8)
		for y in SLOT:
			for x in SLOT:
				var n := FLAT_NORMAL if nrm == null else nrm.get_pixel(x, y)
				var h := 0.0 if hgt == null else hgt.get_pixel(x, y).r
				nh_strip.set_pixel(x, i * SLOT + y, Color(n.r, n.g, n.b, h))

	var err := color_strip.save_png(ProjectSettings.globalize_path(COLOR_OUT))
	if err == OK:
		err = nh_strip.save_png(ProjectSettings.globalize_path(NH_OUT))
	if err != OK:
		push_error("Voxel atlas stitcher: save_png failed with error %d" % err)
		_rebuilding = false
		return

	print("Voxel atlas stitcher: wrote %d layers to %s + %s" % [LAYERS.size(), COLOR_OUT, NH_OUT])
	EditorInterface.get_resource_filesystem().scan()
	_rebuilding = false
