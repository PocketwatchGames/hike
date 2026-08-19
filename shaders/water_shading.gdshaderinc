// The water shading path, shared by every surface that is made of water.
//
// Owned by this file so there is exactly ONE of it: `voxel_water` (the pool /
// river surface) and `waterfall` (the falling sheet) both call these, so a
// cascade cannot drift from the water it pours out of. That drift is not
// hypothetical — the fall was written as a parallel copy first, and the copy's
// missing reflection term alone was enough to stop the top of it reading as
// water at all.
//
// The three stages, in the order a caller uses them:
//   water_light    — how much light reaches this bit of water
//   water_compose  — the volumetric composite (bottom + in-scatter + foam +
//                    reflection) against the screen texture
//   water_adapt    — the dark-adaptation gain on the caller's own light only
//
// What is NOT here is everything specific to one surface: a pool's ripple
// normals, wave displacement and depth-buffer thickness; a fall's flow streaks
// and authored thickness. Those are inputs to these functions, not part of them.

// Lighting inputs.
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
// Owns sun_world_dir / sun_color / sky_sun_dir and the cloud_* set, which
// cloud_shadow needs. Consumers must NOT include it again.
#include "res://shaders/sky_common.gdshaderinc"
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

// Per-zone water styling — all driven by ZoneData.WaterColor through
// WeatherDerivation + SkyController, which is what makes a zone's water colour
// reach a cascade in that zone without the cascade knowing zones exist.
//
// Volumetric model (Beer-Lambert): water has no diffuse albedo. What you see is
// the bottom attenuated by the column, plus light scattered back out of the
// volume, plus a reflection on the surface.
//   water_absorption    — per-channel extinction, 1/metre. Red goes first,
//                         which is why depth turns blue-green on its own.
//   water_scatter_color — the colour the volume settles to once the bottom is
//                         fully extinguished. This IS the zone's water colour.
global uniform vec3 water_absorption;
global uniform vec3 water_scatter_color : source_color;
global uniform vec3 foam_color;
// Lower bound on foam lighting, so foam dims with the weather instead of
// collapsing to black or glowing.
global uniform float foam_min_light;

// Block-light shadow projector — a sprite standing over water dims it here.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

// Dark-adaptation ("night eyes") tone curve. Exact no-op at eye_adaptation == 0.
#include "res://shaders/eye_adaptation.gdshaderinc"

const float WATER_MIN_AMBIENT = 0.0;

// Light reaching the water at a point.
//
// `light_sample_pos` is where the lightmap is read, which is NOT always the
// fragment: a pool's surface reads half a voxel back, INSIDE its own cell,
// while a falling sheet reads forward into the open air it hangs in (reading
// back would land inside the cliff and paint the fall black).
//
// `sun_normal_factor` scales direct sun only, for a surface whose orientation
// genuinely redirects it — a pool passes its ripple-vs-sun ratio, a fall passes
// 1. Block light is omnidirectional in the lightmap, so it is never scaled.
//
// Water is `unshaded` — no light() stage, no ATTENUATION — so cloud shadow is
// applied here rather than by the engine.
void water_light(vec3 world_pos, vec3 light_sample_pos, float time, float sun_normal_factor,
	out float sun_mask, out vec3 sun_lit, out vec3 block_lit, out vec3 light)
{
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	sun_mask = pow(lm.r, light_falloff_exp);
	float cloud = cloud_shadow_ground_gated(world_pos, light_sample_pos, time);
	sun_lit = sun_mask * sun_intensity * sun_color * (1.0 - cloud * cloud_shadow_strength)
		* sun_normal_factor;
	block_lit = lm.gba * block_light_shadow_factor(world_pos);
	light = max(min(vec3(1.0), sun_lit + block_lit), vec3(WATER_MIN_AMBIENT));
}

// The volume's own contribution: light scattered back out of the water,
// saturating toward the zone's colour as the bottom is extinguished. This is
// what gives deep water its colour — it is NOT an albedo laid over the ground.
vec3 water_in_scatter(vec3 shaded_light, vec3 transmittance)
{
	return water_scatter_color * shaded_light * (1.0 - transmittance);
}

// Compositing, done by hand rather than by the blend mode so the reflection can
// sit uniformly on top of the water body and the caller can offset its own
// "behind" sample. Callers output ALPHA = 1 — letting Godot blend on top of
// this would double-blend.
//
// `behind * transmittance` can only ever DARKEN what is behind the water, which
// is why shallow water can't brighten dark ground and why the water's edge needs
// no opacity floor: at zero thickness transmittance is 1 and in-scatter is 0, so
// the shoreline is continuous by construction.
//
// `ea_exempt` comes back for water_adapt: it is the part of the pixel that came
// out of the SCREEN texture, which already carries whatever adaptation drew it.
void water_compose(vec3 behind, vec3 transmittance, vec3 in_scatter, vec3 light,
	float sun_mask, float foam_amount, vec3 refl_col, float refl_mix,
	out vec3 composited, out vec3 ea_exempt)
{
	vec3 seen_through = behind * transmittance;
	vec3 own_light = in_scatter;
	// Foam sits ON the surface, above the whole volume, and keeps its own alpha.
	if (foam_amount > 1e-4)
	{
		vec3 foam_light = max(light, vec3(foam_min_light * sun_mask));
		own_light = mix(own_light, foam_color * foam_light, foam_amount);
		seen_through *= (1.0 - foam_amount);
	}
	own_light = mix(own_light, refl_col, refl_mix);
	seen_through *= (1.0 - refl_mix);
	composited = own_light + seen_through;
	ea_exempt = seen_through;
}

// Dark adaptation, applied ONLY to the light this shader generated. Adapting the
// whole composite would square the gain on the see-through part, weighted by
// transmittance — brightest exactly where the water is thinnest, i.e. a glowing
// band along every shoreline that no shading parameter can dim.
vec3 water_adapt(vec3 composited, vec3 ea_exempt, vec3 sun_lit, vec3 block_lit)
{
	float ea_gain = eye_adaptation_gain(dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114)));
	return max(composited - ea_exempt, vec3(0.0)) * ea_gain + ea_exempt;
}
