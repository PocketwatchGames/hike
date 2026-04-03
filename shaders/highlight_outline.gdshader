shader_type spatial;
render_mode blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, unshaded;

uniform sampler2D texture_albedo : filter_nearest;
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
uniform float outline_width : hint_range(1.0, 5.0) = 2.0;

void vertex() {
	// Fixed-Y billboard: face camera while preserving model scale
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(INV_VIEW_MATRIX[0].xyz * length(MODEL_MATRIX[0].xyz), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0) * length(MODEL_MATRIX[1].xyz),
		vec4(INV_VIEW_MATRIX[2].xyz * length(MODEL_MATRIX[2].xyz), 0.0),
		MODEL_MATRIX[3]
	);
}

void fragment() {
	// Discard interior pixels so the real object shows through
	if (texture(texture_albedo, UV).a > 0.5) discard;

	// How many texels one screen pixel covers on each axis
	vec2 tex_size = vec2(textureSize(texture_albedo, 0));
	float texels_per_screen_px_x = abs(dFdx(UV.x)) * tex_size.x;
	float texels_per_screen_px_y = abs(dFdy(UV.y)) * tex_size.y;

	// Convert outline_width from screen pixels to texels per axis
	float search_x = outline_width * texels_per_screen_px_x;
	float search_y = outline_width * texels_per_screen_px_y;
	vec2 texel = 1.0 / tex_size;

	bool has_opaque_neighbor = false;
	for (float x = -search_x; x <= search_x; x += 1.0) {
		for (float y = -search_y; y <= search_y; y += 1.0) {
			float nx = x / max(search_x, 0.001);
			float ny = y / max(search_y, 0.001);
			if (nx * nx + ny * ny > 1.0) continue;
			vec2 sample_uv = UV + vec2(x, y) * texel;
			if (sample_uv.x >= 0.0 && sample_uv.x <= 1.0 &&
				sample_uv.y >= 0.0 && sample_uv.y <= 1.0) {
				if (texture(texture_albedo, sample_uv).a > 0.5) {
					has_opaque_neighbor = true;
				}
			}
		}
	}

	if (!has_opaque_neighbor) discard;

	ALBEDO = outline_color.rgb;
}
