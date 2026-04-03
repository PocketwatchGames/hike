shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled;

global uniform float camera_clip;

uniform sampler2D sprite_texture : source_color, filter_nearest;
uniform sampler3D light_map : filter_linear;
uniform vec3 light_map_origin;
uniform vec3 light_map_inv_size;

const float MIN_AMBIENT = 0.06;

varying flat vec3 world_origin;

void vertex() {
	// Y-billboard: rotate model to face camera around the Y axis only
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(normalize(cross(vec3(0.0, 1.0, 0.0), INV_VIEW_MATRIX[2].xyz)), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0),
		vec4(normalize(cross(INV_VIEW_MATRIX[0].xyz, vec3(0.0, 1.0, 0.0))), 0.0),
		MODEL_MATRIX[3]
	);
	MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
		vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
		vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
		vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
		vec4(0.0, 0.0, 0.0, 1.0)
	);
	world_origin = MODEL_MATRIX[3].xyz;
}

void fragment() {
	if (world_origin.y > camera_clip) {
		discard;
	}

	vec4 tex = texture(sprite_texture, UV);
	if (tex.a < 0.5) {
		discard;
	}

	// Sample light at the sprite's X/Z origin, but use per-pixel Y for vertical variation
	float world_y = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).y;
	vec3 uvw = (vec3(world_origin.x, world_y, world_origin.z) - light_map_origin) * light_map_inv_size;
	float light = textureLod(light_map, uvw, 0.0).r;
	light = max(light, MIN_AMBIENT);

	NORMAL = vec3(0.0, 1.0, 0.0);
	ALBEDO = tex.rgb;
	EMISSION = tex.rgb * light;
}
