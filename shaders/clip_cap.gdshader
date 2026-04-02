shader_type spatial;
render_mode unshaded, depth_draw_never;
stencil_mode read, compare_equal, 1;

void fragment() {
	ALBEDO = vec3(0.0);
}
