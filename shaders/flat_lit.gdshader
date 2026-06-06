shader_type spatial;
// Flat-color world-lit shader for small solid props that want a painted color
// rather than a texture — the ladder planks. Identical lighting to
// model_lit.gdshader (samples the global light_map so it darkens in caves /
// under cloud shadow and brightens in sun, with a wrap-lambert form term off
// the real mesh normal) but the base color is a flat per-material `color`
// instead of an albedo texture. Used with mesh_xray + mesh_outline as next_pass
// so the planks highlight like every other 3D interactive.
//
// Lighting split matches model_lit / sprite_lit_common:
//   ALBEDO   = direct sun   (scaled by ATTENUATION in light())
//   EMISSION = ambient + block light (never shadowed)
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

// Flat painted color (the plank's paint). One material per color (yellow / raw
// pine / orange); planks cycle through three shared materials.
uniform vec3 color : source_color = vec3(0.8, 0.6, 0.3);

// World-lightmap globals (same set model_lit reads).
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"
#include "res://shaders/night_vision.gdshaderinc"

uniform float form_strength : hint_range(0.0, 1.0) = 0.55;

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec3 base = color;

	// Editor-preview fallback: light globals aren't registered until the game
	// starts; show the flat color full-bright while authoring. Mirrors model_lit.
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(normalize(world_normal), to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float form = mix(1.0, wrap, form_strength);

		float cloud = cloud_shadow_ground(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		vec3 block_lit = apply_night_vision(lm.gba);

		ALBEDO = base * sun_lit * (1.0 - sun_ambient);
		EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim);
	}
}

void light() {
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
