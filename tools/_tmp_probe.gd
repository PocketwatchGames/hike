extends SceneTree
var shots := 0
func _init():
	var root := Node3D.new()
	get_root().add_child(root)
	var cam := Camera3D.new()
	cam.position = Vector3(0, 1.0, 3.2)
	cam.look_at_from_position(Vector3(0,1.0,3.2), Vector3(0,1.0,0), Vector3.UP)
	root.add_child(cam)
	var sun := DirectionalLight3D.new()
	sun.rotation = Vector3(deg_to_rad(-50), deg_to_rad(30), 0)
	root.add_child(sun)
	var env := WorldEnvironment.new()
	var e := Environment.new(); e.background_mode = Environment.BG_COLOR
	e.background_color = Color(0.3,0.4,0.5); e.ambient_light_color = Color(1,1,1); e.ambient_light_energy = 1.0
	env.environment = e; root.add_child(env)

	# Villager: BasicHero_M + model_lit + Hunter allowlist + swordsman idle
	var v = (load("res://assets/models/characters/polysplit/BasicHero_M.fbx") as PackedScene).instantiate()
	v.position = Vector3(-0.7, 0, 0)
	var allow = ["M_Head","M_eyes0","M_eyebrows0","M_mouth0","M_hair_2b","M_Hunter_Top","M_Hunter_Bottom","M_Hunter_FeltedHat"]
	var mat = load("res://resources/materials/model_lit.tres")
	_apply(v, allow, mat)
	var vap := AnimationPlayer.new(); vap.add_animation_library("", load("res://assets/models/characters/polysplit/swordsman_anims.res"))
	v.add_child(vap); root.add_child(v); vap.play("idle")

	# Sparrow: birdy + birdy_lit + idle, scale 0.12
	var b = (load("res://assets/models/characters/birdy/birdy.fbx") as PackedScene).instantiate()
	b.scale = Vector3(0.12,0.12,0.12); b.position = Vector3(0.7, 0.9, 0)
	_apply(b, [], load("res://resources/materials/birdy_lit.tres"))
	var bap := AnimationPlayer.new(); bap.add_animation_library("", load("res://assets/models/characters/birdy/birdy_anims.res"))
	b.add_child(bap); root.add_child(b); bap.play("idle")

	process_frame.connect(_tick)

func _apply(n, allow, mat):
	if n is MeshInstance3D:
		if allow.size() > 0:
			n.visible = allow.has(str(n.name))
		n.material_override = mat
	for c in n.get_children(): _apply(c, allow, mat)

func _tick():
	shots += 1
	if shots == 6:
		var img := get_root().get_texture().get_image()
		img.save_png("res://tools/_probe.png")
		print("SAVED probe png ", img.get_size())
		quit()
