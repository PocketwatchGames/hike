shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never, blend_add;
stencil_mode write, compare_always, 2;

global uniform float camera_clip;
global uniform int clip_debug_mode;

varying vec3 world_vertex;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// Bias toward camera in clip space to pass depth test against coplanar
	// terrain. Combined with the WaterMesher top-face epsilon and shoreline
	// inset, this kills z-fighting at distance where 0.02 world-units of
	// separation approach a depth-buffer quantum.
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	POSITION.z -= 0.001 * POSITION.w;
}

void fragment() {
	if (world_vertex.y > camera_clip) {
		discard;
	}
	// Restrict stencil-2 writes to water voxels actually straddling the
	// cutaway. Water uses depth_draw_never on its front face, so unlike
	// the solid backface stencil (where front-face depth occludes the
	// far backface for fully-visible voxels), our backface always passes
	// depth. Without this lower bound, an underground lake entirely below
	// the clip marks stencil=2 across its full silhouette, and the water
	// cap renders its depth-fade gradient + shoreline foam at cap-Y over
	// the whole lake — visible as a "shoreline through the cap" artifact.
	// One voxel of slack covers voxels whose top sits at camera_clip.
	if (world_vertex.y < camera_clip - 1.0) {
		discard;
	}
	// Debug mode 3: paint stencil-2 write region cyan (additive blend, so
	// it brightens whatever's behind it). Reveals the screen region the
	// water cap is allowed to draw in. Mode 13 also paints cyan, paired
	// with voxel_clip's scene-blackout — both stencil regions show on a
	// dark background simultaneously.
	if (clip_debug_mode == 3 || clip_debug_mode == 13) {
		ALBEDO = vec3(0.0, 1.0, 1.0);
	} else {
		ALBEDO = vec3(0.0);
	}
}
