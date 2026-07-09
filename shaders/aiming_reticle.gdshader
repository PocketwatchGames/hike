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
// When a fragment fails the manual occlusion z-test (scene geometry in front of
// it) its color is mixed toward occluded_color by occluded_mix, in addition to
// the occluded_alpha_scale dim — so the slice of the ring behind a wall / tree /
// grass reads as a distinct "show-through" state rather than just fainter.
uniform float occluded_mix : hint_range(0.0, 1.0) = 0.6;
uniform vec3 occluded_color : source_color = vec3(0.4, 0.55, 1.0);
// Floor on the slope cosine used to keep the draped ring's apparent thickness
// constant (see fragment). Lower = thinner bands tolerated on steeper slopes
// before the compensation is clamped; too low and a near-vertical band vanishes.
uniform float ring_min_slope_cos : hint_range(0.05, 1.0) = 0.35;

// Ground-ring terrain draping. reticle_heights is a RETICLE_PATCH_RES x
// RETICLE_PATCH_RES grid of world-Y surface heights under the ring, resolved
// per-frame on the CPU (AimingReticle.UpdateGroundUndulation) by scanning the
// voxel column at each patch point. The ground-ring vertex path bilinearly
// samples it to set each vertex's world Y, so the ring drapes over hills and
// cave floors instead of sitting on one flat plane. reticle_undulate gates it
// (0.0 = flat fallback; billboard lines never sample it). Array size MUST equal
// RETICLE_PATCH_RES * RETICLE_PATCH_RES.
const int RETICLE_PATCH_RES = 16;
uniform float reticle_heights[256];
uniform float reticle_undulate = 0.0;

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

// World-space mapping of the height patch for this instance: patch covers the
// square [origin, origin + size] in world XZ. Set by AimingReticle alongside
// reticle_heights when undulation is active.
instance uniform vec2 reticle_patch_origin = vec2(0.0);
instance uniform float reticle_patch_size = 1.0;

// Per-instance color tint multiplied into the line/ring color. Defaults to
// white so untreated meshes keep the material's line_color unchanged; the
// ground circle gets pushed to red while the bow is locked on a mob.
instance uniform vec3 instance_color : source_color = vec3(1.0, 1.0, 1.0);

varying vec3 world_pos;
varying vec3 local_pos;
varying float side_uv;
varying float is_billboard;
varying vec2 line_uv;
// Explicitly forward the per-vertex COLOR to the fragment stage. The arc ribbon
// (an ImmediateMesh) carries its fade-in/out in COLOR.a; reading COLOR directly
// in fragment() dropped it to 0 (invisible ribbon), so pass it through a named
// varying. Meshes without vertex colors supply COLOR = (1,1,1,1), so this stays
// a no-op for the lines / ground ring.
varying vec4 v_vertex_color;

void vertex() {
	v_vertex_color = COLOR;
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
		vec3 wp = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
		// Drape the ground ring over terrain: replace the vertex's world Y with
		// the height sampled (bilinearly) from the reticle height-patch. The
		// annulus mask below still uses the planar local_pos.xz, so only the
		// vertical profile changes — the ring stays a clean circle in plan view.
		if (reticle_undulate > 0.5) {
			vec2 pp = (wp.xz - reticle_patch_origin) / max(reticle_patch_size, 1e-4);
			pp = clamp(pp, 0.0, 1.0) * float(RETICLE_PATCH_RES - 1);
			ivec2 c0 = ivec2(floor(pp));
			ivec2 c1 = min(c0 + ivec2(1), ivec2(RETICLE_PATCH_RES - 1));
			vec2 fr = pp - vec2(c0);
			float h00 = reticle_heights[c0.y * RETICLE_PATCH_RES + c0.x];
			float h10 = reticle_heights[c0.y * RETICLE_PATCH_RES + c1.x];
			float h01 = reticle_heights[c1.y * RETICLE_PATCH_RES + c0.x];
			float h11 = reticle_heights[c1.y * RETICLE_PATCH_RES + c1.x];
			wp.y = mix(mix(h00, h10, fr.x), mix(h01, h11, fr.x), fr.y);
		}
		world_pos = wp;
		POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(wp, 1.0);
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
		// Keep the ring's apparent thickness ~constant on slopes. A flat annulus
		// of fixed XZ width drapes onto more *surface* distance as the ground
		// tilts (width / cos(slope)), so it reads as a fat band on steep terrain.
		// Recover the slope from the draped geometry's screen-space derivatives
		// and shrink the band's radial half-width by cos(slope) to compensate.
		// (On flat ground / undulation off, cos_slope = 1 → unchanged.)
		vec3 cr = cross(dFdx(world_pos), dFdy(world_pos));
		float cl = length(cr);
		float cos_slope = cl > 1e-6 ? clamp(abs(cr.y) / cl, ring_min_slope_cos, 1.0) : 1.0;
		float mid = (ring_outer_radius + ring_inner_radius) * 0.5;
		float half_w = (ring_outer_radius - ring_inner_radius) * 0.5 * cos_slope;
		float aa_w = max(fwidth(r), 1e-5);
		// Cap the AA span to the band so near-vertical draped triangles (huge
		// fwidth) don't smear the thin band into a soft blob.
		aa_w = min(aa_w, half_w);
		float d = abs(r - mid);
		if (d > half_w + aa_w) {
			discard;
		}
		alpha *= 1.0 - smoothstep(half_w - aa_w, half_w + aa_w, d);
	}

	float scene_depth_raw = textureLod(DEPTH_TEXTURE, SCREEN_UV, 0.0).x;
	vec4 scene_clip = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth_raw, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_clip;
	float scene_view_z = -scene_view.z / scene_view.w;

	vec4 frag_clip = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_clip;
	float frag_view_z = -frag_view.z / frag_view.w;

	vec3 out_rgb = tex_rgb * instance_color;
	if (frag_view_z > scene_view_z + depth_bias) {
		alpha *= occluded_alpha_scale;
		out_rgb = mix(out_rgb, occluded_color, occluded_mix);
	}

	ALBEDO = out_rgb;
	// v_vertex_color.a carries the arc ribbon's per-vertex fade (in/out), forwarded
	// from the vertex stage. Meshes without vertex colors get (1,1,1,1), so this is
	// a no-op for them.
	ALPHA = alpha * alpha_multiplier * v_vertex_color.a;
}
