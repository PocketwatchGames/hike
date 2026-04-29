shader_type spatial;
// blend_mix forces the alpha queue (Godot requires stencil-reading shaders
// to be in alpha; a pure-opaque queue rejects them). depth_draw_opaque
// then makes the cap occlude later transparent draws — without it, water
// front faces (also depth_draw_never) read past the cap through deeper
// scene depth and bleed through, the original "shoreline through the
// clip" symptom. ALPHA stays 1 so the cap reads as solid.
render_mode blend_mix, unshaded, depth_draw_opaque;
stencil_mode read, compare_equal, 1;

global uniform int clip_debug_mode;

void fragment() {
	if (clip_debug_mode == 6) {
		discard;
	}
	if (clip_debug_mode == 2 || clip_debug_mode == 13) {
		ALBEDO = vec3(1.0, 0.0, 0.0);
	} else {
		ALBEDO = vec3(0.0);
	}
	ALPHA = 1.0;
}
