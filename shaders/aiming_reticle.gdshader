shader_type spatial;
// Aiming reticle overlay — main forward line, vertical drop line, spread
// lines, and ground ring all share this shader. depth_test_disabled puts the
// pass in the transparent queue so DEPTH_TEXTURE is fully populated when we
// sample it for the manual occlusion test (mirrors sprite_xray.gdshader).
//
// Two geometry paths:
//   * line_width_world > 0 → CAMERA-FACING BILLBOARD RIBBON. The mesh is a
//     unit QuadMesh; the vertex shader rotates and stretches it into a quad
//     spanning line_start_world → line_end_world, width = line_width_world,
//     rotated around the line axis to face the camera. side_uv goes -1..+1
//     across the quad's width so the fragment shader can fade alpha at the
//     silhouette via fwidth+smoothstep — coverage-based AA at inner-pixel
//     granularity, which the NN upscale preserves as chunky-pixel partial
//     transparency at edges (the visual style the reticle wants).
//   * line_width_world == 0 → STANDARD MODEL_MATRIX path. Used by the
//     ground ring (a flat PlaneMesh + annulus mask). The cross-section AA
//     formula doesn't apply; ring inner/outer edges have their own fwidth AA.
//
// Per-instance behavior on the billboard path:
//   * gradient_*_distance + gradient_origin_world drive the main line's
//     transparent→opaque ramp from 1m to 3m of player distance. Leaving both
//     distances at 0 disables the gradient — the rest of the reticle uses
//     solid max_alpha.
//
// Per-instance texture mode (billboard path):
//   * use_line_texture > 0.5 → sample `line_texture` over (U = across, V =
//     along the line) instead of running the fwidth/smoothstep coverage AA.
//     The texture's alpha channel becomes the line's alpha (multiplied by
//     max_alpha + gradient + occlusion as usual). RGB is multiplied into
//     line_color so a colored texture can tint the line.
//     `line_texture` is a SHARED uniform — assign it on the material .tres
//     once; only the meshes with use_line_texture=1 actually sample it.
//     Defaults to a white pixel via hint_default_white, so when no texture
//     is assigned the texture path produces a plain solid line equivalent
//     to "no AA, full alpha across the ribbon."
render_mode blend_mix, depth_draw_never, depth_test_disabled, cull_disabled, unshaded, skip_vertex_transform;

uniform sampler2D DEPTH_TEXTURE : hint_depth_texture, filter_nearest;
uniform sampler2D line_texture : source_color, filter_linear, hint_default_white;
uniform vec4 line_color : source_color = vec4(1.0, 1.0, 1.0, 1.0);
uniform float max_alpha : hint_range(0.0, 1.0) = 0.5;
uniform float occluded_alpha_scale : hint_range(0.0, 1.0) = 0.5;
uniform float depth_bias : hint_range(0.0, 1.0) = 0.05;

instance uniform vec3 line_start_world = vec3(0.0);
instance uniform vec3 line_end_world = vec3(0.0);
instance uniform float line_width_world = 0.0;
instance uniform float use_line_texture = 0.0;
// Multiplied into the final ALPHA at fragment exit. Driven from script for
// the 0.15s fade-out when the ranged weapon becomes unavailable mid-aim.
instance uniform float alpha_multiplier : hint_range(0.0, 1.0) = 1.0;

instance uniform vec3 gradient_origin_world = vec3(0.0);
instance uniform float gradient_start_distance = 0.0;
instance uniform float gradient_end_distance = 0.0;

instance uniform float ring_inner_radius = 0.0;
instance uniform float ring_outer_radius = 0.0;

// Per-instance color tint multiplied into the line/ring color. Defaults to
// white so untreated meshes keep the material's line_color unchanged; the
// ground circle gets pushed to red while the bow is locked on a mob.
instance uniform vec3 instance_color : source_color = vec3(1.0, 1.0, 1.0);

varying vec3 world_pos;
varying vec3 local_pos;
varying float side_uv;
varying float is_billboard;
varying vec2 line_uv;

void vertex() {
	if (line_width_world > 0.0) {
		float t = VERTEX.y + 0.5;
		float side = VERTEX.x * 2.0;

		vec3 axis_pos = mix(line_start_world, line_end_world, t);
		vec3 line_delta = line_end_world - line_start_world;
		float line_len = length(line_delta);
		vec3 line_dir = line_len > 1e-5 ? line_delta / line_len : vec3(0.0, 0.0, 1.0);

		vec3 cam_pos = INV_VIEW_MATRIX[3].xyz;
		vec3 to_cam = cam_pos - axis_pos;
		vec3 right = cross(line_dir, to_cam);
		float right_len = length(right);
		if (right_len > 1e-5) {
			right /= right_len;
		} else {
			right = INV_VIEW_MATRIX[0].xyz;
		}

		vec3 vertex_world = axis_pos + right * (side * line_width_world * 0.5);
		POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);
		world_pos = vertex_world;
		local_pos = VERTEX;
		side_uv = side;
		is_billboard = 1.0;
		// U = 0..1 across the ribbon width, V = 0..1 along the line. Textures
		// authored "wide" (long axis = U) will need to be rotated on import.
		line_uv = vec2((side + 1.0) * 0.5, t);
	} else {
		world_pos = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_pos, 1.0);
		local_pos = VERTEX;
		side_uv = 0.0;
		is_billboard = 0.0;
		line_uv = vec2(0.0);
	}
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

	vec3 tex_rgb = line_color.rgb;
	if (is_billboard > 0.5) {
		if (use_line_texture > 0.5) {
			vec4 tex = texture(line_texture, line_uv);
			alpha *= tex.a;
			tex_rgb *= tex.rgb;
		} else {
			float r = abs(side_uv);
			float aa_w = max(fwidth(r), 1e-5);
			alpha *= 1.0 - smoothstep(1.0 - aa_w, 1.0, r);
		}
	}

	if (ring_outer_radius > 0.0) {
		float r = length(local_pos.xz);
		float aa_w = max(fwidth(r), 1e-5);
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

	ALBEDO = tex_rgb * instance_color;
	ALPHA = alpha * alpha_multiplier;
}
