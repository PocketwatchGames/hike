shader_type spatial;
// Billboarded, pixel-exact detail sprites scattered across a tree canopy —
// flowers, buds, acorns, berries, etc. (see FoliageMultiMesh.RebuildDetails /
// FoliageDetailData). Distinct from the leaf cards (tree_cards_lit.gdshader)
// in two ways that matter for tiny details:
//
//   1. CAMERA-FACING BILLBOARD. A leaf card is a fixed-orientation quad on the
//      canopy sphere, so a 6-pixel detail painted on one goes edge-on and
//      sub-pixel as the card tilts / the camera pulls back. A billboard always
//      presents its full face at its authored size, so the few pixels always
//      read.
//   2. VIEW-SPACE DEPTH BIAS toward the camera. The canopy is a soup of
//      overlapping alpha-tested cards; a detail sitting on the shell loses the
//      depth fight to whatever leaf card pokes in front of it. Nudging the
//      billboard a few cm nearer in VIEW space lets it win against the leaves
//      it overlaps, while staying small enough that terrain, the trunk, and
//      other trees still occlude it correctly (it only beats near-coplanar
//      geometry, not real occluders).
//
// Pixel-exact sampling (FRAGCOORD-anchored, source texel → screen pixel) is
// ported from sprite_prop_multimesh so the detail stays crisp and shimmer-free
// at any screen size. Lighting / tint / cloud / block-light / player-occlusion
// fade mirror tree_cards_lit so a detail shades consistently with the canopy.
//
// Per-instance channels (baked by FoliageMultiMesh.RebuildDetails):
//   MODEL_MATRIX[3].xyz     : anchor world position (a canopy-shell point)
//   length(MODEL_MATRIX[0]) : billboard width  (world meters)
//   length(MODEL_MATRIX[1]) : billboard height (world meters)
//   INSTANCE_CUSTOM.x       : per-detail phase seed (wind rustle + tint hash)
//   COLOR.r                 : per-cluster tint mix
//   COLOR.g                 : player-occlusion fade-eligibility flag
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, ambient_light_disabled;
// Stencil 16 = "tree" so the terrain depth-outline skips tree pixels (see
// tree_lit.gdshader for the value convention).
stencil_mode write, compare_always, 16;

uniform sampler2D albedo_tex : source_color, filter_linear;
uniform float alpha_cutoff : hint_range(0.0, 1.0) = 0.5;
uniform bool subpixel_aa = true;

// View-space depth bias in meters — pulls the billboard toward the camera so
// it layers in front of the canopy leaves around it. Keep small (a few cm to
// ~0.2m); too large and details start punching through real occluders.
uniform float depth_bias = 0.12;

// Tint — multiplied into the detail texture. Defaults to white so a flower
// keeps its own authored color. Same three-axis variation construction as
// tree_cards_lit (per-tree hash + per-cluster COLOR.r + per-card phase hash),
// though details usually want little variation, so the defaults are low.
uniform vec3 leaf_tint_a : source_color = vec3(1.0);
uniform vec3 leaf_tint_b : source_color = vec3(1.0);
uniform float per_tree_tint_variation : hint_range(0.0, 1.0) = 0.0;
uniform float per_cluster_tint_variation : hint_range(0.0, 1.0) = 0.0;
uniform float per_card_tint_variation : hint_range(0.0, 1.0) = 0.1;
uniform vec3 tree_origin = vec3(0.0);

// Wind — directional bend pushes detail anchors downstream with gusts (height-
// weighted like the leaves), plus a small per-detail rustle so they aren't
// dead-still. Shares the same globals / per-tree multipliers as tree_cards_lit.
uniform float sway_amplitude = 0.04;
uniform float wind_bend_strength = 1.0;
uniform float canopy_height = 4.0;

// -- Global uniforms shared with model_lit / sprite_lit / tree_cards_lit ----

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

global uniform vec3 wind_dir;
global uniform float wind_amplitude;
global uniform float wind_phase;

// Player-occlusion fade volume — identical semantics to tree_cards_lit so a
// detail dithers out in the camera→player cutaway in lockstep with the leaves
// around it (no detail left hovering in the scooped-out hole).
global uniform vec3 foliage_camera_world;
global uniform vec3 foliage_player_feet_world;
global uniform vec3 foliage_player_head_world;
global uniform float foliage_player_fade_radius;
global uniform float foliage_player_fade_soft_edge;
global uniform vec2 foliage_player_fade_aspect;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

