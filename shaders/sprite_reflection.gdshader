shader_type spatial;
// Transparent queue — same regime as sprite_prop_reflection_multimesh.
// Stencil-clipped to water-visible pixels (water shader writes 4 in
// voxel_water.gdshader). depth_draw_always + default depth test, with
// POSITION.z overridden in vertex() to the SOURCE's projected depth so
// that:
//   1. Lake-floor depth (recorded in the buffer because water uses
//      depth_draw_never) doesn't occlude us — we stamp source-depth in
//      front of it, and the stencil keeps us in water pixels.
//   2. Reflections z-sort against EACH OTHER through the depth buffer
//      (closer source = smaller depth = wins) — and crucially, against
//      sprite_prop_reflection_multimesh.gdshader instances at the same
//      render_priority, so e.g. the player reflection no longer gets
//      hidden behind a tree's prop reflection just because Godot's
//      transparent-queue sort picked a different multimesh AABB center.
// Water no longer composites blue tint over us via screen_tex (we draw
// AFTER water now), so the fragment shader does its own depth-into-water
// mix toward water_deep_tint — same approach as the prop reflection.
render_mode blend_mix, depth_draw_always, cull_disabled, ambient_light_disabled, skip_vertex_transform, unshaded;
stencil_mode read, compare_equal, 4;

// Flipped-vertical companion to sprite_lit.gdshader, used for water
// reflections. Clones the vertex/fragment logic but:
//   - Negates VERTEX.y so the quad extends DOWNWARD from the anchor,
//     which is placed at (sprite.x, 2*water_y - sprite.y, sprite.z).
//   - Flips tex_coord.y so the texture reads top↔bottom accordingly.
//   - Discards fragments above water_y — handles the partial-submerge
//     case where a swimming sprite's reflection anchor ends up above
//     the water surface. Only the submerged portion of the reflection
//     survives, which corresponds to the above-water portion of the
//     original sprite reflected downward.
//   - Skips the light() / ATTENUATION pass. Reflection albedo uses the
//     sprite's own lighting (sampled at the SOURCE position, not the
//     reflection position) so the reflection is lit the same as the
//     sprite it mirrors — otherwise an underground reflection would
//     read as bright daylight if the lightmap under the water is bright.
//   - Tints/dims the result so the reflection reads as a reflection
//     rather than a second copy of the sprite.

// Per-sprite values are `instance uniform` so all reflections sharing a
// texture share one material and batch. sprite_texture stays a regular
// uniform since sampler2D can't be instance in Godot 4.
uniform sampler2D sprite_texture : source_color, filter_nearest;
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"
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
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
// Mirror in lockstep with the visible sprite.
instance uniform bool sprite_mirror = false;
// Match the visible sprite's roll-toward-terrain-normal so a grass blade
// leaning into a slope has a reflection that leans the same way. Same
// uniforms and math as sprite_lit.gdshader; defaults leave behaviour
// unchanged for materials that don't set them.
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"
global uniform float sprite_chunky;
global uniform float sprite_stretch;

// Ripple-driven source-pixel jitter: sampled per-fragment at the
// reflection's WORLD XZ + scrolling ripple_offset_a so adjacent fragments
// get adjacent noise samples (smooth shimmer), not the same uniform shift
// across the whole sprite. Combined with the water shader's refraction
// wobble (which shifts the silhouette as a whole) this gives both
// macro-wobble and internal shimmer — full "rippled reflection" feel.
// All three uniforms are globals from voxel_water.gdshader / SkyController.
global uniform float ripple_scale_a;
global uniform float ripple_scale_b;
global uniform vec2 ripple_offset_a;
global uniform vec2 ripple_offset_b;
global uniform float ripple_strength;

