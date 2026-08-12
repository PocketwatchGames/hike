shader_type spatial;
// Twigs card shader — a single billboard quad spawned at each branch tip by
// FoliageMultiMesh.RebuildBranches. Anchored at the quad's bottom edge (Y=0
// in object space, where the cylinder branch ends) and ROTATED around that
// attach point by the global wind/rustle clock so the free tip swings into
// the cluster.
//
// Two intentional differences from tree_cards_lit.gdshader:
//
//   1. NO foliage cutaway. The leaf cards dither out when the player walks
//      under the canopy (foliage_camera_world / foliage_player_*_world fade
//      volume); the twigs MUST survive that. The whole reason the twigs are
//      here is to give the player a visible vestige of the branch when the
//      leaves are gone — without them a deep forest reads as a row of hard
//      cylinder ends pointing at the camera. Including the cutaway block
//      would defeat the purpose. This is ONLY about that fade — the ceiling
//      clip below is a separate system and twigs take part in it normally.
//
//   2. Real rotation around the attach edge instead of an X-shear. Leaves
//      cheat with `VERTEX.x += sin(...)` because each leaf is small and
//      the shear reads as a wobble; a 2m twigs quad needs to actually
//      rotate around its base or the bottom edge tears off the cylinder
//      branch's end cap as the tip swings.
render_mode ambient_light_disabled, depth_draw_opaque, cull_disabled;
// Stencil 16 = "tree" so the terrain depth-outline skips tree pixels (see
// tree_lit.gdshader for the value convention).
stencil_mode write, compare_always, 16;

uniform sampler2D albedo_tex : source_color, filter_linear;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;

// Multiplied with the texture RGB at fragment time. Defaults to white so the
// authored texture's colors pass through unchanged; per-tree tint variation
// is layered on top via the tree_origin hash.
uniform vec3 tint : source_color = vec3(1.0, 1.0, 1.0);

// Rustle sway — peak rotation angle (radians) at the free tip about the
// attach point. tip_mask scales this linearly across the quad so the
// attach edge stays glued to the cylinder branch. Calibrated against the
// leaves' shear-amplitude so an authored twig reads as visibly STIFFER
// than the surrounding foliage at the same canopy height — twigs are
// woody, leaves are sheets.
uniform float sway_amplitude = 0.1;

// World-space directional bend along wind_dir, gated by tip_mask AND by a
// quadratic height term (height01²) — same curve tree_cards_lit uses so
// twigs and leaves follow one storm response, just at a stiffer baseline.
// Default well under the leaves' 1.0 because twigs are heavier per unit
// tip area than leaf cards.
uniform float wind_bend_strength = 0.35;

// canopy_height matches the leaves' uniform — stamped per-tree by
// FoliageMultiMesh from the cluster bounds. Drives the height01 term in
// the wind bend so twigs on low branches barely move while twigs near the
// top lean the most, mirroring leaf behavior.
uniform float canopy_height = 4.0;

// Wrap-lambert form-strength — mirrors tree_cards_lit so twigs lit by the
// same sun read with consistent contrast. Lower than the leaves' default
// because a twigs card has so little surface area that strong shadow-side
// darkening reads as a black smudge instead of a form cue.
uniform float form_strength : hint_range(0.0, 1.0) = 0.5;

// Per-tree position hashed into a small tint shift, so a forest of identical
// twigs cards still reads as varied. Stamped per-tree by FoliageMultiMesh
// in the same Rebuild pass that sets it on the leaves material.
uniform vec3 tree_origin = vec3(0.0);
uniform float per_tree_tint_variation : hint_range(0.0, 1.0) = 0.25;

// -- Global uniforms shared with model_lit / sprite_lit / tree_cards_lit ---

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

global uniform vec3 fill_a_world_dir;
global uniform vec3 fill_b_world_dir;
global uniform vec3 fill_a_color;
global uniform vec3 fill_b_color;

// Wind globals shared with tree_cards_lit / detail_sprite / voxel_water.
// wind_dir is normalized horizontal; wind_amplitude is meters of bend at the
// reference height; wind_phase is the pre-integrated rustle phase that
// SkyController shares across all foliage so storm gusts pulse in sync.
global uniform vec3 wind_dir;
global uniform float wind_amplitude;
global uniform float wind_phase;

global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

// Ceiling cutaway, anchored on the twig's attach point (the branch tip) so it
// cuts as a unit with the branch it hangs off. Globals owned by GameCamera;
// declare before the include.
global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"

