shader_type spatial;
// Aiming reticle overlay — main forward line, vertical drop line, ground ring,
// and the two spread lines all share this shader. depth_test_disabled puts the
// pass in the transparent queue so DEPTH_TEXTURE is fully populated when we
// sample it for the manual occlusion test (mirrors sprite_xray.gdshader).
//
// Per-instance behavior:
//   * gradient_*_distance + gradient_origin_world drive the main line's
//     transparent→opaque ramp from 1m to 3m of player distance. Leaving both
//     distances at 0 disables the gradient — the rest of the reticle uses
//     solid max_alpha.
//   * line_radius > 0 enables cross-section AA on a thin box. line_axis picks
//     which mesh-local axis runs along the line (0=X, 1=Y, 2=Z); the other
//     two form the cross-section the L∞-distance fades over via fwidth, so
//     the edge is always ~1 inner-viewport pixel wide regardless of camera
//     distance / angle.
//   * ring_outer_radius > 0 turns a unit PlaneMesh into an annulus, with
//     fwidth-AA on both edges. The ground circle uses this; everything else
//     leaves it at 0.
render_mode blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, unshaded;

uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;
uniform vec4 line_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float max_alpha : hint_range(0.0, 1.0) = 0.5;
uniform float occluded_alpha_scale : hint_range(0.0, 1.0) = 0.5;
// Tie-breaker against the opaque pass's own depth values so a line lying
// flush on the ground reads as "visible" rather than flickering between
// occluded and visible. ~5cm matches sprite_xray's bias.
uniform float depth_bias : hint_range(0.0, 1.0) = 0.05;

instance uniform vec3 gradient_origin_world = vec3(0.0);
instance uniform float gradient_start_distance = 0.0;
instance uniform float gradient_end_distance = 0.0;

instance uniform float line_radius = 0.0;
instance uniform float line_axis = 2.0;

instance uniform float ring_inner_radius = 0.0;
instance uniform float ring_outer_radius = 0.0;

varying vec3 world_pos;
varying vec3 local_pos;

void vertex() {
	world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	local_pos = VERTEX;
}

void fragment() {
	float alpha = max_alpha;

	if (gradient_end_distance > gradient_start_distance + 1e-4) {
		float d = distance(world_pos, gradient_origin_world);
		float t = clamp(
			(d - gradient_start_distance) / (gradient_end_distance - gradient_start_distance),
			0.0, 1.0);
		alpha *= t;
	}

	if (line_radius > 0.0) {
		// Cross-section: the two mesh-local axes that aren't "along" the line.
		// L∞ distance (max of abs components) matches the box's square cross-
		// section so the AA tracks the actual silhouette.
		vec2 cross_xy;
		if (line_axis < 0.5) {
			cross_xy = local_pos.yz;
		} else if (line_axis < 1.5) {
			cross_xy = local_pos.xz;
		} else {
			cross_xy = local_pos.xy;
		}
		float r = max(abs(cross_xy.x), abs(cross_xy.y));
		float aa_w = max(fwidth(r), 1e-5);
		alpha *= 1.0 - smoothstep(line_radius - aa_w, line_radius, r);
	}

	if (ring_outer_radius > 0.0) {
		float r = length(local_pos.xz);
		float aa_w = max(fwidth(r), 1e-5);
		// Skip pixels well outside the annulus — they'd contribute zero alpha
		// anyway and the discard saves the depth-texture sample below.
		if (r > ring_outer_radius + aa_w || r < ring_inner_radius - aa_w) {
			discard;
		}
		float outer_aa = 1.0 - smoothstep(ring_outer_radius - aa_w, ring_outer_radius, r);
		float inner_aa = smoothstep(ring_inner_radius, ring_inner_radius + aa_w, r);
		alpha *= outer_aa * inner_aa;
	}

	float scene_depth_raw = textureLod(DEPTH_TEXTURE, SCREEN_UV, 0.0).x;
	vec4 scene_clip = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth_raw, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_clip;
	float scene_view_z = -scene_view.z / scene_view.w;

	vec4 frag_clip = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_clip;
	float frag_view_z = -frag_view.z / frag_view.w;

	if (frag_view_z > scene_view_z + depth_bias) {
		alpha *= occluded_alpha_scale;
	}

	ALBEDO = line_color.rgb;
	ALPHA = alpha;
}
