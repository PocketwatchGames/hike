shader_type spatial;
// X-ray silhouette pass for character sprites — wired as the next_pass of
// sprite_lit_character.tres. Renders the sprite's alpha-cut shape as a flat
// tinted color, but ONLY where the sprite fragment is occluded by something
// else in the depth buffer (terrain, props, other characters in front).
// Where the sprite is visible normally, this pass discards so the lit base
// pass shows through unchanged.
//
// Queue placement: depth_test_disabled pushes this material into the
// transparent queue, which runs after the entire opaque pass — so
// DEPTH_TEXTURE here is fully populated with the base sprite_lit write +
// terrain + props. Comparing each fragment's view-space Z against the
// captured scene depth tells us whether something is in front of us.
//
// Vertex stage: identical to sprite_lit.gdshader's billboard transform so the
// silhouette lines up texel-for-texel with the lit base pass. Must stay in
// sync with that shader's vertex math — keep changes paired across both.
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled, skip_vertex_transform, ambient_light_disabled;
// Skip pixels that were stamped with stencil=8 — that set includes
// sprite_lit_character (player / mobs / interactives) and detail_sprite
// (grass, pebbles, etc.). When the player stands at a campfire the
// campfire's pixels are stencil=8, so the player's X-ray pass discards
// there before fragment work runs; same for a grass blade overlapping
// the player. Voxel terrain and sprite_lit props leave stencil at 0, so
// X-ray still fires correctly when the character is genuinely behind
// world geometry.
//
// Note: water_clip_cap / sprite_reflection use stencil values 2 and 4 —
// they don't equal 8 either, so X-ray will paint over water surfaces /
// reflections without interaction. That's fine; water depth is rarely the
// occluder of interest, and the visual result reads correctly.
stencil_mode read, compare_not_equal, 8;

uniform sampler2D sprite_texture : source_color, filter_linear;
// Godot 4.6 removed the built-in DEPTH_TEXTURE; we now bind it explicitly via
// hint_depth_texture. Filter must stay nearest-equivalent (or at least not
// blur across geometry edges) for the occlusion compare to be sharp at
// silhouette boundaries — `filter_nearest` would be more accurate but the
// error message's recommended form is filter_linear_mipmap and the linear
// blend is harmless for a flat-tinted X-ray.
uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_linear_mipmap;
uniform vec3 xray_tint : source_color = vec3(0.55, 0.65, 0.85);
uniform float xray_alpha : hint_range(0.0, 1.0) = 1.0;
// Alpha used when the mob is BOTH X-rayed (occluded) AND memory-silhouetted
// (silhouette_amount > 0). At silhouette_amount = 1 the X-ray output blends
// fully toward (silhouette_tint, xray_silhouette_alpha) — a translucent
// memory shape behind cover, less attention-grabbing than the bright X-ray
// tint. Default 0.3 = 30% alpha; tweak in the inspector if needed.
uniform float xray_silhouette_alpha : hint_range(0.0, 1.0) = 0.3;
// Tie-breaker against the base pass's own depth write. The base pass
// writes the sprite's front face into the depth buffer, so without a bias
// every non-occluded fragment would compare equal and we'd flicker. ~5cm
// is well inside any meaningful occluder thickness while staying outside
// FP noise. Sprite-on-sprite false positives are now filtered by the
// stencil compare above, so this no longer needs to absorb that case.
uniform float xray_depth_bias : hint_range(0.0, 1.0) = 0.05;

instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
instance uniform bool sprite_mirror = false;
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);
instance uniform float forward_offset = 0.0;
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
// Memory-silhouette uniforms shared with sprite_lit by name. When the mob
// is "discovered but currently unseen" the owner sets silhouette_amount
// toward 1 + silhouette_tint to whatever flat color reads as memory; the
// base pass already paints that tint over the visible body, and we mirror
// the same blend here so the through-cover X-ray output matches the memory
// look instead of contradicting it with the bright xray_tint.
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);
// Per-instance fade multiplier for the X-ray pass. Player + mobs leave
// this at the default 1 so the silhouette is always-on whenever they're
// occluded; interactives drive 0→1→0 from a coarse visibility probe so the
// through-wall silhouette only reads while the player is roughly looking
// at the thing — broadcasting every chest in the world at full brightness
// would be visual noise.
instance uniform float xray_amount : hint_range(0.0, 1.0) = 1.0;

global uniform float sprite_chunky;
global uniform float sprite_stretch;
global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
#include "res://shaders/clip_dither.gdshaderinc"

