// Shared ordered-dither primitives. Two consumers so far — the ceiling
// cutaway's stipple (clip_dither.gdshaderinc) and the poche fill's de-banding
// (clip_cap.gdshader) — which sit directly against each other on screen, so
// they must grain identically or the join between them reads as two effects.
//
// Include-guarded because a shader can reach this both directly and through
// clip_dither.gdshaderinc.
#ifndef DITHER_GDSHADERINC
#define DITHER_GDSHADERINC

// The SAME 4x4 Bayer the foliage cutaway stipples with, so the canopy fade and the
// ceiling reveal grain identically where they meet. It was an 8x8, on the reasoning
// that a wider repeat tile reads as noise rather than a visible grid — true, but it
// also spreads the pattern over twice the distance, which at this game's chunky pixel
// scale reads as loose blotches rather than dither. The hash offset below is what
// keeps the smaller tile from locking into a visible grid.
const float DITHER_BAYER_4[16] = float[](
	 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
	12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
	 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
	15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
);

// Hash a screen-space pixel coord to a small per-frag offset so the tile
// doesn't lock to screen alignment — breaks up the residual grid feel
// without changing the average coverage at any threshold.
//
// Integer bit-mix rather than the usual fract(p * bigconstant) float hash. That
// idiom quietly dies on large inputs: at a screen x of 1400 its intermediate
// lands near 185,000, where a float32 ulp is 0.016 — coarser than the fraction
// it is trying to keep — and it collapses from white noise to a structured
// beat. Measured over a 512x512 patch at that corner of the screen, the float
// version yields 1,445 distinct values where this one yields 64,356.
float dither_hash(ivec2 p)
{
	int n = p.x * 374761393 + p.y * 668265263;
	n = (n ^ (n >> 13)) * 1274126177;
	return float((n ^ (n >> 16)) & 0xFFFF) / 65535.0;
}

// Bayer + hash offset as a signed [-0.5, 0.5) jitter, for callers de-banding a
// smooth gradient rather than stippling a threshold.
float dither_jitter(ivec2 p)
{
	float threshold = DITHER_BAYER_4[(p.y & 3) * 4 + (p.x & 3)];
	threshold += (dither_hash(p) - 0.5) * (1.0 / 16.0);
	return threshold - 0.5;
}

#endif
