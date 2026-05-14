shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_opaque, cull_disabled;

// Lit + emissive particle shader. Samples the same light_map the sprite /
// voxel pipelines do, so dust, blood, splashes and similar non-magical
// particles darken in caves and brighten under sun the same way the world
// around them does. Emissive particles (fire, sparks, magical effects)
// route their color through the `emission` term so they stay full-bright
// regardless of ambient lighting.
//
// Usage:
//   - Lit (dust, blood, water, smoke): set `albedo` (RGB = color, A = alpha),
//     leave `emission` at default 0. Color is multiplied by the local light
//     sample (sun mask + block light), like a regular lit sprite.
//   - Emissive (fire, sparks, magic): set `emission` (RGB = color, A = alpha),
//     leave `albedo` at default 0. Color is output unmodulated.
//   - Mixed (e.g. dimly lit ember with bright core): set both. Alpha is
//     the max of the two so a non-zero emission keeps the particle visible
//     even when albedo.a is 0.
//
// Each particle's process material may also multiply COLOR via color_ramp;
// COLOR.a fades the final alpha over particle lifetime.

uniform vec4 albedo : source_color = vec4(0.0);
uniform vec4 emission : source_color = vec4(0.0);

global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;

varying vec3 world_pos;
// COLOR is the particle's per-instance color (set by the
// ParticleProcessMaterial's color / color_ramp). Capture it as flat in the
// vertex stage rather than relying on default vertex-color interpolation —
// matches the rain_drop.gdshader pattern and keeps fragment access explicit.
varying flat vec4 particle_color;

void vertex() {
	// Spherical billboard with per-particle scale preserved — matches the
	// previous StandardMaterial3D billboard_mode = PARTICLES behavior for
	// the 1x1 anim grid case (which every particle in the project uses).
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		INV_VIEW_MATRIX[0],
		INV_VIEW_MATRIX[1],
		INV_VIEW_MATRIX[2],
		MODEL_MATRIX[3]
	);
	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0)
	);
	world_pos = MODEL_MATRIX[3].xyz;
	particle_color = COLOR;
}

void fragment() {
	// Editor-preview fallback: light globals aren't registered until the
	// game starts, so the standalone .tres preview gets vec3(0) for
	// light_map_inv_size. Fall back to "full bright" so authoring sees
	// the particle's authored color without a cave-dimmed read.
	bool globals_ready = length(light_map_inv_size) > 1e-6;

	vec3 lit;
	if (globals_ready) {
		// Particles spawn at arbitrary positions and can briefly intersect
		// solid voxels (e.g. blood spawned at a mob's chest center on a
		// solid mob, dust from a footstep on a chunk top face). Push the
		// sample point up half a voxel into the air the same way sprite
		// shaders do, so we read the adjacent air voxel's light instead
		// of zero.
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);
		vec3 sun_lit = sun_mask * sun_intensity * sun_color;
		vec3 block_lit = lm.gba;
		lit = sun_lit + block_lit;
	} else {
		lit = vec3(1.0);
	}

	// render_mode is unshaded → final output = ALBEDO + EMISSION. Write
	// everything through ALBEDO and zero EMISSION so we don't accidentally
	// double-add anything (per the CLAUDE.md unshaded fragment-output note).
	// Match StandardMaterial3D's default behavior: vertex_color_use_as_albedo
	// defaults to false, so the per-particle COLOR.rgb did NOT tint the
	// rendered output. Only COLOR.a (driven by the process material's
	// color_ramp) drives the lifetime fade. Apply that here.
	ALBEDO = albedo.rgb * lit + emission.rgb;
	EMISSION = vec3(0.0);
	ALPHA = max(albedo.a, emission.a) * particle_color.a;
}