varying vec3 world_pos;
varying vec3 world_normal;
varying vec3 v_card_tint;
varying flat vec3 model_origin;

float hash13(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

void vertex() {
	// QuadMesh in this project: bottom edge at Y=0 (the attach point), top
	// at Y=1, UV.y=1 at bottom and UV.y=0 at top. tip_mask is 1 at the free
	// tip and 0 at the attach edge — same convention tree_cards_lit uses.
	float tip_mask = 1.0 - UV.y;

	// Per-twig phase from the model's world-space origin (each MeshInstance3D
	// for a twigs quad lives at the branch tip position, so MODEL_MATRIX[3]
	// gives a stable per-tip identifier). Mixing in tree_origin keeps two
	// trees with similarly placed tips from rustling in lock-step.
	vec3 attach_world = MODEL_MATRIX[3].xyz;
	float phase = hash13(attach_world + tree_origin * 0.13);

	// Rotation around the LOCAL X axis (the quad's right axis), pivoting at
	// Y=0. Pure rotation, not a shear — the attach edge stays at Y=0 and
	// the free tip arcs through the YZ plane.
	float angle = sin(wind_phase + phase * TAU) * sway_amplitude * tip_mask;
	float c = cos(angle);
	float s = sin(angle);
	vec3 rotated = vec3(VERTEX.x, VERTEX.y * c - VERTEX.z * s, VERTEX.y * s + VERTEX.z * c);
	VERTEX = rotated;

	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	model_origin = attach_world;

	// World-space directional bend along wind_dir, gated by tip_mask AND by
	// height01² — twigs on low branches barely move, twigs near the top
	// lean noticeably. Same curve tree_cards_lit applies to leaves so the
	// whole canopy + twig system responds to storms in one cohesive arc.
	float height01 = clamp((world_pos.y - tree_origin.y) / max(canopy_height, 1e-3), 0.0, 1.0);
	float bend_weight = tip_mask * height01 * height01;
	world_pos += wind_dir * (wind_amplitude * wind_bend_strength * bend_weight);

	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_pos, 1.0);
	world_normal = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);

	// Per-tree tint hash — same construction as tree_cards_lit's tree-axis
	// shift, kept signed around 1.0 so the default tint is unchanged at zero
	// variation.
	float tree_hash = hash13(tree_origin * 0.7);
	float tree_offset = (tree_hash - 0.5) * per_tree_tint_variation;
	v_card_tint = tint * (1.0 + tree_offset);
}

void fragment() {
	if (clip_dither_should_discard_anchored(model_origin, world_pos, FRAGCOORD.xy)) {
		discard;
	}

	vec4 tex = texture(albedo_tex, UV);
	if (tex.a < alpha_cutoff) {
		discard;
	}
	vec3 base = tex.rgb * v_card_tint;

	vec3 n = world_normal;
	if (!FRONT_FACING) {
		n = -n;
	}

	// Editor-preview fallback — light globals aren't registered until the
	// game starts; mirror tree_cards_lit's full-bright authoring path.
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		vec3 light_sample_pos = world_pos + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);

		vec3 to_sun = -normalize(sun_world_dir);
		float ndl = dot(n, to_sun);
		float wrap = ndl * 0.5 + 0.5;
		float sun_active = clamp(sun_intensity, 0.0, 1.0);
		float effective_form_strength = form_strength * sun_active;
		float form = mix(1.0, wrap, effective_form_strength);

		float cloud = cloud_shadow_ground_gated_up(world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;

		vec3 sun_lit = sun_mask * sun_intensity * sun_color * form;
		vec3 block_lit = lm.gba * block_light_shadow_factor(world_pos);

		float fill_a_facing = max(0.0, dot(n, -fill_a_world_dir));
		float fill_b_facing = max(0.0, dot(n, -fill_b_world_dir));
		vec3 fill_a_tint = mix(fill_a_color, vec3(1.0), fill_a_facing);
		vec3 fill_b_tint = mix(fill_b_color, vec3(1.0), fill_b_facing);
		vec3 base_filled = base * fill_a_tint * fill_b_tint;

		ALBEDO = base_filled * sun_lit * (1.0 - sun_ambient);
			EMISSION = base_filled * (block_lit + sun_lit * sun_ambient * cloud_dim);
	}
}

void light() {
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground_gated_up(world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
