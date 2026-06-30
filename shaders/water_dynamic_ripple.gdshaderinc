// Dynamic radial water ripples — expanding rings emitted by entity wakes (open
// water) and footsteps (terrain puddles). Shared by voxel_water.gdshader and
// voxel_clip.gdshader so both read the same ring buffer. The C# side
// (SkyController.EmitWaterRipple / TickDynamicRipples) is the single producer.

// Width=MaxDynamicRipples × height=1 Rgbaf row populated by
// SkyController.TickDynamicRipples each frame. Per-pixel layout is
// (world_x, world_z, age_seconds, strength). Inactive slots beyond
// water_ripple_count are skipped via the loop's bounds check.
global uniform sampler2D water_ripple_tex : filter_nearest;
global uniform int water_ripple_count;
// Ring expansion speed in world units / second. Combined with age this
// gives the current ring radius for each impact.
global uniform float water_ripple_speed;
// Total active lifetime per impact. Beyond this the C# tick drops the
// entry; the shader applies a linear age fade so the wave dies smoothly.
global uniform float water_ripple_lifetime;
// Fade-in window in seconds. At age=0 the ring radius is 0 and the
// Gaussian sits entirely at the impact origin — a huge central pulse.
// Ramping amplitude from 0 → full over this window lets the ring
// propagate outward before becoming visible, so impacts read as
// expanding rings rather than instantaneous stamps.
global uniform float water_ripple_fade_in;
// Gaussian envelope tightness around the moving ring crest. Higher =
// thinner ring (rapid falloff with distance from crest); lower = wider,
// softer ring. Drives the visible ring's apparent thickness in meters.
global uniform float water_ripple_falloff;
// Overall amplitude of the radial perturbation applied to the water
// normal. 0 disables the effect entirely without skipping the loop.
global uniform float water_ripple_tilt;

// Core dynamic-ripple accumulator. Each active ripple is a single expanding
// ring: a Gaussian bump in surface height centered at the ring radius
// (= age × speed × radius_mul) and falling off with `falloff × falloff_mul` on
// either side. Returns the radial slope of that height field as a normal tilt:
//   d/d(dr)[exp(-falloff·dr²)] = -2·falloff·dr·exp(-falloff·dr²)
// positive inside the ring (dr < 0), negative outside — one propagating ring,
// no inner oscillations. Also outputs `ring`, the summed Gaussian envelope, so
// a low-contrast surface (puddles) can draw a visible bright rim where the
// normal tilt alone wouldn't read.
//
// `speed` is the ring expansion rate (m/s) — passed in, not read from the
// global, so puddles can run at a different speed than open-water wakes. The
// *_mul args let puddles use thinner (falloff_mul > 1), smaller (radius_mul < 1),
// subtler (amp_mul < 1) rings off the same buffer. Fade-in over
// `water_ripple_fade_in` prevents the age=0 central pulse; linear fade-out
// across the rest of lifetime avoids a hard cutoff.
vec2 dynamic_ripple_core(vec2 world_xz, float speed, float amp_mul,
		float falloff_mul, float radius_mul, out float ring) {
	vec2 tilt = vec2(0.0);
	ring = 0.0;
	if (water_ripple_tilt < 1e-4 || water_ripple_count <= 0) {
		return tilt;
	}
	float lifetime = max(water_ripple_lifetime, 1e-3);
	float falloff = water_ripple_falloff * falloff_mul;
	for (int i = 0; i < water_ripple_count; i++) {
		vec4 r = texelFetch(water_ripple_tex, ivec2(i, 0), 0);
		vec2 to = world_xz - r.xy;
		float d = length(to);
		if (d < 1e-3) { continue; }
		float age = r.z;
		float strength = r.w;
		float radius = age * speed * radius_mul;
		float dr = d - radius;
		float envelope = exp(-dr * dr * falloff);
		float slope = -2.0 * falloff * dr * envelope;
		float fade_in = clamp(age / max(water_ripple_fade_in, 1e-3), 0.0, 1.0);
		float fade_out = clamp(1.0 - age / lifetime, 0.0, 1.0);
		float fade = fade_in * fade_out;
		tilt -= (to / d) * (slope * fade * strength * water_ripple_tilt * amp_mul);
		ring += envelope * fade * strength;
	}
	return tilt;
}

// Open-water wake perturbation — global speed, unscaled, ring output discarded.
vec2 dynamic_ripple_perturb(vec2 world_xz) {
	float ring;
	return dynamic_ripple_core(world_xz, water_ripple_speed, 1.0, 1.0, 1.0, ring);
}
