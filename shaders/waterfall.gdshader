shader_type spatial;
// Same shading model as voxel_water, because a cascade is the same substance as
// the pool it pours out of: no diffuse albedo, a volumetric composite, and the
// sheet drawn double-sided so it is visible from behind too.
//
// But it BLENDS, where voxel_water composites by hand against the screen texture,
// and that difference is a correctness fix rather than a style choice. Godot
// captures the screen copy ONCE, before the transparent pass — a transparent
// object never sees another transparent object in it, whatever their render
// priorities say. The pool is transparent (voxel_water, priority -3), so a sheet
// sampling `screen_tex` got back the opaque scene BEHIND the pool, which is bare
// rock: a fall hanging in front of bright water drew the grey cliff over it, and
// no ordering could fix it because the copy predates both of them. Real blending
// reads the actual framebuffer, which by the time this draws already has the pool
// in it.
//
// PREMULTIPLIED, so the water's own light is emitted at full value and ALPHA
// carries only how much of the background it hides — which is exactly the split
// water_compose already returns. What it gives up is screen-space refraction, and
// there is no way to have both: refraction needs the copy this cannot trust.
render_mode blend_premul_alpha, depth_draw_never, cull_disabled, unshaded;

// Still draws after both water passes (-3 front, -2 backface) and before the
// default priority everything else uses, so the BLEND lands on top of the pool.
// The ordering table lives in ChunkMesh.cs, where the water materials are built.

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"

// THE water shading path, shared with voxel_water — the lightmap + cloud-shadow
// + block-light globals, the per-zone water colour, the volumetric composite and
// the adaptation tail. Everything below is only about what makes this water
// FALLING; how it answers to light is not this file's business.
#include "res://shaders/water_shading.gdshaderinc"

// Surface reflection, same globals and same meaning as on the pool.
global uniform float fresnel_power;
global uniform float reflection_strength;
global uniform float reflection_min;
global uniform bool water_hide;
global uniform bool water_disable_foam;

// --- Per-instance, set by Waterfall.cs from the site's measurements ----------
// How much water this fall carries where it is FALLING, in metres of optical
// depth. A pool reads its thickness off the depth buffer; a curtain hanging in
// open air would measure the far side of the valley, so this is authored per
// size tier instead.
instance uniform float sheet_thickness = 0.5;
// Whitewater coverage, 0..1.
instance uniform float sheet_foam = 0.5;
// World Y of the lip and the landing, for the plunge-foam ramp.
instance uniform float fall_top_y = 0.0;
instance uniform float fall_bottom_y = 0.0;

// --- Authored on the material -----------------------------------------------
// Metres per second the streak pattern falls. Real water accelerates; the
// pattern doesn't, because a scroll rate that varied with height would smear
// the texture rather than read as acceleration.
uniform float flow_speed : hint_range(0.0, 40.0) = 9.0;
// Streak shape. `across` is the pattern's frequency across the sheet, `along`
// its frequency down the fall — much lower, which is what turns noise into
// vertical ropes of water rather than blobs.
uniform float streak_scale_across : hint_range(0.05, 8.0) = 1.6;
uniform float streak_scale_along : hint_range(0.005, 2.0) = 0.12;
// How much the streaks vary the sheet's thickness, 0..1. This is where the
// curtain gets its gaps: a thin streak lets more of the scene behind through.
uniform float streak_depth : hint_range(0.0, 1.0) = 0.55;
// Multiple of the falling thickness the sheet carries where it is still acting
// as a SURFACE (the shoulder rolling off the lip). Well above 1 on purpose: the
// pool it continues is opaque, so a shoulder that is already a veil reads as a
// separate object hung under the water instead of as the same water leaving it.
uniform float surface_thickness_scale : hint_range(1.0, 30.0) = 12.0;
// Where the fall goes white approaching its landing. A FRACTION of the fall's
// own height, capped in metres — an absolute distance turns a short drop
// entirely to foam, which reads as a painted slab rather than as water.
uniform float base_foam_fraction : hint_range(0.0, 1.0) = 0.3;
uniform float base_foam_height : hint_range(0.0, 12.0) = 3.0;
// Churn scale — the noise that breaks the plunge foam into whitewater instead
// of a gradient. Higher = finer froth.
uniform float churn_scale : hint_range(0.1, 8.0) = 1.7;

