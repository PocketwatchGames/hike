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

// Water styling. The ZONE owns the hue and the baseline clarity — that is how a
// region defines its palette — and a water BLOCK says only how far from it this
// particular body sits.
//
// Volumetric model (Beer-Lambert): water has no diffuse albedo. What you see is
// the bottom attenuated by the column, plus light scattered back out of the
// volume, plus a reflection on the surface.
//   water_absorption    — per-channel extinction, 1/metre. Red goes first,
//                         which is why depth turns blue-green on its own.
//   water_scatter_color — the colour the volume settles to once the bottom is
//                         fully extinguished.
//
// These two are the zone's FINISHED values, derived in C#. They remain correct
// for everything that is made of water but carries no block id to look a type up
// with — puddles, sprite reflections, the cutaway cap — and a caller with no
// per-fragment id passes them straight through.
global uniform vec3 water_absorption;
global uniform vec3 water_scatter_color : source_color;

// The inputs those two were derived FROM, so a water block can redo the
// derivation per fragment against its own turbidity. Deriving it in C# alone
// cannot vary per voxel; deriving it in both places would drift, which is why
// SkyController now pushes the endpoints instead of a second finished value.
global uniform float water_zone_muddiness;
global uniform vec3 water_hue : source_color;
global uniform float water_absorb_clear;
global uniform float water_absorb_muddy;
global uniform float water_albedo_clear;
global uniform float water_albedo_muddy;

// How much murkier (+) or clearer (-) than its zone a water block is, applied as
// a PUSH TOWARD THE ENDPOINT rather than a clamped addition: +0.3 has to still
// do something in a swamp already sitting at 0.9, and -0.3 has to still do
// something in a tropical zone at 0.1. Reads as "this far toward murky from
// whatever this region is", and saturates at neither end.
//
// Must match BlockData.waterTurbidityDelta's documented form exactly — the C#
// side authors against this curve.
float water_muddiness_for(float turbidity_delta)
{
	float z = clamp(water_zone_muddiness, 0.0, 1.0);
	float d = clamp(turbidity_delta, -1.0, 1.0);
	return d > 0.0 ? mix(z, 1.0, d) : mix(z, 0.0, -d);
}

// The zone's own derivation, per fragment, against a block's turbidity. Mirrors
// WeatherDerivation + SkyController.
//
// HUE comes from the zone's authored water colour and nothing else; muddiness
// moves only the INTENSITY (scatter albedo) and how far you see in (absorption).
// It used to pull the hue toward a sediment colour derived from the zone's
// DustColor, which was wrong twice: dust is the AIR — fog is DustColor outright —
// so the water was being tinted with the pigment the fog then washed over it,
// guaranteeing a low-contrast scene; and the pull was weighted `m * 0.6`, so the
// muddier the zone the LESS its authored colour meant. In the swamp it did not
// dilute the authored brown, it inverted it: dust there is far greener than the
// water, so G rose 88% against R's 19% and the hue crossed over to green. No
// authorable value survived that. If a zone's silt has its own colour, that
// colour IS the zone's water colour — one authored value, not two.
//
// Absorption stays the COMPLEMENT of the scatter colour: a channel the water
// does not send back is one it swallows, which is why depth reddens out with no
// second authored tint.
void water_optics(float turbidity_delta, out vec3 scatter_color, out vec3 absorption)
{
	float m = water_muddiness_for(turbidity_delta);
	vec3 scatter = water_hue;
	scatter_color = scatter * mix(water_albedo_clear, water_albedo_muddy, m);
	absorption = max(vec3(1.0) - scatter, vec3(0.02))
		* mix(water_absorb_clear, water_absorb_muddy, m);
}

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

// --- Shoreline surf ---------------------------------------------------------
// SHARED, and it has to be: a cascade's shoulder is the pool surface continuing
// over the lip, so a band the pool draws and the fall does not reads as a hole
// punched in the surf exactly where the two meet. This lived in voxel_water,
// which is why that hole was there.
//
// What each caller supplies is only how its own water is RUNNING OUT — a pool
// measures that off its depth, a fall off how far it has rolled past the lip —
// because that is the one part the two genuinely disagree on.

// Pixels-per-world-unit noise UV quantization. Snaps the samples to a discrete
// grid so the surface reads as hand-drawn pixel ripples rather than a smoothly
// scrolling noise pattern.
global uniform float ripple_pixel_size;
// Contiguous shoreline rim — a solid band of foam-colored pixels right at the
// water/land boundary, drawn under the noisy foam so the two blend together.
global uniform float water_rim_strength;
// How far out the noise band reaches, and its shape.
global uniform float foam_scale;
// World-XZ scroll offset for the foam noise UVs. Integrated in C# as
// `foamScroll * windSpeed * dt` so the pattern only drifts when wind is driving
// wave energy at shore (and stops when wind dies).
global uniform vec2 foam_offset;
global uniform float foam_strength;
global uniform float foam_threshold;
global uniform float foam_sharpness;

float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i); float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0)); float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1)); float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1)); float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

// Quantize noise UVs to a pixel grid. Used for the shoreline foam mask
// (procedural vnoise); the ripple height sampled from water_noise_tex already
// gets its pixel feel from filter_nearest.
vec2 pixelize(vec2 uv) {
	float px = max(ripple_pixel_size, 0.01);
	return floor(uv * px) / px;
}

// Surf where water is running out, in [0,1].
//
//   `shallowness` — 0 where the water is at full body, 1 where it is about to
//                   end. Drives the NOISY band, so it reads as surf texture
//                   rather than a painted line.
//   `rimness`     — the same measure, but for the hard contiguous edge line;
//                   separate because the rim is a structural water/land cue and
//                   is deliberately not weather-driven.
//   `wind_factor` — wave energy. Damps the noisy half only, so a cave pool
//                   still reads as still water against its rock.
float water_shore_foam(vec2 world_xz, float shallowness, float rimness, float wind_factor)
{
	vec2 fuv = pixelize(world_xz * foam_scale + foam_offset);
	float fn = vnoise(vec3(fuv, 0.0));
	float band = max(1e-4, 1.0 - foam_sharpness);
	float mask = smoothstep(foam_threshold, min(foam_threshold + band, 1.0), fn);
	float noisy = shallowness * mask * foam_strength * wind_factor;
	return clamp(max(noisy, rimness * water_rim_strength), 0.0, 1.0);
}

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
vec3 water_in_scatter(vec3 scatter_color, vec3 shaded_light, vec3 transmittance)
{
	return scatter_color * shaded_light * (1.0 - transmittance);
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
