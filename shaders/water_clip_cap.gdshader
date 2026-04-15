shader_type spatial;
render_mode blend_mix, depth_draw_never, unshaded;
stencil_mode read, compare_equal, 2;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
uniform vec3 water_color : source_color = vec3(0.15, 0.5, 0.95);
uniform vec3 water_deep_tint : source_color = vec3(0.05, 0.15, 0.4);
uniform float water_alpha_min : hint_range(0.0, 1.0) = 0.4;
global uniform float water_alpha_max;
global uniform float water_depth_scale;

void fragment() {
	// Reconstruct world position of this fragment on the cap plane
	vec3 world_pos = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;

	// Sample lighting at the cap plane position
	vec3 uvw = (world_pos - light_map_origin) * light_map_inv_size;
	float light = pow(textureLod(light_map, uvw, 0.0).r, light_falloff_exp);

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

	// Deeper water is darker and more opaque — matches front face logic
	vec3 color = mix(water_color, water_deep_tint, depth_factor);
	ALBEDO = color * light;
	ALPHA = mix(water_alpha_min, water_alpha_max, depth_factor);
}
