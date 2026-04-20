shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_disabled, ambient_light_disabled, skip_vertex_transform, unshaded;

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

uniform sampler2D sprite_texture : source_color, filter_nearest;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);

global uniform float camera_clip;
global uniform float sprite_chunky;
global uniform float sprite_stretch;

// World Y of the reflecting water surface. The reflection anchor is at
// (2*water_y - source.y) and vertices extend downward, so the clip
// test `world_y > water_y` removes the above-water slab that appears
// when the source sprite is partially submerged.
uniform float water_y = -1e30;
// World position of the sprite being reflected — used to sample the
// lightmap at the source, not at the reflection, so underground water's
// reflection of a sunlit sprite still reads correctly.
uniform vec3 source_world_pos = vec3(0.0);
// Per-instance dimming to sell "this is a reflection, not a second sprite."
uniform float reflection_tint : hint_range(0.0, 1.0) = 0.7;

varying flat vec2 anchor_px;
varying flat vec3 world_origin;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;

	vec3 cam_forward = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), cam_forward));
	float stretch = max(sprite_stretch, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	// Downward-extending quad: VERTEX.y in [0, H] places vertices in
	// world_y ∈ [world_origin.y - H*chunky, world_origin.y].
	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * sprite_right
		- VERTEX.y * chunky * stretch * vec3(0.0, 1.0, 0.0);
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
}

void fragment() {
	// Ceiling-cutaway: the source sprite discards above camera_clip, so
	// match here. Anchor y is the REFLECTION anchor, below water; compare
	// against source_world_pos for the true cutaway relationship.
	if (source_world_pos.y > camera_clip) {
		discard;
	}

	// Reconstruct the fragment's world Y from the anchor + screen delta.
	// anchor_px is on-screen at world_origin; a fragment d.y pixels below
	// the anchor on-screen sits d.y*chunky/stretch world units below the
	// anchor (quad extends downward in world space for reflections).
	vec2 d = FRAGCOORD.xy - anchor_px;
	float frag_world_y = world_origin.y - d.y * sprite_chunky * sprite_stretch;
	// Small epsilon so the row right at the water line still renders —
	// otherwise a sharp visible gap appears along the surface.
	if (frag_world_y > water_y + 0.001) {
		discard;
	}

	// Texture coordinate: FRAGCOORD y is inverted in screen space relative
	// to world y (screen y grows downward). For reflections the quad goes
	// downward in world → downward on screen → d.y positive. Map:
	//   d.y ≈ 0                   → tex_coord.y = sprite_size.y - 1 (feet)
	//   d.y ≈ sprite_size.y - 1   → tex_coord.y = 0                 (head)
	ivec2 tex_coord = ivec2(
		int(floor(d.x)) + sprite_size.x / 2,
		sprite_size.y - 1 - int(floor(d.y))
	);
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
	if (tex.a < 0.5) {
		discard;
	}

	// Light the reflection from the SOURCE position, not the reflection's
	// position. Same push-up-half-a-voxel trick as sprite_lit so the
	// lightmap read lands in air above the sprite's feet.
	vec3 light_sample_pos = source_world_pos + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	float cloud = cloud_shadow_ground(source_world_pos, TIME);
	float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
	vec3 block_lit = lm.gba;
	vec3 light = min(vec3(1.0), sun_lit + block_lit);

	ALBEDO = tex.rgb * light * reflection_tint;
}
