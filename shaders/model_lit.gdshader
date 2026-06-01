shader_type spatial;
// Lit shader for 3D skinned character meshes (the polyperfect player model).
// Samples the same global `light_map` the sprite / voxel / particle pipelines
// use, so the model darkens in caves and under cloud shadow and brightens in
// sun exactly like the world around it. Unlike sprite_lit (which fakes a flat
// up-normal because billboards have no real geometry), this uses the mesh's
// real surface normal for a softened wrap-lambert form term, so the model
// reads as a 3D shape rather than a flat cutout.
//
// Lighting split matches sprite_lit_common / voxel_clip so it composes with
// Godot's directional shadow atlas in light():
//   ALBEDO   = direct sun color  (scaled by ATTENUATION in light())
//   EMISSION = ambient sky bounce + block light  (never shadowed)
// final = ALBEDO * ATTENUATION + EMISSION (ambient_light_disabled).
//
// Real geometry, so the mesh casts/receives Godot shadows directly — no
// ShadowsOnly proxy like the billboard sprites need.
render_mode ambient_light_disabled, depth_draw_opaque, cull_back;

// Character albedo atlas. Nearest filter + no mip to keep the flat, pixel-art-
// adjacent low-poly look consistent with the rest of the game.
uniform sampler2D albedo_tex : source_color, filter_nearest;

// RGBA8 light map: R = sun mask, GBA = post-pow block light per RGB channel.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
// Cloud occlusion + sun direction (owned by SkyController). sun_world_dir,
// cloud_offset, cloud_threshold, cloud_sharpness, cloud_noise_tex and
// cloud_altitude are required by cloud_shadow.gdshaderinc and must be declared
// before the include.
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

// How strongly the wrap-lambert form term darkens surfaces facing away from
// the sun. 0 = flat (lit purely by the volumetric light map, like a sprite);
// 1 = full wrap-lambert. A middle value keeps the world-consistent lightmap
// look while giving the mesh enough shading to read as 3D.
uniform float form_strength : hint_range(0.0, 1.0) = 0.55;

// Per-material multiplicative tint applied to the sampled albedo BEFORE the
// lighting math. Default (1,1,1) leaves the texture untouched (back-compatible
// for character materials). Used by rock_lit.tres to brighten/warm the Synty
// rock palette (which UV-samples a flat mid-grey region) into a stone tone
// that reads at desert biome ambient without changing the source atlas.
uniform vec3 color_tint : source_color = vec3(1.0, 1.0, 1.0);

// --- Ambient occlusion ------------------------------------------------------
// AO is a multiplicative darken on the diffuse lighting (the same place the
// terrain applies its baked AO — voxel_clip). Two independent sources feed it,
// because different model_lit users have different geometry available:
//
//   1. Baked vertex AO (`vertex_ao_strength`) — for meshes that bake real
//      occlusion into vertex COLOR at construction. The procedural tree trunks
//      do this (TreeTrunk.BakeSkeleton): the full branch skeleton is in hand, so
//      we compute genuine concavity — fork crotches where limbs meet, plus root-
//      ground contact — instead of approximating. COLOR.r carries BRIGHTNESS
//      (1 = open, <1 = occluded), so a mesh with no vertex-color array reads as
//      vec4(1.0) = fully open and is unaffected. Off (0) by default so imported
//      meshes that happen to ship stray FBX vertex colors (some Synty rocks do)
//      are ignored unless a material opts in.
//   2. Contact AO (`ao_contact_strength`) — for imported meshes we CAN'T bake
//      (rocks, chests, statues): fragments near the model's base (object-space Y
//      near `ao_base_y`, fading over `ao_height`) darken, faking the ground
//      contact shadow. Pure position ramp, no geometry needed.
//
// We deliberately DON'T fake a downward-facing/hemisphere AO term: the lighting
// here isn't a uniform ambient dome but directional sky + bounced block lights,
// so "faces down → darker" doesn't correspond to anything physical. Concavity is
// the thing worth darkening — baked where we can compute it, contact-approximated
// where we can't.
//
// Runtime gate `model_ao_strength` (global uniform, CVar `model_ao`) scales BOTH
// sources: 0 = off (pre-AO look), 1 = authored, >1 exaggerates to confirm where
// it lands — mirrors the terrain `ao_strength` CVar.
global uniform float model_ao_strength;
// Strength of the baked vertex-COLOR occlusion in [0,1]. 0 = ignore vertex color
// (default — safe for any mesh). Trunk bark materials set this to 1.
uniform float vertex_ao_strength : hint_range(0.0, 1.0) = 0.0;
// Object-space Y of the contact point (model base). 0 works for props whose
// pivot sits on the ground — the convention for the FBX rocks.
uniform float ao_base_y = 0.0;
// Height (object-space units ≈ meters) over which contact AO fades from full at
// the base to none above. ~1.5 grounds a rock without creeping up the model.
uniform float ao_height = 1.5;
// Strength of the contact darken in [0,1]. Authored per material — set to 0 on
// meshes that bake their own AO (the trunks) so the base isn't double-darkened.
uniform float ao_contact_strength : hint_range(0.0, 1.0) = 0.55;

