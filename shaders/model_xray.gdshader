shader_type spatial;
// X-ray pass for 3D skinned-character / discoverable meshes — wired as the
// next_pass of model_lit_character materials (e.g. forest_bunny_lit.tres). The
// 3D-mesh counterpart of sprite_xray.gdshader: where the mesh is occluded by
// anything in the depth buffer (terrain, props, characters in front), it
// alpha-blends translucent BLACK over the occluder, darkening it by
// `xray_alpha`. Where the mesh is normally visible the depth check discards and
// the lit base pass shows through unchanged.
//
// Godot's default over-blend does the math for free with ALBEDO = vec3(0):
//   framebuffer = framebuffer * (1 - ALPHA)
// EMISSION is zeroed so the unshaded `ALBEDO + EMISSION` output stays black.
//
// Unlike the sprite version this needs no custom billboard vertex — the mesh
// has real geometry, so the default vertex stage (MVP + automatic skinning)
// produces the correct silhouette, and the geometry itself is the silhouette
// (no albedo alpha-cut needed). depth_test_disabled puts the pass in the
// transparent queue so DEPTH_TEXTURE is fully populated when we sample it.
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled, ambient_light_disabled;
// Skip pixels stamped with stencil = 8 by model_lit_character / sprite_lit_
// character (player / mobs / character interactives), so the X-ray doesn't draw
// over other characters in front. Terrain / props leave stencil 0, so X-ray
// still fires through every kind of real cover.
stencil_mode read, compare_not_equal, 8;

// Main viewport depth, sampled to decide occlusion. MUST be filter_nearest —
// depth can't be interpolated across geometry boundaries.
uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;
// How much the X-ray darkens the framebuffer where the mesh is occluded.
// 0 = invisible, 1 = solid black silhouette. Authored on model_xray.tres so the
// whole game shares one strength (matches sprite_xray's xray_alpha default).
uniform float xray_alpha : hint_range(0.0, 1.0) = 0.2;
// FP-precision tie-breaker against the base pass's own depth write. ~5cm is
// well inside any meaningful occluder thickness while outside FP noise.
uniform float xray_depth_bias : hint_range(0.0, 1.0) = 0.05;

// Instance-uniform order MUST match model_lit_body.gdshaderinc exactly for every
// shared name (visibility / silhouette_amount / silhouette_tint), or Godot
// assigns mismatched indices across the base + next_pass and routes pushes to
// the wrong slot. Xray-only uniforms go AFTER all shared names.
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
// Not read here — the X-ray cue is a flat black-at-alpha silhouette independent
// of memory state — but kept declared (same name + position) to hold the
// instance-uniform indices aligned with the base pass.
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);
// === xray-only uniforms below — must follow all shared names ===
// Per-instance fade for the X-ray. Mobs leave this at 1 (silhouette always on
// whenever occluded); an InteractiveXray-style driver can gate 0→1→0 so a mesh
// interactive only X-rays while the player is roughly looking at it.
instance uniform float xray_amount : hint_range(0.0, 1.0) = 1.0;

void fragment() {
	// Early out when the per-instance fade has driven X-ray fully off.
	if (xray_amount <= 0.001) {
		discard;
	}
	// Honor the same discovery dither as the base pass so an undiscovered /
	// fading mob doesn't leak its silhouette through walls at full strength.
	if (visibility < 1.0) {
		const mat4 bayer = mat4(
			vec4( 0.0,  8.0,  2.0, 10.0),
			vec4(12.0,  4.0, 14.0,  6.0),
			vec4( 3.0, 11.0,  1.0,  9.0),
			vec4(15.0,  7.0, 13.0,  5.0)
		) * (1.0 / 16.0);
		ivec2 fp = ivec2(FRAGCOORD.xy) % 4;
		if (bayer[fp.y][fp.x] >= visibility) {
			discard;
		}
	}

	// Occlusion test in linearized view-space depth (robust across reverse-Z /
	// projection variants — same approach as sprite_xray).
	float scene_depth_raw = textureLod(DEPTH_TEXTURE, SCREEN_UV, 0.0).x;
	vec4 scene_clip = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth_raw, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_clip;
	float scene_view_z = -scene_view.z / scene_view.w;

	vec4 frag_clip = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_clip;
	float frag_view_z = -frag_view.z / frag_view.w;

	// Visible (in front of or at the scene surface): let the lit base pass show.
	if (frag_view_z <= scene_view_z + xray_depth_bias) {
		discard;
	}

	// Occluded: alpha-blend translucent black. framebuffer *= (1 - ALPHA).
	ALBEDO = vec3(0.0);
	EMISSION = vec3(0.0);
	ALPHA = xray_alpha * xray_amount;
}
