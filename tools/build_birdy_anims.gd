@tool
extends SceneTree

# One-time build tool: extracts the single clip from each Birdy (Magic Pig
# Games / Infinity PBR) per-animation FBX and packs them into one
# AnimationLibrary saved as birdy_anims.res, renamed to the canonical
# EAnimation slot names the sparrow MobData authors (idle / run / die / dead /
# hitstun / ...). The ModelAnimator on sparrow.tscn plays clips by those slot
# names, exactly like the forest bunny's forest_bunny_anims.res.
#
# Unlike the bunny FBX (whose lone clip is named "Take 001"), each Birdy FBX
# keeps its authored clip name (e.g. "Air Idle"), so we read the first entry of
# the AnimationPlayer's list rather than hardcoding a name.
#
# The sparrow flies, so its "ground" locomotion slots map to flight clips:
# idle -> hover (Air Idle), run/swim -> Fly Forward, etc. The source anim FBX
# are build-inputs only; the baked .res is self-contained (mirrors the bunny /
# swordsman pipeline). To regenerate, re-copy the FBX from the external
# AssetDump into assets/models/characters/birdy/anims/ (see paths in MAP), then:
#   Godot --path . --headless -s res://tools/build_birdy_anims.gd
#
# All clips share the Birdy "Skeleton3D" track paths, so they retarget onto the
# base mesh when played by an AnimationPlayer rooted at the FBX root.

const ANIMS := "res://assets/models/characters/birdy/anims/"
const OUT := "res://assets/models/characters/birdy/birdy_anims.res"

# slot_name -> [fbx_relpath, loop]. The sparrow reads best when it FLAPS while
# moving and GLIDES (wings still) when idle — the opposite of the obvious
# mapping. So the in-place hover-flap (air_idle) drives all locomotion, and the
# calm near-motionless perch (ground_idle) drives the idles. The forward-flight
# clips (fly_forward / fly_speed_forward) are deliberately NOT used: they carry
# root motion that slides the body out from under the physics-driven mob ("drops
# it in space"); air_idle flaps in place, so locomotion stays put.
const MAP := {
	"idle":      ["ground_idle.fbx", true],
	"swim_idle": ["ground_idle.fbx", true],
	"run":       ["air_idle.fbx", true],
	"swim":      ["air_idle.fbx", true],
	"sprint":    ["air_idle.fbx", true],
	"fall":      ["air_idle.fbx", true],
	"die":       ["death_in_air.fbx", false],
	"dizzy":     ["hit_front.fbx", true],
	"hitstun":   ["hit_front.fbx", false],
	# Extra clip other mobs sharing this rig may want; harmless for the sparrow.
	"attack":    ["air_attack.fbx", false],
}

func _find(n, cls):
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r = _find(c, cls)
		if r:
			return r
	return null

# Return the lone source clip from an anim FBX, whatever its authored name.
func _clip(players: Dictionary, f: String) -> Animation:
	if not players.has(f):
		var inst = (load(ANIMS + f) as PackedScene).instantiate()
		players[f] = _find(inst, "AnimationPlayer")
	var ap: AnimationPlayer = players[f]
	if ap == null:
		return null
	var list = ap.get_animation_list()
	if list.is_empty():
		return null
	return ap.get_animation(list[0])

# Snapshot the source's final pose into a single-keyframe looping Animation, so
# a persistent state (dead) holds a static pose. ModelAnimator re-issues Play()
# for the active state every tick once a non-looping clip finishes, which would
# otherwise restart the full death clip; a 1-frame loop renders identically on
# every re-play.
func _freeze_last(src: Animation) -> Animation:
	var t := src.length
	var dst := Animation.new()
	dst.length = 0.1
	dst.loop_mode = Animation.LOOP_LINEAR
	for ti in range(src.get_track_count()):
		var t_type = src.track_get_type(ti)
		var idx = dst.add_track(t_type)
		dst.track_set_path(idx, src.track_get_path(ti))
		match t_type:
			Animation.TYPE_POSITION_3D:
				dst.position_track_insert_key(idx, 0.0, src.position_track_interpolate(ti, t))
			Animation.TYPE_ROTATION_3D:
				dst.rotation_track_insert_key(idx, 0.0, src.rotation_track_interpolate(ti, t))
			Animation.TYPE_SCALE_3D:
				dst.scale_track_insert_key(idx, 0.0, src.scale_track_interpolate(ti, t))
			_:
				dst.remove_track(idx)
	return dst

func _init() -> void:
	var players := {}
	var lib := AnimationLibrary.new()
	for slot in MAP:
		var entry: Array = MAP[slot]
		var src := _clip(players, entry[0])
		if src == null:
			print("MISSING ", entry[0], " for slot ", slot)
			continue
		var anim: Animation = src.duplicate(true)
		anim.loop_mode = Animation.LOOP_LINEAR if entry[1] else Animation.LOOP_NONE
		lib.add_animation(slot, anim)
		print("added ", slot, " <- ", entry[0], " (", anim.length, "s loop=", entry[1], ")")
	# Persistent corpse pose: frozen final frame of the death clip.
	var die_src := _clip(players, "death_in_air.fbx")
	if die_src != null:
		lib.add_animation("dead", _freeze_last(die_src))
		print("added dead <- death_in_air.fbx (frozen final pose)")
	var err := ResourceSaver.save(lib, OUT)
	print("\nSaved ", OUT, " err=", err, " count=", lib.get_animation_list().size())
	quit()
