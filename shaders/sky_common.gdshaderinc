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
// sun's ACTUAL direction (never flips to the moon at night, unlike
// sun_world_dir which holds the primary light direction). Used here
// to place the sun disk at the correct sky position while lighting
// continues to key off the moon at night via sun_world_dir.
global uniform vec3 sky_sun_dir;
global uniform vec3 moon_color;
// Per-disk halo strength. SkyController fades sun_disk_glow to 0
// below the horizon (invisible sun) and ramps moon_disk_glow up at
// night so the moon reads as a crisp disk with a subtle halo.
global uniform float sun_disk_glow;
global uniform float moon_disk_glow;
global uniform vec3 horizon_color;
global uniform vec3 zenith_color;
global uniform vec2 cloud_offset;
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
// Night factor [0, 1] — 0 at day, 1 at night. Multiplied into the sky
// gradient + cloud output so the dome and water reflections darken to
// near-black after sunset; the sun/moon disks are kept at full brightness
// regardless so the moon still reads as a bright object against black
// sky. A future starfield pass would render on top of the darkened base.
global uniform float sky_night_factor;
// Sun/moon are textured sprites drawn in the sky. Each is positioned at its
// body's world direction; a tangent-plane projection maps the view ray to
// texture UV. Angular size controls how much of the sky dome the sprite
// covers; intensity is a brightness multiplier (>1 triggers bloom). The
// moon texture can be swapped for phase-authored variants.
global uniform sampler2D sun_texture : filter_linear, repeat_disable;
global uniform sampler2D moon_texture : filter_linear, repeat_disable;
// Half-angle of the sprite in degrees — used to compute the cosine threshold
// and the tangent-plane scale for UV mapping. 2° = real-world sun angular
// size; ~6° reads as a chunky pixel-art disk.
global uniform float sun_angular_size_deg;
global uniform float moon_angular_size_deg;
global uniform float sun_disk_intensity;
global uniform float moon_disk_intensity;
// Debug: force the sun sprite to render as pure magenta, bypassing cloud
// occlusion and horizon gating — so you can verify the sprite is being
// drawn at all before tuning tint/intensity.
global uniform bool sky_debug_sun_disk;

// World-unit tiling for clouds when sampled by world XZ (as opposed to
// by sky direction). Must stay in sync with CLOUD_TEX_TILING in
// cloud_shadow.gdshaderinc — a reflected cloud should correspond to the
// shadow that same cloud casts on the ground, so they share a scale.
const float SKY_CLOUD_GROUND_TILING = 0.012;

// Sky gradient + clouds (no sun/moon/stars). Splits the work so the water
// shader can use TRUE reflection direction for clouds — so reflected clouds
// align with the shadows they cast on the ground — while remapping the
// celestial bodies into a wide fake FOV separately.
vec3 sample_sky_clouds(vec3 origin, vec3 dir, float t)
{
	dir = normalize(dir);

	// Gradient: horizon at dir.y=0, zenith at dir.y=1. Darkened at night.
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 base = mix(horizon_color, zenith_color, pow(up, 0.6));
	base *= mix(1.0, 0.15, sky_night_factor);

	// Cloud sampled at origin's own world XZ (no dir.xz projection) — so
	// a water fragment at world position P reflects the cloud directly
	// above P, which is the SAME cloud that casts its shadow onto P via
	// cloud_shadow_ground. If we included dir.xz projection, camera motion
	// would shift reflected clouds away from their shadow positions
	// because refl depends on view_to_frag.
	vec2 cuv = origin.xz * SKY_CLOUD_GROUND_TILING + cloud_offset;
	float c = texture(cloud_noise_tex, cuv).r;
	float band = max(1e-4, 1.0 - cloud_sharpness);
	float cloud_mask = smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
	cloud_mask = pow(cloud_mask, 2.2);
	float cloud_shade = mix(0.4, 1.2, c);
	vec3 cloud_rgb = cloud_color * cloud_shade * mix(1.0, 0.2, sky_night_factor);
	return mix(base, cloud_rgb, cloud_mask);
}

// Sun + moon sprites only. Returns additive color to add on top of the
// gradient/cloud pass. Keyed off sky_sun_dir (sun's actual direction,
// always) — moon is the anti-sun on the celestial sphere. Sprite mapping:
// project `dir` onto the tangent plane perpendicular to the body, normalize
// by sprite angular radius, sample the texture.
//
// The sky dome passes its real view dir; the water shader passes a
// virtual wide-FOV dir so the sun sprite reads as a prominent arc across
// the reflection instead of a narrow vanishing sliver.
vec3 sample_sky_celestial(vec3 dir, float t)
{
	dir = normalize(dir);
	vec3 to_sun = -normalize(sky_sun_dir);
	vec3 to_moon = -to_sun;
	float sd = max(dot(dir, to_sun), 0.0);
	float md = max(dot(dir, to_moon), 0.0);
	float sun_cos_thresh = cos(radians(sun_angular_size_deg));
	float moon_cos_thresh = cos(radians(moon_angular_size_deg));
	float sun_up = smoothstep(-0.05, 0.05, to_sun.y);
	float moon_up = smoothstep(-0.05, 0.05, to_moon.y);

	vec4 sun_px = vec4(0.0);
	vec4 moon_px = vec4(0.0);
	if (sd > sun_cos_thresh) {
		vec3 sun_ref = abs(to_sun.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
		vec3 sun_tx = normalize(cross(sun_ref, to_sun));
		vec3 sun_ty = cross(to_sun, sun_tx);
		vec3 dir_tan = dir - to_sun * sd;
		float u = dot(dir_tan, sun_tx);
		float v = dot(dir_tan, sun_ty);
		float sun_tan_max = sqrt(max(1.0 - sun_cos_thresh * sun_cos_thresh, 1e-6));
		vec2 sun_uv = vec2(u, v) / sun_tan_max * 0.5 + 0.5;
		sun_px = texture(sun_texture, sun_uv);
	}
	if (md > moon_cos_thresh) {
		vec3 moon_ref = abs(to_moon.y) > 0.99 ? vec3(0.0, 0.0, 1.0) : vec3(0.0, 1.0, 0.0);
		vec3 moon_tx = normalize(cross(moon_ref, to_moon));
		vec3 moon_ty = cross(to_moon, moon_tx);
		vec3 dir_tan = dir - to_moon * md;
		float u = dot(dir_tan, moon_tx);
		float v = dot(dir_tan, moon_ty);
		float moon_tan_max = sqrt(max(1.0 - moon_cos_thresh * moon_cos_thresh, 1e-6));
		vec2 moon_uv = vec2(u, v) / moon_tan_max * 0.5 + 0.5;
		moon_px = texture(moon_texture, moon_uv);
	}

	if (sky_debug_sun_disk) {
		// Force sun sprite area to pure magenta for verification.
		return vec3(1.0, 0.0, 1.0) * sun_px.a;
	}
	vec3 result = vec3(0.0);
	result += sun_color * sun_px.rgb * sun_px.a * sun_disk_intensity * sun_up;
	result += moon_color * moon_px.rgb * moon_px.a * moon_disk_intensity * moon_up;
	return result;
}

// Full sky = gradient + clouds + celestial. Used by the sky dome; the
// water shader calls the two halves separately with different directions
// so it can use virtual FOV for celestial only.
vec3 sample_sky(vec3 origin, vec3 dir, float t)
{
	return sample_sky_clouds(origin, dir, t) + sample_sky_celestial(dir, t);
}

