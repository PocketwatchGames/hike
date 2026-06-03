@tool
extends SceneTree

# One-time build tool: extracts the single "Take 001" clip from each Goblin
# per-animation FBX (the Sword & Shield loadout) and packs them into one
# AnimationLibrary saved as goblin_anims.res, renamed to the canonical
# EAnimation slot names the goblin MobData authors (idle / run / attack / die /
# dead / hitstun / ...). The ModelAnimator on goblin.tscn plays clips by those
# slot names, exactly like the forest bunny and player pipelines.
#
# The source anim FBX are build-inputs only (not committed — the baked .res is
# self-contained, and assets/models/characters/*/anims/ is gitignored). To
# regenerate, re-copy them from the external AssetDump
# ("HEROIC FANTASY CREATURES FULL PACK VOL 1/Must Have Fantasy Villains Pack/
# Goblin/FBX Files/Goblin@<clip>SwordShield.FBX") into
# assets/models/characters/goblin/anims/ (see MAP), then run:
#   Godot --path . --headless -s res://tools/build_goblin_anims.gd
#
# The Goblin has no swim / fall / dizzy clips, so those slots fall back to the
# nearest fitting clip (idle / run / get-hit) — an accepted tradeoff for the
# sprite->model swap, mirroring the forest bunny. All clips share the goblin's
# 53-bone Skeleton3D track paths, so they retarget onto the base mesh when
# played by an AnimationPlayer rooted at the FBX root.

const ANIMS := "res://assets/models/characters/goblin/anims/"
const OUT := "res://assets/models/characters/goblin/goblin_anims.res"

# slot_name -> [fbx_relpath, loop]. "run"/"swim" use the in-place clips (physics
# drives locomotion, so a root-motion _RM clip would slide the body). "dead" is
# handled specially below — a frozen final-frame pose of the die clip.
const MAP := {
	"idle":       ["IdleSwordShield.fbx", true],
	"run":        ["RunSwordShield.fbx", true],
	"swim":       ["RunSwordShield.fbx", true],
	"swim_idle":  ["IdleSwordShield.fbx", true],
	"fall":       ["IdleSwordShield.fbx", true],
	"attack":     ["Attack1SwordShield.fbx", false],
	"die":        ["DeathSwordShield.fbx", false],
	"hitstun":    ["GetHitSwordShield.fbx", false],
	"dizzy":      ["GetHitSwordShield.fbx", true],
	# Shield-up ready stance reads as an aggressive alert for the goblin's
	# battle-cry behavior.
	"battlecry":  ["IdleProtectedSwordShield.fbx", true],
}

func _find(n, cls):
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r = _find(c, cls)
		if r:
			return r
	return null

func _clip(players: Dictionary, f: String) -> Animation:
	if not players.has(f):
		var inst = (load(ANIMS + f) as PackedScene).instantiate()
		players[f] = _find(inst, "AnimationPlayer")
	var ap: AnimationPlayer = players[f]
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
	var die_src := _clip(players, "DeathSwordShield.fbx")
	if die_src != null:
		lib.add_animation("dead", _freeze_last(die_src))
		print("added dead <- DeathSwordShield.fbx (frozen final pose)")
	var err := ResourceSaver.save(lib, OUT)
	print("\nSaved ", OUT, " err=", err, " count=", lib.get_animation_list().size())
	quit()
