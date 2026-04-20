shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

// MultiMesh-instanced detail sprite (grass, flowers, twigs, etc.).
//
// Per-instance state arrives via INSTANCE_CUSTOM, written by ChunkDetailScatter:
//   .rgb : unlit albedo of the voxel directly below this sprite. Used as the
//          target color for the bottom-edge fade so the sprite blends into the
//          terrain it's planted on. Sample is unlit so the runtime light_map
//          pass lights it identically to the terrain underneath — fade still
//          matches in shadow, at night, near torches, etc.
//   .a   : reserved.
//
// Author the mesh as a quad in the XY plane with its base at Y=0 (so VERTEX.y
// reads 0 at the bottom and increases upward). Wind, player push, and the
// bottom fade all key off VERTEX.y, so taller sprites sway proportionally
// more — no per-mesh height uniform needed.

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

// --- Per-material tuning ---
// Bottom-fade band in world units. The bottom `fade_height` of the sprite
// blends from the sprite texture into INSTANCE_CUSTOM.rgb (the ground color
// baked at scatter time). A typical pixel-art sprite at 16 px/unit fades the
// bottom 10 source pixels at fade_height = 0.625.
uniform float fade_height = 0.625;

varying vec3 world_origin;
// World-space height of this vertex above the sprite anchor. Used by the
// fragment stage for the bottom-edge fade so the band thickness is in real
// world units (not mesh-local — those would scale with per-instance scale
// jitter and produce a different fade band per blade).
varying float world_height;
// INSTANCE_CUSTOM is vertex-only in Godot spatial shaders — copy it into a
// varying so the fragment stage can read the per-instance ground color.
varying flat vec3 ground_color;
// Fragment->light pass for cloud shadow composition (same trick sprite_lit
// uses so a cloud + terrain shadow read as one shadow, not stacked).
varying float cloud_dim;

// Cheap 2D hash for per-instance wind phase. INSTANCE_CUSTOM is full so we
// derive a phase from the instance origin — adjacent instances naturally have
// different XZ origins so this gives every sprite its own sway phase.
float hash12(vec2 p) {
	vec3 p3 = fract(vec3(p.xyx) * 0.1031);
	p3 += dot(p3, p3.yzx + 33.33);
	return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	ground_color = INSTANCE_CUSTOM.rgb;

	// Y-aligned billboard: rotate the quad around the world-up axis to face
	// the camera horizontally, but never tilt — same approach sprite_lit uses
	// so detail sprites and authored sprites read consistently. The mesh's
	// per-instance basis rotation is intentionally ignored; only its scale
	// (from `length(MODEL_MATRIX[0].xyz)`) is applied so scale jitter still
	// affects sprite size. Authoring contract: VERTEX.x = horizontal extent
	// (centered around 0), VERTEX.y = vertical (0 at base, +H at top).
	float scale = length(MODEL_MATRIX[0].xyz);
	vec3 cam_forward = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), cam_forward));
	vec3 vertex_world = world_origin
		+ VERTEX.x * scale * sprite_right
		+ VERTEX.y * scale * vec3(0.0, 1.0, 0.0);

	// World-space height above the anchor. Bend curve squared so the bottom
	// 30% barely moves and the top accelerates — reads as a hinged stem
	// rather than a uniform shear.
	float h = max(vertex_world.y - world_origin.y, 0.0);
	world_height = h;
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

	// Bottom fade: blend the sprite albedo toward the baked ground color over
	// the bottom `fade_height` world units of the sprite. world_height is
	// already in world units (post-instance-scale) so the fade band thickness
	// stays consistent across scale-jittered blades.
	float fade_t = 1.0 - smoothstep(0.0, fade_height, world_height);
	vec3 sprite_albedo = mix(tex.rgb, ground_color, fade_t);

	// Sample the light map at the sprite's anchor (root). Sampling per-vertex
	// or per-fragment in world space would shimmer as the sprite sways and
	// across the (x,z) extent of a single blade the answer barely changes.
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
	ALBEDO = sprite_albedo * sun_lit * (1.0 - sun_ambient);
	EMISSION = sprite_albedo * (block_lit + sun_lit * sun_ambient * cloud_dim);
}

void light() {
	DIFFUSE_LIGHT += ALBEDO * min(ATTENUATION, cloud_dim);
}
