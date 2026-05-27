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

varying vec3 world_pos;
varying vec3 world_normal;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	// Uniform-scale rig, so transforming the normal by MODEL_MATRIX (without
	// the inverse-transpose) is correct.
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
}

void fragment() {
	vec4 tex = texture(albedo_tex, UV);
	vec3 base = tex.rgb;

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

		// Same split as sprite_lit_common: ALBEDO carries direct sun (light()
		// scales it by the shadow atlas), EMISSION carries ambient + block.
		ALBEDO = base * sun_lit * (1.0 - sun_ambient);
		EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim);
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