// How hard the ropes separate from the gaps. At 0 the streak field is the raw
// smooth noise, which reads as a soft scrolling wash; toward 1 it goes nearly
// binary, and the sheet breaks into distinct vertical ropes with hard edges and
// real gaps between them.
uniform float streak_contrast : hint_range(0.0, 1.0) = 0.85;
// How hard the pixel-grid noise perturbs the streak field, before the contrast
// remap. ADDED rather than averaged in: averaging two fields narrows the result
// toward its mean, so mixing the specks in that way made the ropes SOFTER, not
// noisier. Centred on 0 so it widens the field instead, which is what leaves
// room for the contrast remap to cut hard edges out of it.
uniform float streak_pixel_noise : hint_range(0.0, 1.0) = 0.6;

// --- What the falling half does with the sky ---------------------------------
// A falling sheet is an agitated vertical surface and mirrors nothing, so the
// fresnel-weighted reflection stays on the shoulder. But the curtain still hangs
// in open sky, and cutting its sky term to exactly zero is most of why it reads
// dark: the gaps are thin enough to show the shadowed cliff behind, the water's
// own in-scatter goes to nothing when the sheet is thin, and there is no ambient
// floor (WATER_MIN_AMBIENT is 0), so nothing at all was being added in front of
// the rock. This is the fraction of the pool's reflection the falling half keeps
// — ambient sky light on water, not a mirror image, which is also what makes it
// answer to the weather and the time of day the way the pool beside it does.
uniform float falling_sky_reflect : hint_range(0.0, 1.0) = 0.35;
// How far the falling half's sky-sample normal is tilted up regardless of its
// own. Necessary, not cosmetic: sample_sky projects clouds onto their plane at
// t = (altitude - y) / max(dir.y, 0.05), so a ray taken off a vertical sheet
// grazes the horizon and walks the sample point hundreds of metres per pixel —
// the same degeneracy that put crawling noise over the shoulder. It is bounded
// here rather than fixed in sample_sky because a near-horizontal ray is a real
// thing to ask the sky dome for; it is only this surface that has no business
// asking it.
uniform float falling_sky_up : hint_range(0.0, 1.0) = 0.6;

// --- The back curtain --------------------------------------------------------
// A second layer of water behind the front sheet, composited as EXTRA OPTICAL
// DEPTH rather than as a second surface. That is the whole trick: two layers of
// water along one ray is one longer path through water, so the thicknesses add
// and Beer-Lambert does the rest. Each layer still carries its own streak field
// at its own speed, so the two patterns slide over each other and read as depth.
//
// Real geometry was the obvious alternative and it does not work here. The sheet
// is `depth_draw_never` and composites by hand against `screen_tex`, and Godot
// captures that copy BEFORE the object sampling it — so a back layer only shows
// through the front's gaps as a separate draw at a lower render priority, which
// costs a second full-screen copy and another slot in the -3/-2/-1/0 band. Both
// layers in one mesh and the front's gaps still sample the cliff, having doubled
// the geometry for nothing.
//
// This is what lets the streaks be genuinely gappy. A gap in a single sheet
// exposes the shadowed rock behind it, so every notch of extra noise made the
// fall darker; a gap in the front layer now exposes MORE WATER.
//
// Where the back curtain starts, in metres below the lip. It is not there at the
// brink — the shoulder is the pool surface leaving the edge, one body of water —
// and it fades in as the front sheet pulls away from the wall.
uniform float back_start_depth : hint_range(0.0, 4.0) = 0.5;
// Its scroll rate, as a fraction of the front's. Slower reads as further away.
uniform float back_flow_scale : hint_range(0.0, 1.0) = 0.55;
// How much water it carries, against the front sheet's authored thickness. Above
// 1 because this layer is the dense body of the fall; the front is the veil.
uniform float back_thickness_scale : hint_range(0.0, 4.0) = 1.6;
// How gappy it is. Well below the front's `streak_depth` on purpose — if the back
// layer had holes of its own they would line up with the front's often enough to
// punch straight through to the rock, which is the thing this exists to stop.
uniform float back_streak_depth : hint_range(0.0, 1.0) = 0.3;
// Pattern frequency against the front's. Coarser also reads as further away.
uniform float back_streak_scale : hint_range(0.1, 3.0) = 0.6;
// How much of its whitewater survives being seen through the front sheet.
uniform float back_foam_scale : hint_range(0.0, 1.0) = 0.35;

