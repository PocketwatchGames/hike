shader_type spatial;
// Shadow-only caster for generated roofs.
//
// Two reasons it can't be the plain voxel_shadow_caster:
//   - It must NOT discard on camera_clip. The roof's visible material does, and
//     Godot runs fragment() in the shadow pass too, so a cut-away roof would
//     stop shadowing the interior it just revealed.
//   - It MUST discard on the broken-roof noise, using the identical test the
//     visible surface uses. Otherwise a hole you can see through still casts a
//     solid shadow, and no light lands under it.
render_mode unshaded, cull_back, depth_draw_opaque;

#include "res://shaders/roof_broken.gdshaderinc"

// Pushed per MeshInstance3D by Roof.Build, from the style — same values the
// visible surface gets, so the two agree on where the holes are.
instance uniform float broken : hint_range(0.0, 1.0) = 0.0;
instance uniform float broken_scale = 0.35;
instance uniform float broken_edge_scale = 1.6;
instance uniform float broken_edge_jagged = 0.35;

varying vec3 world_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (roof_is_hole(world_pos, broken, broken_scale, broken_edge_scale, broken_edge_jagged)) {
		discard;
	}
	ALBEDO = vec3(0.0);
}
