// Thrust smear: a few arcs launched one after another that travel outward
// across the WeaponSmear mesh (Thrust motion). Only each arc's leading edge
// is bright; a short tail trails behind it. UV.x / UV2.x run along the travel
// from the near radius (0) to the reach (1); UV.y runs across the arc.
//
// Timing is normalized to the smear's lifetime (`exit_time`, set per scene on
// WeaponSmear), so the material sets the shape of the burst and the scene sets
// how long it lasts. `entry_time` / `exit_start_time` are declared only
// because WeaponSmear sets them on every smear; this shader ignores them.
shader_type spatial;
render_mode unshaded, cull_disabled, blend_add, depth_draw_never, shadows_disabled, ambient_light_disabled, fog_disabled;

uniform vec4 tint : source_color = vec4(1.0, 0.95, 0.85, 1.0);
uniform int arc_count : hint_range(1, 8) = 3;
// Fraction of the lifetime between one arc's launch and the next.
uniform float arc_stagger : hint_range(0.0, 0.5, 0.01) = 0.18;
// Brightness of each arc relative to the previous one.
uniform float arc_falloff : hint_range(0.0, 1.0, 0.01) = 0.7;
// Where an arc starts and stops along the travel (0 = near radius, 1 = reach).
uniform float travel_start : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float travel_end : hint_range(0.0, 1.5, 0.01) = 1.0;
// Ease-out power on the travel: 1 = constant speed, higher = a punchier
// launch that settles.
uniform float travel_ease : hint_range(1.0, 4.0, 0.05) = 2.0;
// Width of the bright leading edge and length of the tail behind it, both in
// travel units.
uniform float edge_width : hint_range(0.0, 0.2, 0.001) = 0.012;
uniform float tail_length : hint_range(0.0, 0.5, 0.001) = 0.03;
// Brightness of the tail where it meets the edge; it falls off quadratically
// to zero over tail_length.
uniform float tail_strength : hint_range(0.0, 1.0, 0.01) = 0.35;
// Extra forward bulge at the middle of each arc, in travel units — on top of
// the curvature the arc already has from being centered on the actor.
uniform float arc_bow : hint_range(-0.3, 0.3, 0.005) = 0.04;
// Fade toward the two ends of each arc.
uniform float end_fade : hint_range(0.0, 0.5, 0.01) = 0.3;
// Fade-out over the last part of each arc's travel.
uniform float travel_fade : hint_range(0.0, 1.0, 0.01) = 0.4;

instance uniform float time = 0.0;
instance uniform float entry_time = 0.0;
instance uniform float exit_start_time = 0.1;
instance uniform float exit_time = 0.3;

varying vec2 v_uv;

void vertex()
{
	v_uv = UV;
}

void fragment()
{
	float life = clamp(time / max(exit_time, 1e-4), 0.0, 1.0);
	float across = v_uv.y * 2.0 - 1.0;
	float bow = arc_bow * (1.0 - across * across);
	float s = v_uv.x - bow;
	float aa = fwidth(v_uv.x);

	// Each arc gets the remainder of the lifetime after the last launch.
	float travel_span = max(1.0 - arc_stagger * float(arc_count - 1), 1e-3);

	float coverage = 0.0;
	float strength = 1.0;
	for (int k = 0; k < arc_count; k++)
	{
		float p = (life - arc_stagger * float(k)) / travel_span;
		if (p > 0.0 && p < 1.0)
		{
			float eased = 1.0 - pow(1.0 - p, travel_ease);
			float front = mix(travel_start, travel_end, eased);
			// d > 0 is behind the front, d < 0 ahead of it (invisible).
			float d = front - s;
			float ahead = smoothstep(-aa, 0.0, d);
			float edge = 1.0 - smoothstep(edge_width, edge_width + aa, d);
			float tail_t = 1.0 - clamp((d - edge_width) / max(tail_length, 1e-4), 0.0, 1.0);
			float tail = tail_strength * tail_t * tail_t;
			float arc = ahead * max(edge, tail);
			float fade = 1.0 - smoothstep(1.0 - travel_fade, 1.0, p);
			coverage += arc * fade * strength;
		}
		strength *= arc_falloff;
	}

	float ends = smoothstep(1.0, 1.0 - end_fade, abs(across));
	coverage = min(coverage * ends, 1.0);
	ALBEDO = tint.rgb * tint.a * coverage;
	ALPHA = 1.0;
}
