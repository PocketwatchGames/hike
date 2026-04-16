shader_type spatial;
render_mode blend_mix, depth_draw_opaque, cull_disabled, unshaded, skip_vertex_transform;

// Sun-billboarded shadow caster sprite, used by LitSprite's hidden shadow
// proxy child. Renders the source sprite's silhouette (alpha-cut) into Godot's
// directional shadow atlas during the depth/shadow pass. The visible LitSprite
// stays camera-billboarded; this proxy is camera-billboarded TOO via the
// vertex math below — but during the shadow pass INV_VIEW_MATRIX is the sun
// camera, so the silhouette is automatically the right shape from the sun's
// point of view. The proxy itself is hidden via CastShadow.ShadowsOnly.
//
// Deliberately omits any camera_clip discard: the user's design wants
// above-clip casters (trees) to still throw shadows on visible terrain when
// the player goes inside (ceiling cutaway active).

uniform sampler2D sprite_texture : source_color, filter_nearest;
uniform ivec2 sprite_size = ivec2(1, 1);
uniform ivec2 sprite_region_origin = ivec2(0, 0);

global uniform float sprite_chunky;

varying vec2 src_uv;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;

	// Match sprite_lit's Y-billboard math but using whichever camera is
	// rendering THIS pass. During the shadow atlas pass that's the sun
	// camera, so the sprite faces the sun and casts its full width.
	vec3 cam_forward = -INV_VIEW_MATRIX[2].xyz;
	vec3 light_horiz = normalize(vec3(cam_forward.x, 0.0, cam_forward.z));
	vec3 sprite_right = normalize(cross(vec3(0.0, 1.0, 0.0), light_horiz));
	float chunky = max(sprite_chunky, 1e-6);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * sprite_right
		+ VERTEX.y * chunky * vec3(0.0, 1.0, 0.0);
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

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
	// Godot writes depth automatically into the shadow atlas; ALBEDO is unused
	// for shadow-only meshes so leaving it at 0 is fine.
	ALBEDO = vec3(0.0);
}
