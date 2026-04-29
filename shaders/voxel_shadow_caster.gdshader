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

// block-shadow directional light direction. Used to identify the block-shadow light's shadow
// pass and discard, so terrain never writes depth into the block-shadow atlas.
// Without this, indoor terrain (cave roofs etc.) would block the block-shadow
// light from above and the entire indoor space would read as fully
// shadowed — defeating the per-sprite grounding shadow goal.
global uniform vec3 block_shadow_dir;

void fragment() {
	// block-shadow pass discard: when this material is being rendered into the block-shadow
	// light's shadow atlas, the rendering camera's forward axis equals
	// block_shadow_dir. Discard so terrain contributes nothing to that atlas.
	// Sun + moon shadow passes have very different cam_forward, so the
	// 0.99 threshold leaves a safe margin from any other pass.
	vec3 cam_forward = -INV_VIEW_MATRIX[2].xyz;
	if (dot(cam_forward, normalize(block_shadow_dir)) > 0.99) {
		discard;
	}
	ALBEDO = vec3(0.0);
}
