// Shared sky evaluation — used by shaders/sky.gdshader to render the
// background radiance and by shaders/voxel_water.gdshader as the SSR miss
// fallback. Keeping one formula in both places avoids drift between the
// on-screen sky and the reflection.
//
// Direction convention: `dir` is the view ray from the surface outward
// (toward the sky). sun_world_dir is the direction light travels (from
// WorldState.ShadowLightDirection), so the vector from a point toward the
// sun is -sun_world_dir.

global uniform vec3 sun_world_dir;
global uniform vec3 sun_color;
global uniform vec3 horizon_color;
global uniform vec3 zenith_color;
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_coverage;
global uniform vec3 cloud_color;

float sky_hash(vec2 p)
{
	return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453);
}

float sky_noise(vec2 p)
{
	vec2 i = floor(p);
	vec2 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float a = sky_hash(i);
	float b = sky_hash(i + vec2(1.0, 0.0));
	float c = sky_hash(i + vec2(0.0, 1.0));
	float d = sky_hash(i + vec2(1.0, 1.0));
	return mix(mix(a, b, f.x), mix(c, d, f.x), f.y);
}

float sky_fbm(vec2 p)
{
	float v = 0.0;
	float a = 0.5;
	for (int i = 0; i < 4; i++)
	{
		v += a * sky_noise(p);
		p *= 2.0;
		a *= 0.5;
	}
	return v;
}

vec3 sample_sky(vec3 dir, float t)
{
	dir = normalize(dir);

	// Gradient: horizon at dir.y=0, zenith at dir.y=1. Below horizon mirrors
	// the horizon color (water reflections rarely point down, but guard it).
	float up = clamp(dir.y, 0.0, 1.0);
	vec3 base = mix(horizon_color, zenith_color, pow(up, 0.6));

	// Sun disk + soft glow. Vector from surface toward the sun is
	// -sun_world_dir; cos(angle) = dir . (-sun_world_dir).
	vec3 to_sun = -normalize(sun_world_dir);
	float sd = max(dot(dir, to_sun), 0.0);
	float disk = smoothstep(0.9985, 0.9995, sd);
	float glow = pow(sd, 48.0) * 0.6;
	base += sun_color * (disk + glow);

	// Clouds projected onto the xz plane, divided by dir.y to stretch toward
	// the horizon. Scrolled by cloud_scroll * time. The cloud_band factor
	// sits the layer in the lower half of the sky so the zenith stays open.
	vec2 cuv = (dir.xz / max(abs(dir.y), 0.15)) * cloud_scale + cloud_scroll * t;
	float c = sky_fbm(cuv);
	float cloud_mask = smoothstep(1.0 - cloud_coverage, 1.0 - cloud_coverage * 0.5, c);
	float cloud_band = smoothstep(0.0, 0.35, up) * (1.0 - smoothstep(0.4, 0.95, up));
	base = mix(base, cloud_color, cloud_mask * cloud_band);

	return base;
}
