shader_type spatial;
// Tree variant of model_lit. Five stylization passes layer on top of the
// same lit-by-world-lightmap base; the four geometric/shading passes are
// each independently gated by a CVar via a global uniform strength, plus
// one always-on authoring pass (per-instance leaf tint):
//
//   1. Wind sway (tree_wind_strength)         — vertex shader displaces canopy
//      verts in a sinusoidal sway. Trunk verts (low Y) are untouched. The
//      ORIGINAL (pre-sway) object-space position is forwarded as a varying so
//      the shader-side detail noise and sphere-normal direction stay glued
//      to the leaves instead of swimming over them.
//
//   2. Sphere normal (tree_sphere_normal_strength) — replace the mesh normal
//      with a normal pointing radially outward from `canopy_center_obj` on
//      an ellipsoid with semi-axes `canopy_radii` (default (1,1,1) = true
//      sphere; narrow XZ + tall Y reads tall conifers correctly). Reads
//      the crown as a soft shape instead of a faceted polyhedron; feeds
//      the wrap-lambert form term in the lighting computation. Trunk verts
//      keep the real mesh normal. The center DEFAULT is the trunk base
//      (0, 0, 0) so every canopy vert sits above it and the radial normal
//      carries a +Y component — leaves shade as if facing mostly upward,
//      matching real foliage. Raising the center back into the canopy
//      (e.g. y=5) gives true sphere shading but creates a hard shadow on
//      the sun-opposite hemisphere; pick what reads best per tree.
//
//   3. Detail noise (tree_detail_noise_strength) — per-pixel 3D value noise
//      sampled in object space modulates the albedo (multiplicative tint
//      ±detail_noise_contrast). Adds leafy mottling without a texture.
//
//   4. Silhouette breakup (tree_silhouette_breakup_strength) — discards a
//      fraction (`silhouette_breakup_amount` × strength) of canopy pixels
//      at the dark spots of a noise field, with the discard CONCENTRATED
//      at the silhouette via a Fresnel-like edge factor
//      (`pow(1 - max(0, sphere_normal · view), edge_breakup_falloff)`).
//      Pixels in the canopy INTERIOR that would have been discarded at
//      the silhouette instead darken to `silhouette_shadow_darkness`,
//      reading as deeper foliage shadow rather than holes-through-the-
//      tree. Breaks the polygonal silhouette and adds perceived depth
//      without changing geometry. Crank toward 1.0 for seasonal leaf loss.
//
//   5. Per-instance leaf tint — value_noise_3d sampled at the TREE's world
//      origin (one sample per instance, NOT gated by a CVar) selects a blend
//      between `leaf_tint_a` and `leaf_tint_b`, applied as a multiplicative
//      tint on the canopy. Lets a forest of identical-mesh trees read as a
//      mix of e.g. green / yellow-green without per-tree authoring. Sampled
//      at the same spatial scale as the per-pixel detail noise so
//      neighboring trees (typically meters apart) land on independent
//      values; all vertices of one instance share the same MODEL_MATRIX
//      translation so the tint is uniform across an individual canopy.
//
// All five canopy terms are gated by a smoothstep height mask
// (canopy_y_start..canopy_y_end in object Y) so the trunk stays clean — no
// sway, no fake sphere shading, no leaf mottling on the bark.
//
// Shares model_lit's ALBEDO+EMISSION split (direct sun → ALBEDO scaled by
// ATTENUATION in light(); ambient+block → EMISSION never shadowed) so it
// composes with Godot's directional shadow atlas and the cloud_shadow
// include the same way.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;
// Stamp stencil 16 = "tree" so the terrain depth-outline (depth_outline.gdshader,
// compare_equal 0) skips tree pixels. 16 avoids the in-use values (2/4 water,
// 8 character), leaving X-ray and water reflections unaffected.
stencil_mode write, compare_always, 16;

uniform sampler2D albedo_tex : source_color, filter_nearest;

