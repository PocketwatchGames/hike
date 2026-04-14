shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, unshaded;

// Mesh shadow caster for MeshInstance3D entities (player/mob capsules etc).
// Writes the fragment's light-axis depth into the shadow map so receivers
// can depth-test against it.

global uniform vec3 shadow_origin;
global uniform vec3 shadow_depth_axis;

varying vec3 caster_world;

void vertex() {
	caster_world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
}

void fragment() {
	float depth = dot(caster_world - shadow_origin, shadow_depth_axis);
	ALBEDO = vec3(clamp(depth, 0.0, 1.0));
}
