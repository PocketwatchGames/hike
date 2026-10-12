// Animated weapon-swing smear. The mesh (WeaponSmear.cs) is a flat annular
// sector lying in the actor's forward plane. UV.x runs along the smear's
// travel and UV.y across it — along the arc for a sweep, radially for a
// thrust. UV2.x is the normalized position along the travel from the entry
// side (0) to the exit side (1); the mesh bakes the motion and sweep direction
// into it.
//
// Two soft wipes drive the alpha over the swing:
//  - reveal (entry): instant when entry_time <= 0, otherwise a front that
//    sweeps from the entry side toward the exit side over [0, entry_time].
//  - erase  (exit):  a front that erases from the entry/back side over
//    [exit_start_time, exit_time].
// Premultiplied into ALBEDO with additive blending so the streak glows and a
// wiped-away fragment contributes nothing.
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never, shadows_disabled, ambient_light_disabled, fog_disabled;

uniform sampler2D streak_tex : source_color, filter_linear, repeat_enable;
uniform vec4 tint : source_color = vec4(1.0, 0.95, 0.85, 1.0);
// Across-travel repeats of the streak texture — higher values pack more
// streaks running along the motion.
uniform float streak_tiling = 6.0;
// Width (in normalized wipe units) of the soft reveal / erase fronts.
uniform float edge_softness = 0.12;
// Soft fade at the two edges of the band that run along the travel.
uniform float radial_fade = 0.12;

// Per-instance timing so a single shared material drives every smear. Set from
// WeaponSmear.cs via SetInstanceShaderParameter; `time` is advanced each frame.
instance uniform float time = 0.0;
instance uniform float entry_time = 0.0;
instance uniform float exit_start_time = 0.1;
instance uniform float exit_time = 0.3;

varying vec2 v_uv;
varying float v_s;

void vertex()
{
	v_uv = UV;
	v_s = UV2.x;
}

void fragment()
{
	float reveal = 1.0;
	if (entry_time > 0.0)
	{
		float front = time / entry_time;
		reveal = 1.0 - smoothstep(front - edge_softness, front + edge_softness, v_s);
	}

	float erase_front = (time - exit_start_time) / max(exit_time - exit_start_time, 1e-4);
	float erase = smoothstep(erase_front - edge_softness, erase_front + edge_softness, v_s);

	float side = smoothstep(0.0, radial_fade, v_uv.y) * (1.0 - smoothstep(1.0 - radial_fade, 1.0, v_uv.y));
	float streak = texture(streak_tex, vec2(v_uv.x, v_uv.y * streak_tiling)).r;

	float coverage = reveal * erase * side * streak;
	ALBEDO = tint.rgb * tint.a * coverage;
	ALPHA = 1.0;
}