// World-lightmap globals (same set model_lit / sprite_lit / voxel_clip read).
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"
// Dark-adaptation ("night eyes") tone curve. Driven by the player sim
// (Player.EyeDilation) via GameClient; exact no-op when eye_adaptation == 0.
#include "res://shaders/eye_adaptation.gdshaderinc"

// Per-feature CVar gates (0 = off, 1 = full). Seeded by ChunkManager._Ready
// from the matching tree_wind / tree_sphere_normal / tree_detail_noise /
// tree_silhouette_breakup CVars; subsequent live edits push through the
// CVar OnChanged callbacks.
global uniform float tree_wind_strength;
global uniform float tree_sphere_normal_strength;
global uniform float tree_detail_noise_strength;
global uniform float tree_silhouette_breakup_strength;

// Wrap-lambert blend matching model_lit.
uniform float form_strength : hint_range(0.0, 1.0) = 0.55;

// Height mask in OBJECT-space Y: vertices below `canopy_y_start` are pure
// trunk (no sway, no sphere normal, no detail noise); vertices above
// `canopy_y_end` are pure canopy. The Synty Generic trees in this project
// have trunks roughly 0..2m and canopies 2..8m in their authored object
// space, so the defaults work without per-tree tuning.
uniform float canopy_y_start = 2.0;
uniform float canopy_y_end = 4.0;

// Wind sway parameters. Amplitude is in METERS of horizontal displacement at
// the top of the canopy (height mask = 1); falls to zero at the trunk.
uniform float sway_amplitude = 0.18;
uniform float sway_frequency = 1.4;
// Spatial scale: how much neighboring trees / parts of one tree sway out of
// phase. Tied to OBJECT-space position so two trees authored from the same
// FBX sway identically (visually fine; if you want per-instance phase the
// scale could be applied to MODEL_MATRIX[3] instead).
uniform float sway_spatial_scale = 0.35;

// Sphere-normal canopy center in OBJECT space. Defaults to the trunk BASE
// so every canopy vert sits above it and the radial-outward sphere normal
// carries a +Y component — leaves shade as if facing mostly upward (matches
// real phototropic foliage). Raising the center back into the canopy gives
// true sphere shading but creates a hard shadow on the sun-opposite
// hemisphere where outside_obj's Y component goes negative.
uniform vec3 canopy_center_obj = vec3(0.0, 0.0, 0.0);
// Ellipsoid semi-axes the radial normal is computed against. The normal at
// a vertex is `normalize(offset / (radii * radii))`, which gives the true
// outward direction on an ellipsoid (matches the implicit-surface gradient
// of (x/a)² + (y/b)² + (z/c)² = 1). Defaults to (1, 1, 1) which degenerates
// to a sphere — the original behavior. For a tall narrow conifer try
// roughly (0.4, 2.0, 0.4): the small XZ radii make side normals point
// almost straight out horizontally instead of leaning up toward the apex,
// so the silhouette-breakup edge factor reads narrower trees as actually
// narrow. The values are RATIOS, not meters — only their relative
// magnitudes matter (uniform scaling cancels in the normalize).
uniform vec3 canopy_radii = vec3(1.0, 1.0, 1.0);

// Detail-noise spatial frequency. Higher = finer mottling. ~10 gives a noise
// period of ~0.1m in object space, which reads as leaf-cluster-scale variation
// on a ~4-5m canopy.
uniform float detail_noise_scale = 10.0;
// How much the detail noise darkens / brightens the base color at full
// strength. ±0.18 ≈ 36% peak-to-peak variation, enough to read as texture
// without going cartoonish.
uniform float detail_noise_contrast : hint_range(0.0, 0.5) = 0.18;