varying vec3 world_pos;
varying vec3 world_normal;
// Object-space Y forwarded for the contact-AO height ramp in fragment().
varying float object_y;
// Baked per-vertex occlusion brightness (COLOR.r): 1 = open, <1 = occluded.
varying float vert_brightness;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	object_y = VERTEX.y;
	vert_brightness = COLOR.r;
	// Uniform-scale rig, so transforming the normal by MODEL_MATRIX (without
	// the inverse-transpose) is correct.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	vec3 base = tex.rgb * color_tint;

	// Editor-preview fallback: light globals aren't registered until the game
	// starts, so a standalone .tres / editor preview gets vec3(0) for
	// light_map_inv_size. Fall back to "full bright" so authoring sees the
	// model's atlas color without a cave-dimmed read. Mirrors particle_lit.
	// (Godot disallows `return` in fragment(), so this is an if/else.)
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		// Push the sample point up half a voxel into the air above the surface
		// so we read the adjacent air voxel's light instead of zero inside a
		// solid (same trick the sprite / particle shaders use).
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		// Wrap-lambert form term from the real surface normal. sun_world_dir
		// points FROM the sun, so the surface-to-sun vector is -sun_world_dir.
		// The wrap (ndl*0.5+0.5) keeps back faces softly lit rather than
		// crushed to black, since the lightmap already carries world occlusion.
		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(normalize(world_normal), to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float form = mix(1.0, wrap, form_strength);

		float cloud = cloud_shadow_ground(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		vec3 block_lit = lm.gba;

		// AO (see uniforms above): baked vertex occlusion (real concavity, where a
		// mesh provides it) plus the contact-height ramp (for un-baked meshes).
		// Summed and clamped — for any given material typically only one source is
		// non-zero. Gated by the runtime model_ao_strength for toggle / verify.
		float baked_ao = (1.0 - vert_brightness) * vertex_ao_strength;
		float contact_ao = (1.0 - smoothstep(ao_base_y, ao_base_y + ao_height, object_y)) * ao_contact_strength;
		float ao = clamp(baked_ao + contact_ao, 0.0, 1.0);
		float ao_factor = 1.0 - ao * model_ao_strength;

		// Same split as sprite_lit_common: ALBEDO carries direct sun (light()
		// scales it by the shadow atlas), EMISSION carries ambient + block.
		// AO multiplies both diffuse halves (matches voxel_clip's baked AO).
		ALBEDO = base * sun_lit * (1.0 - sun_ambient) * ao_factor;
		EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim) * ao_factor;
	}
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas (terrain + this mesh's
	// own cast shadow). Compose cloud shadow as min() so a cast shadow + cloud
	// shadow read as one shadow, not two stacked. Per-source energy gate
	// matches sprite_lit_common / voxel_clip: LIGHT_COLOR = color*energy*PI.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
