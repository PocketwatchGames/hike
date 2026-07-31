// Procedural "broken roof" noise — the holes a derelict roof is missing tiles
// through. Shared by roof_lit (the visible surface) and roof_shadow_caster (so
// light actually falls through a hole instead of the shadow staying solid).
//
// MUST STAY IN SYNC WITH RoofBrokenNoise.cs. The CPU port decides which voxel
// columns the sun-occlusion pass leaves open, which is what lets a god ray come
// down through a hole — so if the two drift, the shaft and the hole you can see
// stop lining up.
//
// Deliberately INTEGER-hashed rather than the usual fract(sin(...)): the same
// arithmetic has to produce the same bits in C#, and a float trig hash does not
// port reliably. Same hash shape clip_dither.gdshaderinc already uses.
//
// Sampled on world XZ, not UV. A hole is a vertical opening — the column of air
// beneath it is exactly what has to stay lit — so keying on the world column
// makes the visible hole and the lit column agree for free, at any roof
// orientation, with no UV inversion to do on the CPU side.

float roof_broken_hash(ivec2 p)
{
	int n = p.x * 374761393 + p.y * 668265263;
	n = (n ^ (n >> 13)) * 1274126177;
	return float((n ^ (n >> 16)) & 0xFFFF) / 65535.0;
}

// Smooth value noise in [0,1].
float roof_broken_noise(vec2 p)
{
	vec2 fl = floor(p);
	ivec2 i = ivec2(fl);
	vec2 f = p - fl;
	// Smoothstep weights, so holes have soft edges rather than a grid of squares.
	vec2 u = f * f * (3.0 - 2.0 * f);
	float a = roof_broken_hash(i);
	float b = roof_broken_hash(i + ivec2(1, 0));
	float c = roof_broken_hash(i + ivec2(0, 1));
	float d = roof_broken_hash(i + ivec2(1, 1));
	return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

// Two octaves. The COARSE one decides where holes are; the FINE one is added as
// a signed perturbation rather than a second set of holes, so it only matters
// near the threshold crossing — which is the hole's edge. Result: big openings
// with ragged, splintered rims instead of either smooth blobs or a rash of
// pinholes. Away from an edge the coarse value is far enough from the threshold
// that the perturbation can't flip it.
float roof_broken_field(vec2 world_xz, float scale, float edge_scale, float jagged)
{
	float coarse = roof_broken_noise(world_xz * scale);
	if (jagged <= 0.0)
	{
		return coarse;
	}
	float fine = roof_broken_noise(world_xz * edge_scale);
	return coarse + (fine - 0.5) * jagged;
}

// True where the roof is missing. `broken` is the fraction of the surface gone:
// 0 = intact, 1 = nothing left. Compared directly against the field so the
// authored value reads as roughly "how much of it is holes".
bool roof_is_hole(vec3 world_pos, float broken, float scale, float edge_scale, float jagged)
{
	if (broken <= 0.0)
	{
		return false;
	}
	return roof_broken_field(world_pos.xz, scale, edge_scale, jagged) < broken;
}
