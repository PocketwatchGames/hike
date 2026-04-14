shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded, skip_vertex_transform;

// Proxy sprite shader for the shadow SubViewport. Mirrors sprite_lit's
// world-space Y-billboard VERTEX math so the quad occupies the same world
// footprint as the visible sprite. Unlike sprite_lit, the fragment here must
// NOT pin output to source-pixel screen size via FRAGCOORD — that only works
// when screen texel size matches main-cam `chunky`, which it does for the
// main camera but not for the shadow camera. Sample via UVs instead so the
// silhouette fills the full world-space quad.

uniform sampler2D sprite_texture : source_color, filter_nearest;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);

global uniform float sprite_chunky;
global uniform vec3 shadow_origin;
global uniform vec3 shadow_depth_axis;

varying vec2 src_uv;
varying vec3 caster_world;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;

	// Orient sprite_right perpendicular to the horizontal component of the
	// shadow camera's forward (= the light direction). This keeps the sprite
	// always facing the sun, so its full width projects into the shadow map
	// regardless of main-camera yaw. Using the main camera's forward instead
	// would make the sprite edge-on to the light at some yaws, collapsing
	// the shadow to a thin sliver.
	vec3 cam_forward = -INV_VIEW_MATRIX[2].xyz;
	vec3 light_horiz = normalize(vec3(cam_forward.x, 0.0, cam_forward.z));
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), light_horiz));
	float chunky = max(sprite_chunky, 1e-6);

	// No vertical stretch here. sprite_lit's stretch cancels out the main
	// camera's pitch foreshortening so one source pixel = one screen pixel —
	// it's a screen-space correction. The sprite's actual world-space height
	// is chunky * source_height, and that's what should cast shadow.
	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * sprite_right
		+ VERTEX.y * chunky * vec3(0.0, 1.0, 0.0);
	caster_world = vertex_world;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	// Map VERTEX space to texture UV. Per sprite_lit's authoring convention
	// (centered=false, offset=(-W/2, H)): VERTEX.x in [-W/2, W/2], VERTEX.y
	// in [0, H] with y increasing UP. Texture y is 0 at TOP, so flip V.
	float w = float(sprite_size.x);
	float h = float(sprite_size.y);
	src_uv = vec2((VERTEX.x + w * 0.5) / max(w, 1.0), 1.0 - VERTEX.y / max(h, 1.0));
}

void fragment() {
	if (src_uv.x < 0.0 || src_uv.x > 1.0 || src_uv.y < 0.0 || src_uv.y > 1.0) {
		discard;
	}
	ivec2 tex_coord = ivec2(
		int(floor(src_uv.x * float(sprite_size.x))),
		int(floor(src_uv.y * float(sprite_size.y)))
	);
	tex_coord = clamp(tex_coord, ivec2(0), sprite_size - ivec2(1));

	vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
	if (tex.a < 0.5) {
		discard;
	}

	// Encode depth along the light axis (0 at shadow camera, 1 at far plane).
	// Receivers compare this against their own light-axis depth so a caster
	// only shadows geometry BEHIND it along the light ray.
	float depth = dot(caster_world - shadow_origin, shadow_depth_axis);
	ALBEDO = vec3(clamp(depth, 0.0, 1.0));
}
