extends SceneTree

func _init():
	var d = load("res://resources/data/world_gen/mob_descriptors/goblin_desert.tres")
	print("DESC class_ok=", d.get_script() != null, " mob=", d.get("mob") != null)
	var pal = d.get("palette")
	var recs = pal.get("recolors") if pal != null else null
	print("PALETTE recolors=", recs.size() if recs != null else -1)
	if recs != null and recs.size() > 0:
		print("ENTRY color=", recs[0].get("color"), " meshes=", recs[0].get("meshNames"))
	# CreateState path
	var st = d.call("CreateState", Vector3.ZERO, 0.0)
	print("CreateState ok=", st != null, " Palette_set=", st.get("Palette") != null if st != null else false)
	quit()
