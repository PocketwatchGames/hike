shader_type spatial;
render_mode unshaded, cull_front, depth_draw_never, blend_add;
stencil_mode write, compare_always, 1;

global uniform float camera_clip;
global uniform int clip_debug_mode;

varying vec3 world_vertex;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	// Mode 14: skip the above-clip discard so above-clip backfaces also
	// run (additive bright YELLOW). This is the diagnostic for
	// "would-be coverage if the discard were removed". If mode 14 paints
	// yellow over a water-poke-through region that mode 13 left bare,
	// the discard is the reason stencil isn't covering that region.
	bool above_clip = world_vertex.y > camera_clip;
	if (above_clip && clip_debug_mode != 14) {
		discard;
	}
	// Debug mode 4: paint stencil-1 write region green (additive blend).
	// Reveals the screen region the ceiling cap is allowed to draw in.
	// Mode 13 also paints green here — voxel_clip blanks the scene to
	// black in mode 13, so the additive green shows up cleanly without
	// being washed out by bright terrain albedo (which is mode 4's
	// problem on lit-up walls).
	if (clip_debug_mode == 4 || clip_debug_mode == 13) {
		ALBEDO = vec3(0.0, 1.0, 0.0);
	} else if (clip_debug_mode == 14) {
		// Below clip: green (same as mode 13). Above clip: yellow so the
		// extra coverage that the discard normally kills is visible.
		ALBEDO = above_clip ? vec3(1.0, 1.0, 0.0) : vec3(0.0, 1.0, 0.0);
	} else {
		ALBEDO = vec3(0.0);
	}
}
