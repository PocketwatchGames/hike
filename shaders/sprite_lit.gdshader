shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

// Y-aligned, camera-facing pixel-art sprite.
//
// The quad is authored on a Sprite3D with centered = false, pixel_size = 1.0,
// billboard = 0, offset = (-W/2, H). VERTEX therefore spans
//   x in [-W/2, W/2], y in [0, H]
// in source-pixel units, with VERTEX = (0, 0) at the node origin (the anchor,
// by convention the center-bottom of the sprite where it touches the ground).
//
// The vertex stage places each vertex in world space on a Y-aligned billboard
// that rotates around Y to face the camera (but does not tilt — it is not a
// full billboard). Vertical offsets are stretched by 1/cos(pitch) so a row of
// source pixels projects to one screen-pixel row despite the camera's tilt.
//
// The fragment stage computes the source texel from FRAGCOORD relative to the
// snapped screen-space projection of the anchor, so every fragment samples an
// exact integer source pixel — no UV interpolation drift.

uniform sampler2D sprite_texture : source_color, filter_nearest;
// RGBA8 light map: R = sun mask, GBA = post-pow block light per RGB channel.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform float cloud_shadow_scale;
global uniform float cloud_speed;
global uniform vec2 cloud_direction;
global uniform float cloud_cutoff;
global uniform float cloud_power;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);

global uniform float camera_clip;
global uniform float sprite_chunky;
global uniform float sprite_stretch;

varying flat vec2 anchor_px;
varying flat vec3 world_origin;

float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i); float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0)); float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1)); float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1)); float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;

	vec3 cam_forward = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), cam_forward));
	float stretch = max(sprite_stretch, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * sprite_right
		+ VERTEX.y * chunky * stretch * vec3(0.0, 1.0, 0.0);
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
}

void fragment() {
	if (world_origin.y > camera_clip) {
		discard;
	}

	vec2 d = FRAGCOORD.xy - anchor_px;
	ivec2 tex_coord = ivec2(
		int(floor(d.x)) + sprite_size.x / 2,
		int(floor(d.y)) + sprite_size.y
	);
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
	if (tex.a < 0.5) {
		discard;
	}

	// Sprite anchor sits on the ground — i.e. at the top face of a solid
	// voxel — so sampling at world_origin lands inside that solid voxel and
	// reads 0 light. Push up half a voxel into the air above so we read the
	// adjacent air-voxel's light. (Same trick voxel_clip uses with normal
	// offset, but for a sprite "up" is always the relevant direction.)
	vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = textureLod(light_map, uvw, 0.0);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Cloud shadows disabled — see voxel_clip.gdshader for rationale.
	vec3 sun_lit = sun_mask * sun_intensity * sun_color;
	vec3 block_lit = lm.gba;
	vec3 light = min(vec3(1.0), sun_lit + block_lit);

	NORMAL = vec3(0.0, 1.0, 0.0);
	// Match voxel_clip's split so sprites and terrain shadow consistently:
	//   ALBEDO   = direct sun color (scaled by ATTENUATION in light())
	//   EMISSION = ambient sky bounce + block light (never shadowed)
	// Final pixel = ALBEDO * ATTENUATION + EMISSION (ambient_light_disabled).
	ALBEDO = tex.rgb * sun_lit * (1.0 - sun_ambient);
	EMISSION = tex.rgb * (block_lit + sun_lit * sun_ambient);
}

void light() {
	// ATTENUATION on a DirectionalLight3D is the shadow factor only — used as
	// a pure mask. No LIGHT_COLOR / NdotL so sprites' billboarded fake normal
	// doesn't introduce a fake lambert term.
	DIFFUSE_LIGHT += ALBEDO * ATTENUATION;
}
