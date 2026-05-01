shader_type spatial;
// Front-face mask material — used in the off-screen cap-mask render only.
// Discards above-clip fragments (same dither logic as voxel_clip) so the
// mask depth buffer has NO depth at clipped pixels. Below-clip fragments
// write depth and ALBEDO=0 (black), so the back-face pass that runs
// alongside this can fail its depth test wherever a front-face wrote.
render_mode unshaded, depth_draw_opaque, cull_back, shadows_disabled;

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
#include "res://shaders/clip_dither.gdshaderinc"

varying vec3 world_vertex;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (clip_dither_should_discard(world_vertex.y, FRAGCOORD.xy)) {
		discard;
	}
	ALBEDO = vec3(0.0);
}
