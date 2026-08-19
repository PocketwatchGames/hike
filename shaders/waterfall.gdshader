shader_type spatial;
// Same render setup as voxel_water, because a cascade is the same substance as
// the pool it pours out of: no diffuse albedo, manual compositing against the
// screen texture, and ALPHA = 1 because the blend has already been done here.
// The sheet is one double-sided surface, so it is visible from behind too.
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

// Draws AFTER both water passes (-3 front, -2 backface) so the pool the sheet
// hangs in front of is already in the screen texture when we sample it, and
// before the default priority everything else uses. The ordering table lives in
// ChunkMesh.cs, where the water materials are built.

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

uniform sampler2D screen_tex : hint_screen_texture, filter_nearest_mipmap;

// Surface reflection, same globals and same meaning as on the pool.
global uniform float fresnel_power;
global uniform float reflection_strength;
global uniform float reflection_min;
global uniform float water_muddiness;
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
// Screen-space refraction of what's behind the sheet, in normalized UV. Follows
// the streaks, so the background wobbles with the flow.
uniform float refraction_strength : hint_range(0.0, 0.1) = 0.015;

float wf_hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float wf_vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = wf_hash13(i); float n100 = wf_hash13(i + vec3(1,0,0));
	float n010 = wf_hash13(i + vec3(0,1,0)); float n110 = wf_hash13(i + vec3(1,1,0));
	float n001 = wf_hash13(i + vec3(0,0,1)); float n101 = wf_hash13(i + vec3(1,0,1));
	float n011 = wf_hash13(i + vec3(0,1,1)); float n111 = wf_hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

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

// Two octaves of the streak field, in [0,1]. 1 = a thick rope of water, 0 = a
// gap the background shows through.
float wf_streaks(vec2 uv) {
	float a = wf_vnoise(vec3(uv, 0.0));
	float b = wf_vnoise(vec3(uv * 2.7 + vec2(11.3, 4.1), 0.0));
	return clamp(a * 0.65 + b * 0.35, 0.0, 1.0);
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

	vec2 uv = wf_sheet_uv(sheet_uv, TIME);
	float streaks = wf_streaks(uv);

	// Thickness. Falling water is thin and gappy — the streaks are what you see
	// through — while the shoulder carries the pool's own opacity.
	float falling = sheet_thickness * mix(1.0 - streak_depth, 1.0, streaks);
	float thickness = max(mix(falling, sheet_thickness * surface_thickness_scale, surfaceness), 0.001);
	vec3 transmittance = exp(-water_absorption * thickness);

	// Foam. Whitewater collects where the sheet lands and where it breaks up on
	// the way down. The shoulder is glassy — water going over an edge is smooth
	// right up to the point it breaks — so foam fades out with `surfaceness`.
	float from_bottom = world_vertex.y - fall_bottom_y;
	float fall_height = max(fall_top_y - fall_bottom_y, 0.01);
	float plunge_span = min(base_foam_height, fall_height * base_foam_fraction);
	float plunge = 1.0 - smoothstep(0.0, max(plunge_span, 0.01), from_bottom);
	// Churn: a second noise, scrolling faster than the streaks and independent
	// of them, so the whitewater at the foot boils rather than sliding down with
	// the sheet. Multiplied into the plunge ramp — a gradient alone reads as a
	// painted band, and that band is what makes a short fall look like a slab.
	float churn = wf_vnoise(vec3(world_vertex.xz * churn_scale,
		world_vertex.y * churn_scale * 0.5 - TIME * 2.5));
	plunge *= smoothstep(0.2, 0.7, churn * 0.6 + streaks * 0.4);
	float broken = smoothstep(0.45, 0.95, streaks);
	float foam_amount = clamp(max(plunge, broken) * sheet_foam * (1.0 - surfaceness), 0.0, 1.0);
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
	float face_shade = mix(mix(0.6, 0.8, abs(n.z)), 1.0, surfaceness);
	vec3 in_scatter = water_in_scatter(face_shade * light, transmittance);

	// Sky reflection. A pool's surface is mostly fresnel-weighted sky over a
	// floor (reflection_min — at the grazing angles an iso camera gives,
	// physical fresnel alone is ~5% and reads as no reflection at all), and
	// without it the top of a fall stops reading as water. Falling water is an
	// agitated vertical surface and mirrors nothing, hence the same weighting.
	vec3 view_to_frag = normalize(world_vertex - CAMERA_POSITION_WORLD);
	float fresnel = pow(1.0 - clamp(dot(-view_to_frag, n), 0.0, 1.0), fresnel_power);
	float refl_mix = clamp(max(fresnel * reflection_strength, reflection_min), 0.0, 1.0) * surfaceness;
	vec3 refl_col = sample_sky(world_vertex, reflect(view_to_frag, n), TIME) * sun_mask;

	// What's behind the sheet, refracted along the flow. Muddy water damps the
	// wobble the same way it does on a pool.
	vec2 refract_uv = SCREEN_UV;
	if (refraction_strength > 1e-5) {
		float wobble = (streaks - 0.5) * 2.0;
		refract_uv += vec2(wobble, wobble * 0.35) * refraction_strength * (1.0 - water_muddiness);
	}
	vec3 behind = textureLod(screen_tex, clamp(refract_uv, vec2(0.001), vec2(0.999)), 0.0).rgb;

	vec3 composited;
	vec3 ea_exempt;
	water_compose(behind, transmittance, in_scatter, light, sun_mask,
		foam_amount, refl_col, refl_mix, composited, ea_exempt);
	ALBEDO = water_adapt(composited, ea_exempt, sun_lit, block_lit);
	ALPHA = 1.0;
}
