// Flat-on-ground pixel-art sprite. Uses the same shared fragment + light()
// stages as the upright variant via sprite_lit_common.gdshaderinc — the
// only thing different is the vertex stage, which lays the quad in the
// world XZ plane (camera-yawed) instead of as a Y-aligned billboard.
//
// Anchor convention: the quad is authored on a Sprite3D with
// centered = false, pixel_size = 1.0, offset = (-W/2, -H/2). VERTEX
// therefore spans:
//   x in [-W/2, W/2], y in [-H/2, H/2]
// in source-pixel units, with VERTEX = (0, 0) at the node origin (the
// center of the flat sprite). FlatLitSprite.cs's ApplyOffset() forces
// this offset so the sprite radiates outward from the anchor. The
// .tscn-authored Offset is overwritten — flat sprites are always
// center-anchored, by convention.
//
// Math:
//   VERTEX.x → sprite_right (horizontal, camera-yawed, perpendicular to
//              camera view direction). Projects 1:1 to screen X under
//              orthographic projection, so no stretch is needed on this
//              axis — same situation as upright's horizontal axis.
//   VERTEX.y → sprite_depth_away (horizontal, points AWAY from camera, so
//              VERTEX.y > 0 lands at higher screen Y under iso pitch).
//              Under fixed iso pitch θ this axis projects to screen Y
//              with factor sin(θ); we scale it by sprite_stretch_flat
//              (= 1/sin(θ), pushed by GameClient) so 1 source pixel
//              maps to 1 screen pixel.
//
// Result: sprite_right_screen ≈ (1, 0) and sprite_up_screen ≈ (0, -1)
// just like the upright variant when align_to_terrain=0. The fragment
// stage's screen-basis inversion to recover source-pixel coords works
// identically, giving the same texelFetch-style pixel-perfect output.
//
// The sprite rotates with camera yaw (around its anchor) to keep this
// invariant. Visually that means a flat sprite spins as the camera yaws
// — fine for rotationally-symmetric art (campfire ring, magic circle)
// but readable-as-spinning for directional art (an arrow). Authors who
// need world-fixed flat sprites need a different shader/class — this
// variant is calibrated for camera-yawed only.

// Per-body src_px Y origin factor consumed by the shared fragment stage.
// Flat sprites are authored with offset = (-W/2, -H/2) so VERTEX.y spans
// [-H/2, H/2] (anchor at the SPRITE CENTER, not at an edge). Common's
// fragment formula is src_px.y = -sxy.y + sprite_size.y * factor; with
// factor = 0.5 this becomes -sxy.y + H/2, so VERTEX.y = 0 maps to source
// row H/2 (center of source art) instead of past-the-bottom-edge.
// Declared BEFORE the include so the const is in scope when common's
// fragment() references it.
const float src_px_y_origin_factor = 0.5;

#include "res://shaders/sprite_lit_common.gdshaderinc"

// 1/sin(camera_pitch_from_horizontal). Pushed every frame by GameClient
// alongside sprite_stretch (= 1/cos). Required for pixel-perfect output:
// the depth axis under iso pitch projects to screen Y with sin(pitch),
// and we compensate by stretching that axis by 1/sin(pitch) in world.
global uniform float sprite_stretch_flat;

void vertex() {
	world_origin = MODEL_MATRIX[3].xyz;
	// Column length = scale along that axis (for uniform-scale + rotation).
	instance_scale = length(MODEL_MATRIX[0].xyz);

	vec3 world_up = vec3(0.0, 1.0, 0.0);
	vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
	vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
	// sprite_forward points FROM the sprite TOWARD the camera (horizontal
	// projection of cam_back). The flat sprite's "up" in source art lies
	// along the OPPOSITE direction so VERTEX.y > 0 lands farther from the
	// camera (and thus higher in screen under iso pitch).
	vec3 sprite_forward = cross(sprite_right_yaw, world_up);
	vec3 sprite_depth_away = -sprite_forward;

	float stretch = max(sprite_stretch_flat, 1e-4);
	float chunky = max(sprite_chunky, 1e-6);

	// Right axis is horizontal perpendicular to camera direction → already
	// 1:1 to screen X under orthographic projection. No stretch.
	vec3 right_axis = sprite_right_yaw;
	// Depth axis is horizontal toward away-from-camera → projects to
	// screen Y with factor sin(pitch). Scale by sprite_stretch_flat
	// (= 1/sin(pitch)) so 1 source pixel maps to 1 screen pixel.
	vec3 up_axis = sprite_depth_away * stretch;

	vec3 vertex_world = world_origin
		+ VERTEX.x * chunky * instance_scale * right_axis
		+ VERTEX.y * chunky * instance_scale * up_axis;
	frag_world_pos = vertex_world;
	POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

	// Project origin + a 1-source-pixel step along each axis into pixel
	// space, so the fragment stage can invert the basis to recover the
	// source pixel for each screen fragment. Should collapse to
	// (1, 0) / (0, -1) when sprite_stretch_flat is correctly calibrated
	// to 1/sin(pitch), giving the same screen-basis the upright variant
	// produces — fragment math is shared.
	vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
	vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * right_axis, 1.0);
	vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + chunky * instance_scale * up_axis, 1.0);
	vec2 origin_ndc = origin_clip.xy / origin_clip.w;
	vec2 right_ndc = right_clip.xy / right_clip.w;
	vec2 up_ndc = up_clip.xy / up_clip.w;
	anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
	sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
	sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}
