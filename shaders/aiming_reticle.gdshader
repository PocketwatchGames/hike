shader_type spatial;
// Aiming reticle overlay — main forward line, vertical drop line, ground ring,
// and the two spread lines all share this shader. depth_test_disabled puts the
// pass in the transparent queue so DEPTH_TEXTURE is fully populated when we
// sample it for the manual occlusion test (mirrors sprite_xray.gdshader).
//
// AA note: the lines are box meshes, and the visible faces of a box sit AT
// the silhouette — so a fwidth-based fade keyed on cross-section distance
// collapses to alpha=0 on every side face (one cross axis is pinned to the
// box edge there). This shader therefore does NOT shader-AA the line edges;
// the boxes are sized so the line is reliably ≥ 2 inner-viewport pixels and
// the result reads as a clean chunky line consistent with the rest of the
// pixel-art pipeline. The ground ring is a flat PlaneMesh — `length(xz)`
// varies smoothly across the surface, so smoothstep AA on its inner / outer
// edge does work and is applied below.
//
// Per-instance behavior:
//   * gradient_*_distance + gradient_origin_world drive the main line's
//     transparent→opaque ramp from 1m to 3m of player distance. Leaving both
//     distances at 0 disables the gradient — the rest of the reticle uses
//     solid max_alpha.
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
