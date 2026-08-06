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

// Iris cutaway (camera_clip_mode 4) cuts at two heights at once: a base plane
// over the world and a lower one inside a disc in plan. A cap is a flat quad and
// cannot sit at both, so there are TWO of them and each fills only its own
// region — without this the base cap, being the higher of the two, draws straight
// over the interior the disc just revealed.
global uniform bool clip_iris_enabled;
global uniform vec3 clip_iris_center;
global uniform vec3 clip_iris_right;
global uniform vec3 clip_iris_up;
global uniform float clip_iris_radius;
// Which region this instance fills: false = outside the disk (at the base
// height), true = inside it (at the disk's lower plane).
uniform bool iris_inside_disc = false;

void fragment() {
	if (clip_iris_enabled && clip_iris_radius > 0.0) {
		// Same camera-plane measure the clip shaders use, so the two caps meet
		// exactly on the disk's edge with no seam and no overlap. VERTEX is
		// view-space in fragment, hence the transform back out to world.
		vec3 cap_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec3 delta = cap_world - clip_iris_center;
		vec2 screen = vec2(dot(delta, clip_iris_right), dot(delta, clip_iris_up));
		bool inside = length(screen) <= clip_iris_radius;
		if (inside != iris_inside_disc) {
			discard;
		}
	} else if (iris_inside_disc) {
		// No disc up: the inner cap has no region to fill.
		discard;
	}
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