// --- Sparkle -----------------------------------------------------------------
// Per-pixel jitter on the reflection normal. Falling water is not one smooth
// surface — it is tearing into droplets - so neighbouring pixels catch the sun
// and the sky at different angles, and that scatter IS the sparkle.
//
// Applied AFTER the up-tilt, and that ordering is load-bearing: the jitter has to
// ride on a normal already pointing well up, or the perturbed ray grazes the
// horizon and walks sample_sky's cloud-plane projection hundreds of metres per
// pixel, which is the crawling speckle this shader had before.
uniform float sparkle_amount : hint_range(0.0, 1.0) = 0.35;

// --- Aeration ----------------------------------------------------------------
// Extinction through the sheet has TWO parts, and they are different physics.
//
// `water_absorption` is the POOL's: clear water swallowing light, wavelength by
// wavelength (red first, which is why depth goes blue on its own). It is also
// very SLOW — SkyController derives it from waterClearAbsorption, about 0.08 per
// metre, so a one-metre sheet stops roughly 8% of what is behind it. No authored
// thickness fixes that: reaching opaque on absorption alone would take tens of
// metres of water. That is why the curtain read as a window onto whatever was
// behind it, and why every gap opened in the streaks came back dark.
//
// What actually makes a waterfall opaque is the AIR in it. A fall is a bubble
// cloud, and bubbles SCATTER — strongly, and with no colour preference, which is
// why whitewater is white rather than blue. So the falling half gets a second,
// grey, much stronger extinction term. It blocks the background and feeds the
// water's own in-scatter at the same time, which is exactly what scattering does.
//
// Per metre, and it only applies where the sheet is FALLING: the shoulder is the
// pool surface leaving the lip, and that water is clear.
uniform float aeration_density : hint_range(0.0, 8.0) = 2.0;
// The counterweight, and it is not optional. Making the curtain opaque takes away
// the background it had been borrowing its brightness from, so a fall in shadow
// gets DARKER the more solid it becomes — and there is no ambient floor to catch
// it (WATER_MIN_AMBIENT is 0). A bubble cloud is also genuinely lit from every
// direction at once, scattering skylight that never reached the lightmap cell the
// sheet hangs in, so a floor here is honest rather than a fudge.
uniform float falling_ambient : hint_range(0.0, 1.0) = 0.18;

// --- Whitewater flecks -------------------------------------------------------
// Built the way shoreline surf is (water_shore_foam): the sample UV is snapped
// to the pixel grid BEFORE the noise is read, and the threshold band is narrow,
// so a fleck is opaque whitewater or it is not there at all. That quantize is
// the whole difference between specks of surf and a soft gradient — a plain
// smoothstep over smooth noise cannot produce it at any threshold.
// Cells per metre the sheet position is snapped to. Its OWN grid rather than
// shoreline surf's `pixelize`, because that one quantizes world XZ and this UV
// runs across and down a sheet that is mostly vertical.
uniform float fleck_pixels : hint_range(1.0, 64.0) = 24.0;
// Noise cell size across the sheet and down it, in cells per metre. Deliberately
// ANISOTROPIC and by a wide margin: a fleck has to be a vertical DASH of falling
// water, not a round dot. It also has to survive the scroll — at flow_speed the
// pattern crosses an along-cell several times a second, and an isotropic cell
// fine enough to read as a speck would decorrelate every frame and strobe like
// static instead of streaming.
uniform float fleck_scale_across : hint_range(0.05, 24.0) = 6.0;
uniform float fleck_scale_along : hint_range(0.02, 8.0) = 0.6;
// Where the fleck noise is cut, and how hard. Sharpness near 1 leaves nothing
// between "fleck" and "no fleck".
//
// MEASURED against the field, not guessed: vnoise is bell-shaped about 0.5, not
// uniform (5th/95th percentiles are 0.15 and 0.85), so these two are calibrated
// to give ~14% coverage where a quiet sheet carries only its tier's foam and
// ~63% in the plunge. A threshold picked as though the noise were flat lands
// almost entirely on or almost entirely off.
uniform float fleck_threshold : hint_range(0.0, 1.0) = 0.86;
uniform float fleck_sharpness : hint_range(0.0, 1.0) = 0.9;
// How far a fully whitewatered fragment pulls that threshold DOWN. This is the
// COVERAGE knob: more whitewater means more specks, never greyer ones.
uniform float fleck_coverage : hint_range(0.0, 1.0) = 0.44;
// Peak opacity of a fleck.
uniform float fleck_strength : hint_range(0.0, 1.0) = 1.0;
// How much a fleck thickens the sheet beneath it. Whitewater is aerated water
// and hides what is behind it; without this the specks float over a curtain you
// can still see straight through and stop reading as part of it.
uniform float fleck_opacity_boost : hint_range(0.0, 8.0) = 3.5;
// How much the streak ropes break up into flecks on the way down. Water tears
// along the ropes, so the specks belong on them rather than scattered evenly.
uniform float streak_breakup : hint_range(0.0, 1.0) = 0.45;

