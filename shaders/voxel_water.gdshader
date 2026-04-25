shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

global uniform float camera_clip;

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

const float MIN_AMBIENT = 0.0;

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

// Procedural caustic intensity at a world-space XZ location. Takes two
// scrolling noise octaves in opposite directions and uses the absolute
// difference to produce thin bright ribbon edges where the two patterns
// cross. pow() sharpens the ribbons into crisp focused bands. Range ~[0,1].
float caustic_pattern(vec2 world_xz, float time)
{
	float t = time * caustic_speed;
	vec3 p1 = vec3(world_xz * caustic_scale, t);
	vec3 p2 = vec3(world_xz * caustic_scale * 1.73 + vec2(5.2, 1.3), -t * 0.83);
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
		v.y += h * water_wave_amp;
	}
	VERTEX = v;
	world_vertex = (MODEL_MATRIX * vec4(v, 1.0)).xyz;
	world_normal = NORMAL;
}

void fragment() {
	if (world_vertex.y > camera_clip) {
		discard;
	}

	// Only the top face of a water voxel (normal.y ~= +1) represents an
	// open surface. Sides/bottom are submerged rims seen through adjacent
	// transparent cells — leave them flat and opaque.
	bool is_top = world_normal.y > 0.5;
	// Skip the two normal-map samples + slerp on calm water — ripple_strength
	// is already quadratic in wind speed (WeatherDerivation), so at low wind /
	// no rain it's effectively zero but the texture work still costs the same.
	bool ripples_active = is_top && !water_disable_ripples && ripple_strength > 0.01;
	vec3 n = ripples_active ? ripple_normal(world_vertex.xz, TIME) : (is_top ? vec3(0.0, 1.0, 0.0) : world_normal);

	// Wave streaks: a coarser scrolling-noise layer that adds tilt onto
	// the base ripple normal — conceptually just "ripple layer 2" with its
	// own scale/speed/weight. The streak path does NOT compute foam; foam
	// fires below from the FINAL composite tilt regardless of which layer
	// drove it.
	if (is_top && !water_disable_ripples && wave_streak_strength > 1e-3) {
		vec2 streak_tilt = wave_streak(world_vertex.xz);
		n = normalize(n + vec3(streak_tilt.x, 0.0, streak_tilt.y));
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
		whitecap_foam_amt = clamp(crest_mask * speckle_mask * whitecap_foam_strength, 0.0, 1.0);
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
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
	vec3 block_lit = lm.gba;
	vec3 light = max(min(vec3(1.0), sun_lit + block_lit), vec3(MIN_AMBIENT));
	vec3 shaded_light = face_shade * light;

	// Deeper water is darker and more opaque
	vec3 base_color = mix(water_shallow_tint, water_deep_tint, depth_factor);
	vec3 water_col = base_color * shaded_light;

	// Shoreline foam: stronger as water gets shallower (thickness → 0).
	// Only on top faces; side faces abut solid terrain and the foam would
	// smear up the vertical rim. Animated by scrolling noise so it reads
	// as surf rather than a static outline. foam_color is region-tinted:
	// muddy water's "foam" carries the water's own color (scum/film) while
	// clean water stays white. Noise UVs are pixelized so the foam reads
	// as hand-drawn edge pixels rather than a smoothly scrolling pattern.
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
		float noisy_foam = shallow * mask * foam_strength;

		foam_amount = max(max(noisy_foam, rim), whitecap_foam_amt);
		// All foam (shoreline rim + noisy surf + crest whitecaps) share
		// a single light floor driven from C# based on overall scene
		// clarity + light level. Bright clear day → high floor (foam
		// stays crisp white). Overcast / foggy / dim night → low floor
		// (foam dims with the scene rather than glowing emissively
		// against dark surroundings). Daytime/torchlit `light` still
		// wins through max() when it's brighter than the floor.
		vec3 foam_light = max(light, vec3(foam_min_light));
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

		// Starfield — gated on uniform `star_intensity * sky_night_factor`
		// so the unproject + INV_VIEW_MATRIX rotate + reflect + texture
		// sample are all skipped during full daylight (when night_factor=0)
		// or when stars are tuned out via star_intensity=0. The branch is
		// uniform across all fragments so the GPU skips the entire block.
		vec3 stars = vec3(0.0);
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

		// The whole sky reflection composite is multiplied by sun_mask at
		// the end, so when the water cell is in full shadow (under a roof,
		// inside a cave) the reflection contributes zero regardless. Gate
		// the cloud noise samples + cloud-mask sample + horizon/zenith
		// gradient on sun_mask to skip that work entirely. This is the
		// biggest remaining sky-reflection cost on outdoor water that
		// happens to be in cloud shadow on the ground (cloud_sun handles
		// dimming, this skips the reflection sampling overhead).
		if (sun_mask > 1e-3) {
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
	// Edge opacity floor — clamp the surface alpha at the shoreline from
	// below so clean water doesn't fade to invisible. Muddy water's
	// water_alpha_min already sits above the floor so it's unaffected.
	float effective_edge_alpha = max(water_alpha_min, water_edge_opacity);
	float water_alpha = max(mix(effective_edge_alpha, water_alpha_max, depth_factor), foam_amount);
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
	// the same direct-sunlight product the surface uses (sun_mask *
	// sun_intensity * cloud_sun) — caustics are focused refractions of
	// direct sun, so they vanish at night, in shadow, and dim under
	// clouds. needs_behind also gates: if the water is fully opaque
	// (muddy + deep) the through-water sample was skipped, so adding
	// caustics into uninitialized `behind` would render garbage.
	if (is_top && needs_behind && caustic_strength > 1e-5 && thickness > 1e-3) {
		vec4 seabed_world = INV_VIEW_MATRIX * scene_view;
		float pattern = caustic_pattern(seabed_world.xz, TIME);
		float depth_attn = exp(-thickness / max(caustic_depth_fade, 0.01));
		float sun_factor = sun_mask * sun_intensity * cloud_sun;
		behind += caustic_color * pattern * caustic_strength * depth_attn * sun_factor;
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
			// Caustic pattern isolation — RED channel only, bypassing
			// caustic_color so we can tell pattern == 0 (solid black) from
			// caustic_color == 0 (red ribbons but no color in normal mode).
			vec4 seabed_world_dbg = INV_VIEW_MATRIX * scene_view;
			float pattern_dbg = caustic_pattern(seabed_world_dbg.xz, TIME);
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
	}

	ALBEDO = composited;
	ALPHA = 1.0;
}
