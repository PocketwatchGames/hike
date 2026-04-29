shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;
// Mark every pixel where the water surface draws so the prop reflection
// shader (sprite_prop_reflection_multimesh.gdshader) can render only
// inside that mask. Reference value 4 — distinct from voxel_backface (1)
// and voxel_water_backface (2). Discarded fragments (camera_clip cutaway)
// don't write the stencil, so reflections are correctly suppressed there.
stencil_mode write, compare_always, 4;

global uniform float camera_clip;
global uniform int clip_debug_mode;
// When true, the water shader discards every fragment. Lets you see the
// terrain underneath without water tinting / depth fade in the way.
global uniform bool water_hide;

// filter_nearest on both depth + screen textures: linear filtering blends
// player+seabed values at silhouette edges, making the depth-aware check
// ambiguous (sometimes passing for half-player-half-seabed pixels) and
// producing player-colored speckle in the screen sample. Nearest filtering
// keeps each sample bound to a single source pixel — refraction wobble
// stays sharp at object edges instead of bleeding their colors into water.
uniform sampler2D depth_tex : hint_depth_texture, filter_nearest_mipmap;
uniform sampler2D screen_tex : hint_screen_texture, filter_nearest_mipmap;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
// sky_common.gdshaderinc owns: sun_world_dir, sun_color, horizon_color,
// zenith_color, cloud_offset, cloud_scale, cloud_threshold,
// cloud_sharpness, cloud_color. Including it here makes the SSR miss
// fallback sample the SAME sky the sky dome shows — so reflections carry
// clouds + sun disk + horizon gradient, not a flat gradient.
#include "res://shaders/sky_common.gdshaderinc"
// Cloud shadow include needs sun_world_dir and the cloud_* uniforms (all
// declared above by sky_common) plus cloud_shadow_strength.
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

// Per-region water styling — all globals driven by RegionData.WaterColor
// through WeatherDerivation + SkyController.Apply. Authoring one RGBA per
// region controls muddiness, depth color, foam tint, ripple/wave behavior.
global uniform float water_alpha_min;
global uniform float water_alpha_max;
global uniform float water_depth_scale;
global uniform float water_turbidity_exp;
global uniform float water_muddiness;
global uniform vec3 water_shallow_tint : source_color;
global uniform vec3 water_deep_tint : source_color;

// Ripple / reflection tuning — registered as globals by SkyController so
// inspector tweaks propagate live to every water material at once.
global uniform float ripple_scale_a;
global uniform float ripple_scale_b;
global uniform vec2 ripple_offset_a;
global uniform vec2 ripple_offset_b;
global uniform float ripple_strength;
// Pixels-per-world-unit noise UV quantization. Snaps the ripple samples
// to a discrete grid so the surface reads as hand-drawn pixel ripples
// rather than a smoothly scrolling noise pattern.
global uniform float ripple_pixel_size;
global uniform float fresnel_power;
global uniform float reflection_strength;
// Floor on the reflection blend amount independent of fresnel. Physical
// fresnel at ~35° view angle gives ~5% reflection which reads as "no
// reflection at all" under ortho iso cameras — this raises the minimum
// so reflections stay visible at any view angle. Muddiness + light level
// modulate this in SkyController so dim/murky scenes still dim correctly.
global uniform float reflection_min;
// Fake reflection FOV — remaps each water fragment's screen UV into a
// virtual sky direction using a wide artificial FOV. Under ortho iso
// cameras the true reflection direction is nearly parallel across all
// fragments, so sun/clouds/sky features occupy a tiny sliver. The fake
// FOV spreads the sky across the water coherently — sun, moon, clouds,
// horizon all appear at consistent positions relative to each other.
global uniform float reflection_fov_h_deg;
global uniform float reflection_fov_v_deg;
global uniform float reflection_fov_v_center;
// SSR removed — screen-space reflection cannot produce flipped reflections
// of upright objects (it samples the un-flipped framebuffer at the projected
// reflection-ray UV, which IS the upright sprite's screen position). Each
// reflectable Sprite3D now spawns a flipped child sprite mirrored across the
// water plane (see scripts/gameplay/LitSprite.cs#UpdateReflection); the
// water shader composites those naturally as underwater geometry visible
// through the translucent surface. Reflections in this shader are now sky
// only (clouds, sun, moon, stars, horizon gradient).
// Large-scale vertex undulation (top face only). Amplitude is already wind-
// scaled and muddy-damped in SkyController; shader just applies it.
global uniform float water_wave_amp;
global uniform float water_wave_length;
// Spatial scale for the intermittent wave envelope — a slow noise field
// that multiplies wave amplitude so there are patches of calm and active
// water instead of uniform surface motion.
global uniform float water_wave_gate_scale;
// Wave temporal phase, integrated each frame in C# at a wind-scaled rate
// (windSpeed * waveSpeedPerMps). Replaces a fixed `time * 1.2` so calm
// water freezes and stormy water pumps the surface fast.
global uniform float water_wave_phase;
// Two normal-map textures for ripple perturbation. Each texel's RGB
// encodes a tangent-space normal via Godot's NoiseTexture2D as_normal_map
// bake — the output stores (nx, nz, ny) in [0,1] with ny biased upward.
// Sampled with filter_linear so ripples read smooth between texels, at
// different world scales (A, B) to break tiling, then combined and
// slerped toward +Y by ripple_strength.
uniform sampler2D ripple_tex_a : filter_linear, repeat_enable, hint_normal;
uniform sampler2D ripple_tex_b : filter_linear, repeat_enable, hint_normal;
// Dynamic ripple impacts emitted by entities moving through water.
// Width=MaxDynamicRipples × height=1 Rgbaf row populated by
// SkyController.TickDynamicRipples each frame. Per-pixel layout is
// (world_x, world_z, age_seconds, strength). Inactive slots beyond
// water_ripple_count are skipped via the loop's bounds check.
global uniform sampler2D water_ripple_tex : filter_nearest;
global uniform int water_ripple_count;
// Ring expansion speed in world units / second. Combined with age this
// gives the current ring radius for each impact.
global uniform float water_ripple_speed;
// Total active lifetime per impact. Beyond this the C# tick drops the
// entry; the shader applies a linear age fade so the wave dies smoothly.
global uniform float water_ripple_lifetime;
// Fade-in window in seconds. At age=0 the ring radius is 0 and the
// Gaussian sits entirely at the impact origin — a huge central pulse.
// Ramping amplitude from 0 → full over this window lets the ring
// propagate outward before becoming visible, so impacts read as
// expanding rings rather than instantaneous stamps.
global uniform float water_ripple_fade_in;
// Gaussian envelope tightness around the moving ring crest. Higher =
// thinner ring (rapid falloff with distance from crest); lower = wider,
// softer ring. Drives the visible ring's apparent thickness in meters.
global uniform float water_ripple_falloff;
// Overall amplitude of the radial perturbation applied to the water
// normal. 0 disables the effect entirely without skipping the loop.
global uniform float water_ripple_tilt;
// Screen-space refraction strength (UV offset in normalized units). 0 skips
// the extra screen sample entirely. Muddy water damps to near zero.
global uniform float water_refraction_strength;
// Minimum surface alpha at the water's edge (thickness → 0). Clamps the
// authored WaterColor.a from below so clean water still reads as visible
// color along the shoreline instead of fading to full transparency.
global uniform float water_edge_opacity;
// Contiguous shoreline rim — a solid band of foam-colored pixels right at
// the water/land boundary, on top of the noisy shoreline foam. _width is
// world-units from the edge; _strength is how strong the rim reads.
global uniform float water_rim_width;
global uniform float water_rim_strength;
// --- Debug ---------------------------------------------------------------
// Reflection-path visualizer. See CVars.cs `water_debug` for mode docs.
global uniform int water_debug_mode;
// Force the shading normal flat — bypasses ripple texture perturbation.
global uniform bool water_disable_ripples;
// Max mip LOD bias applied to the reflected starfield at full ripple
// strength. 0 = stars stay sharp on rippled water (aliases/flickers). Higher
// values trade detail for stability as ripples scatter neighboring
// fragments across the star panorama. Flat water always samples mip 0.
global uniform float star_ripple_blur_lod;
// Wind direction (normalized; y unused). Used for wave direction.
global uniform vec3 wind_dir;
// Coarse wind-factor 3D texture — 1.0 = full ambient wind (open sky),
// 0.0 = sealed (deep cave / building interior). Damps wave amplitude,
// ripple/streak tilt, shoreline noise foam, and whitecap output so a
// stormy lake reads as glassy still water once the player walks under
// a cave roof. Same `(world_pos - origin) * inv_size` UVW convention as
// light_map; texel resolution is 1/4 of voxel resolution so the gradient
// at a cave mouth blends across roughly 4 world units.
global uniform sampler3D wind_map : filter_linear;
global uniform vec3 wind_map_origin;
global uniform vec3 wind_map_inv_size;
// Shoreline foam — lighter noise band where water thickness falls below
// `foam_depth`, so shallow edges of water bodies read as surf.
global uniform vec3 foam_color;
global uniform float foam_depth;
global uniform float foam_scale;
// World-XZ scroll offset for the shoreline foam noise UVs. Integrated in
// C# as `foamScroll * windSpeed * dt` so the pattern only drifts when wind
// is driving wave energy at shore (and stops when wind dies).
global uniform vec2 foam_offset;
global uniform float foam_strength;
global uniform float foam_threshold;
global uniform float foam_sharpness;
// Lower bound on foam lighting — prevents foam from collapsing to black
// in dim scenes while letting overcast / foggy / nighttime weather pull
// it down to "dim white" instead of "emissive white". SkyController
// computes this per-frame from clarity × lightLevel so a clear sunny
// day keeps foam crisp and a heavy overcast night dims it accordingly.
global uniform float foam_min_light;