// Where a fragment sits on the sheet, from the mesh's own UVs: U runs across the
// curtain and V down it, both in metres (WaterfallMeshBuilder). Taking this off
// the mesh rather than off world position is what lets the sheet curve — a jet's
// surface is not axis-aligned, so no combination of world X/Y/Z runs along it.
//
// Time is SUBTRACTED from V. A feature sits where (v - t*speed) is constant, so
// its v grows with time and the pattern travels toward larger v — down the fall.
// Adding scrolls it up the waterfall, which is the giveaway that the sign is
// wrong.
vec2 wf_sheet_uv(vec2 mesh_uv, float time) {
	return vec2(mesh_uv.x * streak_scale_across,
		(mesh_uv.y - time * flow_speed) * streak_scale_along);
}

// The streak field, in [0,1]. 1 = a thick rope of water, 0 = a gap the background
// shows through. The anisotropy is in the UV (streak_scale_along is an order of
// magnitude below across), not here; this is only about how BROKEN it is.
//
// Three octaves, then the pixel-grid noise added on top, then a narrow remap
// about the median. All three steps matter and the order is not free:
//
//   - More octaves alone make it SOFTER. Averaging independent fields pulls the
//     result toward its mean, so the three-octave field is measurably narrower
//     than the two-octave one it replaced (5th/95th percentiles 0.28/0.72 against
//     0.24/0.76) and a fixed remap band therefore catches more of it.
//   - The pixel noise is added CENTRED ON ZERO for that reason. Mixing it in as a
//     lerp would have narrowed the field again; adding it widens it to 0.19/0.80,
//     and it carries the hard grid edges that make the ropes ragged rather than
//     merely wobbly.
//   - The remap is centred on 0.5 because that is the field's MEASURED median.
//     vnoise is bell-shaped, not uniform, so a band placed anywhere else clips
//     one side of the distribution and the sheet goes mostly-solid or mostly-gap.
float wf_streaks(vec2 uv, float pixel_noise) {
	float a = vnoise(vec3(uv, 0.0));
	float b = vnoise(vec3(uv * 2.7 + vec2(11.3, 4.1), 0.0));
	float c = vnoise(vec3(uv * 6.3 + vec2(3.7, 19.1), 0.0));
	float raw = a * 0.5 + b * 0.3 + c * 0.2
		+ (pixel_noise - 0.5) * streak_pixel_noise;
	float half_band = mix(0.5, 0.02, clamp(streak_contrast, 0.0, 1.0));
	return smoothstep(0.5 - half_band, 0.5 + half_band, clamp(raw, 0.0, 1.0));
}

// The fleck field's own UV. Metres across and down the sheet, scrolling with the
// flow, SNAPPED to a square grid, and only then scaled anisotropically for the
// noise lookup.
//
// The snap has to come first and in SQUARE metres: it is what gives the field
// hard pixel edges (the same trick shoreline surf plays with `pixelize`), and
// doing it after the anisotropic scale would quantize the two axes by wildly
// different world distances and chop the sheet into tall blocks.
vec2 wf_fleck_uv(vec2 mesh_uv, float time) {
	float px = max(fleck_pixels, 0.01);
	vec2 p = floor(vec2(mesh_uv.x, mesh_uv.y - time * flow_speed) * px) / px;
	return p * vec2(fleck_scale_across, fleck_scale_along);
}

