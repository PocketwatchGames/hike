shader_type spatial;
// depth_draw_opaque + render priority below water: the reflection draws
// BEFORE the water surface, lands in `hint_screen_texture` with proper
// depth, and the water shader's refraction picks it up via the now-
// correct depth-aware offset — giving rippled reflection wobble for free
// in lockstep with the surface above. Water's own depth-attenuation
// (water_alpha + thickness compositing) tints the deeper portions of the
// reflection toward water_deep_tint naturally, so no manual tinting is
// needed in this shader. nearest filtering on screen_tex/depth_tex (set
// in voxel_water.gdshader) prevents player-colored bleed at silhouettes
// that previously made this render order unviable.
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform, unshaded;

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

// Ripple-driven source-pixel jitter: sampled per-fragment at the
// reflection's WORLD XZ + scrolling ripple_offset_a so adjacent fragments
// get adjacent noise samples (smooth shimmer), not the same uniform shift
// across the whole sprite. Combined with the water shader's refraction
// wobble (which shifts the silhouette as a whole) this gives both
// macro-wobble and internal shimmer — full "rippled reflection" feel.
// All three uniforms are globals from voxel_water.gdshader / SkyController.
global uniform float ripple_scale_a;
global uniform vec2 ripple_offset_a;
global uniform float ripple_strength;

// World Y of the reflecting water surface. The reflection anchor is at
// (2*water_y - source.y) and vertices extend downward, so the clip
// test `world_y > water_y` removes the above-water slab that appears
// when the source sprite is partially submerged.
uniform float water_y = -1e30;
// World position of the sprite being reflected — used to sample the
// lightmap at the source, not at the reflection, so underground water's
// reflection of a sunlit sprite still reads correctly.
uniform vec3 source_world_pos = vec3(0.0);
// Global dimming that sells "this is a reflection, not a second sprite."
// Pushed by SkyController so weather (muddiness, fog, light level) drives
// how much reflections are dimmed — clear bright water shows them at full
// brightness; murky / overcast water dims them toward zero.
global uniform float reflection_tint;
// Maximum source-pixel jitter applied to the reflection's tex_coord lookup
// at full ripple_strength. SkyController scales by weather/wind so calm
// water shows a rigid mirror, choppy water visibly distorts the reflection.
global uniform float reflection_pixel_jitter_max;
// Match sprite_lit's discovery-fade + silhouette uniforms so a fading /
// silhouetted sprite's water reflection stays consistent with its body.
uniform float visibility : hint_range(0.0, 1.0) = 1.0;
uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
uniform vec3 silhouette_tint : source_color = vec3(0.0);

// Procedural noise (matching voxel_water.gdshader's hash13/vnoise) — used
// to sample a smoothly varying jitter from the reflection's world XZ +
// scrolled ripple_offset_a so the reflection wobbles in lockstep with the
// water surface above it without needing the actual normal-map textures.
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
	if (source_world_pos.y > camera_clip) {
		discard;
	}

	// Small epsilon so the row right at the water line still renders —
	// otherwise a sharp visible gap appears along the surface. World Y
	// comes straight from the vertex stage so it stays correct when the
	// reflection is rolled off vertical.
	if (vertex_world_var.y > water_y + 0.001) {
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
	// Per-fragment ripple jitter — sampled at THIS fragment's world XZ
	// (not the sprite anchor) so adjacent fragments get adjacent noise
	// samples, producing internal shimmer rather than a uniform
	// whole-sprite shift. Combined with the water shader's refraction
	// wobble (which shifts which reflection pixel each water fragment
	// samples), this gives both macro-wobble and internal pixel
	// shimmer. ripple_strength is already weather-damped by SkyController.
	vec2 jitter = vec2(0.0);
	float jitter_amp = ripple_strength * reflection_pixel_jitter_max;
	if (jitter_amp > 1e-4) {
		vec3 jp = vec3(vertex_world_var.xz * ripple_scale_a + ripple_offset_a, 0.0);
		float jx = refl_vnoise(jp) - 0.5;
		float jy = refl_vnoise(jp + vec3(11.7, 5.3, 0.0)) - 0.5;
		jitter = vec2(jx, jy) * 2.0 * jitter_amp;
	}

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

	// Silhouette bypasses lighting (same as sprite_lit) then the reflection
	// dim still applies so the tinted reflection reads as reflected, not
	// as a second flat shape. No manual water-column tint — the reflection
	// now renders BEFORE water and lands in screen_tex with depth, so the
	// water shader composites it through `behind` and applies depth tint
	// via water_alpha mixing with water_col. Simpler and physically right.
	vec3 base = mix(tex.rgb * light, silhouette_tint, silhouette_amount);
	ALBEDO = base * reflection_tint;
}
