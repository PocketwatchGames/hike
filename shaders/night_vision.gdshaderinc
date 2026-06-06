// Night-vision lift for perceived ambient block lighting.
//
// When the player has a night-vision status effect active, `night_vision`
// is driven from C# (Player) in 0..1 — the *degree* of night vision,
// mirroring PlayerPerception's relief term (a NightVision stat of 1.85
// yields 0.85). At 0 this is an exact identity, so rendering with no
// night vision is byte-for-byte unchanged.
//
// What it does: raises the LOWER HALF of the block-light brightness range
// partway toward 50% (see RESPONSE), so light that normally reads as "dark"
// (roughly the 0.1..0.5 band) instead reads as "dim". The lift:
//   - preserves hue — it scales the RGB triple by the brightness gain
//     rather than lifting each channel independently (a torch stays warm),
//   - preserves contrast — it's a monotonic curve, not a flat floor, so
//     relative brightness within the world is retained,
//   - meets the identity exactly at 0.5 (sqrt(0.5*0.5) == 0.5), so there is
//     no seam between the lifted lower half and the untouched upper half,
//   - KEEPS PITCH BLACK PITCH BLACK. The sqrt curve alone has a near-
//     vertical slope at 0, which would amplify the faintest trace of light
//     into something clearly visible — the opposite of "you can't see in
//     true darkness". So the lift is gated by a smoothstep that ramps from
//     0 at v == 0 to full at v == FLOOR (0.1): below the floor the boost
//     tapers to nothing (deep shadow and absolute black stay dark), and it
//     reaches full strength right where the perceptible "dim" band begins.
//
// Caller declares nothing — this file owns the `night_vision` global. Any
// shader that composes `lm.gba` into its lit color includes this file once
// and wraps the sample: `apply_night_vision(lm.gba)`.
global uniform float night_vision;

vec3 apply_night_vision(vec3 block_lit)
{
	if (night_vision <= 0.0)
	{
		return block_lit;
	}
	// Brightness = max channel (HSV "value").
	float v = max(block_lit.r, max(block_lit.g, block_lit.b));
	if (v <= 0.0)
	{
		return block_lit;
	}
	const float MID = 0.5;
	const float FLOOR = 0.1;
	// The visual lift is a deliberately NON-LINEAR (quadratic) response to the
	// night-vision degree — it should not track the stat 1:1. Squaring keeps
	// weak sources subtle and reserves the stronger lift for high degrees.
	// MAX_STRENGTH caps the lift at full degree (1.0); it is tuned so the
	// strongest in-game source (the Glimp Visor, 0.85 degree) lands at a ~0.40
	// lift toward the ceiling: 0.85^2 * 0.554 ≈ 0.40. Worked out, the visor
	// reads ~10% block light at ~0.15 and ~30% at ~0.33 — clearly noticeable
	// without flattening the world into uniform dim. Full degree tops out ~0.55.
	const float MAX_STRENGTH = 0.554;
	float response = night_vision * night_vision * MAX_STRENGTH;
	// lifted = sqrt(MID * v): for v < MID this is > v (raise toward MID);
	// for v >= MID it is <= v, so max() clamps it back to identity.
	float lifted = max(v, sqrt(MID * v));
	// Ramp the lift in across [0, FLOOR] so true black and the deepest
	// shadow keep their darkness — only light at/above the floor is dimmed
	// up. smoothstep is 0 at v == 0, so pitch black is untouched.
	float gate = smoothstep(0.0, FLOOR, v);
	float amount = response * gate;
	float target = mix(v, lifted, amount);
	return block_lit * (target / v);
}
