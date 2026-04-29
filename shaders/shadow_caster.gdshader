shader_type spatial;
// Opaque queue (no blend_mix) — the shadow caster only needs depth, and
// alpha cut is handled by discard. Visible-pass output is suppressed by
// CastShadow.ShadowsOnly anyway, so the queue choice only affects whether
// other shadow-casters can batch with this one.
render_mode depth_draw_opaque, cull_disabled, unshaded, skip_vertex_transform;

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

// Per-sprite values are `instance uniform` so all shadow proxies sharing
// a texture can share one material and batch into a single shadow-pass
// draw call. sprite_texture stays a regular uniform on the material since
// sampler2D can't be instance.
uniform sampler2D sprite_texture : source_color, filter_nearest;
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
// Mirror in lockstep with the visible sprite so the shadow silhouette
// matches its source.
instance uniform bool sprite_mirror = false;
// Align with the visible sprite: same roll-toward-terrain-normal math
// sprite_lit uses, so the cast shadow silhouette matches the rolled
// sprite's world-space shape. Defaults keep the pre-alignment behaviour.
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);
// Dither fade matches sprite_lit's uniform; pushed by LitSprite.Visibility
// so a fading-in sprite's cast shadow stipples in alongside its visible body.
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;

global uniform float sprite_chunky;

varying vec2 src_uv;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;
	// Match sprite_lit: pull uniform scale out of MODEL_MATRIX so any
	// Node3D.Scale on the LitSprite chain resizes the shadow silhouette
	// in lockstep with the visible sprite.
	float instance_scale = length(MODEL_MATRIX[0].xyz);

	// Match sprite_lit's two-stage billboard (yaw + roll-toward-terrain-
	// normal), but yaw toward whichever camera is rendering THIS pass —
	// during the shadow atlas pass that's the sun, so the silhouette casts
	// its full width into the atlas. Horizontalising the light direction
	// keeps the sprite plane vertical under a low sun angle (pure Y
	// billboard, no pitch into the atlas). Roll then leans the plane
	// toward the slope's normal the same way the visible sprite does.
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_forward = -INV_VIEW_MATRIX[2].xyz;
	vec3 light_horiz = normalize(vec3(cam_forward.x, 0.0, cam_forward.z));
	vec3 sprite_right_yaw = normalize(cross(world_up, light_horiz));
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
	float chunky = max(sprite_chunky, 1e-6);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * sprite_right_rolled
		+ VERTEX.y * chunky * instance_scale * sprite_up_rolled;
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
	if (sprite_mirror) {
		tex_coord.x = sprite_size.x - 1 - tex_coord.x;
	}

	vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
	if (tex.a < 0.5) {
		discard;
	}
	// Match the visible sprite's Bayer stipple so a half-faded mob's shadow
	// is half-stippled rather than full-coverage. Pattern is screen-space
	// via FRAGCOORD; during the shadow pass FRAGCOORD is the shadow-atlas
	// fragment, which stays stable within a frame so dots don't flicker.
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
	// Godot writes depth automatically into the shadow atlas; ALBEDO is unused
	// for shadow-only meshes so leaving it at 0 is fine.
	ALBEDO = vec3(0.0);
}