// One INDEPENDENT value per snapped cell. Sampling vnoise on integer coordinates
// lands exactly on its lattice, so the interpolation weights are zero and what
// comes back is the raw hash — white noise per cell, with no smooth field joining
// neighbours. That is what separates glitter from a wobble.
vec3 wf_sparkle(vec2 mesh_uv, float time) {
	float px = max(fleck_pixels, 0.01);
	vec2 cell = floor(vec2(mesh_uv.x, mesh_uv.y - time * flow_speed) * px);
	return vec3(
		vnoise(vec3(cell + vec2(19.7, 5.1), 0.0)),
		vnoise(vec3(cell + vec2(41.3, 13.7), 0.0)),
		vnoise(vec3(cell + vec2(3.3, 27.9), 0.0))) - vec3(0.5);
}

// Whitewater specks, in [0,1]. `whitewater` (0..1) is how much foam this bit of
// the sheet is carrying; it pulls the threshold down rather than scaling the
// result, so it buys more flecks instead of greyer ones.
float wf_flecks(float pixel_noise, float whitewater) {
	float t = fleck_threshold - clamp(whitewater, 0.0, 1.0) * fleck_coverage;
	float band = max(1e-4, 1.0 - fleck_sharpness);
	return smoothstep(t, t + band, pixel_noise);
}

varying vec3 world_vertex;
varying vec3 world_normal;
varying vec2 sheet_uv;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// The entity is placed with no rotation and the sweep is built in world axes,
	// so the normal needs no basis transform.
	world_normal = NORMAL;
	sheet_uv = UV;
	// Same bias water_clip_cap uses: the shoulder rolls off the lip flush with
	// the pool's own surface, and 0.02 world units of separation is under a
	// depth quantum at range.
	POSITION = PROJECTION_MATRIX * MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
	POSITION.z -= 0.001 * POSITION.w;
}