varying flat vec2 anchor_px;
varying flat vec3 world_origin;
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
varying flat ivec2 sprite_size;
varying vec3 v_card_tint;
varying float v_fade_eligible;
varying vec3 frag_world_pos;

// iq's hash13 — same construction as tree_cards_lit, for the tint axes.
float hash13(vec3 p) {
	p = fract(p * vec3(0.1031, 0.1030, 0.0973));
	p += dot(p, p.yzx + 33.33);
	return fract((p.x + p.y) * p.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	float world_w = length(MODEL_MATRIX[0].xyz);
	float world_h = length(MODEL_MATRIX[1].xyz);

	// Wind: height-weighted directional bend + a small per-detail rustle.
	float height01 = clamp((world_origin.y - tree_origin.y) / max(canopy_height, 1e-3), 0.0, 1.0);
	world_origin += wind_dir * (wind_amplitude * wind_bend_strength * height01 * height01);
	float phase = INSTANCE_CUSTOM.x;
	world_origin.x += sin(wind_phase + phase * TAU) * sway_amplitude;

	// Upright yaw billboard — face the camera around world Y, stay vertical.
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(world_up, cam_back));
	vec3 sprite_up = world_up;

	vec3 vertex_world = world_origin
		+ VERTEX.x * world_w * sprite_right
		+ VERTEX.y * world_h * sprite_up;
	frag_world_pos = vertex_world;

	// View-space depth bias toward camera (camera looks down -Z, so a larger
	// .z is nearer). Applied to BOTH the rendered vertex and the pixel-exact
	// anchor basis below, so they live in the same biased frame and the
	// source-texel → screen-pixel mapping stays correct.
	vec4 vertex_view = VIEW_MATRIX * vec4(vertex_world, 1.0);
	vertex_view.z += depth_bias;
	POSITION = PROJECTION_MATRIX * vertex_view;

	sprite_size = textureSize(albedo_tex, 0);
	float src_px_w = world_w / max(float(sprite_size.x), 1.0);
	float src_px_h = world_h / max(float(sprite_size.y), 1.0);
	vec4 origin_view = VIEW_MATRIX * vec4(world_origin, 1.0);
	vec4 right_view = VIEW_MATRIX * vec4(world_origin + src_px_w * sprite_right, 1.0);
	vec4 up_view = VIEW_MATRIX * vec4(world_origin + src_px_h * sprite_up, 1.0);
	origin_view.z += depth_bias;
	right_view.z += depth_bias;
	up_view.z += depth_bias;
	vec4 origin_clip = PROJECTION_MATRIX * origin_view;
	vec4 right_clip = PROJECTION_MATRIX * right_view;
	vec4 up_clip = PROJECTION_MATRIX * up_view;
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	vec2 right_ndc = right_clip.xy / right_clip.w;
	vec2 up_ndc = up_clip.xy / up_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
	sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
	sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;

	// Tint mix — same three additive axes around 0.5 as tree_cards_lit.
	float tree_hash = hash13(tree_origin * 0.7);
	float cluster_hash = hash13(vec3(COLOR.r * 13.7, tree_origin.x * 0.31, tree_origin.z * 0.31));
	float card_hash = hash13(vec3(phase * 17.3, tree_origin.x * 0.29, tree_origin.z * 0.29));
	float final_mix = clamp(0.5
		+ (tree_hash - 0.5) * per_tree_tint_variation
		+ (cluster_hash - 0.5) * per_cluster_tint_variation
		+ (card_hash - 0.5) * per_card_tint_variation, 0.0, 1.0);
	v_card_tint = mix(leaf_tint_a, leaf_tint_b, final_mix);

	v_fade_eligible = COLOR.g;
}