// Wave streaks — a second layer of scrolling noise (similar to ripples but
// at coarser scales and slower drift) that compounds with the base ripple
// normal AND seeds foam where noise crosses a threshold. Two octaves with
// independent scales + drift speeds (along wind), gradient sampled via
// finite differences for normal perturbation. Foam threshold isolates the
// brightest noise peaks as crest "whitecaps" — visible at night via a
// higher light floor than shoreline foam.
global uniform float wave_streak_strength;
global uniform float wave_streak_scale_a;
global uniform float wave_streak_scale_b;
// Wave-streak scroll offsets, integrated in SkyController each frame
// (offset -= windXZ * speed * dt). Same pattern as ripple_offset_a/b —
// keeps the noise pattern locked to world-XZ via `world_xz * scale +
// offset`, with the offset advancing over time but never depending on
// camera position. Using shader TIME * speed directly here was less
// reliable across chunk boundaries.
global uniform vec2 wave_streak_offset_a;
global uniform vec2 wave_streak_offset_b;

// Whitecap foam — emitted wherever the FINAL composed surface normal is
// sufficiently sideways, regardless of WHICH ripple layer (base ripples
// or wave-streak overlay) produced the tilt. Threshold is sin² of the
// tilt angle; strength multiplies the resulting foam mask.
global uniform float whitecap_foam_strength;
global uniform float whitecap_foam_threshold;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
// Sampled in fragment() at world_vertex; the returned factor multiplies
// the block_lit term in the water composite so a sprite standing above
// dims the surface here without affecting open water elsewhere.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

// Underwater caustics — fake focused-sunlight bands projected onto the seabed
// reconstructed from the depth buffer. Strength=0 skips the sample entirely.
// Pattern is procedural (two scrolling noises, their absolute difference
// giving thin ribbon edges) so no extra texture asset is needed. Masked by
// direct sun visibility and attenuated by water thickness so deep water and
// shaded shores don't light up.
global uniform float caustic_strength;
global uniform float caustic_scale;
global uniform float caustic_speed;
global uniform float caustic_sharpness;
global uniform float caustic_depth_fade;
global uniform vec3 caustic_color : source_color;
// World-space scroll offset for the caustic noise. Integrated in C# as
// `wind_dir * windSpeed * causticDriftPerMps * dt` (same convention as
// foam_offset / ripple_offset). The shader subtracts this from the
// seabed world position before sampling so the field translates along
// wind in world units — calm = frozen, storm = drifting fast. Layer 2
// scrolls at a fraction of layer 1's offset so the abs-difference
// ribbons keep evolving as the field translates rather than just rigidly
// sliding past.
global uniform vec3 caustic_offset;

const float MIN_AMBIENT = 0.0;

// Trilinear wind-factor sample at a world-space point. Texture sampler
// clamps to edge so out-of-bounds positions read the boundary cell —
// fine for finite worlds where water never sits beyond the map extent.
float sample_wind_factor(vec3 world_pos) {
	vec3 uvw = (world_pos - wind_map_origin) * wind_map_inv_size;
	return texture(wind_map, uvw).r;
}

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

// Procedural caustic intensity at a 3D world position. Two value-noise
// octaves are sampled and abs-differenced to produce thin ribbon edges;
// pow() sharpens them into crisp focused bands.
//
// Sampling in full 3D (world_pos.xyz, not just XZ) means slopes and
// walls get unique noise values across their surface — a vertical wall
// gets variation along Y rather than the long stretched ribbons that
// XZ-only sampling produced when Y varied but XZ didn't.
//
// Wind-driven evolution: layer 1 is permanently anchored to world XZ;
// layer 2 is offset by caustic_offset (set per-frame in C# from wind
// direction × speed). As layer 2 slides past stationary layer 1, the
// abs-difference iso-contours where n1 ≈ n2 morph in place — the
// pattern doesn't translate as a whole, it CHANGES. That matches the
// real perceptual impression of windy caustics ("things are happening
// faster") rather than "the field is drifting." Calm wind → both
// layers frozen → pattern still. Range ~[0,1].
float caustic_pattern(vec3 world_pos)
{
	vec3 p1 = world_pos * caustic_scale;
	vec3 p2 = (world_pos + caustic_offset) * caustic_scale * 1.73 + vec3(5.2, 1.3, 7.7);
	float n1 = vnoise(p1);
	float n2 = vnoise(p2);
	float c = 1.0 - abs(n1 - n2) * 2.0;
	return pow(clamp(c, 0.0, 1.0), max(caustic_sharpness, 0.01));
}

// Quantize noise UVs to a pixel grid. Used for the shoreline foam mask
// (procedural vnoise); the ripple height sampled from water_noise_tex
// already gets its pixel feel from filter_nearest.
vec2 pixelize(vec2 uv) {
	float px = max(ripple_pixel_size, 0.01);
	return floor(uv * px) / px;
}

// Decode a stored normal map texel: Godot's as_normal_map produces
// (nx, nz, ny) in [0,1]. We remap to [-1,1], swizzle to (nx, ny, nz),
// and leave normalization to the caller (averages don't need to be
// pre-normalized).
vec3 decode_normal_map(sampler2D tex, vec2 uv) {
	vec3 c = texture(tex, uv).rgb * 2.0 - 1.0;
	return vec3(c.r, c.b, c.g);
}

