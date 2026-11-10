// A static ground stain (a blood pool) for the ground-stain projector: a flat
// layer-5 quad drawn from above into ground_stain_tex. The stain textures are
// white shapes, so one material per SHAPE serves every colour — each scene sets
// its own `tint` (instance_shader_parameters/tint), sRGB with coverage in alpha.
shader_type spatial;
render_mode unshaded, cull_disabled, fog_disabled, blend_mix, depth_draw_never;

uniform sampler2D stain_texture : source_color, filter_nearest;
instance uniform vec4 tint : source_color = vec4(1.0);

void fragment()
{
	vec4 s = texture(stain_texture, UV);
	ALBEDO = s.rgb * tint.rgb;
	ALPHA = s.a * tint.a;
}
