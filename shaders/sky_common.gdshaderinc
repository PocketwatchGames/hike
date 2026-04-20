// Shared sky evaluation — used by shaders/sky.gdshader to render the
// background radiance and by shaders/voxel_water.gdshader as the SSR miss
// fallback. Keeping one formula in both places avoids drift between the
// on-screen sky and the reflection.
//
// Direction convention: `dir` is the view ray from the surface outward
// (toward the sky). sun_world_dir is the direction light travels (from
// WorldState.ShadowLightDirection), so the vector from a point toward the
// sun is -sun_world_dir.
//
// Cloud noise: samples `cloud_noise_tex` — the SAME baked texture
// shaders/cloud_shadow.gdshaderinc uses to cast ground shadows. So a
// cloud visible in the sky (or in a water reflection) corresponds to
// the same noise shape that's shadowing the ground at that moment. The
// two do NOT share screen-space positions — cloud_shadow projects by
// world XZ plus sun offset, sample_sky projects the view ray onto an
// infinite cloud plane via dir.xz/dir.y — but they share the underlying
// noise pattern so shapes feel coherent.

global uniform vec3 sun_world_dir;
global uniform vec3 sun_color;
global uniform vec3 horizon_color;
global uniform vec3 zenith_color;
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform vec3 cloud_color;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
// World-Y of the flat cloud plane, shared with cloud_shadow.gdshaderinc.
// Used by sample_sky_from() to project a fragment's reflection ray onto
// the cloud plane so different water fragments see different clouds even
// under an orthographic camera (where reflection directions are uniform).
global uniform float cloud_altitude;
// Debug: when set, force clouds to bright magenta so they're unmistakable
// in both the sky dome and any reflection that calls sample_sky.
global uniform int debug_magenta_clouds;

// World-unit tiling for clouds when sampled by world XZ (as opposed to
// by sky direction). Must stay in sync with CLOUD_TEX_TILING in
// cloud_shadow.gdshaderinc — a reflected cloud should correspond to the
// shadow that same cloud casts on the ground, so they share a scale.
const float SKY_CLOUD_GROUND_TILING = 0.012;

vec3 sample_sky(vec3 dir, float t)
{
	dir = normalize(dir);

	// Gradient: horizon at dir.y=0, zenith at dir.y=1. Below horizon mirrors
	// the horizon color (water reflections rarely point down, but guard it).
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 base = mix(horizon_color, zenith_color, pow(up, 0.6));

	// Sun disk + soft glow. Vector from surface toward the sun is
	// -sun_world_dir; cos(angle) = dir . (-sun_world_dir). The disk
	// angular width is intentionally larger than the real sun so it
	// reads clearly when reflected in rippling water — a 1° pinprick
	// disappears at reflection scale.
	vec3 to_sun = -normalize(sun_world_dir);
	float sd = max(dot(dir, to_sun), 0.0);
	float disk = smoothstep(0.994, 0.998, sd) * 2.0;
	float glow = pow(sd, 32.0) * 1.0;
	base += sun_color * (disk + glow);

	// Clouds: project the view ray onto a flat cloud plane (stretch toward
	// the horizon by dividing xz by y) and sample the shared noise texture.
	// cloud_band sits the layer in the lower half of the sky so zenith
	// stays open. repeat_enable on the sampler handles tiling seamlessly
	// since the texture was baked seamless.
	vec2 cuv = (dir.xz / max(abs(dir.y), 0.15)) * cloud_scale + cloud_scroll * t;
	float c = texture(cloud_noise_tex, cuv).r;
	// Same threshold/sharpness remap cloud_shadow.gdshaderinc uses, so sky
	// cloud density matches the shadow density the clouds cast.
	float band = max(1e-4, 1.0 - cloud_sharpness);
	float cloud_mask = smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
	float cloud_band = smoothstep(0.0, 0.35, up) * (1.0 - smoothstep(0.4, 0.95, up));
	vec3 cc = debug_magenta_clouds != 0 ? vec3(1.0, 0.0, 1.0) : cloud_color;
	base = mix(base, cc, cloud_mask * cloud_band);

	return base;
}

// Origin-aware variant of sample_sky: projects `dir` from the given world
// `origin` to the flat cloud plane at cloud_altitude, then samples the
// cloud noise at that intersection. This is necessary for water
// reflections under an orthographic camera — where every water fragment
// has an identical reflection direction — so the cloud sample varies by
// fragment XZ and the user sees cloud shapes on flat water instead of a
// single uniform color.
//
// The cloud_xz formula matches cloud_shadow_ground(): cloud position is
// world XZ offset along the direction by the altitude ratio. This aligns
// the reflected cloud pattern with the shadow cast by the same cloud.
vec3 sample_sky_from(vec3 origin, vec3 dir, float t)
{
	dir = normalize(dir);
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 base = mix(horizon_color, zenith_color, pow(up, 0.6));

	vec3 to_sun = -normalize(sun_world_dir);
	float sd = max(dot(dir, to_sun), 0.0);
	float disk = smoothstep(0.994, 0.998, sd) * 2.0;
	float glow = pow(sd, 32.0) * 1.0;
	base += sun_color * (disk + glow);

	// Cloud-plane intersection. Guard against near-horizontal rays that
	// would project to points effectively at infinity. cloud_xz is in
	// world units; use the ground-tiling scale (NOT cloud_scale, which
	// is calibrated for dir-space sampling in the sky dome) so the
	// reflected cloud size matches the cloud shadow's on the ground.
	float dy = max(dir.y, 0.15);
	float tt = (cloud_altitude - origin.y) / dy;
	vec2 cloud_xz = origin.xz + dir.xz * tt;
	vec2 cuv = cloud_xz * SKY_CLOUD_GROUND_TILING + cloud_scroll * t;
	float c = texture(cloud_noise_tex, cuv).r;
	float band = max(1e-4, 1.0 - cloud_sharpness);
	float cloud_mask = smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
	// No cloud_band here (unlike sample_sky). That factor fades clouds
	// away from mid-sky for sky-dome aesthetics; in a water reflection
	// the reflected ray often points at the zenith (flat water +
	// orthographic camera), so that factor would wipe out the clouds
	// entirely. Reflections should show clouds wherever the projected
	// cloud plane has them.
	vec3 cc = debug_magenta_clouds != 0 ? vec3(1.0, 0.0, 1.0) : cloud_color;
	base = mix(base, cc, cloud_mask);

	return base;
}
