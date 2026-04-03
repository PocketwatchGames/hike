shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded;
stencil_mode write, compare_always, 2;

uniform sampler2D texture_albedo : source_color, filter_nearest;

void vertex() {
	MODELVIEW_MATRIX = VIEW_MATRIX * mat4(
		vec4(INV_VIEW_MATRIX[0].xyz * length(MODEL_MATRIX[0].xyz), 0.0),
		vec4(0.0, 1.0, 0.0, 0.0) * length(MODEL_MATRIX[1].xyz),
		vec4(INV_VIEW_MATRIX[2].xyz * length(MODEL_MATRIX[2].xyz), 0.0),
		MODEL_MATRIX[3]
	);
}

void fragment() {
	vec4 col = texture(texture_albedo, UV);
	if (col.a < 0.5) discard;
	ALBEDO = col.rgb;
}
