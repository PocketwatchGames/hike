// Dithered ceiling-clip transition.
//
// GameCamera animates `camera_clip_blend` from 0 to 1 over a short fade
// each time the cutaway elevation changes (player walks under a new
// ceiling, or out from under one). During the fade, fragments that sit
// in the band between the prev and new clip Y are dithered via a 4x4
// Bayer pattern in screen-space FRAGCOORD so the cutaway phases in/out
// instead of popping. When blend == 1 the helper reduces to a plain
// `world_y > camera_clip` discard.
//
// Caller must declare these globals before #include — other shaders
// already own `camera_clip` so we don't redeclare to avoid multi-decl
// errors:
//
//   global uniform float camera_clip;
//   global uniform float camera_clip_prev;
//   global uniform float camera_clip_blend;
// 8x8 Bayer over a 4x4 — wider repeat tile reads as noise rather than a
// visible grid at the brief fade durations the cutaway uses, while still
// being a single index → one mul-add lookup per fragment.
const float CLIP_DITHER_BAYER_8[64] = float[](
	 0.0/64.0, 32.0/64.0,  8.0/64.0, 40.0/64.0,  2.0/64.0, 34.0/64.0, 10.0/64.0, 42.0/64.0,
	48.0/64.0, 16.0/64.0, 56.0/64.0, 24.0/64.0, 50.0/64.0, 18.0/64.0, 58.0/64.0, 26.0/64.0,
	12.0/64.0, 44.0/64.0,  4.0/64.0, 36.0/64.0, 14.0/64.0, 46.0/64.0,  6.0/64.0, 38.0/64.0,
	60.0/64.0, 28.0/64.0, 52.0/64.0, 20.0/64.0, 62.0/64.0, 30.0/64.0, 54.0/64.0, 22.0/64.0,
	 3.0/64.0, 35.0/64.0, 11.0/64.0, 43.0/64.0,  1.0/64.0, 33.0/64.0,  9.0/64.0, 41.0/64.0,
	51.0/64.0, 19.0/64.0, 59.0/64.0, 27.0/64.0, 49.0/64.0, 17.0/64.0, 57.0/64.0, 25.0/64.0,
	15.0/64.0, 47.0/64.0,  7.0/64.0, 39.0/64.0, 13.0/64.0, 45.0/64.0,  5.0/64.0, 37.0/64.0,
	63.0/64.0, 31.0/64.0, 55.0/64.0, 23.0/64.0, 61.0/64.0, 29.0/64.0, 53.0/64.0, 21.0/64.0
);

// Hash a screen-space pixel coord to a small per-frag rotation/offset so
// the 8x8 tile doesn't lock to screen alignment — breaks up the residual
// grid feel without changing the average coverage at any threshold.
float clip_dither_hash(ivec2 p)
{
	int n = p.x * 374761393 + p.y * 668265263;
	n = (n ^ (n >> 13)) * 1274126177;
	return float((n ^ (n >> 16)) & 0xFFFF) / 65535.0;
}

bool clip_dither_should_discard(float world_y, vec2 frag_xy)
{
	bool above_prev = world_y > camera_clip_prev;
	bool above_new = world_y > camera_clip;
	if (above_prev && above_new)
	{
		return true;
	}
	if (!above_prev && !above_new)
	{
		return false;
	}
	// In the transition band: visibility goes from 1 -> 0 (fading out)
	// when the fragment is above the new clip but was below the prev,
	// and from 0 -> 1 (fading in) when above the prev but below the new.
	float clip_visibility = above_new ? (1.0 - camera_clip_blend) : camera_clip_blend;
	ivec2 ip = ivec2(frag_xy);
	int bx = ip.x & 7;
	int by = ip.y & 7;
	// Add a small hash-noise offset (±1/128) to the threshold to break
	// the residual ordered-pattern feel of pure Bayer. Cheap (one int
	// hash) and stable per-pixel so the noise isn't temporally noisy.
	float threshold = CLIP_DITHER_BAYER_8[by * 8 + bx];
	threshold += (clip_dither_hash(ip) - 0.5) * (1.0 / 64.0);
	return clip_visibility <= threshold;
}
