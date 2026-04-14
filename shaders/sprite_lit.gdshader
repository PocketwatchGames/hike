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
uniform sampler3D light_map : filter_linear;
uniform vec3 light_map_origin;
uniform vec3 light_map_inv_size;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);

global uniform float camera_clip;
global uniform float sprite_chunky;

varying flat vec2 anchor_px;
varying flat vec3 world_origin;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;

	vec3 cam_forward = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), cam_forward));
	float pitch = asin(clamp(abs(cam_forward.y), 0.0, 1.0));
	float stretch = 1.0 / max(cos(pitch), 1e-4);
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

	vec3 uvw = (world_origin - light_map_origin) * light_map_inv_size;
	float light = textureLod(light_map, uvw, 0.0).r;

	NORMAL = vec3(0.0, 1.0, 0.0);
	ALBEDO = tex.rgb;
	EMISSION = tex.rgb * light;
}
