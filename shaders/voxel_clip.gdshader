shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_back, unshaded;

global uniform float camera_clip;

uniform sampler3D light_map : filter_nearest;
uniform vec3 light_map_origin;
uniform vec3 light_map_inv_size;

const float MIN_AMBIENT = 0.06;

varying vec3 world_vertex;
varying float shaded_light;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;

	// Sample light from the air voxel adjacent to this face
	vec3 uvw = (CUSTOM0.xyz - light_map_origin) * light_map_inv_size;
	float light = textureLod(light_map, uvw, 0.0).r;
	light = max(light, MIN_AMBIENT);

	// Face direction shading from normal
	vec3 n = NORMAL;
	vec3 an = abs(n);
	float face_shade;
	if (an.y > 0.5) {
		face_shade = n.y > 0.0 ? 1.0 : 0.5;
	} else if (an.z > 0.5) {
		face_shade = 0.8;
	} else {
		face_shade = 0.6;
	}

	shaded_light = face_shade * light;
}

void fragment() {
	ALBEDO = COLOR.rgb * shaded_light;
	if (world_vertex.y > camera_clip) {
		discard;
	}
}
