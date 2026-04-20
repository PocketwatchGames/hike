shader_type spatial;
render_mode blend_mix, depth_draw_never, cull_back, unshaded;

global uniform float camera_clip;

uniform sampler2D depth_tex : hint_depth_texture, filter_linear_mipmap;
// Framebuffer sampled by the SSR raymarch. Because water renders in the
// transparent pass AFTER opaque terrain + sprites, this holds everything
// we need to reflect (terrain silhouettes, tree lines, standing sprites).
uniform sampler2D screen_tex : hint_screen_texture, filter_linear_mipmap;
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
// sky_common.gdshaderinc owns: sun_world_dir, sun_color, horizon_color,
// zenith_color, cloud_scroll, cloud_scale, cloud_threshold,
// cloud_sharpness, cloud_color. Including it here makes the SSR miss
// fallback sample the SAME sky the sky dome shows — so reflections carry
// clouds + sun disk + horizon gradient, not a flat gradient.
#include "res://shaders/sky_common.gdshaderinc"
// Cloud shadow include needs sun_world_dir and the cloud_* uniforms (all
// declared above by sky_common) plus cloud_shadow_strength.
global uniform float cloud_shadow_strength;
#include "res://shaders/cloud_shadow.gdshaderinc"

uniform float water_alpha_min : hint_range(0.0, 1.0) = 0.3;
global uniform float water_alpha_max;
global uniform float water_depth_scale;
uniform vec3 water_shallow_tint : source_color = vec3(0.35, 0.7, 0.7);
uniform vec3 water_deep_tint : source_color = vec3(0.05, 0.15, 0.4);

// Ripple / reflection tuning — registered as globals by SkyController so
// inspector tweaks propagate live to every water material at once.
global uniform float ripple_scale_a;
global uniform float ripple_scale_b;
global uniform vec2 ripple_scroll_a;
global uniform vec2 ripple_scroll_b;
global uniform float ripple_strength;
global uniform float fresnel_power;
global uniform float reflection_strength;
global uniform float glint_sharpness;
global uniform float glint_strength;
global uniform float ssr_max_distance;
global uniform float ssr_steps;
global uniform float ssr_thickness;
// Shoreline foam — lighter noise band where water thickness falls below
// `foam_depth`, so shallow edges of water bodies read as surf.
global uniform vec3 foam_color;
global uniform float foam_depth;
global uniform float foam_scale;
global uniform vec2 foam_scroll;
global uniform float foam_strength;
global uniform float foam_threshold;
global uniform float foam_sharpness;

const float MIN_AMBIENT = 0.0;
// Hard cap on SSR iterations — GLSL needs a compile-time loop bound.
// ssr_steps (the global) clamps below this at runtime.
const int SSR_MAX_STEPS = 24;

float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i); float n100 = hash13(i + vec3(1,0,0));
	float n010 = hash13(i + vec3(0,1,0)); float n110 = hash13(i + vec3(1,1,0));
	float n001 = hash13(i + vec3(0,0,1)); float n101 = hash13(i + vec3(1,0,1));
	float n011 = hash13(i + vec3(0,1,1)); float n111 = hash13(i + vec3(1,1,1));
	return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
	           mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

// Two scrolling noise layers sum into a scalar height field. Sampled at
// world XZ so ripples stay locked to the world, not the camera.
float ripple_height(vec2 world_xz, float time) {
	vec2 uv_a = world_xz * ripple_scale_a + ripple_scroll_a * time;
	vec2 uv_b = world_xz * ripple_scale_b + ripple_scroll_b * time;
	return (vnoise(vec3(uv_a, 0.0)) + vnoise(vec3(uv_b, 0.0))) * 0.5;
}

