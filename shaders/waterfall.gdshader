shader_type spatial;
// Mirrors voxel_water's render setup so a cascade and the pool it pours into
// are the same substance: no diffuse albedo, manual compositing against the
// screen texture, and ALPHA = 1 because the blend has already been done here.
render_mode blend_mix, depth_draw_never, cull_disabled, unshaded;

// Draws AFTER both water passes (-3 front, -2 backface) so the pool the sheet
// hangs in front of is already in the screen texture when we sample it, and
// before the default priority everything else uses.
// The ordering table lives in ChunkMesh.cs where the water materials are built.

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"

uniform sampler2D screen_tex : hint_screen_texture, filter_nearest_mipmap;

global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
// Declares sun_world_dir / sun_color / the cloud_* set that cloud_shadow needs.
#include "res://shaders/sky_common.gdshaderinc"
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

// The same per-zone water styling voxel_water reads, driven by ZoneData's
// WaterColor through WeatherDerivation + SkyController. Reading these rather
// than authoring a waterfall colour is what keeps a swamp cascade swamp-green
// without anything on the waterfall side knowing about zones.
global uniform vec3 water_absorption;
global uniform vec3 water_scatter_color : source_color;
global uniform float water_muddiness;
global uniform float fresnel_power;
global uniform float reflection_strength;
global uniform float reflection_min;
global uniform vec3 foam_color;
global uniform float foam_min_light;
global uniform bool water_hide;
global uniform bool water_disable_foam;

global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"
#include "res://shaders/eye_adaptation.gdshaderinc"

// --- Per-instance, set by Waterfall.cs from the site's measurements ----------
// How much water this fall is carrying, in metres of optical depth. A pool
// reads its thickness off the depth buffer; a curtain hanging in open air has
// nothing behind it to measure against (the scene behind is the far side of the
// valley, which would read as infinitely deep water), so the thickness is
// authored per size tier instead. This is the single number that decides
// whether a fall is a veil or a wall.
instance uniform float sheet_thickness = 0.5;
// Whitewater coverage, 0..1.
instance uniform float sheet_foam = 0.5;
// World Y of the lip and the landing. The foam ramps key off these, so a short
// fall is foamy over most of its length and a tall one only at its ends.
instance uniform float fall_top_y = 0.0;
instance uniform float fall_bottom_y = 0.0;

// --- Authored on the material -----------------------------------------------
// Metres per second the streak pattern falls. Real water accelerates; the
// pattern doesn't, because a scroll rate that varied with height would smear
// the texture rather than read as acceleration.
uniform float flow_speed : hint_range(0.0, 40.0) = 9.0;
// Streak shape. `across` is the pattern's frequency along the width of the
// sheet, `along` its frequency down the fall — much lower, which is what turns
// noise into vertical ropes of water rather than blobs.
uniform float streak_scale_across : hint_range(0.05, 8.0) = 1.6;
uniform float streak_scale_along : hint_range(0.005, 2.0) = 0.12;
// How much the streaks vary the sheet's thickness, 0..1. This is where the
// curtain gets its gaps: a thin streak lets more of the scene behind through.
uniform float streak_depth : hint_range(0.0, 1.0) = 0.8;
// Where the fall goes white approaching its landing. A FRACTION of the fall's
// own height, capped in metres — an absolute distance turns a short drop
// entirely to foam, which reads as a painted slab rather than as water.
uniform float base_foam_fraction : hint_range(0.0, 1.0) = 0.3;
uniform float base_foam_height : hint_range(0.0, 12.0) = 3.0;
// Distance below the lip over which the water is still a smooth unbroken sheet
// — the band that is both foam-free and thick.
uniform float lip_smooth_height : hint_range(0.0, 12.0) = 1.5;
// Multiple of the sheet's nominal thickness carried at the lip itself. Well
// above 1 on purpose: it is what makes the top of the fall read as continuous
// with the opaque pool feeding it instead of as a veil hung under it.
uniform float lip_thickness_scale : hint_range(1.0, 20.0) = 6.0;
// Churn scale — the noise that breaks the plunge foam into whitewater instead
// of a gradient. Higher = finer froth.
uniform float churn_scale : hint_range(0.1, 8.0) = 1.7;
// Screen-space refraction of what's behind the sheet, in normalized UV. Follows
// the streaks, so the background wobbles with the flow.
uniform float refraction_strength : hint_range(0.0, 0.1) = 0.012;
// Extra brightness on the lip's top face, where the water thins and speeds up
// as it goes over the edge.
uniform float lip_brightness : hint_range(0.0, 4.0) = 1.0;

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

