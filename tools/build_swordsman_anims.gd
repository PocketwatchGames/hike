@tool
extends SceneTree

# One-time build tool: bakes still-pose snapshots from HeroPoses.fbx's "Take
# 001" animation into an AnimationLibrary saved as swordsman_anims.res. Every
# EAnimation slot maps to a single-keyframe "still" animation sampled at a
# chosen time in HeroPoses, so the model holds a hero pose per state instead
# of rapidly cycling through the full take. State categories share a pose so
# similar states (all idles, all locomotion, etc.) look related, with distinct
# poses for action moments (attack, die, cast).
#
# Replace with per-state Mixamo clips when those arrive — just rewrite the
# SLOTS table to point at real animation files.

const BASE := "res://assets/models/characters/polysplit/"
const OUT := "res://assets/models/characters/polysplit/swordsman_anims.res"

# Slot name -> [sample_time_in_HeroPoses_seconds, loop?]. Times picked to
# distribute several distinct hero poses across state categories; HeroPoses
# is 1.125s long. Loop=true makes a held still pose; loop=false fires
# animation_finished after `length`, which Player.cs needs for one-shots.
const SLOTS := {
	# Idle family — held neutral hero pose.
	"idle":        [0.95, true],
	"sneak_idle":  [0.95, true],
	"swim_idle":   [0.95, true],
	"dead":        [0.95, false],
	"hitstun":     [0.95, true],
	# Locomotion family — a different distinct pose.
	"run":         [0.30, true],
	"sprint":      [0.30, true],
	"sneak":       [0.42, true],
	"swim":        [0.30, true],
	"swim_sprint": [0.30, true],
	"skating":     [0.30, true],
	# Vertical motion / dash — mid-take pose.
	"jump":        [0.55, false],
	"fall":        [0.55, false],
	"dash":        [0.55, false],
	# Item use / interaction.
	"drinking":    [0.40, true],
	"eating":      [0.40, true],
	"reading":     [0.40, true],
	"using":       [0.40, true],
	"interacting": [0.40, true],
	"casting":     [0.18, true],
	# Combat one-shots — distinct action-ish poses.
	"attack":      [0.70, false],
	"attack2":     [0.85, false],
	"die":         [1.00, false],
}

# One-shot clips need a length so Finished signals. 0.5s is a reasonable
# placeholder for attack/die/jump pacing.
const STILL_LENGTH := 0.5

func _find(n, cls):
	if n.get_class() == cls: return n
	for c in n.get_children():
		var r = _find(c, cls)
		if r: return r
	return null

# Build a one-keyframe Animation that holds the source's pose at `sample_time`.
func _bake_still(src: Animation, sample_time: float, loop: bool) -> Animation:
	var dst := Animation.new()
	dst.length = STILL_LENGTH
	dst.loop_mode = Animation.LOOP_LINEAR if loop else Animation.LOOP_NONE
	for ti in range(src.get_track_count()):
		var t_type = src.track_get_type(ti)
		var new_idx = dst.add_track(t_type)
		dst.track_set_path(new_idx, src.track_get_path(ti))
		match t_type:
			Animation.TYPE_POSITION_3D:
				var v = src.position_track_interpolate(ti, sample_time)
				dst.position_track_insert_key(new_idx, 0.0, v)
			Animation.TYPE_ROTATION_3D:
				var v = src.rotation_track_interpolate(ti, sample_time)
				dst.rotation_track_insert_key(new_idx, 0.0, v)
			Animation.TYPE_SCALE_3D:
				var v = src.scale_track_interpolate(ti, sample_time)
				dst.scale_track_insert_key(new_idx, 0.0, v)
			_:
				# Unknown / unhandled track type — drop it from the still pose;
				# the skeleton bone tracks (position/rotation/scale) are what
				# matter for the visible pose.
				dst.remove_track(new_idx)
	return dst

func _init():
	var inst = (load(BASE + "HeroPoses.fbx") as PackedScene).instantiate()
	var ap := _find(inst, "AnimationPlayer") as AnimationPlayer
	var src: Animation = ap.get_animation("Take 001")
	var lib := AnimationLibrary.new()
	for name in SLOTS:
		var cfg: Array = SLOTS[name]
		var t: float = cfg[0]
		var loop: bool = cfg[1]
		var a := _bake_still(src, t, loop)
		lib.add_animation(name, a)
		print("baked ", name, " @ t=", t, " loop=", loop)
	var err := ResourceSaver.save(lib, OUT)
	print("\nSaved ", OUT, " err=", err, " count=", lib.get_animation_list().size())
	inst.free()
	quit()
