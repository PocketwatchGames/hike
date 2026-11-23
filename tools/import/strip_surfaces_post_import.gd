@tool
extends EditorScenePostImport

# Post-import script for a character FBX: deletes every mesh surface whose
# imported material is named in STRIPPED_MATERIALS. For layers the game's
# single-material, pixel-snapped look cannot draw — alpha hair-strand cards
# render as noise at this scale, and ModelAnimator's whole-mesh material
# override would paint them with the body atlas anyway.
#
# Opt a model in by setting `import_script/path` in its .fbx.import to this
# file, and add its material name below. Removing at import (not hiding at
# runtime) means the surface costs nothing: no draw call, no outline pass.

const STRIPPED_MATERIALS := [
	"M_GiantRatFur",
]

func _post_import(scene: Node) -> Object:
	_strip(scene)
	return scene

func _strip(node: Node) -> void:
	if node is MeshInstance3D:
		var mesh := (node as MeshInstance3D).mesh as ArrayMesh
		if mesh != null:
			for s in range(mesh.get_surface_count() - 1, -1, -1):
				var mat := mesh.surface_get_material(s)
				if mat != null and STRIPPED_MATERIALS.has(mat.resource_name):
					mesh.surface_remove(s)
	for child in node.get_children():
		_strip(child)
