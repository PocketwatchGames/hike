shader_type spatial;
// Visible-only sibling of shadow_caster.gdshader, dedicated to the
// BlockLightShadowProjector's color pass. Same billboard math (camera-
// aligned via INV_VIEW_MATRIX, which is the projector camera in this pass)
// and same alpha-cut, but writes ALBEDO=1 instead of 0 so the projector's
// SubViewport texture reads a coverage of 1.0 inside silhouettes and 0.0
// elsewhere. Cast shadow is OFF; this proxy contributes ONLY to the
// projector's color render. The original `shadow_caster.gdshader` proxy
// (ShadowsOnly + default layer) keeps casting sun/moon shadows untouched.
//
// We use a separate shader because there's no in-shader way to detect
// "shadow atlas pass vs visible pass" in 3D — and combining both roles
// in one proxy with CastShadow.On caused per-sprite sun/moon shadows to
// vanish. Two proxies, one shader each, one job each.
//
// `ambient_light_disabled` so Godot's default ambient AMBIENT_LIGHT
// doesn't leak into the unshaded ALBEDO output and turn the silhouette
// from 1.0 into something larger that saturates the texture clamp.
render_mode depth_draw_opaque, cull_disabled, unshaded, ambient_light_disabled, skip_vertex_transform;

uniform sampler2D sprite_texture : source_color, filter_nearest;
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
instance uniform bool sprite_mirror = false;
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;

global uniform float sprite_chunky;

varying vec2 src_uv;

void vertex() {
	vec3 world_origin = MODEL_MATRIX[3].xyz;
	float instance_scale = length(MODEL_MATRIX[0].xyz);

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
	// Solid white: lit shaders read this as `coverage = 1.0` and dim
	// block_lit by `block_light_shadow_strength`.
	ALBEDO = vec3(1.0);
}