void fragment() {
	// Player-occlusion fade — verbatim from tree_cards_lit so details dither
	// out inside the camera→player cutaway exactly like the leaves they sit
	// among. See tree_cards_lit.gdshader for the full rationale.
	if (v_fade_eligible > 0.5 && foliage_player_fade_radius > 0.01) {
		vec3 player_axis = foliage_player_head_world - foliage_player_feet_world;
		float axis_len_sq = max(dot(player_axis, player_axis), 1e-4);
		float t_axis = clamp(dot(frag_world_pos - foliage_player_feet_world, player_axis) / axis_len_sq, 0.0, 1.0);
		vec3 player_axis_pt = foliage_player_feet_world + player_axis * t_axis;

		vec3 seg_dir = player_axis_pt - foliage_camera_world;
		float seg_len_sq = max(dot(seg_dir, seg_dir), 1e-4);
		float t_seg = clamp(dot(frag_world_pos - foliage_camera_world, seg_dir) / seg_len_sq, 0.0, 1.0);
		vec3 closest = foliage_camera_world + seg_dir * t_seg;

		vec3 perp = frag_world_pos - closest;
		float horiz = length(perp.xz);
		float vert = abs(perp.y);
		vec2 aspect = max(foliage_player_fade_aspect, vec2(1e-3));
		float dist = length(vec2(horiz / aspect.x, vert / aspect.y));

		float n = sin(frag_world_pos.x * 1.3 + frag_world_pos.z * 0.7) * 0.3
		        + sin(frag_world_pos.x * 0.5 - frag_world_pos.z * 1.7) * 0.2
		        + sin(frag_world_pos.x * 2.1 + frag_world_pos.y * 1.1 + frag_world_pos.z * 0.4) * 0.15;
		float perturbed_dist = dist + n;

		float r = foliage_player_fade_radius;
		float soft = max(foliage_player_fade_soft_edge, 1e-3);
		float visibility = clamp((perturbed_dist - r) / soft, 0.0, 1.0);

		if (visibility < 0.999) {
			const float bayer4[16] = float[](
				 0.0/16.0,  8.0/16.0,  2.0/16.0, 10.0/16.0,
				12.0/16.0,  4.0/16.0, 14.0/16.0,  6.0/16.0,
				 3.0/16.0, 11.0/16.0,  1.0/16.0,  9.0/16.0,
				15.0/16.0,  7.0/16.0, 13.0/16.0,  5.0/16.0
			);
			ivec2 ip = ivec2(FRAGCOORD.xy);
			int bx = ip.x & 3;
			int by = ip.y & 3;
			if (visibility <= bayer4[by * 4 + bx]) {
				discard;
			}
		}
	}

	// Pixel-exact source-texel lookup (ported from sprite_prop_multimesh):
	// invert the screen-space sprite basis to recover the source pixel under
	// this fragment, so one source texel maps to an integer block of screen
	// pixels regardless of the billboard's on-screen size.
	vec2 d = FRAGCOORD.xy - anchor_px;
	float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
	float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
	vec2 sxy = vec2(
		(sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
		(sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
	);
	float half_w = float(sprite_size.x / 2);
	vec2 src_px = vec2(sxy.x + half_w, -sxy.y + float(sprite_size.y));

	ivec2 tex_coord = ivec2(int(floor(src_px.x)), int(floor(src_px.y)));
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec2 px_aa;
	if (subpixel_aa) {
		vec2 seam = floor(src_px) + 0.5;
		vec2 dp = max(fwidth(src_px), vec2(1e-6));
		px_aa = seam + clamp((src_px - seam) / dp, -0.5, 0.5);
	} else {
		px_aa = floor(src_px) + 0.5;
	}
	px_aa = clamp(px_aa, vec2(0.5), vec2(sprite_size) - vec2(0.5));

	vec2 tex_size = vec2(textureSize(albedo_tex, 0));
	vec4 tex = texture(albedo_tex, px_aa / tex_size);
	if (tex.a < alpha_cutoff) {
		discard;
	}
	vec3 base = tex.rgb * v_card_tint;

	// Flat billboard lighting — same split-EMISSION model as sprite_lit /
	// sprite_prop_multimesh (no wrap-lambert form term; a billboard has a
	// single upright normal, so the leaves' sphere-shading doesn't apply).
	NORMAL = vec3(0.0, 1.0, 0.0);
	bool globals_ready = length(light_map_inv_size) > 1e-6;
	if (!globals_ready) {
		ALBEDO = base;
		EMISSION = vec3(0.0);
	} else {
		vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
		vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
		vec4 lm = textureLod(light_map, uvw, 0.0);
		float sun_mask = pow(lm.r, light_falloff_exp);
		float cloud = cloud_shadow_ground(frag_world_pos, TIME);
		float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
		vec3 sun_lit = sun_mask * sun_intensity * sun_color;
		vec3 block_lit = lm.gba * block_light_shadow_factor(world_origin);
		ALBEDO = base * sun_lit * (1.0 - sun_ambient);
		EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim);
	}
}

void light() {
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float cloud = cloud_shadow_ground(frag_world_pos, TIME);
	float cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