// Silhouette breakup: fraction of the canopy area to discard at the noise's
// dark spots, multiplied by tree_silhouette_breakup_strength at runtime.
// At the authored 0.12, a constant ~12% of canopy pixels are punched out
// along leaf-cluster boundaries. The discard is concentrated at the
// SILHOUETTE (where the sphere normal points perpendicular to / away from
// the view), reusing the per-pixel noise field driving detail mottling so
// holes form where the eye already expects darker cluster edges. Reads as
// foliage translucency and breaks the polygonal silhouette without
// changing geometry. Crank toward 1.0 for seasonal leaf loss.
uniform float silhouette_breakup_amount : hint_range(0.0, 1.0) = 0.12;
// Spatial scale for the silhouette breakup's noise lookup. Independent
// from detail_noise_scale because the breakup wants larger blobs (a few
// holes per face of the canopy) than the per-pixel mottling (fine speckle
// at ~0.1m).
uniform float silhouette_breakup_scale = 5.0;
// Edge concentration exponent. 0 = uniform across the canopy (old behavior);
// 1 = linear Fresnel falloff (a lot of breakup mid-edge, none facing camera);
// higher values pull the breakup tighter to the silhouette rim. The
// effective threshold is `silhouette_breakup_amount * pow(1 - max(0,
// dot(sphere_normal, view_dir)), edge_breakup_falloff)`.
uniform float edge_breakup_falloff : hint_range(0.0, 8.0) = 2.0;
// What happens to canopy pixels that would have been discarded at the
// silhouette but sit in the canopy INTERIOR (sphere normal facing toward
// the camera). Instead of vanishing — interior holes read as "the tree has
// a window through it" rather than "leaf cluster edge" — they darken to
// this fraction of their lit color, faking deeper-foliage shadow. 1.0 = no
// shadow (full brightness), 0.0 = black. ~0.5 reads as a believable leaf-
// cluster shadow without crushing detail.
uniform float silhouette_shadow_darkness : hint_range(0.0, 1.0) = 0.5;

// Per-instance leaf tint endpoints. The per-tree noise sample picks a mix
// in [0, 1] between these two colors and multiplies the canopy albedo by
// the result. Defaults bias toward green / yellow-green so a forest reads
// as varied broadleaf foliage without per-tree authoring. Set both to white
// to disable (no CVar gate — this is meant as a constant authoring choice,
// not a runtime A/B knob).
uniform vec3 leaf_tint_a : source_color = vec3(0.78, 1.0, 0.62);
uniform vec3 leaf_tint_b : source_color = vec3(1.05, 1.0, 0.45);

varying vec3 world_pos;
varying vec3 world_normal;
// Object-space position BEFORE the wind sway displacement is applied — used
// for the detail noise and sphere-normal direction so both stay glued to the
// leaves rather than swimming/rotating as the canopy waves.
varying vec3 vertex_obj_orig;
// Per-instance canopy tint, computed once per vertex (but identical across
// all vertices of one instance since the noise input is MODEL_MATRIX[3]).
// Forwarded as a varying so the fragment stage just reads it instead of
// re-sampling the noise once per pixel.
varying vec3 instance_tint;

