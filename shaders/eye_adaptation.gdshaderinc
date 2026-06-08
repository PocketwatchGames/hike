// Eye adaptation ("night eyes") — a LOCAL, per-fragment tone curve that lifts
// dimly-lit surfaces toward visibility and pushes brightly-lit ones toward
// blowout, both scaled by how dark-adapted the player currently is.
//
// Why per-shader and not a post pass: our lit shaders run ambient_light_disabled
// and route surface brightness through EMISSION, and an isometric frame almost
// always mixes lit ground, sky, and a shadowed nook. A global post auto-exposure
// can only slide the whole histogram by the FRAME AVERAGE, so it can't lift a
// shadow while blowing a highlight in the SAME frame, and it keys off screen
// content rather than where the player stands. Doing it here lets each fragment
// respond to ITS OWN local light level, while a single global drives the eye's
// adaptation STATE from the perceived light at the player.
//
// `eye_adaptation` (0..1) is the pupil-dilation state. It is OWNED by the player
// simulation (Player.EyeDilation), updated each physics tick from the perceived
// light where the player stands (asymmetric smoothing: dilate slowly, constrict
// quickly), and pushed to this global each frame by GameClient. At 0 the curve is
// an EXACT identity — fully light-adapted, byte-identical to pre-feature output.
//
// Usage: compute `light_est` = the local AREA illuminance at this fragment
// (direct + block light, BEFORE cast-shadow attenuation, NOT albedo — a dark
// rock in bright light must not read as a dim spot), then scale the lit output
// by the returned gain. For the ALBEDO/EMISSION split shaders, multiply BOTH
// channels by the gain: final = ALBEDO*ATTENUATION + EMISSION scales cleanly.

global uniform float eye_adaptation;       // 0 = light-adapted (identity), 1 = fully dark-adapted.
global uniform float eye_adapt_dark_gain;   // lift at the darkest (light_est = 0), e.g. ~10.
global uniform float eye_adapt_light_gain;  // lift at/above the knee (bright), e.g. ~2.
global uniform float eye_adapt_knee;        // light_est at which the lift has fallen to light_gain.

// Single MONOTONIC lift: from dark_gain at black down to light_gain at/above the
// knee, smoothstepped so there's no hard edge and — critically — no mid-tone gap.
// The old two-knee form left the mid-tones at gain 1.0 (a lift-then-blowout pair
// with a dead band between), which made a lifted dark read BRIGHTER than the
// un-lifted mid beside it and left a visible cutoff at the dark knee. Here every
// fragment is lifted; darks just get lifted more. Brights settle at light_gain
// (>1) and the filmic tonemap blows them out from there. Exact 1.0 when
// eye_adaptation == 0, so the no-op path is unchanged.
float eye_adaptation_gain(float light_est)
{
	if (eye_adaptation <= 0.0)
	{
		return 1.0;
	}
	// t: 0 at black, easing to 1 by the knee. Wide knee = the ramp spans a large
	// tonal range (dark through bright), which is what keeps the lift seam-free.
	float t = smoothstep(0.0, max(eye_adapt_knee, 1e-4), light_est);
	float gain = mix(eye_adapt_dark_gain, eye_adapt_light_gain, t);
	return mix(1.0, gain, eye_adaptation);
}
