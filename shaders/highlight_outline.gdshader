shader_type spatial;
render_mode blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, unshaded, skip_vertex_transform;

// Outline overlay that mirrors sprite_lit.gdshader's vertex + fragment math so
// the outline lines up exactly with the snapped source sprite in the chunky
// inner viewport. The sprite is placed from a pixel-snapped anchor and each
// fragment resolves to an integer source texel via FRAGCOORD, so sampling
// neighbors in texel space gives a crisp 1-chunky-pixel outline.

uniform sampler2D sprite_texture : source_color, filter_nearest;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
uniform int outline_width : hint_range(1, 5) = 1;

global uniform float sprite_chunky;

varying flat vec2 anchor_px;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;

	vec3 cam_forward = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), cam_forward));
	float pitch = asin(clamp(abs(cam_forward.y), 0.0, 1.0));
	float stretch = 1.0 / max(cos(pitch), 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	// The Sprite3D quad covers exactly the source sprite bounds, so an outline
	// drawn inside would only fill transparent pixels within the sprite. Push
	// each corner outward by outline_width chunky pixels so the outline sits
	// just outside the silhouette.
	float ow = float(outline_width);
	vec2 vx = VERTEX.xy;
	vx.x += sign(VERTEX.x) * ow;
	vx.y += (VERTEX.y > 0.5 * float(sprite_size.y)) ? ow : -ow;

	vec3 vertex_world = world_origin
		+ vx.x * chunky * sprite_right
		+ vx.y * chunky * stretch * vec3(0.0, 1.0, 0.0);
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
}

void fragment() {
	vec2 d = FRAGCOORD.xy - anchor_px;
	ivec2 tex_coord = ivec2(
		int(floor(d.x)) + sprite_size.x / 2,
		int(floor(d.y)) + sprite_size.y
	);

	// Fragments inside the source sprite bounds let the real sprite show
	// through on opaque texels. Fragments in the expanded outline border
	// (outside the bounds) always proceed to the neighbor search.
	bool inside = tex_coord.x >= 0 && tex_coord.x < sprite_size.x
		&& tex_coord.y >= 0 && tex_coord.y < sprite_size.y;
	if (inside && texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0).a >= 0.5) {
		discard;
	}

	// Look for an opaque neighbor in integer texel space — one texel per
	// chunky pixel — so the outline is exactly outline_width chunky pixels.
	bool has_opaque_neighbor = false;
	int r = outline_width;
	for (int y = -r; y <= r; y++) {
		for (int x = -r; x <= r; x++) {
			if (x == 0 && y == 0) continue;
			ivec2 n = tex_coord + ivec2(x, y);
			if (n.x < 0 || n.x >= sprite_size.x || n.y < 0 || n.y >= sprite_size.y) continue;
			if (texelFetch(sprite_texture, n + sprite_region_origin, 0).a >= 0.5) {
				has_opaque_neighbor = true;
			}
		}
	}

	if (!has_opaque_neighbor) discard;

	ALBEDO = outline_color.rgb;
}
