// Upright camera-facing pixel-art sprite. Body is shared between
// sprite_lit.gdshader (props) and sprite_lit_character.gdshader (player /
// mobs / interactives). Both wrappers declare shader_type spatial and the
// same render_mode set; the character variant additionally declares
// `stencil_mode write, compare_always, 8` so its base-pass writes mark the
// depth buffer as "this pixel was a character/interactive" — the X-ray
// pass reads that stencil bit to skip those pixels and avoid sprite-on-
// sprite false positives.
//
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
// The fragment stage (in sprite_lit_common.gdshaderinc) computes the source
// texel from FRAGCOORD relative to the snapped screen-space projection of
// the anchor, so every fragment samples an exact integer source pixel — no
// UV interpolation drift.

// Per-body src_px Y origin factor consumed by the shared fragment stage.
// Upright: VERTEX.y ∈ [0, H], anchor at bottom edge → 1.0 (so src_px.y =
// -sxy.y + sprite_size.y maps anchor fragments to source row H-1).
// Declared BEFORE the include so the const is in scope when common's
// fragment() references it.
const float src_px_y_origin_factor = 1.0;

#include "res://shaders/sprite_lit_common.gdshaderinc"

// align_to_terrain / terrain_normal / forward_offset are declared in
// sprite_lit_common.gdshaderinc (so all sprite_lit-family shaders share
// the same instance-uniform index order; sprite_xray as a next_pass
// relies on that alignment). Their semantic comments live here since
// only the upright vertex stage actually consumes them:
//
//   align_to_terrain : 0 = upright billboard (default), 1 = sprite plane
//                      rolls in-plane around sprite_forward to follow
//                      terrain_normal (same math detail_sprite uses for
//                      scattered grass). Lerp the roll angle in between.
//                      Flat ground (terrain_normal = world-up) makes
//                      roll = 0 regardless, so it's safe to leave at
//                      its default when a caller doesn't know the slope.
//
//   terrain_normal   : the per-instance world-space normal of the surface
//                      under the sprite anchor; ignored when
//                      align_to_terrain is 0.
//
//   forward_offset   : meters of push toward the camera along the
//                      horizontal billboard-forward axis. Recomputed
//                      from cam_back every frame so it follows camera
//                      yaw — unlike a static node Z offset, which bakes
//                      a fixed world-space vector into MODEL_MATRIX and
//                      only reads as "forward" from one angle. Used to
//                      park the sprite at the front edge of a cylinder
//                      collider so the art lines up visually with the
//                      walkable footprint; collider stays at node
//                      origin, only the rendered sprite shifts.

global uniform float sprite_stretch;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	// Column length = scale along that axis (for uniform-scale + rotation).
	instance_scale = length(MODEL_MATRIX[0].xyz);

	// Two-stage billboard identical to detail_sprite.gdshader's:
	//   1) Yaw — rotate the sprite plane around world Y so it faces the
	//      camera. sprite_right_yaw is horizontal, world_up is the plane's
	//      authored up axis. The plane never pitches into/out of screen.
	//   2) Roll — rotate within the plane around sprite_forward (the
	//      out-of-plane axis) so the sprite's up axis follows terrain_normal.
	//      The angle is lerped by align_to_terrain, so 0 keeps the original
	//      upright behaviour and 1 fully rolls to match the slope.
	// Flat ground (terrain_normal = world-up) makes roll = 0 regardless of
	// align_to_terrain, so it's safe to leave terrain_normal at its default
	// when a caller doesn't know the slope.
	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
	vec3 sprite_forward = cross(sprite_right_yaw, world_up);

	// Shift the sprite toward the camera along the billboard-forward axis.
	// Must happen after sprite_forward is resolved and before world_origin is
	// consumed by the billboard expansion + clip-space projection below, so
	// vertices, anchor_px, and the fragment's light-sample position all share
	// the shifted origin. sprite_forward = cross(sprite_right_yaw, world_up)
	// evaluates to the horizontal projection of cam_back, which points FROM
	// the sprite TOWARD the camera (cam_back is the world direction of the
	// camera's +Z / "behind-the-lens" axis, which from any point in the
	// scene reads as "toward the camera"). So we add.
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

	// Camera-pitch compensation: stretch only the world-Y component of each
	// rolled axis so that whichever axis ends up vertical in world space
	// (the upright-case up axis, or a partly-vertical axis of a rolled
	// sprite) projects 1 source pixel to 1 screen pixel under iso pitch.
	// Horizontal components pass through unchanged — they're already in the
	// screen plane.
	vec3 right_stretched = vec3(sprite_right_rolled.x, sprite_right_rolled.y * stretch, sprite_right_rolled.z);
	vec3 up_stretched = vec3(sprite_up_rolled.x, sprite_up_rolled.y * stretch, sprite_up_rolled.z);

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * right_stretched
		+ VERTEX.y * chunky * instance_scale * up_stretched;
	frag_world_pos = vertex_world;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	// Project origin + a 1-source-pixel step along each rolled axis into
	// pixel space, so the fragment stage can invert the basis to recover
	// the source pixel for each screen fragment. This replaces the upright-
	// only d.x/d.y → tex_coord mapping; for align_to_terrain = 0 the basis
	// collapses to (1, 0) / (0, -1) and reproduces the old mapping exactly.
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
