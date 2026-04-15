shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

global uniform float camera_clip;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
uniform sampler3D light_map : filter_linear;
uniform vec3 light_map_origin;
uniform vec3 light_map_inv_size;
uniform float water_alpha_min : hint_range(0.0, 1.0) = 0.4;
global uniform float water_alpha_max;
global uniform float water_depth_scale;
uniform vec3 water_deep_tint : source_color = vec3(0.05, 0.15, 0.4);

const float MIN_AMBIENT = 0.0;

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
	float light = max(texture(light_map, uvw).r, MIN_AMBIENT);
	float shaded_light = face_shade * light;

	// Deeper water is darker and more opaque
	vec3 color = mix(COLOR.rgb, water_deep_tint, depth_factor);
	ALBEDO = color * shaded_light;
	ALPHA = mix(water_alpha_min, water_alpha_max, depth_factor);
}
