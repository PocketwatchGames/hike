// The poche: the colour of NO LIGHT. The cutaway cap is filled with it, and every
// shader that renders something lit fades to it as its light goes to zero — so an
// unlit tunnel and the cut rock above it are the same value, and the tunnel has no
// silhouette. Anything that draws in the dark must end at poche_color, not at a
// hardcoded black, or it outlines itself against the cap.
//
// Colours are LINEAR (GameCamera converts); globals carry no source_color hint.
#ifndef POCHE_GDSHADERINC
#define POCHE_GDSHADERINC

global uniform vec3 poche_color;
// Light level (the caller's area-illuminance measure, same units as eye
// adaptation's) at which a surface has fully left the poche.
global uniform float poche_light_knee;

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
vec3 poche_unlit_light(vec3 light)
{
	return poche_color * poche_weight(dot(light, vec3(0.299, 0.587, 0.114)));
}

vec3 poche_unlit(vec3 sun_lit, vec3 block_lit)
{
	return poche_unlit_light(sun_lit + block_lit);
}

#endif