// Coarse wind-factor 3D texture (see voxel_water.gdshader). Sampled at
// the reflection's water-surface intersection so reflections in caves /
// indoors stop shimmering — without this gate they ignore the wind grid
// and ripple in lockstep with the open-air reflection on the same
// material.
global uniform sampler3D wind_map : filter_linear, repeat_enable;
global uniform vec3 wind_map_origin;
global uniform vec3 wind_map_inv_size;

// Ripple normal-map textures — same pair voxel_water samples, bound by
// LitSprite.EnsureReflection on the shared reflection material. Without
// these, the physics-derived UV jitter below collapses (default sampler
// returns white → constant tilt at every fragment, no shimmer).
uniform sampler2D ripple_tex_a : filter_linear, repeat_enable, hint_normal;
uniform sampler2D ripple_tex_b : filter_linear, repeat_enable, hint_normal;

// World Y of the reflecting water surface. The reflection anchor is at
// (2*water_y - source.y) and vertices extend downward, so the clip
// test `world_y > water_y` removes the above-water slab that appears
// when the source sprite is partially submerged.
instance uniform float water_y = -1e30;
// World position of the sprite being reflected — used to sample the
// lightmap at the source, not at the reflection, so underground water's
// reflection of a sunlit sprite still reads correctly.
instance uniform vec3 source_world_pos = vec3(0.0);
// Global dimming that sells "this is a reflection, not a second sprite."
// Pushed by SkyController so weather (muddiness, fog, light level) drives
// how much reflections are dimmed — clear bright water shows them at full
// brightness; murky / overcast water dims them toward zero.
global uniform float reflection_tint;
// Maximum source-pixel jitter applied to the reflection's tex_coord lookup
// at full ripple_strength. SkyController scales by weather/wind so calm
// water shows a rigid mirror, choppy water visibly distorts the reflection.
global uniform float reflection_pixel_jitter_max;

// Water-tint globals (declared and set by SkyController for voxel_water).
// We sit on top of water in the transparent queue now — water's blend_mix
// doesn't get to tint us anymore — so the fragment shader does its own
// depth-into-water mix toward water_deep_tint.
global uniform vec3 water_shallow_tint : source_color;
global uniform vec3 water_deep_tint : source_color;
global uniform float water_depth_scale;
global uniform float water_alpha_max;
global uniform float water_edge_opacity;
// Drives reflection ALPHA so muddy water lets its own surface dominate
// instead of stamping the sprite reflection over it. See the prop
// reflection shader for the full rationale.
global uniform float water_muddiness;

// Same reflection_debug_mode visualizer the prop-reflection shader has.
// CVar `reflection_debug` (1..7) replaces ALBEDO with diagnostic colors
// so we can pinpoint which intermediate is breaking the ripple effect.
// Modes match the prop shader exactly — see CVars.cs for documentation.
global uniform int reflection_debug_mode;
// Match sprite_lit's discovery-fade + silhouette uniforms so a fading /
// silhouetted sprite's water reflection stays consistent with its body.
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);

// Procedural noise (matching voxel_water.gdshader's hash13/vnoise) — used
// to sample a smoothly varying jitter from the reflection's world XZ +
// scrolled ripple_offset_a so the reflection wobbles in lockstep with the
// water surface above it without needing the actual normal-map textures.
// 2D scalar hash for the per-source depth epsilon — keeps two reflections
// at the exact same camera distance from z-fighting.
float refl_hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

