shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, depth_test_disabled, skip_vertex_transform, shadows_disabled;

// Full-screen nearest-neighbor upscale for the chunky pixel-art pipeline.
// Samples the inner SubViewport texture with subpixel UV offset so the
// outer camera's output shifts smoothly while the scene itself stays
// pixel-snapped. An outer Camera3D renders this quad so Godot's built-in
// glow post-process can run on the upscaled image at full screen resolution.

uniform sampler2D inner_tex : filter_nearest, source_color;
uniform vec2 uv_scale = vec2(1.0, 1.0);
uniform vec2 uv_offset = vec2(0.0, 0.0);

void vertex()
{
	POSITION = vec4(VERTEX.xy, 0.0, 1.0);
}

void fragment()
{
	vec2 inner_uv = UV * uv_scale + uv_offset;
	ALBEDO = texture(inner_tex, vec2(inner_uv.x, 1.0 - inner_uv.y)).rgb;
}