// 3D hash → [0, 1). Cheap; quality is sufficient for value-noise interpolation
// at the spatial scales we sample at. Same construction as the well-known
// "iq hash13" used in shadertoy demos. Defined before vertex() so the
// per-instance tint sample in the vertex stage can call it too.
float hash13(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

// 3D value noise: trilinear interp of hashes at the 8 corners of the integer
// cell containing `p`, with smoothstep on the cell-local position.
float value_noise_3d(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);

	float n000 = hash13(i + vec3(0.0, 0.0, 0.0));
	float n100 = hash13(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash13(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash13(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash13(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash13(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash13(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash13(i + vec3(1.0, 1.0, 1.0));

	return mix(
		mix(mix(n000, n100, f.x), mix(n010, n110, f.x), f.y),
		mix(mix(n001, n101, f.x), mix(n011, n111, f.x), f.y),
		f.z
	);
}

void vertex() {
	vertex_obj_orig = VERTEX;

	// Per-instance leaf tint. MODEL_MATRIX[3].xyz is the tree's world-space
	// origin and is the same for every vertex of this instance, so sampling
	// the noise here gives one mix value per tree (not per vertex / per
	// pixel). Same spatial frequency as the per-pixel detail noise so two
	// trees a few meters apart land in unrelated cells of the same noise
	// field — equivalent to giving each tree an independent random tint.
	vec3 instance_seed = MODEL_MATRIX[3].xyz;
	float instance_mix = value_noise_3d(instance_seed * detail_noise_scale);
	instance_tint = mix(leaf_tint_a, leaf_tint_b, instance_mix);

	// Wind sway: displace canopy verts horizontally. Two sin waves at
	// different frequencies (one on X, one on Z) produce a roughly circular
	// canopy motion rather than a straight-line wobble. Mask by the height
	// curve so the trunk doesn't move.
	float canopy_mask = smoothstep(canopy_y_start, canopy_y_end, VERTEX.y);
	if (tree_wind_strength > 0.0) {
		float wave_x = sin(TIME * sway_frequency + VERTEX.x * sway_spatial_scale + VERTEX.z * sway_spatial_scale * 0.7);
		float wave_z = cos(TIME * sway_frequency * 0.83 + VERTEX.z * sway_spatial_scale * 1.1 + VERTEX.y * sway_spatial_scale * 0.4);
		float amp = sway_amplitude * canopy_mask * tree_wind_strength;
		VERTEX.x += wave_x * amp;
		VERTEX.z += wave_z * amp;
	}

	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// Uniform-scale prop, so transforming the normal by MODEL_MATRIX (without
	// the inverse-transpose) is correct.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	vec3 base = tex.rgb;

	// Recompute the canopy mask from the ORIGINAL object-space Y (not the
	// post-sway VERTEX, which we don't have access to here anyway). Identical
	// to the vertex-side mask so trunk/canopy classification stays consistent.
	float canopy_mask = smoothstep(canopy_y_start, canopy_y_end, vertex_obj_orig.y);

	// Per-pixel noise samples. The fine sample (at detail_noise_scale,
	// typically ~10) drives the mottling tint. The coarse sample (at
	// silhouette_breakup_scale, typically ~2) drives the silhouette
	// breakup — coarser blobs are more "leaf cluster"-shaped than fine
	// speckle would be. Both sample the pre-sway object position so the
	// patterns stay glued to the canopy as it waves.
	float pixel_noise = value_noise_3d(vertex_obj_orig * detail_noise_scale);
	float breakup_noise = value_noise_3d(vertex_obj_orig * silhouette_breakup_scale);

	// Radial canopy normal — outward from canopy_center_obj on an
	// ellipsoid with semi-axes canopy_radii. Default radii (1,1,1) gives
	// a true sphere; setting any axis smaller / larger reshapes the
	// implicit surface so a tall narrow tree's side normals point
	// horizontally instead of leaning toward the apex. Computed once here
	// (rather than only inside the sphere_normal_strength branch in the
	// lighting block below) because the silhouette breakup wants to know
	// which way the canopy is "facing" at this pixel, regardless of
	// whether sphere shading is enabled.
	vec3 to_outside_obj = vertex_obj_orig - canopy_center_obj;
	// Guard against the zero-length case at the exact canopy center;
	// fall back to up so the normalize doesn't NaN.
	if (length(to_outside_obj) < 1e-4) {
		to_outside_obj = vec3(0.0, 1.0, 0.0);
	}
	// Ellipsoid gradient: dividing by radii² (component-wise) yields the
	// outward normal direction at this surface point. Reduces to the
	// original sphere case when radii = (1,1,1).
	vec3 ellipsoid_normal_obj = to_outside_obj / (canopy_radii * canopy_radii);
	vec3 sphere_normal_world = normalize((MODEL_MATRIX * vec4(normalize(ellipsoid_normal_obj), 0.0)).xyz);

	// Silhouette breakup with edge concentration. The base threshold is
	// gated by canopy_mask so trunk pixels are never touched. The edge
	// factor concentrates the breakup at the silhouette rim — pixels
	// whose sphere normal faces the camera (canopy interior) keep their
	// pixels, pixels whose sphere normal grazes / points away (silhouette)
	// see the full breakup. Pixels that fall under the base threshold but
	// pass the edge-scaled discard threshold get DARKENED instead of
	// discarded, so the canopy interior gets believable leaf-cluster
	// shadows in place of holes-through-the-tree. Discard for clean
	// depth/shadow behavior; shadow as a simple multiplicative tint.
	if (tree_silhouette_breakup_strength > 0.0 && canopy_mask > 0.0) {
		float base_threshold = silhouette_breakup_amount * tree_silhouette_breakup_strength * canopy_mask;
		vec3 view_dir = normalize(CAMERA_POSITION_WORLD - world_pos);
		// 0 when the sphere normal points straight at the camera (canopy
		// center pixel); 1 when perpendicular or facing away (silhouette).
		float edge_factor = pow(1.0 - max(0.0, dot(sphere_normal_world, view_dir)), edge_breakup_falloff);
		float discard_threshold = base_threshold * edge_factor;
		if (breakup_noise < discard_threshold) {
			discard;
		} else if (breakup_noise < base_threshold) {
			// In the middle of the canopy, would-be discards become
			// shadows. Smoothly ramp the shadow's depth so the band
			// between the discard threshold and base threshold fades
			// from full shadow (just above discard) to no shadow (at
			// base threshold), instead of popping at the boundary.
			float t = (breakup_noise - discard_threshold) / max(base_threshold - discard_threshold, 1e-4);
			base *= mix(silhouette_shadow_darkness, 1.0, t);
		}
	}

	// Per-instance leaf tint (no CVar gate, always on at full strength).
	// canopy_mask scopes it to the leaves so the trunk keeps its bark color.
	base *= mix(vec3(1.0), instance_tint, canopy_mask);

	// Detail noise → multiplicative tint on base color, gated by the CVar
	// strength and the canopy mask.
	if (tree_detail_noise_strength > 0.0) {
		float tint = 1.0 + (pixel_noise * 2.0 - 1.0) * detail_noise_contrast;
		float gate = tree_detail_noise_strength * canopy_mask;
		base *= mix(1.0, tint, gate);
	}

	// Editor-preview fallback (mirrors model_lit): light globals aren't
	// registered until the game starts, so a standalone .tres preview gets
	// vec3(0) for light_map_inv_size. Fall back to "full bright" so authoring
	// sees the recolored base without a cave-dimmed read.
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		// Pick which normal feeds the wrap-lambert form term. For canopy
		// pixels we blend toward the sphere normal (computed at the top of
		// fragment() — reused here instead of recomputed). Trunk pixels
		// keep the real mesh normal verbatim.
		vec3 shade_normal = world_normal;
		if (tree_sphere_normal_strength > 0.0) {
			float gate = tree_sphere_normal_strength * canopy_mask;
			shade_normal = normalize(mix(world_normal, sphere_normal_world, gate));
		}

		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(shade_normal, to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float form = mix(1.0, wrap, form_strength);

		float cloud = cloud_shadow_ground(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		vec3 block_lit = lm.gba;

		ALBEDO = base * sun_lit * (1.0 - sun_ambient);
		EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim);

		// Eye adaptation: lift in low local light when dark-adapted, push toward
		// blowout in bright. light_est = area illuminance (sun + block,
		// pre-shadow, not albedo). No-op when eye_adaptation == 0.
		float ea_gain = eye_adaptation_gain(dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114)));
		ALBEDO *= ea_gain;
		EMISSION *= ea_gain;
	}
}

void light() {
	// Same composition as model_lit: ATTENUATION carries Godot's directional
	// shadow atlas, cloud_dim is min()'d in so a cast shadow + cloud shadow
	// read as one.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
