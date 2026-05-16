shader_type spatial;
// X-ray pass for character sprites — wired as the next_pass of
// sprite_lit_character.tres. Where the sprite fragment is occluded by
// anything in the depth buffer (terrain, props, other characters in
// front), alpha-blends translucent BLACK over the occluder, darkening
// it by `xray_alpha`. Where the sprite is visible normally, the depth
// check discards and the lit base pass shows through unchanged.
//
// Godot's default blend (over alpha-blend) does the math for free:
//   framebuffer = framebuffer * (1 - alpha) + ALBEDO * alpha
// With ALBEDO = vec3(0), that's just `framebuffer * (1 - alpha)` per
// channel. One uniform, no per-channel tint, no clamp. EMISSION is
// zeroed so the unshaded `ALBEDO + EMISSION` output stays black.
//
// depth_test_disabled puts the pass in the transparent queue so
// DEPTH_TEXTURE is fully populated by the time we sample it.
//
// Vertex stage: identical to sprite_lit.gdshader's billboard transform so
// the silhouette lines up texel-for-texel with the lit base pass. Must
// stay in sync with that shader's vertex math — keep changes paired
// across both.
render_mode unshaded, depth_test_disabled, depth_draw_never, cull_disabled, skip_vertex_transform, ambient_light_disabled;
// Skip pixels stamped with stencil=8 by sprite_lit_character (player /
// mobs / character-style interactives). Keeps the X-ray pass from
// drawing over other characters in front of the player. Voxel terrain,
// sprite_lit props, detail sprites, and multimesh props all leave
// stencil at 0, so X-ray fires correctly through every kind of cover
// the character is genuinely behind. water_clip_cap / sprite_reflection
// use stencil values 2 and 4 — they don't equal 8 either, so X-ray
// paints over water surfaces / reflections without interaction.
stencil_mode read, compare_not_equal, 8;

uniform sampler2D sprite_texture : source_color, filter_linear;
// Main viewport's depth buffer, sampled to decide whether this fragment
// is occluded. MUST be filter_nearest — depth values can't be
// interpolated across geometry boundaries; linear filtering would
// average player depth with whatever is behind it at sprite edges and
// fake an occluder that doesn't exist, firing xray on the player's own
// visible body.
uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;
// How much the X-ray darkens the framebuffer where the sprite is
// occluded. 0 = invisible, 1 = solid black silhouette. 0.2 reads as a
// subtle "something behind cover" cue. Authored on sprite_xray.tres
// so the whole game shares one xray strength.
uniform float xray_alpha : hint_range(0.0, 1.0) = 0.2;
// FP-precision tie-breaker against the base pass's own depth write. ~5cm
// is well inside any meaningful occluder thickness while staying outside
// FP noise. Sprite-on-sprite false positives are filtered by the stencil
// compare above, so this doesn't need to absorb that case.
uniform float xray_depth_bias : hint_range(0.0, 1.0) = 0.05;

// Instance-uniform order MUST match sprite_lit_common.gdshaderinc exactly
// for every uniform name shared between the base pass and this next_pass.
// Godot assigns instance-uniform indices in declaration order; mismatched
// indices across passes produce
//   "More than one material in instance export the same instance shader
//   uniform '<name>', but they do it with different indices."
// and silently route push(name, value) to whichever index Godot picked
// first — the other shader reads stale defaults. The depth-compare quad
// (forward_offset, etc.) is in this set, so a mis-aligned index makes
// the xray vertex stage compute a different POSITION than the base pass,
// breaking the discard-when-visible check entirely.
// Xray-only uniforms (xray_amount) go AFTER all shared names so they get
// fresh, non-conflicting indices.
instance uniform ivec2 sprite_size = ivec2(1, 1);
instance uniform ivec2 sprite_region_origin = ivec2(0, 0);
instance uniform bool sprite_mirror = false;
instance uniform float align_to_terrain : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 terrain_normal = vec3(0.0, 1.0, 0.0);
instance uniform float forward_offset = 0.0;
instance uniform float visibility : hint_range(0.0, 1.0) = 1.0;
// Memory-silhouette uniforms. NOT read by this shader anymore — the
// X-ray pass is just a flat black-at-alpha cue, independent of memory
// state. They MUST stay declared (same name + position) to keep
// instance-uniform indices aligned with sprite_lit_common.
instance uniform float silhouette_amount : hint_range(0.0, 1.0) = 0.0;
instance uniform vec3 silhouette_tint : source_color = vec3(0.0);
// === xray-only uniforms below — must follow all shared names ===
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
	// base pass uses; we only need the integer pixel coord for the alpha
	// cut (output is flat black, no per-texel color sampling).
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
	// across reverse-Z / projection variants — going through
	// INV_PROJECTION_MATRIX gives us a robust "meters in front of camera"
	// comparison.
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

	// Alpha-blend translucent black: framebuffer *= (1 - ALPHA). xray_amount
	// is the per-instance fade (InteractiveXray driver gates 0→1→0); the
	// shader-wide xray_alpha is the global strength knob.
	ALBEDO = vec3(0.0);
	EMISSION = vec3(0.0);
	ALPHA = xray_alpha * xray_amount;
}
