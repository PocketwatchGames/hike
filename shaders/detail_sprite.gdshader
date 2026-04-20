shader_type spatial;
// Alpha-tested opaque, NOT alpha-blended:
//   - depth_prepass_alpha runs an alpha-tested depth prepass so the depth
//     buffer correctly reflects the cut-out sprite shapes before the main
//     color pass. With MultiMesh we can't reorder instances per-frame for
//     transparent-pass back-to-front sorting; rendering in the opaque pass
//     with depth-write makes z-sort an automatic consequence of the depth
//     buffer instead.
//   - depth_draw_opaque writes depth in the color pass too, so subsequent
//     opaque draws (including other detail-sprite chunks) sort correctly.
// blend_mix is omitted — the sprite is fully opaque or discarded; nothing
// to blend.
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

// MultiMesh-instanced detail sprite (grass, flowers, twigs, etc.).
//
// Author the mesh as a quad in the XY plane with its base at Y=0 (so VERTEX.y
// reads 0 at the bottom and increases upward). Wind and player push key off
// VERTEX.y, so taller sprites sway proportionally more — no per-mesh height
// uniform needed.

uniform sampler2D sprite_texture : source_color, filter_nearest;

// Ground-rooting: the bottom N texels of the sprite lerp toward COLOR.rgb,
// which ChunkDetailScatter sets per-instance from VoxelTypeInfo.GroundTint
// of the voxel below. Kills the "sprite floating above the ground" read at
// iso angles. smoothstep gives a gradient within the band; per texel-column
// noise jitters the start of the fade so the tinted region has an irregular
// pixel-art boundary rather than a clean horizontal band.
uniform float ground_fade_pixels : hint_range(0.0, 8.0) = 3.0;
uniform float ground_fade_noise_pixels : hint_range(0.0, 8.0) = 1.5;

// --- Lighting globals (shared with sprite_lit / voxel_clip) ---
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

global uniform float camera_clip;
// 1 / cos(camera_pitch). Stretches sprite Y so a row of source pixels projects
// to one screen-pixel row regardless of the iso camera's tilt — same trick
// sprite_lit uses; without it, detail sprites read as foreshortened relative
// to authored 2D sprites in the same scene.
global uniform float sprite_stretch;

// --- Wind / interaction globals ---
// wind_dir.xz = horizontal direction; .y unused (kept vec3 for cheap mix with
// world up if we ever want vertical sway). amplitude scales the displacement
// in world units at the *top* of a 1m-tall sprite.
global uniform vec3 wind_dir;
global uniform float wind_amplitude;
// Pre-integrated sway phase (SkyController accumulates wind_frequency*dt each
// frame). Passing a phase instead of a frequency avoids the sin-speed ramp you
// get when weather lerps wind_frequency while the shader is still multiplying
// by TIME — the phase integrates the current frequency; past cycles stay put.
global uniform float wind_phase;

// player_pos is the player's world-space foot position. Sprites within
// player_radius push their tops away radially in the XZ plane; falloff is
// smoothstep so the boundary is soft.
global uniform vec3 player_pos;
global uniform float player_radius;
global uniform float player_strength;

varying vec3 world_origin;
// Fragment->light pass for cloud shadow composition (same trick sprite_lit
// uses so a cloud + terrain shadow read as one shadow, not stacked).
varying float cloud_dim;

