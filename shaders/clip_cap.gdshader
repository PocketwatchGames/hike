shader_type spatial;
// Cap plane that fills in the screen pixels where above-clip front-faces
// were discarded. The mask texture is built off-screen by a SubViewport
// rendering the chunk meshes on CapMaskLayer (cap_mask_terrain front-face
// + cap_mask_backface back-face). Sampling that mask via SCREEN_UV and
// discarding non-cap pixels gives the cap exactly the silhouette of
// above-clip solid in screen space — no stencil, no render-order quirks.
//
// Opaque queue (no blend_mix), depth_draw_opaque, normal depth test:
// the cap depth-tests against terrain front-faces and sprites in the
// main scene, so anything geometrically in front of the cap plane (e.g.,
// a grass sprite at the cap region's lower edge) correctly occludes it.
render_mode depth_draw_opaque, cull_back, unshaded;

global uniform int clip_debug_mode;
uniform sampler2D cap_mask_tex : filter_nearest, repeat_disable;

void fragment() {
	// 6 = cap disabled outright. 21 = the cut volume is being painted instead of
	// discarded, so the cap has nothing to fill and would only draw black over
	// the diagnostic.
	if (clip_debug_mode == 6 || clip_debug_mode == 21) {
		discard;
	}
	float mask = texture(cap_mask_tex, SCREEN_UV).r;
	if (mask < 0.5) {
		discard;
	}
	if (clip_debug_mode == 2 || clip_debug_mode == 13) {
		ALBEDO = vec3(1.0, 0.0, 0.0);
	} else {
		ALBEDO = vec3(0.0);
	}
}
