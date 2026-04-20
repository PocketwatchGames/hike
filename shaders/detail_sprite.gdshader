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

// --- Lighting globals (shared with sprite_lit / voxel_clip) ---
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_scroll;
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
global uniform float wind_frequency;

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
	float scale = length(MODEL_MATRIX[0].xyz);
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
		+ VERTEX.x * scale * sprite_right_rolled
		+ VERTEX.y * scale * sprite_up_rolled;

	// Bend reference height in *unstretched* world units so wind amplitude
	// doesn't grow when the camera tilts more.
	float h = max(VERTEX.y * scale, 0.0);
	float bend = h * h;

	// Per-sprite phase from the instance origin so a field doesn't sway in
	// lock-step. Two octaves at coprime frequencies for non-repeating motion.
	float phase = hash12(world_origin.xz) * 6.2831853;
	float t = TIME * wind_frequency;
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
