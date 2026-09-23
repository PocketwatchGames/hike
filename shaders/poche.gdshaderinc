// The poche: the colour of NO LIGHT. The cutaway cap is filled with it, and every
// shader that renders something lit fades to it as its light goes to zero — so an
// unlit tunnel and the cut rock above it are the same value, and the tunnel has no
// silhouette. Anything that draws in the dark must end at poche_fill, not at black,
// or it outlines itself against the cap.
//
// The pattern coordinate is the fragment projected along the view onto the
// camera's image plane. Under the ortho camera that is constant along a view ray,
// so the cap, the floor seen through it and the fog in between all resolve the SAME
// value at a pixel, whatever height each sits at. It also advances at a fixed rate
// per pixel, so unlike a world-XZ pattern on a steeply viewed plane it never packs
// enough cells into a pixel to alias.
//
// Colours are LINEAR (GameCamera converts); globals carry no source_color hint.
#ifndef POCHE_GDSHADERINC
#define POCHE_GDSHADERINC

#include "res://shaders/dither.gdshaderinc"

global uniform vec3 poche_color;
// Crest of the marbling. Equal to poche_color = a flat fill.
global uniform vec3 poche_swirl_color;
// Noise cells per metre of the image plane.
global uniform float poche_swirl_scale;
// Domain-warp strength in noise cells — what makes it swirl rather than blob.
global uniform float poche_swirl_warp;
// Noise cells per second the warp field churns.
global uniform float poche_swirl_speed;
// De-banding jitter in units of the mix range; keep it worth about one output code.
global uniform float poche_dither;
// Light level (the caller's area-illuminance measure, same units as eye
// adaptation's) at which a surface has fully left the poche.
global uniform float poche_light_knee;

float poche_vnoise(vec2 p)
{
	vec2 fl = floor(p);
	ivec2 i = ivec2(fl);
	vec2 f = p - fl;
	f = f * f * (3.0 - 2.0 * f);
	float a = dither_hash(i);
	float b = dither_hash(i + ivec2(1, 0));
	float c = dither_hash(i + ivec2(0, 1));
	float d = dither_hash(i + ivec2(1, 1));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

// Two octaves, normalized back to 0..1.
float poche_fbm(vec2 p)
{
	return (poche_vnoise(p) * 0.6 + poche_vnoise(p * 2.1 + 11.7) * 0.3) / 0.9;
}

// `inv_view` is the stage's INV_VIEW_MATRIX (columns 0/1 = camera right/up);
// `frag` is FRAGCOORD.xy. Built-ins are not visible inside include functions.
vec3 poche_fill(vec3 world, mat4 inv_view, vec2 frag)
{
	vec2 p = vec2(dot(world, inv_view[0].xyz), dot(world, inv_view[1].xyz)) * poche_swirl_scale;
	float t = TIME * poche_swirl_speed;
	// The two warp components move in OPPOSITE directions so the field churns in
	// place; a global drift reads as the world sliding under you.
	vec2 q = vec2(poche_fbm(p + vec2(t, 0.0)), poche_fbm(p + vec2(0.0, -t) + 5.2)) * 2.0 - 1.0;
	float n = poche_fbm(p + poche_swirl_warp * q);
	// Ordered, not white, jitter: the Bayer tile reads as the same stipple the
	// cutaway edge beside it uses, where per-pixel noise reads as grain.
	n += dither_jitter(ivec2(frag)) * poche_dither;
	return mix(poche_color, poche_swirl_color, clamp(n, 0.0, 1.0));
}

// 1 = no light (all poche), 0 = lit past the knee.
float poche_weight(float light)
{
	return 1.0 - smoothstep(0.0, max(poche_light_knee, 1e-5), light);
}

// What a lit shader adds to its output so it ends at the poche as its light goes
// to zero. `sun_lit` / `block_lit` are the area light eye adaptation reads.
// Add it AFTER adaptation (the cap is unadapted) and BEFORE any silhouette or
// debug override. It is emission: it ignores albedo, so every surface converges
// on exactly the cap's value.
vec3 poche_unlit_light(vec3 world, mat4 inv_view, vec2 frag, vec3 light)
{
	float w = poche_weight(dot(light, vec3(0.299, 0.587, 0.114)));
	if (w <= 0.0)
	{
		return vec3(0.0);
	}
	return poche_fill(world, inv_view, frag) * w;
}

vec3 poche_unlit(vec3 world, mat4 inv_view, vec2 frag, vec3 sun_lit, vec3 block_lit)
{
	return poche_unlit_light(world, inv_view, frag, sun_lit + block_lit);
}

// A point on this pixel's view ray, for shaders whose VERTEX is not a reliable
// view-space position (skip_vertex_transform). Only its image-plane projection
// matters to poche_fill, so any depth on the ray gives the same fill.
vec3 poche_pixel_world(vec2 screen_uv, float frag_z, mat4 inv_proj, mat4 inv_view)
{
	vec4 v = inv_proj * vec4(screen_uv * 2.0 - 1.0, frag_z, 1.0);
	return (inv_view * vec4(v.xyz / v.w, 1.0)).xyz;
}

#endif
