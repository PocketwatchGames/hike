shader_type spatial;
render_mode unshaded, cull_back, depth_draw_opaque;

// Shadow-only caster for voxel terrain. The visible mesh (voxel_clip)
// runs with CastShadow.Off because its fragment() discards above
// camera_clip for the ceiling cutaway — which would also erase the
// shadow atlas contribution from above-clip geometry and make terrain
// above the cutaway stop casting shadows down into the visible interior.
// This shader omits that discard, so ceiling voxels still cast shadows.
//
// Depth is written automatically by Godot's shadow pass. ALBEDO is
// irrelevant for ShadowsOnly meshes; the fragment body is a formality.

void fragment() {
	ALBEDO = vec3(0.0);
}
