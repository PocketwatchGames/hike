shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, unshaded;

global uniform float camera_clip;
global uniform vec3 debug_solid; // (0,0,0) = disabled, otherwise forces ALBEDO.

uniform sampler3D light_map : filter_linear;
uniform vec3 light_map_origin;
uniform vec3 light_map_inv_size;

uniform sampler2DArray tile_array : filter_nearest, repeat_enable;

uniform sampler2D shadow_map : filter_linear;
global uniform vec3 shadow_origin;
global uniform vec3 shadow_axis_u;
global uniform vec3 shadow_axis_v;
global uniform vec3 shadow_depth_axis;
global uniform float shadow_strength;
global uniform vec3 shadow_color;
const float SHADOW_DEPTH_BIAS = 0.002;

const float MIN_AMBIENT = 0.0;

varying vec3 world_vertex;
varying float face_shade;
varying vec3 world_normal;
varying flat float tile_index;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;
	tile_index = CUSTOM0.w;

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
	vec3 rel = world_vertex - shadow_origin;
	vec2 suv = vec2(dot(rel, shadow_axis_u), dot(rel, shadow_axis_v)) + 0.5;
	vec3 shadow_tint = vec3(1.0);
	if (suv.x >= 0.0 && suv.x <= 1.0 && suv.y >= 0.0 && suv.y <= 1.0) {
		// 4-tap PCF: run the depth test at four small offsets around the
		// receiver and average the binary results. Uses the Y-flip to
		// compensate for Godot SubViewport textures sampling upside-down.
		vec2 base_uv = vec2(suv.x, 1.0 - suv.y);
		float receiver_depth = dot(rel, shadow_depth_axis);
		float pcf_radius = 1.5 / 1024.0;
		float shadow_sum = 0.0;
		float d0 = texture(shadow_map, base_uv + vec2(-pcf_radius, -pcf_radius)).r;
		float d1 = texture(shadow_map, base_uv + vec2( pcf_radius, -pcf_radius)).r;
		float d2 = texture(shadow_map, base_uv + vec2(-pcf_radius,  pcf_radius)).r;
		float d3 = texture(shadow_map, base_uv + vec2( pcf_radius,  pcf_radius)).r;
		shadow_sum += step(d0 + SHADOW_DEPTH_BIAS, receiver_depth);
		shadow_sum += step(d1 + SHADOW_DEPTH_BIAS, receiver_depth);
		shadow_sum += step(d2 + SHADOW_DEPTH_BIAS, receiver_depth);
		shadow_sum += step(d3 + SHADOW_DEPTH_BIAS, receiver_depth);
		float shadow_amount = shadow_sum * 0.25 * shadow_strength;
		// tint = 1 - amount*(1-color): with color=(0,0,0) collapses to
		// 1-amount (pure darkening); color=(1,1,1) gives 1 (no shadow).
		shadow_tint = vec3(1.0) - shadow_amount * (vec3(1.0) - shadow_color);
	}

	// Triplanar blend — DC vertices have arbitrary normals, so a dominant-axis
	// pick produces visible seams. Sample the tile on all three planes and
	// blend by |normal|^k.
	vec3 blend = pow(abs(world_normal), vec3(4.0));
	blend /= (blend.x + blend.y + blend.z + 1e-5);
	vec4 tex_xy = texture(tile_array, vec3(world_vertex.x, -world_vertex.y, tile_index));
	vec4 tex_xz = texture(tile_array, vec3(world_vertex.x,  world_vertex.z, tile_index));
	vec4 tex_yz = texture(tile_array, vec3(world_vertex.z, -world_vertex.y, tile_index));
	vec4 tex_color = tex_xz * blend.y + tex_xy * blend.z + tex_yz * blend.x;

	// Sample lightmap per-pixel so it interpolates smoothly across the face.
	// Push 0.5 along the normal to land in the adjacent air voxel's center plane.
	vec3 light_sample_pos = world_vertex + world_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	float light = max(texture(light_map, uvw).r, MIN_AMBIENT);
	float shaded_light = face_shade * light;

	ALBEDO = tex_color.rgb * COLOR.rgb * shaded_light * shadow_tint;
	if (dot(debug_solid, vec3(1.0)) > 0.001) {
		ALBEDO = debug_solid;
	}
	if (world_vertex.y > camera_clip) {
		discard;
	}
}