float refl_hash13(vec3 p)
{
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float refl_vnoise(vec3 p)
{
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = refl_hash13(i); float n100 = refl_hash13(i + vec3(1,0,0));
	float n010 = refl_hash13(i + vec3(0,1,0)); float n110 = refl_hash13(i + vec3(1,1,0));
	float n001 = refl_hash13(i + vec3(0,0,1)); float n101 = refl_hash13(i + vec3(1,0,1));
	float n011 = refl_hash13(i + vec3(0,1,1)); float n111 = refl_hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

varying flat vec2 anchor_px;
varying flat vec3 world_origin;
// Uniform scale from MODEL_MATRIX; see sprite_lit for rationale.
varying flat float instance_scale;
// Same screen-space basis varyings sprite_lit uses, with the Y-flip baked
// in (see vertex). Fragment inverts this 2x2 matrix to recover the source
// pixel for each reflection fragment, which keeps texelFetch exact when
// the reflection is rolled to match a sloped terrain normal.
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
// Interpolated fragment world position — XZ feeds the per-fragment ripple
// jitter sampling so adjacent fragments produce smoothly varying noise
// (not a uniform shift across the whole sprite, which would just be
// redundant with the water shader's refraction wobble). Y feeds the
// water-plane clip below (replaces the old derived-from-d math, which
// broke when the reflection rolled to terrain normals).
varying vec3 vertex_world_var;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	instance_scale = length(MODEL_MATRIX[0].xyz);

	// Same yaw+roll-toward-terrain-normal math sprite_lit uses. See
	// sprite_lit.gdshader for the reasoning; the only difference here is
	// that the final axes have their world-Y component flipped — that's
	// the reflection across the water plane.
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
	vec3 sprite_forward = cross(sprite_right_yaw, world_up);

	vec3 n = normalize(terrain_normal);
	vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
	float right_comp = dot(n_in_plane, sprite_right_yaw);
	float up_comp = dot(n_in_plane, world_up);
	float roll = atan(right_comp, up_comp) * clamp(align_to_terrain, 0.0, 1.0);
	float cr = cos(roll);
	float sr = sin(roll);

	vec3 sprite_right_rolled = cr * sprite_right_yaw - sr * world_up;
	vec3 sprite_up_rolled = sr * sprite_right_yaw + cr * world_up;

	float stretch = max(sprite_stretch, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	// Camera-pitch stretch on the Y component of each rolled axis (same
	// trick sprite_lit uses), then flip Y to mirror across the water
	// plane. X/Z components pass through unchanged — the reflection
	// preserves horizontal shape and only negates vertical.
	vec3 right_flipped = vec3(sprite_right_rolled.x, -sprite_right_rolled.y * stretch, sprite_right_rolled.z);
	vec3 up_flipped = vec3(sprite_up_rolled.x, -sprite_up_rolled.y * stretch, sprite_up_rolled.z);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * right_flipped
		+ VERTEX.y * chunky * instance_scale * up_flipped;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	// Override Z with a NEAR-PLANE SLAB depth that sits in front of any
	// scene geometry, with closer-source reflections at slightly larger
	// ndc.z so they win depth-test against farther ones. See the matching
	// block in sprite_prop_reflection_multimesh.gdshader for the full
	// rationale — short version: a constant per-instance depth based on
	// the source's surface-projected position can still land BEHIND
	// lake-floor depth at individual screen pixels (lake floor varies per
	// pixel; our depth doesn't), which would reject the reflection
	// fragment-by-fragment. Remapping into [0.999, 1.0] keeps every
	// reflection ahead of any scene geometry while preserving relative
	// source-distance order between reflections.
	vec3 surface_anchor = vec3(source_world_pos.x, water_y, source_world_pos.z);
	vec4 src_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(surface_anchor, 1.0);
	float surface_ndc_z = src_clip.z / src_clip.w;
	float ndc_z = mix(0.999, 1.0, clamp(surface_ndc_z, 0.0, 1.0));
	float src_eps = (refl_hash12(source_world_pos.xz + vec2(0.137, 0.293)) - 0.5) * 1e-5;
	ndc_z += src_eps;
	POSITION.z = ndc_z * POSITION.w;

	vertex_world_var = vertex_world;

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * right_flipped, 1.0);
	vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * up_flipped, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	vec2 right_ndc = right_clip.xy / right_clip.w;
	vec2 up_ndc = up_clip.xy / up_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
	sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
	sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}

void fragment() {
	// Master disable: when SkyController pushes reflection_tint == 0
	// (CVar `sprite_reflections_disabled` true, or fully muddy/dim
	// conditions), skip the entire fragment instead of rendering opaque
	// black. blend_mix would composite ALBEDO=(0,0,0)*ALPHA=1 as a black
	// silhouette over the water otherwise.
	if (reflection_tint <= 0.0) {
		discard;
	}

	// Ceiling-cutaway: the source sprite discards above camera_clip, so
	// match here. Anchor y is the REFLECTION anchor, below water; compare
	// against source_world_pos for the true cutaway relationship.
	if (clip_dither_should_discard(source_world_pos, FRAGCOORD.xy)) {
		discard;
	}

	// World-Y > water_y discard fires only for the IN-WATER case
	// (above_water <= 0). For above-water sources we now anchor at the
	// source's base (localY = 0 in LitSprite.UpdateReflection) — the
	// reflection's geometry sits entirely above water in world Y, and
	// the stencil mask alone clips us to water-visible pixels. Matches
	// sprite_prop_reflection_multimesh.gdshader's conditional discard.
	float above_water_chk = source_world_pos.y - water_y;
	if (above_water_chk <= 0.0 && vertex_world_var.y > water_y + 0.001) {
		discard;
	}

	// Invert the screen-space basis to recover the source-pixel offset of
	// this fragment, same trick sprite_lit uses. For align_to_terrain=0
	// the basis collapses to (1,0) / (0,+1) (the reflection's quad extends
	// down in world, which is +y in Godot's top-down FRAGCOORD), and this
	// reproduces the old d.x / d.y mapping exactly.
	vec2 d = FRAGCOORD.xy - anchor_px;
	float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
	float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
	vec2 sxy = vec2(
		(sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
		(sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
	);
	// Intermediates captured for the debug visualizer below. Defaulted
	// to safe zeros so the short-circuit cases (ripple_strength==0 or
	// vy near horizontal) still display deterministically in the modes.
	vec2 dbg_ripple_tilt = vec2(0.0);
	vec2 dbg_surface_xz = vec2(0.0);
	float dbg_source_above_water = 0.0;
	float dbg_path_len = 0.0;
	vec2 dbg_shift_world_xz = vec2(0.0);

	vec2 jitter = vec2(0.0);
	if (ripple_strength > 1e-4) {
		vec3 cam_fwd_world = -normalize(mat3(INV_VIEW_MATRIX) * vec3(0.0, 0.0, 1.0));
		float vy = cam_fwd_world.y;
		if (abs(vy) > 1e-4) {
			float t_surf = (water_y - vertex_world_var.y) / vy;
			vec2 surface_xz = vertex_world_var.xz + cam_fwd_world.xz * t_surf;
			dbg_surface_xz = surface_xz;

			vec2 uv_a = surface_xz * ripple_scale_a + ripple_offset_a;
			vec2 uv_b = surface_xz * ripple_scale_b + ripple_offset_b;
			vec3 na_raw = texture(ripple_tex_a, uv_a).rgb * 2.0 - 1.0;
			vec3 nb_raw = texture(ripple_tex_b, uv_b).rgb * 2.0 - 1.0;
			vec3 na = vec3(na_raw.r, na_raw.b, na_raw.g);
			vec3 nb = vec3(nb_raw.r, nb_raw.b, nb_raw.g);
			vec3 n_combined = normalize(na + nb);
			// Sample the wind subgrid at the reflection's water-surface
			// intersection — same trilinear gating as voxel_water.gdshader
			// so the reflection ripples fade in lockstep with the surface.
			vec3 wind_sample_pos = vec3(surface_xz.x, water_y, surface_xz.y);
			vec3 wind_uvw = (wind_sample_pos - wind_map_origin) * wind_map_inv_size;
			// .a holds WindFactor (RGB now carries per-cell wind velocity).
			float wind_factor = texture(wind_map, wind_uvw).a;
			vec2 ripple_tilt = mix(vec2(0.0), n_combined.xz, ripple_strength * wind_factor);
			dbg_ripple_tilt = ripple_tilt;

			float chunky = max(sprite_chunky, 1e-6);
			float world_dim = chunky * instance_scale;
			float pixel_world_w = world_dim / max(float(sprite_size.x), 1.0);
			float pixel_world_h = world_dim / max(float(sprite_size.y), 1.0);
			float source_pixel_h = sxy.y * pixel_world_h;
			// Treat the source as if its base sits AT the waterline for
			// ripple-amplitude purposes — matches the "feet-at-waterline"
			// anchor LitSprite uses for above-water sources. Including the
			// real (source.y - water_y) offset would make a player standing
			// on a 5m cliff produce ~7m path_len and obliterate the
			// reflection sprite with massive UV shifts. With this scheme
			// ripple amplitude is bounded by the sprite's own world height,
			// which is what reads as physically plausible shimmer.
			float source_above_water = source_pixel_h;
			dbg_source_above_water = source_above_water;

			float path_len = source_above_water / max(abs(vy), 0.05);
			dbg_path_len = path_len;

			vec2 shift_world_xz = 2.0 * ripple_tilt * path_len;
			dbg_shift_world_xz = shift_world_xz;

			vec2 sprite_right_xz = vec2(INV_VIEW_MATRIX[0].x, INV_VIEW_MATRIX[0].z);
			float right_len = length(sprite_right_xz);
			if (right_len > 1e-4) {
				sprite_right_xz /= right_len;
			}
			float jitter_u = dot(shift_world_xz, sprite_right_xz) / pixel_world_w;

			vec2 cam_fwd_xz = cam_fwd_world.xz;
			float cam_fwd_xz_len = length(cam_fwd_xz);
			float jitter_v = 0.0;
			if (cam_fwd_xz_len > 1e-4) {
				cam_fwd_xz /= cam_fwd_xz_len;
				jitter_v = dot(shift_world_xz, cam_fwd_xz) / pixel_world_h;
			}

			jitter = vec2(jitter_u, jitter_v);
		}
	}

	// Debug visualizer — set ALBEDO to a diagnostic color and skip the
	// remainder of the fragment. Wraps the rest of the body in an else
	// branch since GLSL `return` isn't allowed inside Godot's fragment
	// processor. Modes match sprite_prop_reflection_multimesh.gdshader.
	if (reflection_debug_mode > 0) {
		vec3 dbg = vec3(0.0);
		if (reflection_debug_mode == 1) {
			dbg = vec3(dbg_ripple_tilt * 0.5 + 0.5, 0.0);
		} else if (reflection_debug_mode == 2) {
			dbg = vec3(fract(dbg_surface_xz.x * 0.1), fract(dbg_surface_xz.y * 0.1), 0.0);
		} else if (reflection_debug_mode == 3) {
			dbg = vec3(clamp(dbg_source_above_water / 16.0, 0.0, 1.0), 0.0, 0.0);
		} else if (reflection_debug_mode == 4) {
			dbg = vec3(clamp(dbg_path_len / 64.0, 0.0, 1.0), 0.0, 0.0);
		} else if (reflection_debug_mode == 5) {
			float mag = length(dbg_shift_world_xz);
			dbg = vec3(clamp(mag / 4.0, 0.0, 1.0), 0.0, 0.0);
		} else if (reflection_debug_mode == 6) {
			dbg = vec3(clamp(jitter.x / 32.0 + 0.5, 0.0, 1.0),
			           clamp(jitter.y / 32.0 + 0.5, 0.0, 1.0),
			           0.0);
		} else if (reflection_debug_mode == 7) {
			dbg = vec3(ripple_strength, 0.0, 0.0);
		}
		ALBEDO = dbg;
		ALPHA = 1.0;
	} else {

	// Reflections read source image top↔bottom:
	//   sxy.y ≈ 0               → tex_coord.y = sprite_size.y - 1 (feet)
	//   sxy.y ≈ sprite_size.y-1 → tex_coord.y = 0                 (head)
	ivec2 tex_coord = ivec2(
		int(floor(sxy.x + jitter.x)) + sprite_size.x / 2,
		sprite_size.y - 1 - int(floor(sxy.y + jitter.y))
	);
	if (sprite_mirror) {
		tex_coord.x = sprite_size.x - 1 - tex_coord.x;
	}
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
	if (tex.a < 0.5) {
		discard;
	}

	// Match sprite_lit's dither stipple so a fading mob's reflection stipples
	// in lockstep — avoids a solid reflection next to a half-dithered body.
	if (visibility < 1.0) {
		const mat4 bayer = mat4(
			vec4( 0.0,  8.0,  2.0, 10.0),
			vec4(12.0,  4.0, 14.0,  6.0),
			vec4( 3.0, 11.0,  1.0,  9.0),
			vec4(15.0,  7.0, 13.0,  5.0)
		) * (1.0 / 16.0);
		ivec2 fp = ivec2(FRAGCOORD.xy) % 4;
		if (bayer[fp.y][fp.x] >= visibility) {
			discard;
		}
	}

	// Light the reflection from the WATER FRAGMENT position, not the
	// source sprite's position. Sampling at the source made a sunlit
	// sprite's reflection over dark cave water render bright (the source
	// was lit, so the reflection was lit) — read as "emissive in the
	// darkness". Sampling at the reflection fragment keeps the
	// reflection tinted by the medium it's seen in: dark cave water →
	// dark reflection, sunlit pond → sunlit reflection. Push up a small
	// epsilon so the sample lands in the air voxel above the water
	// surface rather than reading the water voxel itself (which has
	// little or no propagated light).
	vec3 light_sample_pos = vertex_world_var + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	float cloud = cloud_shadow_ground(vertex_world_var, TIME);
	float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
	// Reflection block_lit is dimmed by the projector at the reflection's
	// own water-fragment position — same anchor as the lightmap sample
	// above, so a sprite standing above the water dims the reflection
	// surface here without affecting reflections elsewhere.
	vec3 block_lit = lm.gba * block_light_shadow_factor(vertex_world_var);
	vec3 light = min(vec3(1.0), sun_lit + block_lit);

	// Silhouette bypasses lighting (same as sprite_lit) then the reflection
	// dim still applies so the tinted reflection reads as reflected, not
	// as a second flat shape.
	vec3 base = mix(tex.rgb * light, silhouette_tint, silhouette_amount);

	// Manual water-color tint. We sit on top of water in the transparent
	// queue now, so water's blend_mix doesn't get to tint us — recreate
	// it here. Same two-component mix as sprite_prop_reflection_multimesh:
	// surface tint at the waterline (water_edge_opacity), ramping up
	// toward water_alpha_max at depth_scale below the surface.
	float depth_below = max(0.0, water_y - vertex_world_var.y);
	float depth_t = clamp(depth_below / max(water_depth_scale, 0.01), 0.0, 1.0);
	vec3 tint_target = mix(water_shallow_tint, water_deep_tint, depth_t) * light;
	float surface_blend = clamp(water_edge_opacity, 0.0, 1.0);
	float blend = mix(surface_blend, water_alpha_max, depth_t);
	vec3 tinted = mix(base, tint_target, blend);

		ALBEDO = tinted * reflection_tint;
		// Fade reflection opacity by water clarity so muddy water lets its
		// own surface show through instead of being stamped over by the
		// reflection sprite. Clear water → ALPHA=1; swamp/muddy water →
		// ALPHA→0. Power curve keeps the falloff subtle in the middle
		// range and bites only as muddiness saturates.
		ALPHA = pow(clamp(1.0 - water_muddiness, 0.0, 1.0), 1.5);
	}
}
