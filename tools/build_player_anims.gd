@tool
extends SceneTree

# One-time build tool: extracts clips from the polyperfect anim FBX scenes and
# packs them into a single AnimationLibrary saved as player_anims.res, renamed
# to the canonical EAnimation slot names authored on default_player.tres.
#
# The source anim FBX are NOT committed (the baked player_anims.res is fully
# self-contained, so ~55MB of FBX build-inputs don't belong in the repo). To
# regenerate, first re-copy them from the external AssetDump into
# assets/models/characters/polyperfect/anims/ (paths in MAP below), then run:
#   Godot --path . --headless -s res://tools/build_player_anims.gd
#
# The
# clips all share identical bone-track paths (Group/Main/DeformationSystem/
# Skeleton3D:Bone) and the single-clip skeletons are a strict subset of
# man_casual_Rig's 82-bone skeleton, so they retarget cleanly onto the player
# mesh when played by an AnimationPlayer whose root_node is the rig root.

const BASE := "res://assets/models/characters/polyperfect/"
const OUT := "res://assets/models/characters/polyperfect/player_anims.res"

# target_name -> [fbx_relpath, source_clip, loop]
const MAP := {
	"idle":        ["anims/Idle_Generic.fbx", "Take 001", true],
	"run":         ["anims/Run_InPlace.fbx", "Take 001", true],
	"sprint":      ["anims/Run_InPlace.fbx", "Take 001", true],
	"sneak":       ["anims/Walk_Crouching.fbx", "Take 001", true],
	"sneak_idle":  ["anims/Idle_Crouching.fbx", "Take 001", true],
	"jump":        ["anims/Standing_Jump.fbx", "Take 001", false],
	"fall":        ["anims/Standing_Jump.fbx", "Take 001", false],
	"dash":        ["anims/Standing_Jump.fbx", "Take 001", false],
	"attack":      ["anims/Punch_RightHand.fbx", "Take 001", false],
	"attack2":     ["anims/Punch_LeftHand.fbx", "Take 001", false],
	"casting":     ["anims/Wizard_Attack.fbx", "Take 001", true],
	"die":         ["anims/Death_FallForwards.fbx", "Take 001", false],
	"dead":        ["anims/Death_FallForwards.fbx", "Take 001", false],
	# placeholders (reuse) for states without a dedicated source clip
	"hitstun":     ["anims/Idle_Generic.fbx", "Take 001", true],
	"swim":        ["anims/Run_InPlace.fbx", "Take 001", true],
	"swim_idle":   ["anims/Idle_Generic.fbx", "Take 001", true],
	"swim_sprint": ["anims/Run_InPlace.fbx", "Take 001", true],
	"skating":     ["anims/Run_InPlace.fbx", "Take 001", true],
	# everyday-life clips from the bundled Common_Animations set (82-bone)
	"interacting": ["anims/Common_Animations.fbx", "Common_Animation_Set_Make_Ok_Gesture", true],
	"reading":     ["anims/Common_Animations.fbx", "Common_Animation_Set_Standing_Texting", true],
	"using":       ["anims/Common_Animations.fbx", "Common_Animation_Set_Standing_Texting", true],
	"drinking":    ["anims/Common_Animations.fbx", "Common_Animation_Set_Standing_Idle", true],
	"eating":      ["anims/Common_Animations.fbx", "Common_Animation_Set_Standing_Idle", true],
}

func _find(n: Node, cls: String) -> Node:
	if n.get_class() == cls:
		return n
	for c in n.get_children():
		var r := _find(c, cls)
		if r:
			return r
	return null

func _init() -> void:
	# cache instantiated scenes + their AnimationPlayers by file
	var players := {}
	var lib := AnimationLibrary.new()
	for target in MAP:
		var entry: Array = MAP[target]
		var f: String = entry[0]
		var clip: String = entry[1]
		var loop: bool = entry[2]
		if not players.has(f):
			var inst = (load(BASE + f) as PackedScene).instantiate()
			players[f] = _find(inst, "AnimationPlayer")
		var ap: AnimationPlayer = players[f]
		if ap == null or not ap.has_animation(clip):
			print("MISSING ", f, " :: ", clip, " for ", target)
			continue
		var anim: Animation = ap.get_animation(clip).duplicate(true)
		anim.loop_mode = Animation.LOOP_LINEAR if loop else Animation.LOOP_NONE
		lib.add_animation(target, anim)
		print("added ", target, " <- ", f, "::", clip, " (", anim.length, "s loop=", loop, ")")
	var err := ResourceSaver.save(lib, OUT)
	print("\nSaved ", OUT, " err=", err, " count=", lib.get_animation_list().size())
	quit()