// Slerp from `a` to `b` by t. Preserves unit length (unlike a lerp +
// normalize, which produces a shorter intermediate vector for wide
// angles). Falls back to lerp when the two vectors are nearly parallel
// to avoid division by sin(omega≈0).
vec3 slerp_normals(vec3 a, vec3 b, float t) {
	float cos_omega = clamp(dot(a, b), -0.9999, 0.9999);
	float omega = acos(cos_omega);
	if (omega < 1e-3) { return normalize(mix(a, b, t)); }
	float sin_omega = sin(omega);
	return (sin((1.0 - t) * omega) * a + sin(t * omega) * b) / sin_omega;
}

// Billboard a celestial body (sun or moon) onto the water's screen UV.
// Instead of per-fragment angular sampling (which squashes the sprite
// asymmetrically under wide FOV), we compute the body's screen position
// once and draw it as a 2D sprite with fixed pixel radius. Circular
// shape guaranteed regardless of FOV.
//
// Projection: reflect the body's world direction over the water plane
// (+Y), transform to view space, apply the virtual FOV via perspective
// divide, remap to screen UV centered at (0.5, v_center).
//
// Adds a soft additive halo around the crisp disk: out to
// pixel_radius * halo_radius_mult, contributes body_color * intensity *
// halo_intensity with a smooth falloff. Halo has alpha=0 so the disk
// itself is still the only thing that occludes stars; the halo reads as
// blown-out bloom surrounding it.
//
// Returns vec4(premultiplied_rgb, disk_alpha). RGB already includes the
// disk's `tex.rgb * tex.a` and the halo contribution; caller adds the
// RGB directly (no extra `* alpha` step). Alpha is the disk mask only,
// used for star occlusion.
vec4 sample_billboard_body(
	vec2 screen_uv,
	vec2 viewport_size,
	vec3 body_world_dir,   // direction TO the body from origin
	vec3 surface_normal,   // ripple-perturbed water normal at the fragment
	sampler2D body_tex,
	float angular_size_deg,
	float intensity,
	vec3 body_color,
	float halo_radius_mult,
	float halo_intensity,
	mat4 view_matrix
) {
	// Reflect over the ripple-perturbed surface normal — each fragment
	// computes its own body_screen_uv based on its local ripple tilt, so a
	// fragment whose normal happens to bend the reflection toward the body
	// lights up even if it's nowhere near the flat-water disk position.
	// That's what produces shimmer/sparkle on a rippled lit body instead
	// of the disk being a fixed-position decal. A flat-water fragment
	// (n ≈ (0,1,0)) falls back to the previous behavior.
	vec3 refl_world = reflect(body_world_dir, surface_normal);
	vec4 view_dir = view_matrix * vec4(refl_world, 0.0);
	// Camera looks along -Z in view space; view_dir.z < 0 means in front.
	if (view_dir.z >= -1e-4) {
		return vec4(0.0);
	}
	float tan_h = tan(radians(reflection_fov_h_deg) * 0.5);
	float tan_v = tan(radians(reflection_fov_v_deg) * 0.5);
	// Perspective divide in view space (treating camera as a pinhole with
	// the virtual FOV). Output is normalized device coordinate in [-1, 1].
	vec2 ndc = vec2(
		view_dir.x / (-view_dir.z) / tan_h,
		view_dir.y / (-view_dir.z) / tan_v
	);
	// Screen UV: flip Y (SCREEN_UV top-left origin), shift vertical center.
	vec2 body_screen_uv = vec2(
		ndc.x * 0.5 + 0.5,
		-ndc.y * 0.5 + reflection_fov_v_center
	);
	// Sprite pixel radius from angular size + horizontal FOV. Using FOV_h
	// as reference keeps the radius independent of FOV_v so the sprite is
	// always circular in pixels.
	float pixel_radius = 0.5 * (radians(angular_size_deg) / radians(reflection_fov_h_deg)) * viewport_size.x;
	// Convert UV offset to pixel offset so distance is aspect-correct.
	vec2 pixel_offset = (screen_uv - body_screen_uv) * viewport_size;
	float dist = length(pixel_offset);
	float halo_outer = pixel_radius * max(halo_radius_mult, 1.001);
	if (dist > halo_outer) {
		return vec4(0.0);
	}

	vec3 rgb = vec3(0.0);
	float alpha = 0.0;
	if (dist <= pixel_radius) {
		// Crisp disk — sample texture, premultiply by alpha so the caller
		// composites with a plain add. Alpha preserved so the disk still
		// masks the starfield via (1.0 - moon_bb.a).
		vec2 tex_uv = pixel_offset / pixel_radius * 0.5 + 0.5;
		vec4 tex = texture(body_tex, tex_uv);
		rgb = body_color * tex.rgb * intensity * tex.a;
		alpha = tex.a;
	}

	// Halo across the ENTIRE 0..halo_outer region, including inside the
	// disk. Sampling only outside the disk left a dark donut in the gap
	// between the sun texture's soft-alpha edge (which dies before
	// pixel_radius) and the start of the halo — inside that gap the disk
	// branch contributed zero and the halo branch never ran. Contributing
	// everywhere also makes the bright core blown-out core and fades smoothly.
	float halo_t = dist / halo_outer;
	float halo_w = pow(1.0 - clamp(halo_t, 0.0, 1.0), 2.5);
	rgb += body_color * intensity * halo_intensity * halo_w;
	return vec4(rgb, alpha);
}

// Intermittent wave envelope — slow, coarse noise that multiplies wave
// amplitude so the surface has patches of lively motion surrounded by
// calm, instead of uniform chop everywhere.
float wave_gate(vec2 world_xz, float time) {
	vec3 p = vec3(world_xz * water_wave_gate_scale, time * 0.15);
	float n = vnoise(p);
	return mix(0.3, 1.3, smoothstep(0.35, 0.75, n));
}

// Gerstner-ish directional wave for vertex displacement + whitecap crest
// detection. Two sine frequencies produce a naturally uneven crest. The
// `time_phase` argument is the wind-integrated wave phase from C#, not raw
// shader TIME — at zero wind the surface freezes.
float wave_height(vec2 world_xz, vec2 dir, float time_phase) {
	float k = 6.2831853 / max(water_wave_length, 0.1);
	float phase = dot(world_xz, dir) * k + time_phase;
	float w = sin(phase) * 0.7 + sin(phase * 1.7 + 1.3) * 0.3;
	return w;
}

// Combine two pre-baked normal-map textures and slerp toward +Y by
// ripple_strength. At ripple_strength=0 the normal is exactly +Y (mirror
// water); at ripple_strength=1 it's the raw combined texture normal.
// Each texture is sampled at its own scale (ripple_scale_a/b) with its
// own integrated wind offset — scrolling stays locked to the world.
vec3 ripple_normal(vec2 world_xz, float time) {
	vec2 uv_a = world_xz * ripple_scale_a + ripple_offset_a;
	vec2 uv_b = world_xz * ripple_scale_b + ripple_offset_b;
	vec3 na = decode_normal_map(ripple_tex_a, uv_a);
	vec3 nb = decode_normal_map(ripple_tex_b, uv_b);
	vec3 combined = normalize(na + nb);
	return slerp_normals(vec3(0.0, 1.0, 0.0), combined, ripple_strength);
}

