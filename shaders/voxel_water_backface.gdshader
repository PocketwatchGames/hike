shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never, blend_add;
stencil_mode write, compare_always, 2;

global uniform float camera_clip;

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
	ALBEDO = vec3(0.0);
}
