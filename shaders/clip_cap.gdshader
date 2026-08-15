shader_type spatial;
// Cap plane that fills in the screen pixels where above-clip front-faces
// were discarded. The mask texture is built off-screen by a SubViewport
// rendering the chunk meshes on CapMaskLayer (cap_mask_terrain front-face
// + cap_mask_backface back-face). Sampling that mask via SCREEN_UV and
// discarding non-cap pixels gives the cap exactly the silhouette of
// above-clip solid in screen space — no stencil, no render-order quirks.
//
// Opaque queue (no blend_mix), depth_draw_opaque, normal depth test:
// the cap depth-tests against terrain front-faces and sprites in the
// main scene, so anything geometrically in front of the cap plane (e.g.,
// a grass sprite at the cap region's lower edge) correctly occludes it.
// fog_disabled because the poche is a GRAPHIC DEVICE, not a surface in the
// world: it is the section cut, and a section reads as solid only if it is one
// flat value. `unshaded` does not exempt a material from the environment's
// built-in depth fog — with fog_depth_begin/end at 70/190 m, a cap spanning
// that range fogs from the authored colour to ~27% blue-grey across one region,
// and the two cap planes (base and iris, at different heights and therefore
// different depths) step hard against each other along their shared boundary.
render_mode depth_draw_opaque, cull_back, unshaded, fog_disabled;

global uniform int clip_debug_mode;
uniform sampler2D cap_mask_tex : filter_nearest, repeat_disable;

// The cutaway cuts at two heights at once: a base plane over the world, and a
// lower one inside the iris. A cap is a flat quad and cannot sit at both, so there
// are TWO of them and each fills only its own region — without this the base cap,
// being the higher of the two, would draw straight over the interior the iris just
// revealed. Both regions come from the shared iris include, so whichever shape is
// selected the caps split on the same boundary the discard paths cut on.
#include "res://shaders/clip_iris.gdshaderinc"
#include "res://shaders/dither.gdshaderinc"

// Which region this instance fills: false = outside the iris (at the base
// height), true = inside it (at the iris's lower plane).
uniform bool iris_inside_disk = false;

// The poche fill: a two-tone marbling between the base colour and the swirl
// crest. Both are pushed from GameCamera; the defaults here only matter if a
// material forgets to. Setting the two equal turns the swirl off.
uniform vec3 poche_color : source_color = vec3(0.098, 0.098, 0.114);
uniform vec3 poche_swirl_color : source_color = vec3(0.137, 0.137, 0.169);
// Noise cells per metre. The pattern is sampled in WORLD XZ — the plane the
// section is cut in — so it stays anchored to the world as the camera moves,
// and both cap planes resolve the same field across the iris boundary.
uniform float poche_swirl_scale = 0.06;
// Domain-warp strength in noise cells. This is what makes it swirl rather
// than blob; 0 leaves plain fbm.
uniform float poche_swirl_warp = 1.5;
// Noise cells per second the warp field slides. The two warp components move
// in OPPOSITE directions, so the field churns in place instead of drifting
// across the world (a global drift reads as the world sliding under you).
uniform float poche_swirl_speed = 0.03;
// De-banding jitter, in units of the mix range. The two poche colours are only
// ~10 8-bit codes apart, so a field this smooth posterizes into contour bands —
// broad regions with a hard edge where the value steps one code, which reads as
// a crease in the fill rather than as banding. One code of jitter dissolves
// them. Scale this with the distance between the two colours: it wants to stay
// worth about one output code.
uniform float poche_dither = 0.1;

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

// The cap plane is 1 km across and viewed at a steep iso pitch, so far
// fragments pack many noise cells into one pixel. Past this many cells per
// pixel the pattern is pure aliasing — it would sparkle as the camera moves,
// which is the one thing a receding fill must never do — so fade it to flat.
const float POCHE_SWIRL_LOD_CELLS = 0.25;

// 0..1 marbling factor, given the swirl coordinate and how many noise cells
// that coordinate advances per pixel.
float poche_swirl(vec2 p, float cells)
{
	float t = TIME * poche_swirl_speed;
	vec2 q = vec2(poche_fbm(p + vec2(t, 0.0)), poche_fbm(p + vec2(0.0, -t) + 5.2)) * 2.0 - 1.0;
	float n = poche_fbm(p + poche_swirl_warp * q);
	float lod = clamp(1.0 - cells / POCHE_SWIRL_LOD_CELLS, 0.0, 1.0);
	// Fade toward the field's mean, not toward 0 — otherwise distant poche
	// would trend darker than near poche instead of just flatter.
	return mix(0.5, n, lod);
}

void fragment() {
	// VERTEX is view-space in fragment, hence the transform back out to world.
	vec3 cap_world = (INV_VIEW_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// Swirl coordinate and its screen-space rate, both resolved BEFORE any
	// discard below: fwidth reads the neighbouring fragments in the quad, and a
	// neighbour that has already discarded has no value to read.
	vec2 swirl_uv = cap_world.xz * poche_swirl_scale;
	float swirl_cells = max(fwidth(swirl_uv.x), fwidth(swirl_uv.y));
	if (clip_iris_active()) {
		if (clip_iris_inside(cap_world) != iris_inside_disk) {
			discard;
		}
	} else if (iris_inside_disk) {
		// No iris up: the inner cap has no region to fill.
		discard;
	}
	// 6 = cap disabled outright. 21 = the cut volume is being painted instead of
	// discarded, so the cap has nothing to fill and would only draw black over
	// the diagnostic.
	if (clip_debug_mode == 6 || clip_debug_mode == 21) {
		discard;
	}
	float mask = texture(cap_mask_tex, SCREEN_UV).r;
	if (mask < 0.5) {
		discard;
	}
	if (clip_debug_mode == 2 || clip_debug_mode == 13) {
		ALBEDO = vec3(1.0, 0.0, 0.0);
	} else {
		float n = poche_swirl(swirl_uv, swirl_cells);
		// Ordered rather than white noise: at this game's chunky pixel scale a
		// per-pixel random jitter reads as grain, while the Bayer tile reads as
		// the same stipple the cutaway edge beside it already uses.
		n += dither_jitter(ivec2(FRAGCOORD.xy)) * poche_dither;
		ALBEDO = mix(poche_color, poche_swirl_color, clamp(n, 0.0, 1.0));
	}
}