// Dynamic-ripple perturbation in world XZ. Each active ripple is a single
// expanding ring: a Gaussian bump in surface height centered at the ring
// radius (= age × speed) and falling off with `falloff` on either side.
// Returning the radial slope of that height field gives the normal tilt:
//   d/d(dr)[exp(-falloff·dr²)] = -2·falloff·dr·exp(-falloff·dr²)
// The slope is positive on the inside of the ring (dr < 0) and negative
// on the outside, producing a single propagating ring of normal
// perturbation — no inner oscillations.
//
// Fade-in over `water_ripple_fade_in` seconds prevents the age=0
// "central pulse" pop. Linear fade-out across the rest of lifetime
// avoids a hard cutoff at expiry.
vec2 dynamic_ripple_perturb(vec2 world_xz) {
	vec2 tilt = vec2(0.0);
	if (water_ripple_tilt < 1e-4 || water_ripple_count <= 0) {
		return tilt;
	}
	float lifetime = max(water_ripple_lifetime, 1e-3);
	for (int i = 0; i < water_ripple_count; i++) {
		vec4 r = texelFetch(water_ripple_tex, ivec2(i, 0), 0);
		vec2 to = world_xz - r.xy;
		float d = length(to);
		if (d < 1e-3) { continue; }
		float age = r.z;
		float strength = r.w;
		float radius = age * water_ripple_speed;
		float dr = d - radius;
		float envelope = exp(-dr * dr * water_ripple_falloff);
		float slope = -2.0 * water_ripple_falloff * dr * envelope;
		float fade_in = clamp(age / max(water_ripple_fade_in, 1e-3), 0.0, 1.0);
		float fade_out = clamp(1.0 - age / lifetime, 0.0, 1.0);
		float fade = fade_in * fade_out;
		float contrib = slope * fade * strength * water_ripple_tilt;
		tilt -= (to / d) * contrib;
	}
	return tilt;
}

// Wave streak field — two octaves of procedural noise scrolled along wind
// at independent speeds. Returns the (tilt_x, tilt_z) horizontal-normal
// perturbation that the fragment shader composites with the base ripple
// normal. Foam is NOT computed here — it lives downstream off the FINAL
// composed normal's tilt magnitude (where the surface is sufficiently
// sideways = wave is breaking = foam appears), so it responds to the
// combined ripple + streak agitation rather than just the streak field.
vec2 wave_streak(vec2 world_xz) {
	// Threshold raised from 1e-4 to 1e-3 — at strengths in (1e-4, 1e-3) the
	// 6 vnoise samples below produce a tilt below 1 pixel of perturbation,
	// invisible against the ripple normal but still paying full cost.
	if (wave_streak_strength <= 1e-3) {
		return vec2(0.0);
	}
	vec2 uv_a = world_xz * wave_streak_scale_a + wave_streak_offset_a;
	vec2 uv_b = world_xz * wave_streak_scale_b + wave_streak_offset_b;

	float n_a = vnoise(vec3(uv_a, 0.0));
	float n_b = vnoise(vec3(uv_b, 0.0));
	float n_streak = (n_a + n_b) * 0.5;

	// Finite-difference gradient → tilt direction.
	float eps = 0.35;
	float n_x = (vnoise(vec3(uv_a + vec2(eps, 0.0), 0.0)) +
	             vnoise(vec3(uv_b + vec2(eps, 0.0), 0.0))) * 0.5 - n_streak;
	float n_z = (vnoise(vec3(uv_a + vec2(0.0, eps), 0.0)) +
	             vnoise(vec3(uv_b + vec2(0.0, eps), 0.0))) * 0.5 - n_streak;

	return vec2(-n_x * wave_streak_strength * 4.0,
	            -n_z * wave_streak_strength * 4.0);
}

varying vec3 world_vertex;
varying vec3 world_normal;

void vertex() {
	vec3 v = VERTEX;
	// Top-face vertex undulation. Displaces Y DOWN ONLY — waves dip the
	// surface below the resting waterline but never rise above it, so a
	// wave crest can't poke up through an adjacent land voxel sitting
	// flush with the water's top. The mesher's TOP_EPSILON (scripts/
	// voxels/WaterMesher.cs) already drops the top face by 0.02 world
	// units to avoid z-fighting; this keeps that epsilon as the hard
	// upper bound for any surface point. Chunk meshes are axis-aligned
	// so local Y == world Y.
	if (NORMAL.y > 0.5 && water_wave_amp > 1e-5) {
		vec3 wv = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		vec2 dir = normalize(vec2(wind_dir.x, wind_dir.z) + vec2(1e-4, 0.0));
		float gate = wave_gate(wv.xz, TIME);
		float h = min(wave_height(wv.xz, dir, water_wave_phase) * gate, 0.0);
		// Damp displacement by local wind: surface goes glassy under cave
		// roofs / building interiors. Trilinear filter on the coarse
		// wind_map gives a smooth shore-to-deep-cave gradient.
		float wind_factor = sample_wind_factor(wv);
		v.y += h * water_wave_amp * wind_factor;
	}
	VERTEX = v;
	world_vertex = (MODEL_MATRIX * vec4(v, 1.0)).xyz;
	world_normal = NORMAL;
}