void fragment() {
	if (water_hide) {
		discard;
	}
	if (clip_dither_should_discard(world_vertex, FRAGCOORD.xy)) {
		discard;
	}

	// One surface drawn from both sides, so the stored normal points the wrong
	// way on whichever side faces away. Renormalized because the varying is
	// interpolated across each quad: linear interpolation between two unit
	// normals is SHORTER than unit in the middle, and every consumer below
	// (surfaceness, the fresnel dot, the reflection) reads it as a direction.
	vec3 n = normalize(FRONT_FACING ? world_normal : -world_normal);

	// THE blend. `surfaceness` is how much this fragment is behaving as a water
	// SURFACE rather than as falling water, taken straight off the normal's Y:
	// 1 on the shoulder rolling off the lip, 0 down the curtain. Every way the
	// two differ — how thick they are, whether they reflect, whether they foam,
	// which side their light comes from — is a lerp on it, so the top of a fall
	// IS the pool it leaves and turns into a falling sheet as the geometry does.
	float surfaceness = smoothstep(0.15, 0.85, abs(n.y));

	// ONE pixel-grid sample, feeding both the streak field and the flecks. Shared
	// deliberately as well as cheaply: foam forms on the water that is actually
	// there, so a speck belongs on a rope the same noise just thickened.
	float pixel_noise = vnoise(vec3(wf_fleck_uv(sheet_uv, TIME), 0.0));
	vec2 uv = wf_sheet_uv(sheet_uv, TIME);
	float streaks = wf_streaks(uv, pixel_noise);

	// The back curtain's own fields, at its own speed and scale, offset so the two
	// layers never share a feature. It fades in over `back_start_depth`: at the
	// brink there is one body of water leaving the lip, not two.
	float back_time = TIME * back_flow_scale;
	vec2 back_offset = vec2(37.1, 11.7);
	float back_pixel = vnoise(vec3(wf_fleck_uv(sheet_uv, back_time) + back_offset, 0.0));
	float back_streaks = wf_streaks(wf_sheet_uv(sheet_uv, back_time) * back_streak_scale
		+ back_offset, back_pixel);
	float back_gate = smoothstep(0.0, max(back_start_depth, 0.01), sheet_uv.y);

	// Foam, in two halves that hand over on `surfaceness` like everything else.
	// The FALLING half is whitewater: it collects where the sheet lands and where
	// it breaks up on the way down, so it fades IN as the shoulder turns.
	float from_bottom = world_vertex.y - fall_bottom_y;
	float fall_height = max(fall_top_y - fall_bottom_y, 0.01);
	float plunge_span = min(base_foam_height, fall_height * base_foam_fraction);
	float plunge = 1.0 - smoothstep(0.0, max(plunge_span, 0.01), from_bottom);
	// Churn: a second noise, scrolling faster than the streaks and independent
	// of them, so the whitewater at the foot boils rather than sliding down with
	// the sheet. It is a threshold bias below, not a multiply — a gradient alone
	// reads as a painted band, and that band is what makes a short fall a slab.
	float churn = vnoise(vec3(world_vertex.xz * churn_scale,
		world_vertex.y * churn_scale * 0.5 - TIME * 2.5));

	// How much whitewater this fragment should be carrying, before it is turned
	// into specks: the tier's own foam everywhere, the plunge ramp boiling on the
	// churn where it lands, and break-up along the ropes — water tears where it
	// is thickest, so the flecks belong on the streaks rather than scattered
	// evenly across the sheet.
	float whitewater = clamp(sheet_foam
		+ plunge * mix(0.4, 1.0, churn)
		+ streaks * streak_breakup, 0.0, 1.0);
	float flecks = wf_flecks(pixel_noise, whitewater);
	// The back curtain's whitewater is seen THROUGH the front sheet, so it lands
	// dimmer; taking the max rather than adding keeps a fleck at fleck opacity
	// instead of stacking two into a blown-out white.
	float back_whitewater = clamp(sheet_foam
		+ plunge * mix(0.4, 1.0, churn)
		+ back_streaks * streak_breakup, 0.0, 1.0);
	float back_flecks = wf_flecks(back_pixel, back_whitewater) * back_gate * back_foam_scale;
	float falling_foam = max(flecks, back_flecks) * fleck_strength * (1.0 - surfaceness);

	// Thickness, and it is NOISY on both counts. The streaks are the gaps you see
	// through, now hard-edged enough to read as separate ropes; and a fleck is
	// aerated water, so it thickens the sheet under itself — without that the
	// specks hover over a curtain you can still see straight through and never
	// join it. The shoulder ignores both and carries the pool's own opacity.
	float falling = sheet_thickness * mix(1.0 - streak_depth, 1.0, streaks)
		* (1.0 + flecks * fleck_opacity_boost)
		// The back curtain, ADDED: two layers of water along one ray is one longer
		// path through water. Where the front sheet has torn open this is all that
		// is left, and it is why a gap now reads as water rather than as cliff.
		+ sheet_thickness * back_thickness_scale * back_gate
			* mix(1.0 - back_streak_depth, 1.0, back_streaks);
	float thickness = max(mix(falling, sheet_thickness * surface_thickness_scale, surfaceness), 0.001);
	// Clear-water absorption everywhere, plus the aerated sheet's grey scattering
	// on the falling half. See `aeration_density`: absorption alone leaves a metre
	// of water ~92% transparent, so this term is the whole reason a gap in the
	// streaks reads as water rather than as whatever is behind the fall.
	vec3 extinction = water_absorption + vec3(aeration_density * (1.0 - surfaceness));
	vec3 transmittance = exp(-extinction * thickness);

	// The SURFACE half's foam is the pool's own shoreline band, out of
	// water_shading.gdshaderinc — because the shoulder is not merely like the
	// pool's edge, it is drawn over it, and a band the pool paints and the sheet
	// does not reads as a hole punched in the surf right at the lip.
	//
	// Full shallowness: the shoulder is water about to leave the ground, which is
	// the shallowest a body of water gets. No rim — that band marks a water/LAND
	// boundary and there is none here, only more water. No wind damping either:
	// water breaking over an edge breaks whether or not there is a wind on it,
	// which is exactly why a cave cascade still roars.
	float shore = water_shore_foam(world_vertex.xz, 1.0, 0.0, 1.0) * surfaceness;
	float foam_amount = clamp(max(falling_foam, shore), 0.0, 1.0);
	if (water_disable_foam) { foam_amount = 0.0; }

	// Where the lightmap is read. A pool reads half a voxel BACK, inside its own
	// cell; a falling sheet has to read forward into the air it hangs in, because
	// backwards is the cliff it is falling down and the sample would come back
	// black. Lerped like everything else, so the shoulder reads its own cell.
	vec3 light_sample_pos = world_vertex + n * mix(0.25, -0.5, surfaceness);
	float sun_mask;
	vec3 sun_lit;
	vec3 block_lit;
	vec3 light;
	water_light(world_vertex, light_sample_pos, TIME, 1.0, sun_mask, sun_lit, block_lit, light);
	// Face shading matches voxel_water's: 1.0 looking up, 0.8 across Z, 0.6
	// across X. Interpolated rather than stepped, because unlike a voxel quad
	// this surface actually curves.
	// Ambient floor for the aerated half — see `falling_ambient`. Applied to the
	// water's own lighting only, never to sun_lit / block_lit, so the adaptation
	// tail and the foam floor still see the real light level.
	light = max(light, vec3(falling_ambient * (1.0 - surfaceness)));
	float face_shade = mix(mix(0.6, 0.8, abs(n.z)), 1.0, surfaceness);
	vec3 in_scatter = water_in_scatter(face_shade * light, transmittance);

	// Sky reflection. A pool's surface is mostly fresnel-weighted sky over a
	// floor (reflection_min — at the grazing angles an iso camera gives,
	// physical fresnel alone is ~5% and reads as no reflection at all), and
	// without it the top of a fall stops reading as water. Falling water is an
	// agitated vertical surface and mirrors nothing, hence the same weighting.
	//
	// Reflected off a normal lerped to +Y, NOT off the sheet's own. The geometry
	// swings from flat-up to vertical within a few centimetres of the lip (the
	// samples are bunched there on purpose), so a ray taken off the true normal
	// sweeps through the horizon across a handful of pixels — and the sky's cloud
	// lookup projects onto its plane at t = (altitude - y) / max(dir.y, 0.05), so
	// the sample point runs away by hundreds of metres from one pixel to the next
	// and the cloud texture comes back as noise. That was the crawling speckle
	// over the top of every fall. Lerped like everything else here, so wherever
	// this term has any weight the shoulder reflects as the pool it continues.
	vec3 refl_n = normalize(mix(n, vec3(0.0, 1.0, 0.0), max(surfaceness, falling_sky_up)));
	// Weighted by the streaks so a sparkle sits on water that is actually there,
	// and by (1 - surfaceness) because the shoulder is a pool surface and answers
	// to the pool's own ripples, not to droplets.
	refl_n = normalize(refl_n + wf_sparkle(sheet_uv, TIME)
		* (sparkle_amount * streaks * (1.0 - surfaceness)));
	vec3 view_to_frag = normalize(world_vertex - CAMERA_POSITION_WORLD);
	float fresnel = pow(1.0 - clamp(dot(-view_to_frag, refl_n), 0.0, 1.0), fresnel_power);
	float refl_mix = clamp(max(fresnel * reflection_strength, reflection_min), 0.0, 1.0)
		* mix(falling_sky_reflect, 1.0, surfaceness);
	vec3 refl_col = sample_sky(world_vertex, reflect(view_to_frag, refl_n), TIME) * sun_mask;

	// Composited against NOTHING: the background is whatever is already in the
	// framebuffer, and the hardware blend puts us over it. See the render_mode
	// note at the top for why a screen-texture sample cannot be trusted here.
	vec3 composited;
	vec3 ea_exempt;
	water_compose(vec3(0.0), transmittance, in_scatter, light, sun_mask,
		foam_amount, refl_col, refl_mix, composited, ea_exempt);

	// How much of the background the sheet hides. Everything water_compose would
	// have multiplied its `behind` by, collapsed to one scalar because a hardware
	// blend has no per-channel alpha — the tint absorption gives the background is
	// lost, which on an aerated sheet this opaque is not worth a second pass.
	float hidden = 1.0 - dot(transmittance, vec3(0.299, 0.587, 0.114))
		* (1.0 - foam_amount) * (1.0 - refl_mix);

	// ea_exempt is zero by construction now: nothing here came out of the screen
	// texture, so all of it is this shader's own light and all of it adapts.
	ALBEDO = water_adapt(composited, ea_exempt, sun_lit, block_lit);
	ALPHA = clamp(hidden, 0.0, 1.0);
}
