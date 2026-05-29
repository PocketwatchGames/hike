shader_type spatial;
// Back-face mask material — used in the off-screen cap-mask render only.
// Renders the inverted-cull voxel mesh and writes white (mask=1) wherever
// the back-face fragment passes the depth test. Above-clip back-faces are
// discarded with the same dither as the front-face shader so the cap
// region above clip stays white (the SubViewport's white clear shows).
// Below-clip back-faces write white over any underground front-face that
// painted black through clipped geometry.
render_mode unshaded, cull_front, depth_draw_opaque, shadows_disabled;

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_delay;
#include "res://shaders/clip_dither.gdshaderinc"

varying vec3 world_vertex;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	if (clip_dither_should_discard(world_vertex, FRAGCOORD.xy)) {
		discard;
	}
	ALBEDO = vec3(1.0);
}
