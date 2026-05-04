shader_type spatial;
render_mode blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, unshaded, skip_vertex_transform;

// Flat-on-ground variant of highlight_outline.gdshader. Mirrors
// sprite_lit_flat.gdshader's vertex math (camera-yawed quad in world XZ
// plane, sin(pitch) stretch on the depth axis) and centered fragment
// anchor convention (anchor_px at sprite CENTER, tex_coord origin at
// sprite_size/2). Used by GameClient.ApplyHighlight when the highlighted
// sprite is a FlatLitSprite — the upright outline shader would build the
// outline on a Y-aligned billboard and miss the flat geometry by 90°.

uniform sampler2D sprite_texture : source_color, filter_nearest;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);
uniform vec4 outline_color : source_color = vec4(1.0, 1.0, 0.8, 1.0);
uniform int outline_width : hint_range(1, 5) = 1;

global uniform float sprite_chunky;
global uniform float sprite_stretch_flat;

varying flat vec2 anchor_px;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right = normalize(cross(world_up, cam_back));
	// sprite_forward points toward camera; the flat sprite's depth axis
	// (VERTEX.y > 0 = away from camera, higher screen Y) is the opposite.
	vec3 sprite_forward = cross(sprite_right, world_up);
	vec3 sprite_depth_away = -sprite_forward;

	float stretch = max(sprite_stretch_flat, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	vec3 right_axis = sprite_right;
	vec3 up_axis = sprite_depth_away * stretch;

	// Push each corner outward by outline_width chunky pixels so the
	// outline sits just outside the silhouette. VERTEX is centered on
	// the sprite (range [-W/2, W/2] × [-H/2, H/2]) so a sign-based push
	// works for both axes.
	float ow = float(outline_width);
	vec2 vx = VERTEX.xy;
	vx.x += sign(VERTEX.x) * ow;
	vx.y += (VERTEX.y > 0.0) ? ow : -ow;

	vec3 vertex_world = world_origin
		+ vx.x * chunky * right_axis
		+ vx.y * chunky * up_axis;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
}

void fragment() {
	// anchor_px sits at the CENTER of the rendered flat sprite (because
	// the flat vertex math centers on world_origin), so tex_coord origin
	// is sprite_size/2, not sprite_size as the upright shader uses.
	vec2 d = FRAGCOORD.xy - anchor_px;
	ivec2 tex_coord = ivec2(
		int(floor(d.x)) + sprite_size.x / 2,
		int(floor(d.y)) + sprite_size.y / 2
	);

	bool inside = tex_coord.x >= 0 && tex_coord.x < sprite_size.x
		&& tex_coord.y >= 0 && tex_coord.y < sprite_size.y;
	if (inside && texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0).a >= 0.5) {
		discard;
	}

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
