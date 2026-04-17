shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

global uniform float camera_clip;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
// Cloud occlusion parameters shared with the sky + fog + voxel + sprite
// shaders (registered by SkyController). cloud_shadow.gdshaderinc uses
// these to project cloud shadows along the sun direction. sun_world_dir
// must also be declared; add it before the include.
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"
uniform float water_alpha_min : hint_range(0.0, 1.0) = 0.3;
global uniform float water_alpha_max;
global uniform float water_depth_scale;
uniform vec3 water_shallow_tint : source_color = vec3(0.35, 0.7, 0.7);
uniform vec3 water_deep_tint : source_color = vec3(0.05, 0.15, 0.4);

const float MIN_AMBIENT = 0.0;

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

varying vec3 world_vertex;
varying vec3 world_normal;
varying float face_shade;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;

	// Face direction shading from normal
	vec3 n = NORMAL;
	vec3 an = abs(n);
	if (an.y > 0.5) {
		face_shade = n.y > 0.0 ? 1.0 : 0.5;
	} else if (an.z > 0.5) {
		face_shade = 0.8;
	} else {
		face_shade = 0.6;
	}
}

void fragment() {
	if (world_vertex.y > camera_clip) {
		discard;
	}

	// Compute water thickness from depth buffer
	float scene_depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 scene_ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth * 2.0 - 1.0, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_ndc;
	scene_view /= scene_view.w;

	vec4 frag_ndc = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z * 2.0 - 1.0, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_ndc;
	frag_view /= frag_view.w;

	float thickness = abs(scene_view.z - frag_view.z);
	float depth_factor = clamp(thickness / water_depth_scale, 0.0, 1.0);

	// Sample lightmap per-pixel for smooth interpolation across the face.
	// Water samples from itself: push 0.5 along -normal to land in the water voxel center.
	vec3 light_sample_pos = world_vertex - world_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Water is `unshaded` (no light() stage / no ATTENUATION available), so
	// cloud shadow multiplies sun_lit directly. Block light (lm.gba) stays
	// unaffected — torches shining on water don't care about clouds.
	float cloud = cloud_shadow_ground(world_vertex, TIME);
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * (1.0 - cloud * cloud_shadow_strength);
	vec3 block_lit = lm.gba;
	vec3 light = max(min(vec3(1.0), sun_lit + block_lit), vec3(MIN_AMBIENT));
	vec3 shaded_light = face_shade * light;

	// Deeper water is darker and more opaque
	vec3 color = mix(water_shallow_tint, water_deep_tint, depth_factor);
	ALBEDO = color * shaded_light;
	ALPHA = mix(water_alpha_min, water_alpha_max, depth_factor);
}
