shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;
stencil_mode read, compare_not_equal, 2;

uniform sampler2D texture_albedo : filter_nearest;
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
uniform float outline_width : hint_range(1.0, 5.0) = 2.0;

void vertex() {
	// Expand quad outward to make room for the outline beyond the sprite silhouette
	vec3 dir = VERTEX;
	float expand = outline_width * 0.05;
	VERTEX += normalize(dir) * expand;

	// Scale UVs to compensate so texture sampling stays aligned
	float len = length(dir);
	float scale = (len + expand) / max(len, 0.001);
	UV = (UV - 0.5) * scale + 0.5;

	// Billboard: face camera while preserving model scale
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(INV_VIEW_MATRIX[0].xyz * length(MODEL_MATRIX[0].xyz), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0) * length(MODEL_MATRIX[1].xyz),
		vec4(INV_VIEW_MATRIX[2].xyz * length(MODEL_MATRIX[2].xyz), 0.0),
		MODEL_MATRIX[3]
	);
}

void fragment() {
	vec2 tex_size = vec2(textureSize(texture_albedo, 0));
	vec2 texel = 1.0 / tex_size;

	bool has_opaque_neighbor = false;
	for (float x = -outline_width; x <= outline_width; x += 1.0) {
		for (float y = -outline_width; y <= outline_width; y += 1.0) {
			if (x * x + y * y > outline_width * outline_width) continue;
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
