@tool
extends SceneTree

# One-time build tool: extracts the single "Take 001" clip from each Giant Rat
# per-animation FBX and packs them into one AnimationLibrary saved as
# rat_anims.res, renamed to the canonical EAnimation slot names rat.tres
# authors. Same vendor and pipeline as build_dog_anims.gd.
#
# The source anim FBX are build-inputs only (gitignored). To regenerate, copy
# them from the AssetDump "HEROIC FANTASY CREATURES FULL PACK VOL 1/Fantasy
# Animals Pack/Giant Rat/FBX Files" folder into asset_src/models/characters/rat/
# anims/ under the names in MAP (GiantRat@Idle -> idle, @Run -> run, @Death ->
# die, @GetHitFront -> hit, @JumpBite -> bite; the in-place clips, never _RM),
# then run:
#   Godot --path . --headless -s res://tools/build_rat_anims.gd
#
# No swim / fall clips, so those slots fall back to run / idle.

# Relative to asset_src/.
const ANIMS := "models/characters/rat/anims/"
const OUT := "res://assets/models/characters/rat/rat_anims.res"

# slot_name -> [fbx_relpath, loop]. "dead" is built below as the frozen final
# pose of the die clip.
const MAP := {
	"idle":       ["idle.fbx", true],
	"run":        ["run.fbx", true],
	"swim":       ["run.fbx", true],
	"swim_idle":  ["idle.fbx", true],
	"fall":       ["idle.fbx", true],
	"die":        ["die.fbx", false],
	"hitstun":    ["hit.fbx", false],
	"attack":     ["bite.fbx", false],
}

func _find(n, cls):
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r = _find(c, cls)
		if r:
			return r
	return null

# asset_src/ is .gdignored, so the FBX is parsed (FbxClipSource) rather than load()ed.
# Loaded by path: a headless -s run never registers C# global class names.
func _parse_player(f: String) -> AnimationPlayer:
	var scene = load("res://scripts/data/FbxClipSource.cs").new().ParseScene(ANIMS + f, false)
	return _find(scene, "AnimationPlayer") if scene else null

func _clip(players: Dictionary, f: String) -> Animation:
	if not players.has(f):
		players[f] = _parse_player(f)
	var ap = players[f]
	if ap == null or not ap.has_animation("Take 001"):
		return null
	return ap.get_animation("Take 001")

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
	# Persistent corpse pose: frozen final frame of the die clip.
	var die_src := _clip(players, "die.fbx")
	if die_src != null:
		lib.add_animation("dead", _freeze_last(die_src))
		print("added dead <- die.fbx (frozen final pose)")
	var err := ResourceSaver.save(lib, OUT)
	print("\nSaved ", OUT, " err=", err, " count=", lib.get_animation_list().size())
	quit()