void fragment() {
	if (water_hide) {
		discard;
	}
	// Debug mode 8: skip the camera_clip discard and paint each fragment
	// based on whether it would have been clipped. RED = above clip (this
	// fragment SHOULD have been discarded), GREEN = below clip (legitimate).
	// Lets us see if the water front face is rendering above the clip line
	// (which it should not if camera_clip is being set correctly on this
	// material) — and if so, whether `world_vertex.y` or `camera_clip` is
	// the wrong value.
	// Debug mode 9: paint world_vertex.y as a grayscale ramp so we can see
	// the actual world Y at each fragment.
	// Debug mode 10: paint camera_clip as a grayscale ramp (uniform color
	// across the screen since it's a global). Tells us what value the
	// shader is actually reading.
	if (clip_debug_mode == 8) {
		if (world_vertex.y > camera_clip) {
			ALBEDO = vec3(1.0, 0.0, 0.0);
		} else {
			ALBEDO = vec3(0.0, 1.0, 0.0);
		}
		ALPHA = 1.0;
	} else if (clip_debug_mode == 9) {
		ALBEDO = vec3(fract(world_vertex.y / 16.0));
		ALPHA = 1.0;
	} else if (clip_debug_mode == 10) {
		ALBEDO = vec3(fract(camera_clip / 16.0));
		ALPHA = 1.0;
	} else if (clip_debug_mode == 11) {
		// Face-type visualizer. Tells us WHICH water-mesh face is bleeding
		// through after the top-face cull fix. CYAN = top, MAGENTA = bottom,
		// YELLOW = side. If we see yellow over the artifact, the side faces
		// of the sealed pocket are still emitting and should be culled the
		// same way the top face was.
		if (world_vertex.y > camera_clip) {
			discard;
		}
		if (world_normal.y > 0.5) {
			ALBEDO = vec3(0.0, 1.0, 1.0);
		} else if (world_normal.y < -0.5) {
			ALBEDO = vec3(1.0, 0.0, 1.0);
		} else {
			ALBEDO = vec3(1.0, 1.0, 0.0);
		}
		ALPHA = 1.0;
	} else if (world_vertex.y > camera_clip) {
		discard;
	} else
	// Debug mode 7: paint actual water front-face surfaces yellow so you
	// can distinguish them from the water cap (mode 1, magenta). Wraps the
	// rest of the fragment body since `return` isn't allowed in fragment().
	if (clip_debug_mode == 7) {
		ALBEDO = vec3(1.0, 1.0, 0.0);
		ALPHA = 1.0;
	} else {

	// Only the top face of a water voxel (normal.y ~= +1) represents an
	// open surface. Sides/bottom are submerged rims seen through adjacent
	// transparent cells — leave them flat and opaque.
	bool is_top = world_normal.y > 0.5;
	// Local wind factor — used to flatten ripples/streaks/whitecaps and
	// damp shoreline foam under cave roofs and indoors. Side faces don't
	// pay for the sample since they skip all the wind-driven paths below.
	float wind_factor = is_top ? sample_wind_factor(world_vertex) : 1.0;
	// Skip the two normal-map samples + slerp on calm water — ripple_strength
	// is already quadratic in wind speed (WeatherDerivation), so at low wind /
	// no rain it's effectively zero but the texture work still costs the same.
	// Also early-out when wind_factor is near zero (deep cave / sealed room) —
	// the slerp at the end of the perturbation block flattens the result anyway,
	// so the ripple texture work would be discarded.
	bool ripples_active = is_top && !water_disable_ripples && ripple_strength > 0.01 && wind_factor > 0.01;
	vec3 n = ripples_active ? ripple_normal(world_vertex.xz, TIME) : (is_top ? vec3(0.0, 1.0, 0.0) : world_normal);

	// Wave streaks: a coarser scrolling-noise layer that adds tilt onto
	// the base ripple normal — conceptually just "ripple layer 2" with its
	// own scale/speed/weight. The streak path does NOT compute foam; foam
	// fires below from the FINAL composite tilt regardless of which layer
	// drove it.
	if (is_top && !water_disable_ripples && wave_streak_strength > 1e-3 && wind_factor > 0.01) {
		vec2 streak_tilt = wave_streak(world_vertex.xz) * wind_factor;
		n = normalize(n + vec3(streak_tilt.x, 0.0, streak_tilt.y));
	}
	// Final flatten: slerp the perturbed normal back toward +Y by the
	// inverse of wind_factor so a cave-mouth gradient takes the surface
	// from rippled to mirror-flat across the trilinear footprint. Damps
	// any residual ripple/streak tilt for partial wind values (e.g. 0.4
	// at cave mouths) — multiplying inputs alone leaves a tiny tilt that
	// reads as "windless rippled water" once you walk inside.
	if (is_top) {
		n = slerp_normals(vec3(0.0, 1.0, 0.0), n, wind_factor);
	}

	// Dynamic ripples (entity impacts) — composited AFTER the wind_factor
	// flatten so footstep / wake ripples remain visible on cave pools and
	// indoor still water where the wind-driven base ripples have been
	// damped to mirror-flat.
	if (is_top && !water_disable_ripples) {
		vec2 dyn_tilt = dynamic_ripple_perturb(world_vertex.xz);
		n = normalize(n + vec3(dyn_tilt.x, 0.0, dyn_tilt.y));
	}

	// Whitecap foam: emitted on the WINDWARD-FACING tilt component of the
	// final composite normal. The dot product of n.xz with the wind
	// direction is positive when the normal aligns with the direction the
	// wind is blowing (the "front face" of a wind-driven wave, which is
	// where real whitecaps trail), and negative on the back face. We take
	// max(0, dot) and square it so foam fires on the front of each crest
	// only — produces directional whitecaps that visually indicate wind
	// direction and align cleanly with the wind-aligned wave streaks.
	//
	// Threshold operates on this `front_sq` value (range [0, ~tilt_sq])
	// with a proportional smoothstep tail so it behaves as a true cutoff
	// at any scale.
	float whitecap_foam_amt = 0.0;
	if (is_top && !water_disable_ripples) {
		vec2 wxz = vec2(wind_dir.x, wind_dir.z);
		float wind_len = length(wxz);
		vec2 wxz_norm = wind_len > 1e-3 ? wxz / wind_len : vec2(1.0, 0.0);
		float front = max(0.0, dot(vec2(n.x, n.z), wxz_norm));
		float front_sq = front * front;
		float t_end = whitecap_foam_threshold + max(whitecap_foam_threshold * 0.5, 0.0005);
		float crest_mask = smoothstep(whitecap_foam_threshold, t_end, front_sq);
		// Ripple-scale noise speckle: sampled at the same world-XZ +
		// scrolling offsets that drive the ripple normal map, so foam
		// flecks break up in lockstep with the ripples beneath. Two
		// octaves keep the speckle from looking too uniform.
		vec2 fnu_a = world_vertex.xz * ripple_scale_a + ripple_offset_a;
		vec2 fnu_b = world_vertex.xz * ripple_scale_b + ripple_offset_b;
		float speckle = (vnoise(vec3(fnu_a, 0.0)) +
		                 vnoise(vec3(fnu_b, 0.0))) * 0.5;
		float speckle_mask = smoothstep(0.4, 0.75, speckle);
		whitecap_foam_amt = clamp(crest_mask * speckle_mask * whitecap_foam_strength * wind_factor, 0.0, 1.0);
	}

	// Face-direction shading (from the perturbed normal so highlight
	// direction shifts subtly with ripples).
	vec3 an = abs(n);
	float face_shade;
	if (an.y > 0.5) {
		face_shade = n.y > 0.0 ? 1.0 : 0.5;
	} else if (an.z > 0.5) {
		face_shade = 0.8;
	} else {
		face_shade = 0.6;
	}

	// Compute water thickness from depth buffer
	float scene_depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 scene_ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth * 2.0 - 1.0, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_ndc;
	scene_view /= scene_view.w;

	vec4 frag_ndc = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z * 2.0 - 1.0, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_ndc;
	frag_view /= frag_view.w;

	float thickness = abs(scene_view.z - frag_view.z);
	float depth_factor_raw = clamp(thickness / water_depth_scale, 0.0, 1.0);
	// Smooth + turbidity-curved depth factor. Clear water (exp > 1) stays
	// translucent through many voxels of depth; muddy water (exp < 1)
	// rushes toward opaque within a voxel or two. Smoothstep softens the
	// near-edge transition so shoreline opacity fades gradually instead
	// of ramping linearly from the very first fragment.
	float depth_factor = pow(smoothstep(0.0, 1.0, depth_factor_raw), water_turbidity_exp);

	// Foam mask + water_alpha hoisted above the lighting block: the rippled
	// normal's contribution to direct sunlight is gated by surface opacity
	// (transparent water → no normal effect, opaque/foamy water → full N·L
	// glints), so we need water_alpha before computing sun_lit. Foam color
	// composition still happens later, after `light` and `water_col` exist.
	float foam_amount = 0.0;
	if (is_top && (foam_strength > 1e-3 || water_rim_strength > 1e-3 || whitecap_foam_amt > 1e-3)) {
		// Contiguous rim band: hard cutoff by thickness with no noise masking,
		// so the very edge of every pool is a solid foam-colored line. Draws
		// underneath the noisy foam so the two blend together at the shore.
		float rim = (1.0 - smoothstep(0.0, max(water_rim_width, 1e-4), thickness)) * water_rim_strength;

		// Noisy shoreline foam: scrolling noise band out to foam_depth. Same
		// foam color, but masked by pixelized noise so it reads as surf
		// texture rather than a solid line.
		float shallow = 1.0 - smoothstep(0.0, foam_depth, thickness);
		vec2 fuv = pixelize(world_vertex.xz * foam_scale + foam_offset);
		float fn = vnoise(vec3(fuv, 0.0));
		float band = max(1e-4, 1.0 - foam_sharpness);
		float mask = smoothstep(foam_threshold, min(foam_threshold + band, 1.0), fn);
		// Shoreline noise foam represents wave energy hitting the shore;
		// damp by wind_factor so cave-pool shores read as still water
		// against the rock. The contiguous `rim` band stays unmodulated —
		// it's a structural water/land edge cue, not weather-driven.
		float noisy_foam = shallow * mask * foam_strength * wind_factor;
		foam_amount = max(max(noisy_foam, rim), whitecap_foam_amt);
	}
	// Edge-opacity floor + depth ramp + foam union — same formula as the
	// final compositing alpha; computed once here and reused below.
	float effective_edge_alpha = max(water_alpha_min, water_edge_opacity);
	float water_alpha = max(mix(effective_edge_alpha, water_alpha_max, depth_factor), foam_amount);

	// Sample lightmap per-pixel for smooth interpolation across the face.
	// Water samples from itself: push 0.5 along -world_normal (the flat
	// face normal — not the ripple-perturbed one) to land inside the cell.
	vec3 light_sample_pos = world_vertex - world_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Water is `unshaded` (no light() stage / no ATTENUATION available), so
	// cloud shadow multiplies sun_lit directly. Block light (lm.gba) stays
	// unaffected — torches shining on water don't care about clouds.
	float cloud = cloud_shadow_ground(world_vertex, TIME);
	float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
	// Rippled-normal lighting modifier, weighted by water_alpha. Transparent
	// water reads as "we're seeing through to the seabed" — its surface
	// orientation should not visibly redirect direct sunlight, because the
	// fragment's apparent color is dominated by what's behind it. Opaque
	// water (deep, muddy) and foam-covered fragments DO have a real surface
	// receiving sunlight, so a ripple tilting the surface toward/away from
	// the sun should brighten/dim it. Ratio is normalized to 1.0 at flat
	// water (n = +Y) so flat surfaces preserve current intensity exactly.
	// Block light (`lm.gba`) is omnidirectional in the lightmap — there's
	// no direction to dot against — so it's left flat.
	vec3 to_sun_dir_lit = -normalize(sky_sun_dir);
	float flat_dot_sun = max(0.0, to_sun_dir_lit.y);
	float ripple_dot_sun = max(0.0, dot(n, to_sun_dir_lit));
	float ripple_light_factor = mix(1.0, ripple_dot_sun / max(flat_dot_sun, 0.01), water_alpha);
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun * ripple_light_factor;
	// Per-fragment block_lit dim from the shadow projector — a sprite
	// standing above the water dims its surface here.
	vec3 block_lit = lm.gba * block_light_shadow_factor(world_vertex);
	vec3 light = max(min(vec3(1.0), sun_lit + block_lit), vec3(MIN_AMBIENT));
	vec3 shaded_light = face_shade * light;

	// Deeper water is darker and more opaque
	vec3 base_color = mix(water_shallow_tint, water_deep_tint, depth_factor);
	vec3 water_col = base_color * shaded_light;

	// Foam color compositing (split out from foam_amount above so the
	// lighting block could see water_alpha first). Same light floor as
	// before — clear day = crisp white foam, dim night = block-lit only.
	if (is_top && foam_amount > 1e-4) {
		vec3 foam_light = max(light, vec3(foam_min_light * sun_mask));
		water_col = mix(water_col, foam_color * foam_light, foam_amount);
	}

	// Reflection path. Values hoisted above the is_top branch so the
	// water_debug visualizer can sample them uniformly.
	vec3 refl = vec3(0.0, 1.0, 0.0);
	vec3 sky_col = vec3(0.0);
	vec3 refl_col = vec3(0.0);
	float fresnel = 0.0;
	if (is_top) {
		// Per-fragment view direction. For an orthographic camera the
		// projection rays are parallel, but we deliberately use the vector
		// from CAMERA_POSITION_WORLD to world_vertex (treating the camera
		// as a point) — this gives reflections natural perspective-like
		// convergence even under an ortho projection, so the sun disk can
		// appear as a localized bright spot on the water instead of firing
		// on every fragment simultaneously. The visual "wrong reflection
		// direction" concerns are handled elsewhere (SSR gate on refl.y).
		vec3 view_to_frag = normalize(world_vertex - CAMERA_POSITION_WORLD);
		refl = reflect(view_to_frag, n);

		// Fresnel: more reflective at grazing view angles.
		fresnel = pow(1.0 - clamp(dot(-view_to_frag, n), 0.0, 1.0), fresnel_power);

		// True-orthographic reflection direction for cloud sampling: reflect
		// the camera's world-forward axis off the ripple-perturbed normal.
		// Unlike refl (pinhole), this is INVARIANT under camera translation —
		// only camera pitch/yaw and the ripple normal affect it. So the
		// reflected cloud XZ = world_vertex.xz + cam_ortho_refl.xz *
		// (cloud_altitude / cam_ortho_refl.y) becomes a fixed offset (a
		// function of camera pitch × cloud_altitude) from the water fragment,
		// which means the cloud reflection drifts IN SYNC with the cloud
		// shadow on the ground (both move only via cloud_offset from wind).
		// Pinhole refl is kept for SSR raymarching so terrain reflections
		// still get natural per-fragment variation.
		vec3 cam_fwd_world = -normalize(mat3(INV_VIEW_MATRIX) * vec3(0.0, 0.0, 1.0));
		vec3 refl_ortho = reflect(cam_fwd_world, n);

		// Split sky sampling into two paths with different projections:
		//   - Clouds + gradient + stars: true-ortho reflection (refl_ortho)
		//     so the cloud reflection stays synced with the cloud shadow
		//     on the ground, offset only by camera pitch + cloud altitude.
		//   - Sun + moon: BILLBOARDED sprites in screen space via the
		//     fake reflection FOV — circular in pixels regardless of FOV
		//     aspect, positioned from view direction vs. body direction.
		vec3 to_sun_dir = -normalize(sky_sun_dir);
		vec3 to_moon_dir = -to_sun_dir;
		// Time-of-day mutual exclusion + implicit camera-direction cull.
		// to_*_dir.y is a function of a global uniform (sky_sun_dir), so
		// the branch is uniform across all fragments and the GPU skips the
		// entire billboard work for the body below horizon. Sun and moon
		// are 180° apart on the celestial sphere, so exactly one is above
		// horizon at any time (with a brief sunset overlap covered by the
		// smoothstep). Inside sample_billboard_body, `view_dir.z >= -1e-4`
		// further culls bodies whose ripple-perturbed reflection happens
		// to point behind the camera, so we don't need a separate
		// camera-direction uniform — the horizon gate handles the
		// dominant vertical axis under iso projection, and the internal
		// view-space check handles the rest.
		float sun_above = smoothstep(-0.05, 0.05, to_sun_dir.y);
		float moon_above = smoothstep(-0.05, 0.05, to_moon_dir.y);
		vec4 sun_bb = vec4(0.0);
		vec4 moon_bb = vec4(0.0);
		// Halo multipliers — sun gets a wide, blown-out 3x halo; moon gets
		// a narrower, dimmer one. Disk alpha is preserved so both still
		// occlude stars through (1.0 - moon_bb.a) below.
		if (sun_above > 0.0 && sun_disk_intensity > 1e-3) {
			sun_bb = sample_billboard_body(
				SCREEN_UV, VIEWPORT_SIZE, to_sun_dir, n, sun_texture,
				sun_angular_size_deg, sun_disk_intensity, sun_color,
				3.0, 1.0, VIEW_MATRIX);
			sun_bb.rgb *= sun_above;
		}
		if (moon_above > 0.0 && moon_disk_intensity > 1e-3) {
			moon_bb = sample_billboard_body(
				SCREEN_UV, VIEWPORT_SIZE, to_moon_dir, n, moon_texture,
				moon_angular_size_deg, moon_disk_intensity, moon_color,
				2.0, 0.35, VIEW_MATRIX);
			moon_bb.rgb *= moon_above;
		}

		// The whole sky reflection composite (stars + clouds + horizon
		// gradient + sun/moon disks) is gated on sun_mask so a water cell
		// in full shadow (under a roof, inside a cave) contributes zero
		// stars/clouds/sky regardless of camera angle or time of day —
		// otherwise a cave pool would reflect a starfield even though no
		// sky is visible from that fragment. Gating here also skips the
		// star unproject + INV_VIEW_MATRIX rotate + reflect + texture
		// sample for shadowed fragments.
		vec3 stars = vec3(0.0);
		if (sun_mask > 1e-3) {
			// Starfield — additionally gated on uniform `star_intensity *
			// sky_night_factor` so daytime / star-disabled scenes also skip.
			if (star_intensity * sky_night_factor > 1e-3) {
				float star_tan_h = tan(radians(reflection_fov_h_deg) * 0.5);
				float star_tan_v = tan(radians(reflection_fov_v_deg) * 0.5);
				vec2 star_ndc = vec2(
					(SCREEN_UV.x - 0.5) * 2.0,
					(reflection_fov_v_center - SCREEN_UV.y) * 2.0);
				vec3 star_view_ray = normalize(vec3(star_ndc.x * star_tan_h, star_ndc.y * star_tan_v, -1.0));
				vec3 star_world_ray = normalize(mat3(INV_VIEW_MATRIX) * star_view_ray);
				// Reflect off the ripple-perturbed normal so ripples actually
				// distort the star reflection — shimmer / twinkle — rather than
				// just modulating its brightness via fresnel.
				vec3 star_dir = reflect(star_world_ray, n);
				// Ripple-driven mip bias: flat water (ripple_strength → 0) samples
				// mip 0 so stars stay crisp; fully rippled water selects a coarser
				// mip so neighboring fragments' jittered directions average into a
				// stable glow instead of per-frame flicker. The water_disable_ripples
				// debug path forces lod to 0 since n is pinned flat there anyway.
				float star_lod = water_disable_ripples ? 0.0 : ripple_strength * star_ripple_blur_lod;
				stars = sample_sky_stars(star_dir, star_lod) * (1.0 - moon_bb.a);
			}

			// Cloud parallax offset — simple and bounded: refl_ortho.xz scaled
			// by cloud_altitude, no 1/refl_ortho.y divisor. Magnitude ≤
			// cloud_altitude, scales smoothly with camera pitch (cos(pitch))
			// instead of 1/sin(pitch) which zooms extremely at shallow angles.
			// Rotating the camera in yaw rotates the offset at a consistent
			// rate; changing pitch smoothly shrinks/grows it.
			vec2 cloud_sample_xz = world_vertex.xz + refl_ortho.xz * cloud_altitude;
			vec3 sky_clouds = sample_sky_clouds_at_xz(refl_ortho, cloud_sample_xz, stars, 0.5);
			// Occlude sun/moon billboards by the cloud mask at the same sampled
			// XZ — a cloud sitting between the water and the celestial body
			// in the reflected path hides it instead of letting it shine
			// through additively.
			float cloud_occ_refl = sample_sky_cloud_mask_at_xz(cloud_sample_xz);
			// sample_billboard_body returns premultiplied rgb (disk already
			// multiplied by tex.a, halo is additive with alpha=0), so compositing
			// is a plain add. .a is still the disk-only mask used by the star
			// occlusion above.
			vec3 sky_celestial = (sun_bb.rgb + moon_bb.rgb) * (1.0 - cloud_occ_refl);
			sky_col = (sky_clouds + sky_celestial) * sun_mask;
		}

		// Reflection is sky-only. SSR was removed because screen-space
		// raymarching cannot produce a flipped reflection of upright
		// objects — it samples the un-flipped framebuffer at the projected
		// reflection-ray UV, which is the upright sprite's actual screen
		// position, producing a "ghosted" duplicate (and per-fragment ray
		// jitter under heavy ripples turned that into noise around every
		// nearby sprite). Reflectable Sprite3D nodes now spawn a flipped
		// child sprite mirrored across the water plane via LitSprite —
		// those render as underwater geometry and are naturally visible
		// through the translucent water composite below.
		refl_col = sky_col;
	}
	// Reflection blend amount — computed outside the is_top branch so the
	// compositing step below can apply it uniformly. Max of physical fresnel
	// × strength and the artistic floor; zero on non-top faces.
	float refl_mix = is_top
		? clamp(max(fresnel * reflection_strength, reflection_min), 0.0, 1.0)
		: 0.0;

	// Compositing: we do it manually so refraction can offset the "behind"
	// sample and so reflection can sit on top of the water body uniformly.
	// Output ALPHA=1 because we composited against the screen texture
	// ourselves — letting Godot blend on top would double-blend.
	// water_alpha computed above (hoisted alongside foam_amount so the
	// lighting block could weight ripple N·L by surface opacity).
	vec3 behind = vec3(0.0);
	// Only sample the screen texture when the water body is actually
	// going to show what's behind it (water_alpha < 1 somewhere on the
	// fragment OR we're on a side face where compositing always runs).
	// Fully-opaque muddy water at depth can skip the sample entirely.
	bool needs_behind = water_alpha < 0.999;
	if (needs_behind) {
		if (is_top && water_refraction_strength > 1e-5) {
			// Scale the UV offset by how far the ray travels through water —
			// deeper water → more refraction — and by the ripple-perturbed
			// normal's horizontal tilt.
			vec2 full_offset = n.xz * water_refraction_strength * depth_factor_raw;
			// Depth-aware refraction: progressively shrink the offset until
			// it lands on a pixel whose depth is BEHIND the water (Godot 4
			// reverse-Z → smaller depth value = farther from camera, so the
			// check is `<=`).
			vec2 refr_uv = clamp(SCREEN_UV, vec2(0.001), vec2(0.999));
			for (int i = 0; i < 4; i++) {
				vec2 try_uv = clamp(SCREEN_UV + full_offset * (1.0 - float(i) * 0.25), vec2(0.001), vec2(0.999));
				if (textureLod(depth_tex, try_uv, 0.0).r <= FRAGCOORD.z) {
					refr_uv = try_uv;
					break;
				}
			}
			behind = textureLod(screen_tex, refr_uv, 0.0).rgb;
		} else {
			behind = textureLod(screen_tex, SCREEN_UV, 0.0).rgb;
		}
	}

	// Underwater caustics. Reconstruct the seabed world position from the
	// depth buffer, sample the procedural ribbon pattern in world-space XZ
	// (so bands stay locked to the ground, not to screen), and add the
	// contribution onto `behind` before water-color compositing. Scaled by
	// TOTAL received light at the surface (sun + block), not just direct
	// sun — a torch in a cave shines `block_lit` onto the water, so its
	// caustics show even though no sky is reachable. Sun caustics still
	// fall off naturally at night because `sun_lit` collapses with
	// `sun_intensity`. needs_behind also gates: if the water is fully opaque
	// (muddy + deep) the through-water sample was skipped, so adding
	// caustics into uninitialized `behind` would render garbage.
	if (is_top && needs_behind && caustic_strength > 1e-5 && thickness > 1e-3) {
		// Sample at an absolute world position computed as
		//     water_surface + view_direction * water_depth.
		// At a fixed water-surface / seabed world-point pair this
		// expression is invariant of camera position, so the noise lookup
		// is anchored to absolute world coordinates regardless of where
		// the player walks. On flat terrain the per-fragment thickness
		// is constant, so the Y component of the sample is constant —
		// the 3D noise's third axis stays on a single horizontal slice
		// and there's no depth-walking. The wind-driven scroll
		// (caustic_offset) is applied INSIDE caustic_pattern to layer 2
		// only, so wind morphs the pattern in place rather than
		// translating it.
		vec3 view_dir_world = normalize(world_vertex - CAMERA_POSITION_WORLD);
		vec3 caustic_sample_pos = world_vertex + view_dir_world * thickness;

		float pattern = caustic_pattern(caustic_sample_pos);
		float depth_attn = exp(-thickness / max(caustic_depth_fade, 0.01));

		// Total-light luminance proxy: max channel of the composited surface
		// light (sun_lit + block_lit, clamped). Sun-lit AND torch-lit
		// fragments both modulate the caustic add — a torch in a cave shines
		// caustics onto the seabed below it, while shadowed water with no
		// nearby light fades to zero. Using max channel (rather than a dot
		// with luminance weights) keeps warm torchlight contributing at full
		// intensity instead of being attenuated by green-channel weighting.
		// Time-of-day dimming is handled implicitly: `sun_intensity` already
		// drops at sunset and to the moon's value at night, so direct-sun
		// caustics fade naturally without needing an explicit grazing term.
		// Squared so caustics scale slightly less than linearly with light —
		// torchlit cave water reads as faint bands rather than full bright
		// daylight intensity (a torch is much weaker than the sun, but
		// `light_amt` only reaches ~0.3-0.5 in caves so linear was over-
		// representing the contribution). MIN_AMBIENT also gets squashed
		// here, so true-shadow water fades cleanly to zero.
		float light_amt = max(max(light.r, light.g), light.b);
		behind += caustic_color * pattern * caustic_strength * depth_attn * light_amt * light_amt;
	}

	// Two-layer compositing: water body first, reflection on top. Water
	// reflects off its SURFACE — reflection should be visible whether the
	// water is transparent or not. Baking reflection into water_col and
	// then attenuating by water_alpha (the old path) dimmed reflections
	// in clear water because the same alpha that let you see through the
	// water also hid the surface reflection. Now:
	//   water_body = mix(behind, water_col, water_alpha)   -- through-water view
	//   composited = mix(water_body, refl_col, refl_mix)  -- surface reflection on top
	// Foam already boosts water_alpha to ~1 at the shoreline, and foam's
	// color is in water_col, so foam still wins there.
	vec3 water_body = mix(behind, water_col, water_alpha);
	vec3 composited = mix(water_body, refl_col, refl_mix);

	// Debug visualizer. When non-zero, replaces the composited output on
	// top faces only (side faces stay normal so shorelines remain readable).
	// See CVars.cs `water_debug` for mode documentation.
	if (is_top && water_debug_mode > 0) {
		if (water_debug_mode == 1) { composited = refl_col; }
		else if (water_debug_mode == 2) { composited = sky_col; }
		else if (water_debug_mode == 3) { composited = sky_col; } // SSR removed; mode 3 now == sky
		else if (water_debug_mode == 4) { composited = vec3(fresnel); }
		else if (water_debug_mode == 5) { composited = abs(refl); }
		else if (water_debug_mode == 6) { composited = abs(n); }
		else if (water_debug_mode == 7) {
			// Intended sun-disk reflection position. Distinct colors so
			// disk-hit vs near-miss is unmistakable:
			//   BRIGHT GREEN = refl inside disk threshold — this pixel
			//                  SHOULD show the sun in normal mode.
			//   RED (dim→bright) = how close refl is to the sun but
			//                      not inside the disk.
			//   BLACK = refl pointing away from the sun.
			// If no facing produces green, the sun is geometrically
			// unreachable — adjust SunMaxElevationDegrees, time_of_day,
			// or camera pitch until green pixels appear.
			vec3 to_sun_dbg = -normalize(sky_sun_dir);
			float sd_dbg = max(dot(refl, to_sun_dbg), 0.0);
			float disk_hit = smoothstep(0.80, 0.92, sd_dbg);
			float heat = pow(sd_dbg, 4.0);
			composited = vec3(heat * (1.0 - disk_hit), disk_hit, 0.0);
		}
		else if (water_debug_mode == 8) {
			// LIVE caustic path: water surface + view direction × water
			// depth (an absolute world position invariant to camera
			// position for fixed water/seabed pairs).
			vec3 view_dir_world_dbg8 = normalize(world_vertex - CAMERA_POSITION_WORLD);
			vec3 sample_pos_dbg8 = world_vertex + view_dir_world_dbg8 * thickness;
			float pattern_dbg = caustic_pattern(sample_pos_dbg8);
			composited = vec3(pattern_dbg, 0.0, 0.0);
		}
		else if (water_debug_mode == 9) {
			// Reconstructed seabed world XZ visualization — fract() of XZ
			// shows a tiled gradient. Solid color = scene_view reconstruction
			// failed (no depth, or INV_VIEW_MATRIX broken). Visible
			// gradient grid that tracks camera motion = reconstruction OK.
			vec4 seabed_world_dbg = INV_VIEW_MATRIX * scene_view;
			composited = vec3(fract(seabed_world_dbg.x * 0.1), fract(seabed_world_dbg.z * 0.1), 0.0);
		}
		else if (water_debug_mode == 10) {
			// caustic_color upload check — pure caustic_color flat fill.
			// Black here means the global is being set to (0,0,0).
			composited = caustic_color;
		}
		else if (water_debug_mode == 11) {
			// A/B compare: caustic pattern WITHOUT the wind-driven offset.
			// Identical to mode 8 in calm weather; in wind, mode 11 stays
			// frozen while mode 8 drifts. Useful to confirm caustic_offset
			// is doing what it should.
			vec4 seabed_world_dbg11 = INV_VIEW_MATRIX * scene_view;
			float pattern_dbg11 = caustic_pattern(seabed_world_dbg11.xyz);
			composited = vec3(pattern_dbg11, 0.0, 0.0);
		}
		else if (water_debug_mode == 12) {
			// sun_uv visualization for the LIVE path — tiled gradient on
			// the actual sun_uv coordinates the caustic noise is sampling
			// (depth-reconstructed seabed XZ).
			vec4 seabed_world_dbg12 = INV_VIEW_MATRIX * scene_view;
			composited = vec3(fract(seabed_world_dbg12.x * 0.5), fract(seabed_world_dbg12.z * 0.5), 0.0);
		}
		else if (water_debug_mode == 13) {
			// TIME rollover detector. Godot's `TIME` shader builtin wraps
			// at `rendering/limits/time/time_rollover_secs` (default 3600s
			// but can be lower in some configs). At rollover, every
			// time-driven animation restarts from zero — including the
			// caustic Z-slice in caustic_pattern, which would visually
			// read as the noise sample resetting. Watch the red value
			// climb 0 → 1 over 10 seconds; if it suddenly snaps back to
			// 0 at any point, time rollover is the source of the pop.
			composited = vec3(fract(TIME * 0.1), 0.0, 0.0);
		}
		else if (water_debug_mode == 14) {
			// Caustic pattern at a FIXED world origin — strips out spatial
			// variation but keeps wind-driven evolution (since layer 2's
			// caustic_offset still slides past layer 1 even at world
			// origin). Useful to see the temporal evolution rate isolated
			// from spatial noise.
			float pattern_dbg14 = caustic_pattern(vec3(0.0));
			composited = vec3(pattern_dbg14, 0.0, 0.0);
		}
	}

	// `unshaded` in Godot 4 outputs ALBEDO directly — EMISSION is ignored
	// for unshaded materials. The composite is fully self-lit (sun, block,
	// cloud, foam, caustics, and reflection are all baked in above), so
	// it goes into ALBEDO. Block-light shadow is folded into the composite
	// via the dim of `block_lit` above, not via a directional-light overlay.
	ALBEDO = composited;
	ALPHA = 1.0;
	}
}
