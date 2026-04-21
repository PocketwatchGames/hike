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
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);
// Mirror in lockstep with the visible sprite.
uniform bool sprite_mirror = false;
// Match the visible sprite's roll-toward-terrain-normal so a grass blade
// leaning into a slope has a reflection that leans the same way. Same
// uniforms and math as sprite_lit.gdshader; defaults leave behaviour
// unchanged for materials that don't set them.
uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);

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
// Uniform scale from MODEL_MATRIX; see sprite_lit for rationale.
varying flat float instance_scale;
// Same screen-space basis varyings sprite_lit uses, with the Y-flip baked
// in (see vertex). Fragment inverts this 2x2 matrix to recover the source
// pixel for each reflection fragment, which keeps texelFetch exact when
// the reflection is rolled to match a sloped terrain normal.
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
// Interpolated fragment world Y — used by the fragment's water-plane clip.
// Passing it from the vertex (instead of deriving from d.y * chunky * stretch
// like the old upright math) stays correct when the reflection is rolled.
varying float frag_world_y;

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
	frag_world_y = vertex_world.y;

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
	// Ceiling-cutaway: the source sprite discards above camera_clip, so
	// match here. Anchor y is the REFLECTION anchor, below water; compare
	// against source_world_pos for the true cutaway relationship.
	if (source_world_pos.y > camera_clip) {
		discard;
	}

	// Small epsilon so the row right at the water line still renders —
	// otherwise a sharp visible gap appears along the surface. frag_world_y
	// comes straight from the vertex stage so it stays correct when the
	// reflection is rolled off vertical.
	if (frag_world_y > water_y + 0.001) {
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
	// Reflections read source image top↔bottom:
	//   sxy.y ≈ 0               → tex_coord.y = sprite_size.y - 1 (feet)
	//   sxy.y ≈ sprite_size.y-1 → tex_coord.y = 0                 (head)
	ivec2 tex_coord = ivec2(
		int(floor(sxy.x)) + sprite_size.x / 2,
		sprite_size.y - 1 - int(floor(sxy.y))
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