varying flat vec2 anchor_px;
varying flat vec3 world_origin;
varying flat float instance_scale;
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	instance_scale = length(MODEL_MATRIX[0].xyz);

	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
	vec3 sprite_forward = cross(sprite_right_yaw, world_up);
	world_origin += sprite_forward * forward_offset;

	vec3 n = normalize(terrain_normal);
	vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
	float right_comp = dot(n_in_plane, sprite_right_yaw);
	float up_comp = dot(n_in_plane, world_up);
	float roll = atan(right_comp, up_comp) * clamp(align_to_terrain, 0.0, 1.0);
	float cr = cos(roll);
	float sr = sin(roll);

	vec3 sprite_right_rolled = cr * sprite_right_yaw - sr * world_up;
	vec3 sprite_up_rolled = sr * sprite_right_yaw + cr * world_up;

	float stretch = max(sprite_stretch, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	vec3 right_stretched = vec3(sprite_right_rolled.x, sprite_right_rolled.y * stretch, sprite_right_rolled.z);
	vec3 up_stretched = vec3(sprite_up_rolled.x, sprite_up_rolled.y * stretch, sprite_up_rolled.z);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * right_stretched
		+ VERTEX.y * chunky * instance_scale * up_stretched;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * right_stretched, 1.0);
	vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * up_stretched, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	vec2 right_ndc = right_clip.xy / right_clip.w;
	vec2 up_ndc = up_clip.xy / up_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
	sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
	sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}

void fragment() {
	// Early out when the per-instance fade has driven X-ray fully off —
	// skips the texelFetch + depth sample + matrix work entirely for
	// interactives that aren't currently within the player's probe range.
	if (xray_amount <= 0.001) {
		discard;
	}
	// Honor the same camera-clip dither as sprite_lit so the silhouette
	// disappears in lockstep with the base sprite when the camera clip-
	// volume cuts away the character (e.g. behind the camera near plane).
	if (clip_dither_should_discard(world_origin.y, FRAGCOORD.xy)) {
		discard;
	}

	// Recover source-pixel coordinate using the same basis-inversion the
	// base pass uses; we only need integer pixel coords for the alpha cut,
	// no subpixel AA since we're outputting a flat tint.
	vec2 d = FRAGCOORD.xy - anchor_px;
	float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
	float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
	vec2 sxy = vec2(
		(sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
		(sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
	);
	float half_w = float(sprite_size.x / 2);
	vec2 src_px = vec2(sxy.x + half_w, -sxy.y + float(sprite_size.y));

	ivec2 tex_coord = ivec2(int(floor(src_px.x)), int(floor(src_px.y)));
	if (sprite_mirror) {
		tex_coord.x = sprite_size.x - 1 - tex_coord.x;
	}
	if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
		|| tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
		discard;
	}

	vec2 atlas_size = vec2(textureSize(sprite_texture, 0));
	vec2 atlas_uv = (vec2(sprite_region_origin) + vec2(tex_coord) + 0.5) / atlas_size;
	vec4 tex = texture(sprite_texture, atlas_uv);
	if (tex.a < 0.5) {
		discard;
	}

	// Visibility dither (matches sprite_lit's). Without this an undiscovered
	// mob — currently being faded in/out via the visibility uniform — would
	// still leak its silhouette through walls.
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

	// Occlusion test in linearized view-space depth. Comparing the raw
	// non-linear depth-buffer values directly is also doable but brittle
	// across reverse-Z / projection variants — going through INV_PROJECTION
	// gives us a robust "meters in front of camera" comparison.
	float scene_depth_raw = textureLod(DEPTH_TEXTURE, SCREEN_UV, 0.0).x;
	vec4 scene_clip = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth_raw, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_clip;
	float scene_view_z = -scene_view.z / scene_view.w;

	vec4 frag_clip = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_clip;
	float frag_view_z = -frag_view.z / frag_view.w;

	if (frag_view_z <= scene_view_z + xray_depth_bias) {
		discard;
	}

	// Compose with the memory-silhouette state. silhouette_amount = 0 is the
	// pure X-ray case (bright xray_tint at xray_alpha). silhouette_amount = 1
	// is "this mob is also flagged as memory" — fade toward the memory tint
	// at xray_silhouette_alpha (default 30%) so the through-cover read is a
	// quiet translucent shape, not a loud bright silhouette layered on top
	// of the already-flat memory body.
	ALBEDO = mix(xray_tint, silhouette_tint, silhouette_amount);
	ALPHA = mix(xray_alpha, xray_silhouette_alpha, silhouette_amount) * xray_amount;
}
