// Night-vision lift for perceived ambient block lighting.
//
// When the player has a night-vision status effect active, `night_vision`
// is driven from C# (Player) in 0..1 — the *degree* of night vision,
// mirroring PlayerPerception's relief term (a NightVision stat of 1.85
// yields 0.85). At 0 this is an exact identity, so rendering with no
// night vision is byte-for-byte unchanged.
//
// What it does: raises the LOWER HALF of the block-light brightness range
// up toward 50%. Block light at or above 0.5 is untouched; darker values
// are lifted so that light which normally reads as "dark" (roughly the
// 0.1..0.5 band) instead reads as "dim". The lift:
//   - preserves hue — it scales the RGB triple by the brightness gain
//     rather than lifting each channel independently (a torch stays warm),
//   - preserves contrast — it's a monotonic sqrt curve, not a flat floor,
//     so relative brightness within the world is retained,
//   - leaves true black black — areas with no block light at all (max
//     channel == 0) gain nothing; this lifts existing dim light, it does
//     not invent light from nothing.
//
// The sqrt curve meets the identity exactly at 0.5 (sqrt(0.5*0.5) == 0.5),
// so there is no seam between the lifted lower half and the untouched
// upper half.
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
	// lifted = sqrt(MID * v): for v < MID this is > v (raise toward MID);
	// for v >= MID it is <= v, so max() clamps it back to identity.
	float lifted = sqrt(MID * v);
	float target = mix(v, max(v, lifted), night_vision);
	return block_lit * (target / v);
}