// Finite-difference gradient of the height field → tangent-space normal,
// blended with flat +Y by ripple_strength.
vec3 ripple_normal(vec2 world_xz, float time) {
	float eps = 0.1;
	float h = ripple_height(world_xz, time);
	float hx = ripple_height(world_xz + vec2(eps, 0.0), time);
	float hz = ripple_height(world_xz + vec2(0.0, eps), time);
	vec3 n = vec3(-(hx - h) / eps, 1.0, -(hz - h) / eps);
	return normalize(mix(vec3(0.0, 1.0, 0.0), normalize(n), ripple_strength));
}

varying vec3 world_vertex;
varying vec3 world_normal;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;
}

void fragment() {
	if (world_vertex.y > camera_clip) {
		discard;
	}

	// Only the top face of a water voxel (normal.y ~= +1) represents an
	// open surface. Sides/bottom are submerged rims seen through adjacent
	// transparent cells — leave them flat and opaque.
	bool is_top = world_normal.y > 0.5;
	vec3 n = is_top ? ripple_normal(world_vertex.xz, TIME) : world_normal;

	// Face-direction shading (from the perturbed normal so highlight
	// direction shifts subtly with ripples).
	vec3 an = abs(n);
	float face_shade;
	if (an.y > 0.5) {
		face_shade = n.y > 0.0 ? 1.0 : 0.5;
	} else if (an.z > 0.5) {
		face_shade = 0.8;
	} else {
		face_shade = 0.6;
	}

	// Compute water thickness from depth buffer
	float scene_depth = textureLod(depth_tex, SCREEN_UV, 0.0).r;
	vec4 scene_ndc = vec4(SCREEN_UV * 2.0 - 1.0, scene_depth * 2.0 - 1.0, 1.0);
	vec4 scene_view = INV_PROJECTION_MATRIX * scene_ndc;
	scene_view /= scene_view.w;

	vec4 frag_ndc = vec4(SCREEN_UV * 2.0 - 1.0, FRAGCOORD.z * 2.0 - 1.0, 1.0);
	vec4 frag_view = INV_PROJECTION_MATRIX * frag_ndc;
	frag_view /= frag_view.w;

	float thickness = abs(scene_view.z - frag_view.z);
	float depth_factor = clamp(thickness / water_depth_scale, 0.0, 1.0);

	// Sample lightmap per-pixel for smooth interpolation across the face.
	// Water samples from itself: push 0.5 along -world_normal (the flat
	// face normal — not the ripple-perturbed one) to land inside the cell.
	vec3 light_sample_pos = world_vertex - world_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Water is `unshaded` (no light() stage / no ATTENUATION available), so
	// cloud shadow multiplies sun_lit directly. Block light (lm.gba) stays
	// unaffected — torches shining on water don't care about clouds.
	float cloud = cloud_shadow_ground(world_vertex, TIME);
	float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
	vec3 block_lit = lm.gba;
	vec3 light = max(min(vec3(1.0), sun_lit + block_lit), vec3(MIN_AMBIENT));
	vec3 shaded_light = face_shade * light;

	// Deeper water is darker and more opaque
	vec3 base_color = mix(water_shallow_tint, water_deep_tint, depth_factor);
	vec3 water_col = base_color * shaded_light;

	// Shoreline foam: stronger as water gets shallower (thickness → 0).
	// Only on top faces; side faces abut solid terrain and the foam would
	// smear up the vertical rim. Animated by scrolling noise so it reads
	// as surf rather than a static outline.
	float foam_amount = 0.0;
	if (is_top) {
		float shallow = 1.0 - smoothstep(0.0, foam_depth, thickness);
		vec2 fuv = world_vertex.xz * foam_scale + foam_scroll * TIME;
		float fn = vnoise(vec3(fuv, 0.0));
		float band = max(1e-4, 1.0 - foam_sharpness);
		float mask = smoothstep(foam_threshold, min(foam_threshold + band, 1.0), fn);
		foam_amount = shallow * mask * foam_strength;
		// Foam dims correctly in shade — sample the same light we used for
		// the water tint so night / torchlit shores still read right.
		water_col = mix(water_col, foam_color * light, foam_amount);
	}

	// Reflection only matters on the open water surface.
	vec3 final_col = water_col;
	if (is_top) {
		vec3 view_to_frag = normalize(world_vertex - CAMERA_POSITION_WORLD);
		vec3 refl = reflect(view_to_frag, n);

		// Fresnel: more reflective at grazing view angles.
		float fresnel = pow(1.0 - clamp(dot(-view_to_frag, n), 0.0, 1.0), fresnel_power);

		// Sample the sky at the reflection direction. Use the origin-
		// aware variant so each fragment projects its reflection ray to
		// the cloud plane — under an ortho camera, all fragments share
		// a reflection direction and sample_sky would give a uniform
		// color; sample_sky_from uses world_vertex to break that.
		// Gated by sun_mask so underground water sees no sky at all.
		vec3 sky_col = sample_sky_from(world_vertex, refl, TIME) * sun_mask * cloud_sun;

		// Extra wide soft glint on top of the sky dome's tight sun disk.
		// The disk in sample_sky is a 1-pixel pinprick; this adds a
		// ripple-shimmering halo so highlights read from farther away.
		float glint = pow(max(0.0, dot(refl, -normalize(sun_world_dir))), glint_sharpness);
		sky_col += sun_color * glint * glint_strength * sun_mask * cloud_sun;

		// SSR raymarch. Step along the reflection ray in world space,
		// project each step to NDC, compare with depth_tex. First hit
		// inside `ssr_thickness` view-space units counts; sample screen_tex
		// for the color.
		vec3 ssr_col = vec3(0.0);
		float ssr_alpha = 0.0;
		float step_world = ssr_max_distance / max(ssr_steps, 1.0);
		vec3 ray_origin = world_vertex + n * 0.05; // bias off the surface
		for (int i = 1; i <= SSR_MAX_STEPS; i++) {
			if (float(i) > ssr_steps) { break; }
			vec3 p = ray_origin + refl * (step_world * float(i));
			vec4 clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(p, 1.0);
			if (clip.w <= 0.0) { break; }
			vec3 ndc = clip.xyz / clip.w;
			if (abs(ndc.x) > 1.0 || abs(ndc.y) > 1.0 || ndc.z < -1.0 || ndc.z > 1.0) { break; }
			vec2 uv = ndc.xy * 0.5 + 0.5;
			float d = textureLod(depth_tex, uv, 0.0).r;
			float pz = ndc.z * 0.5 + 0.5;
			if (d < pz) {
				// Ray went behind a surface this step. Verify the hit is
				// within a thin slab of the actual geometry (reject back-
				// of-terrain hits where the ray has tunneled way past).
				vec4 hit_ndc = vec4(ndc.xy, d * 2.0 - 1.0, 1.0);
				vec4 hit_view = INV_PROJECTION_MATRIX * hit_ndc;
				hit_view /= hit_view.w;
				vec4 p_view = VIEW_MATRIX * vec4(p, 1.0);
				if (abs(hit_view.z - p_view.z) < ssr_thickness + step_world) {
					// Fade near screen edges so the reflection doesn't just
					// pop off when the ray exits the frame.
					vec2 edge = abs(uv * 2.0 - 1.0);
					float edge_fade = 1.0 - smoothstep(0.85, 1.0, max(edge.x, edge.y));
					ssr_col = textureLod(screen_tex, uv, 0.0).rgb;
					ssr_alpha = edge_fade;
				}
				break;
			}
		}

		vec3 refl_col = mix(sky_col, ssr_col, ssr_alpha);
		float refl_mix = fresnel * reflection_strength;
		final_col = mix(water_col, refl_col, refl_mix);
	}

	ALBEDO = final_col;
	// Foam is opaque-ish — without this it fades into the shallow-water
	// alpha and the surf effect is invisible.
	ALPHA = max(mix(water_alpha_min, water_alpha_max, depth_factor), foam_amount);
}