// Cheap 2D hash for per-instance wind phase. Derived from instance origin so
// adjacent blades have different sway phases and a field doesn't sway in
// lock-step.
float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;

	// Two-stage billboard:
	//   1) Yaw: rotate the quad around world Y so its plane faces the camera
	//      horizontally. sprite_right is horizontal, sprite_up is world Y.
	//      The plane never pitches into/out of screen.
	//   2) Roll: rotate within the sprite plane around the camera-facing
	//      forward axis by an angle derived from the per-instance terrain
	//      normal projected onto the plane. The sprite leans visibly when
	//      viewed across the slope and reads as upright when viewed along
	//      the slope (the projected normal collapses to world-up there).
	// Then stretch only the world-Y component of each rolled axis by
	// sprite_stretch (= 1/cos(camera_pitch)). Foreshortening only affects
	// vertical world distance under iso pitch; horizontal world distance is
	// already in the screen plane and needs no compensation.
	// Non-uniform per-instance scale: the scatter packs texture width and
	// height (in world units) into MODEL_MATRIX columns 0 and 1 respectively,
	// so sprites natively sit at their texture's pixel aspect without any
	// authored mesh-size tuning. ScaleMin/ScaleMax multiplier is folded in
	// uniformly on top.
	float scale_x = length(MODEL_MATRIX[0].xyz);
	float scale_y = length(MODEL_MATRIX[1].xyz);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(vec3(0.0, 1.0, 0.0), cam_back));
	vec3 sprite_up_yaw = vec3(0.0, 1.0, 0.0);
	// sprite_forward is horizontal (perpendicular to world up and to
	// sprite_right_yaw) — the axis we roll around.
	vec3 sprite_forward = cross(sprite_right_yaw, sprite_up_yaw);

	// Project the per-instance terrain normal into the sprite plane and
	// resolve the roll angle as its tilt off world-up within that plane.
	// Flat ground (normal = world Y) → roll = 0, sprite stays upright. The
	// sprite's local up axis ends up aligned with the projected normal, so
	// the top of the sprite leans in the same direction the terrain normal
	// points (i.e. downslope for an outward-facing surface).
	vec3 n = INSTANCE_CUSTOM.xyz;
	vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
	float right_comp = dot(n_in_plane, sprite_right_yaw);
	float up_comp = dot(n_in_plane, sprite_up_yaw);
	float roll = atan(right_comp, up_comp);
	float c = cos(roll);
	float s = sin(roll);

	// Rotate (right, up) in the sprite plane by `roll` around sprite_forward.
	// Choosing sprite_up_rolled = sin*right + cos*up makes the rotated up
	// vector equal to the projected-normal direction in the (right, up)
	// basis — so the lean direction matches the normal, not its mirror.
	vec3 sprite_right_rolled = c * sprite_right_yaw - s * sprite_up_yaw;
	vec3 sprite_up_rolled = s * sprite_right_yaw + c * sprite_up_yaw;

	// Stretch only the world-Y component — the only component that gets
	// foreshortened by camera pitch. Horizontal components are already in
	// the screen plane.
	sprite_right_rolled.y *= sprite_stretch;
	sprite_up_rolled.y *= sprite_stretch;

	vec3 vertex_world = world_origin
		+ VERTEX.x * scale_x * sprite_right_rolled
		+ VERTEX.y * scale_y * sprite_up_rolled;

	// Bend reference height in *unstretched* world units so wind amplitude
	// doesn't grow when the camera tilts more.
	float h = max(VERTEX.y * scale_y, 0.0);
	float bend = h * h;

	// Random horizontal mirror per instance — picks a different hash offset
	// from the sway phase so mirror doesn't correlate with wind timing.
	// Breaks up the read of a repeating sprite across a field without extra
	// art variants. UV flip only (geometry stays put); the texture is
	// sampled from the opposite side at the fragment stage.
	float mirror = step(0.5, hash12(world_origin.xz + vec2(7.31, 13.71)));
	UV.x = mix(UV.x, 1.0 - UV.x, mirror);

	// Per-sprite phase from the instance origin so a field doesn't sway in
	// lock-step. Two octaves at coprime frequencies for non-repeating motion.
	float phase = hash12(world_origin.xz) * 6.2831853;
	float t = wind_phase;
	float sway = sin(t + phase) * 0.7 + sin(t * 1.7 + phase * 1.3) * 0.3;

	vec3 wind_offset = wind_dir * (sway * wind_amplitude * bend);

	// Player push: radial in XZ, falloff to 0 at player_radius.
	vec3 to_sprite = world_origin - player_pos;
	to_sprite.y = 0.0;
	float dist = length(to_sprite);
	vec3 push_offset = vec3(0.0);
	if (player_radius > 0.0 && dist < player_radius && dist > 1e-4) {
		vec3 push_dir = to_sprite / dist;
		float falloff = 1.0 - smoothstep(0.0, player_radius, dist);
		push_offset = push_dir * (falloff * player_strength * bend);
	}

	vertex_world += wind_offset + push_offset;

	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);
}

void fragment() {
	if (world_origin.y > camera_clip) {
		discard;
	}

	vec4 tex = texture(sprite_texture, UV);
	if (tex.a < 0.5) {
		discard;
	}

	// Ground root blend — smoothly lerp the bottom N texels of the sprite
	// toward the per-instance ground tint (COLOR, set by ChunkDetailScatter).
	// Per texel-column hash offsets the fade threshold so the boundary is
	// irregular (pixel-art "drip"), while smoothstep keeps the interior
	// gradient reading as real lerp rather than a flat color block.
	// Quantizing the hash input to the texel grid (not raw UV.x) keeps the
	// boundary stable under filter_nearest — all screen fragments within one
	// texel agree on threshold, so there's no sub-texel shimmer. Per-instance
	// offset decorrelates adjacent blades.
	vec2 tex_size = vec2(textureSize(sprite_texture, 0));
	float texel_col = floor(UV.x * tex_size.x);
	float col_hash = hash12(vec2(texel_col, world_origin.x * 17.0 + world_origin.z * 23.0));
	float jitter_px = (col_hash - 0.5) * ground_fade_noise_pixels;
	float threshold = 1.0 - (ground_fade_pixels + jitter_px) / tex_size.y;
	float ground_blend = smoothstep(threshold, 1.0, UV.y);
	tex.rgb = mix(tex.rgb, COLOR.rgb, ground_blend);

	// Sample the light map at the sprite's anchor (root). Per-fragment
	// sampling would shimmer as the sprite sways; a single anchor sample
	// is correct because light_map is voxel-resolution.
	vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	float cloud = cloud_shadow_ground(world_origin, TIME);
	cloud_dim = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color;
	vec3 block_lit = lm.gba;

	NORMAL = vec3(0.0, 1.0, 0.0);
	// Same split as sprite_lit / voxel_clip so detail sprites and terrain
	// shadow / get torchlit consistently:
	//   ALBEDO   = direct sun color (ATTENUATION-scaled in light())
	//   EMISSION = ambient sky bounce + block light (never shadowed)
	ALBEDO = tex.rgb * sun_lit * (1.0 - sun_ambient);
	EMISSION = tex.rgb * (block_lit + sun_lit * sun_ambient * cloud_dim);
}

void light() {
	// ATTENUATION (Godot's directional shadow atlas) and cloud_dim compose as
	// min() so a cloud + cast shadow read as one shadow, not stacked.
	DIFFUSE_LIGHT += ALBEDO * min(ATTENUATION, cloud_dim);
}
