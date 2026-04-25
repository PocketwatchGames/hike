shader_type spatial;
render_mode blend_mix, depth_draw_never, unshaded;
stencil_mode read, compare_equal, 2;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
#include "res://shaders/sky_common.gdshaderinc"
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

// All water styling comes from region-driven globals — same authored set
// as voxel_water.gdshader so the cap reads continuously with the water
// below when the ceiling cutaway reveals both.
global uniform vec3 water_shallow_tint : source_color;
global uniform vec3 water_deep_tint : source_color;
global uniform float water_alpha_min;
global uniform float water_alpha_max;
global uniform float water_depth_scale;
global uniform float water_turbidity_exp;

global uniform float ripple_scale_a;
global uniform float ripple_scale_b;
global uniform vec2 ripple_offset_a;
global uniform vec2 ripple_offset_b;
global uniform float ripple_strength;
global uniform float ripple_pixel_size;
global uniform float fresnel_power;
global uniform float reflection_strength;
// SSR removed — see voxel_water.gdshader for the rationale. The cap
// reflects sky only; sprite-based reflections handle upright objects.
global uniform vec3 foam_color;
global uniform float foam_depth;
global uniform float foam_scale;
global uniform vec2 foam_offset;
global uniform float foam_strength;
global uniform float foam_threshold;
global uniform float foam_sharpness;

float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i); float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0)); float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1)); float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1)); float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

vec2 pixelize(vec2 uv) {
	float px = max(ripple_pixel_size, 0.01);
	return floor(uv * px) / px;
}

// Sample two pre-baked normal-map textures and slerp toward +Y by
// ripple_strength — matching voxel_water.gdshader.
uniform sampler2D ripple_tex_a : filter_linear, repeat_enable, hint_normal;
uniform sampler2D ripple_tex_b : filter_linear, repeat_enable, hint_normal;

vec3 decode_normal_map(sampler2D tex, vec2 uv) {
	vec3 c = texture(tex, uv).rgb * 2.0 - 1.0;
	return vec3(c.r, c.b, c.g);
}

vec3 slerp_normals(vec3 a, vec3 b, float t) {
	float cos_omega = clamp(dot(a, b), -0.9999, 0.9999);
	float omega = acos(cos_omega);
	if (omega < 1e-3) { return normalize(mix(a, b, t)); }
	float sin_omega = sin(omega);
	return (sin((1.0 - t) * omega) * a + sin(t * omega) * b) / sin_omega;
}

vec3 ripple_normal(vec2 world_xz, float time) {
	vec2 uv_a = world_xz * ripple_scale_a + ripple_offset_a;
	vec2 uv_b = world_xz * ripple_scale_b + ripple_offset_b;
	vec3 na = decode_normal_map(ripple_tex_a, uv_a);
	vec3 nb = decode_normal_map(ripple_tex_b, uv_b);
	vec3 combined = normalize(na + nb);
	return slerp_normals(vec3(0.0, 1.0, 0.0), combined, ripple_strength);
}

void fragment() {
	vec3 world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;

	// The cap is the horizontal slice of water at the cutaway plane. Treat
	// it as a +Y facing surface so ripples and reflections apply uniformly.
	vec3 n = ripple_normal(world_pos.xz, TIME);

	vec3 uvw = (world_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Unshaded (no light() / ATTENUATION), so cloud shadow folds directly
	// into sun_lit. Block light stays unaffected.
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
	vec3 block_lit = lm.gba;
	vec3 light = min(vec3(1.0), sun_lit + block_lit);

	// Compute water thickness from depth buffer
	float scene_depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 scene_ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth * 2.0 - 1.0, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_ndc;
	scene_view /= scene_view.w;

	vec4 frag_ndc = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z * 2.0 - 1.0, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_ndc;
	frag_view /= frag_view.w;

	float thickness = abs(scene_view.z - frag_view.z);
	float depth_factor_raw = clamp(thickness / water_depth_scale, 0.0, 1.0);
	float depth_factor = pow(smoothstep(0.0, 1.0, depth_factor_raw), water_turbidity_exp);

	// Deeper water is darker and more opaque — matches front face logic.
	vec3 base_color = mix(water_shallow_tint, water_deep_tint, depth_factor);
	vec3 water_col = base_color * light;

	// Shoreline foam — scrolling noise band where thickness < foam_depth.
	float shallow = 1.0 - smoothstep(0.0, foam_depth, thickness);
	vec2 fuv = pixelize(world_pos.xz * foam_scale + foam_offset);
	float fn = vnoise(vec3(fuv, 0.0));
	float band = max(1e-4, 1.0 - foam_sharpness);
	float mask = smoothstep(foam_threshold, min(foam_threshold + band, 1.0), fn);
	float foam_amount = shallow * mask * foam_strength;
	water_col = mix(water_col, foam_color * light, foam_amount);

	// Reflection path — same logic as voxel_water.gdshader.
	vec3 view_to_frag = normalize(world_pos - CAMERA_POSITION_WORLD);
	vec3 refl = reflect(view_to_frag, n);
	float fresnel = pow(1.0 - clamp(dot(-view_to_frag, n), 0.0, 1.0), fresnel_power);

	vec3 refl_col = sample_sky(world_pos, refl, TIME) * sun_mask * cloud_sun;
	float refl_mix = fresnel * reflection_strength;
	vec3 final_col = mix(water_col, refl_col, refl_mix);

	ALBEDO = final_col;
	ALPHA = max(mix(water_alpha_min, water_alpha_max, depth_factor), foam_amount);
}
