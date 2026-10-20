# Builds the HUD needs-meter icons (assets/textures/gui/needs/) from the
# Clean Vector Icons sources beside this file. Each source is a dark disc with
# a white glyph, and all share one disc. Writes 64px textures:
#   need_disc.png    - the disc's silhouette, white (every meter's background)
#   <need>_glyph.png - the glyph alone, white (that meter's fill)
# Both are white so Hud tints them per state. Godot does not watch asset_src/,
# so rerun by hand:
#   Godot --path . --headless --script <absolute path to this file>
extends SceneTree

const NEEDS := ["hunger", "thirst", "fatigue"]
const OUT_SIZE := 64
# The source's light outer rim sits past this fraction of the radius; the glyph
# mask stops short of it so the rim never reads as part of the fill.
const GLYPH_RADIUS := 0.86

func _init() -> void:
	var src_dir: String = get_script().resource_path.get_base_dir()
	if src_dir.is_empty():
		src_dir = ProjectSettings.globalize_path("res://").path_join("../asset_src/textures/gui/needs")
	var out_dir := ProjectSettings.globalize_path("res://assets/textures/gui/needs")
	for need in NEEDS:
		var src := Image.load_from_file(src_dir.path_join(need + ".png"))
		src.convert(Image.FORMAT_RGBA8)
		var w := src.get_width()
		var h := src.get_height()
		var disc := Image.create(w, h, false, Image.FORMAT_RGBA8)
		var glyph := Image.create(w, h, false, Image.FORMAT_RGBA8)
		var center := Vector2(w, h) * 0.5
		var radius: float = min(w, h) * 0.5
		for y in h:
			for x in w:
				var c := src.get_pixel(x, y)
				disc.set_pixel(x, y, Color(1, 1, 1, c.a))
				var inside := Vector2(x + 0.5, y + 0.5).distance_to(center) < radius * GLYPH_RADIUS
				# The disc is ~0.2 luminance and the glyph 1.0; ramp between them.
				var lum := clampf((c.get_luminance() - 0.3) / 0.6, 0.0, 1.0)
				glyph.set_pixel(x, y, Color(1, 1, 1, c.a * lum if inside else 0.0))
		disc.resize(OUT_SIZE, OUT_SIZE, Image.INTERPOLATE_LANCZOS)
		glyph.resize(OUT_SIZE, OUT_SIZE, Image.INTERPOLATE_LANCZOS)
		disc.save_png(out_dir.path_join("need_disc.png"))
		glyph.save_png(out_dir.path_join(need + "_glyph.png"))
		print("[need_icons] ", need)
	quit()