// Where a fragment sits on the sheet, from the mesh's own UVs: U runs across
// the curtain and V down it, both in metres (WaterfallMeshBuilder). Scrolling V
// with time is the whole animation — everything else is a static function of it.
// Taking this off the mesh rather than off world position is what lets the sheet
// curve: a jet's surface is not axis-aligned, so no combination of world X/Y/Z
// runs along it.
vec2 wf_sheet_uv(vec2 mesh_uv, float time) {
	return vec2(mesh_uv.x * streak_scale_across,
		(mesh_uv.y + time * flow_speed) * streak_scale_along);
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

	// The sheet is a single-surface curtain drawn from both sides, so the stored
	// normal points the wrong way on whichever side is facing away.
	vec3 n = FRONT_FACING ? world_normal : -world_normal;
	// How much this fragment behaves like a flat water SURFACE rather than a
	// falling face: 1 on the shoulder rolling off the lip, 0 down the curtain.
	// The pool above is all surface, so this is what hands the top of the fall
	// the pool's own look and denies it to the plummeting part, which reflects
	// nothing.
	float flatness = smoothstep(0.15, 0.75, abs(n.y));
	vec2 uv = wf_sheet_uv(sheet_uv, TIME);
	float streaks = wf_streaks(uv);

	float from_bottom = world_vertex.y - fall_bottom_y;
	float from_top = fall_top_y - world_vertex.y;
	float fall_height = max(fall_top_y - fall_bottom_y, 0.01);
	// How much of an unbroken sheet the water still is. Water leaves a lip as a
	// solid tongue and only breaks into ropes as it accelerates, so this is 1 at
	// the lip and 0 by `lip_smooth_height` below it.
	float released = smoothstep(0.0, max(lip_smooth_height, 0.01), from_top);
	float unbroken = 1.0 - released;

	// Thickness, and therefore transparency, varies with the streaks: the gaps
	// between ropes of water are where you see the cliff through the fall. At the
	// lip that thinning is suppressed and the sheet carries several times its
	// nominal depth — the pool it pours out of is opaque, and a curtain that goes
	// straight to a veil at the top reads as a separate object hanging off the
	// water rather than as the same water leaving it.
	float streak_factor = mix(1.0 - streak_depth, 1.0, streaks);
	float thickness = max(sheet_thickness * mix(streak_factor, lip_thickness_scale, unbroken), 0.001);
	vec3 transmittance = exp(-water_absorption * thickness);

	// Foam. Whitewater collects where the sheet lands and where it breaks up on
	// the way down, and is absent for the first metre off the lip where the
	// water is still a smooth sheet.
	float plunge_span = min(base_foam_height, fall_height * base_foam_fraction);
	float plunge = 1.0 - smoothstep(0.0, max(plunge_span, 0.01), from_bottom);
	// Churn: a second noise, scrolling faster than the streaks and independent
	// of them, so the whitewater at the foot boils rather than sliding down with
	// the sheet. Multiplied into the plunge ramp — a gradient alone reads as a
	// painted band, and that band IS what makes a short fall look like a slab.
	float churn = wf_vnoise(vec3(world_vertex.xz * churn_scale,
		world_vertex.y * churn_scale * 0.5 - TIME * 2.5));
	plunge *= smoothstep(0.2, 0.7, churn * 0.6 + streaks * 0.4);
	float broken = smoothstep(0.45, 0.95, streaks) * released;
	// The shoulder is glassy — water going over an edge is smooth right up to
	// the point it breaks — so foam is held off wherever the sheet is still
	// reading as a surface.
	float foam_amount = clamp(max(plunge, broken) * sheet_foam * (1.0 - flatness * 0.75), 0.0, 1.0);
	if (water_disable_foam) { foam_amount = 0.0; }

	// Lighting, sampled and composed exactly as voxel_water's side faces do it:
	// push half a voxel along -normal to land inside the cell the sheet occupies
	// (the fall's own air column), so a cascade in a gorge is as dark as the
	// gorge and one in the sun is as bright as the river above it.
	vec3 light_sample_pos = world_vertex - n * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	float cloud = cloud_shadow_ground_gated(world_vertex, light_sample_pos, TIME);
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * (1.0 - cloud * cloud_shadow_strength);
	vec3 block_lit = lm.gba * block_light_shadow_factor(world_vertex);
	vec3 light = min(vec3(1.0), sun_lit + block_lit);
	// Face shading matches voxel_water's: 1.0 looking up, 0.8 across Z, 0.6
	// across X, so the two sides of a corner read apart. Interpolated rather
	// than stepped, because unlike a voxel quad this surface actually curves.
	float axis_shade = mix(0.6, 0.8, abs(n.z));
	float face_shade = mix(axis_shade, lip_brightness, flatness);
	vec3 shaded_light = face_shade * light;

	// What's behind the sheet, refracted along the flow. Muddy water damps the
	// wobble the same way it does on a pool.
	vec2 refract_uv = SCREEN_UV;
	if (refraction_strength > 1e-5) {
		float wobble = (streaks - 0.5) * 2.0;
		refract_uv += vec2(wobble, wobble * 0.35) * refraction_strength * (1.0 - water_muddiness);
	}
	vec3 behind = textureLod(screen_tex, clamp(refract_uv, vec2(0.001), vec2(0.999)), 0.0).rgb;

	// Sky reflection, and this is the term whose absence made the top of the fall
	// stop reading as water: a pool's surface is never just its own colour, it is
	// mostly fresnel-weighted sky with a floor under it (reflection_min — at the
	// grazing angles an iso camera gives, physical fresnel alone is ~5% and reads
	// as no reflection at all). Weighted by `flatness`, so the shoulder mirrors
	// the sky exactly as the pool it continues does and the falling curtain — an
	// agitated vertical surface — mirrors nothing.
	vec3 view_to_frag = normalize(world_vertex - CAMERA_POSITION_WORLD);
	float fresnel = pow(1.0 - clamp(dot(-view_to_frag, n), 0.0, 1.0), fresnel_power);
	float refl_mix = clamp(max(fresnel * reflection_strength, reflection_min), 0.0, 1.0) * flatness;
	vec3 refl_col = sample_sky(world_vertex, reflect(view_to_frag, n), TIME) * sun_mask;

	// Volumetric composite, identical in form to voxel_water: the scene behind
	// attenuated through the column, plus the light the column scatters back
	// out, foam sitting on top as opaque surface, reflection over the lot.
	vec3 seen_through = behind * transmittance;
	vec3 own_light = water_scatter_color * shaded_light * (1.0 - transmittance);
	if (foam_amount > 1e-4) {
		vec3 foam_light = max(light, vec3(foam_min_light * sun_mask));
		own_light = mix(own_light, foam_color * foam_light, foam_amount);
		seen_through *= (1.0 - foam_amount);
	}
	own_light = mix(own_light, refl_col, refl_mix);
	seen_through *= (1.0 - refl_mix);

	// Eye adaptation applies only to the light this shader generated —
	// `seen_through` already carries the adaptation of whatever drew it into the
	// screen texture, and adapting it twice would square the gain exactly where
	// the sheet is thinnest.
	float ea_gain = eye_adaptation_gain(dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114)));
	ALBEDO = own_light * ea_gain + seen_through;
	ALPHA = 1.0;
}
