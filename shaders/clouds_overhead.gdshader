// 2D screen-space cloud layer rendered as a fullscreen quad while bird's-eye
// view is engaged. Opacity is driven by Beer-Lambert path-length integration
// through a per-XZ cloud column — NO volumetric raymarch, no along-ray noise
// sampling. The shader does at most four texture reads per pixel:
//
//   1. base_noise    — coarse cloud_noise_tex sample that places the cloud's
//                      vertical position within [band_bottom, band_top] at
//                      this pixel's XZ. The cloud at this XZ occupies the
//                      vertical column [effective_base_y, band_top].
//   2. cloud_noise   — primary fine-frequency sample → density (drives both
//                      ALPHA via Beer-Lambert and color via the sun probe).
//   3. detail_noise  — higher-frequency octave that erodes the base coverage,
//                      producing the crisp internal sub-puff structure that
//                      makes a cloud read as "chunky" rather than smooth.
//   4. shadow_probe  — coverage sample at p.xz + to_sun.xz × distance for
//                      the lit-vs-shadow color split.
//
// Path-length math: for a ray going downward through the cloud column at
// this XZ, the traversal `t_exit - t_enter` shortens as the camera moves
// from above the band down through it (and lengthens as the camera rises
// above the band). With density × extinction in the Beer-Lambert term,
// alpha fades in as the camera rises through the cloud layer — the
// classic "fly into the fog" effect, but driven by analytic geometry
// instead of per-step integration.
shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, blend_mix, cull_disabled;

uniform sampler2D depth_tex : hint_depth_texture;

global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform vec3 cloud_color;
global uniform vec3 sun_world_dir;
global uniform vec3 sun_color;

// World-Y bounds of the cloud layer. Pushed by GameClient each frame to
// 50% and 75% of player→camera height. Top must be > bottom; if they
// cross the shader silently skips drawing.
uniform float band_bottom_altitude = 0.0;
uniform float band_top_altitude = 100.0;

// XZ tile rate for the primary coverage / color sample. Smaller =
// larger puffs in world units.
uniform float cloud_tex_tiling : hint_range(0.001, 0.05) = 0.005;
// Coarser tile rate for the base-offset noise — picks the cloud's
// vertical position per XZ. Phase-shifted via cloud_offset * 0.3 so
// it's visually independent from coverage despite sampling the same
// texture.
uniform float cloud_base_tiling : hint_range(0.0005, 0.02) = 0.002;

// Higher-frequency detail octave that erodes the base coverage —
// adds sub-puff structure inside each cloud and sharpens silhouettes
// by carving holes into otherwise smooth blobs. The main "more
// defined internal shapes" knob.
uniform float cloud_detail_scale : hint_range(1.5, 12.0) = 4.5;
uniform float cloud_detail_strength : hint_range(0.0, 0.6) = 0.4;

// Coverage curve sharpener. The smoothstep remaps noise to soft
// coverage; pow(coverage, mask_power) crushes the curve toward a hard
// silhouette.
uniform float cloud_mask_power : hint_range(1.0, 8.0) = 4.0;
// Edge-sharpening smoothstep on the final alpha after Beer-Lambert.
// Pushes near-1 to 1 and near-0 to 0 so puff edges resolve as crisp
// pixels. 0 disables it. Tight values (0.05–0.15) give pixel-art-style
// hard outlines.
uniform float cloud_edge_sharpness : hint_range(0.0, 0.5) = 0.12;

// Optical extinction per meter at full density. Higher = opaque clouds
// at shorter path lengths (camera doesn't need to be far above the
// band before clouds saturate). Lower = soft hazy fog feel.
uniform float cloud_extinction : hint_range(0.001, 1.0) = 0.08;

// Sun-shadow probe — samples coverage at p.xz + to_sun.xz × distance.
uniform float cloud_sun_shadow_distance : hint_range(1.0, 60.0) = 10.0;
uniform float cloud_sun_shadow_strength : hint_range(0.0, 6.0) = 2.5;
uniform vec3 cloud_shadow_color : source_color = vec3(0.35, 0.4, 0.5);
uniform vec3 cloud_ambient : source_color = vec3(0.55, 0.6, 0.7);

// Normal reconstruction via density gradient. Sample cloud_noise at two
// neighbor offsets, take the finite differences against the center to
// approximate the surface gradient at this XZ, then build a fake surface
// normal pointing "up + away from the cloud's center". The +Y component
// is fixed at `cloud_normal_up` so the cloud's top reads as a roughly
// horizontal sheet; gradient × strength tilts the normal outward at
// edges and inward at dense regions, giving the cloud a domed look
// under directional light.
uniform float cloud_normal_uv_offset : hint_range(0.001, 0.05) = 0.008;
uniform float cloud_normal_strength : hint_range(0.5, 12.0) = 4.0;
uniform float cloud_normal_up : hint_range(0.1, 4.0) = 0.6;

// Blinn-Phong specular. Shininess = exponent on (n · half_vec). Higher =
// tighter highlight. Specular brightness scales sun_color directly so
// the highlight reads in the same warm/cool palette as the sky-driven
// diffuse fill.
uniform float cloud_shininess : hint_range(1.0, 128.0) = 24.0;
uniform float cloud_specular_strength : hint_range(0.0, 4.0) = 1.2;

void vertex()
{
	POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
}

void fragment()
{
	ALBEDO = vec3(0.0);
	ALPHA = 0.0;

	float depth = texture(depth_tex, SCREEN_UV).x;
	vec2 ndc_xy = SCREEN_UV * 2.0 - 1.0;

	// Reconstruct the view ray. depth=0 (sky) lands surf_world on the far
	// plane; depth>0 lands it on the scene. surf_world - near_world is
	// parallel to the ortho ray direction either way.
	vec4 near_view = INV_PROJECTION_MATRIX * vec4(ndc_xy, 1.0, 1.0);
	near_view.xyz /= near_view.w;
	vec3 near_world = (INV_VIEW_MATRIX * vec4(near_view.xyz, 1.0)).xyz;
	vec4 surf_view = INV_PROJECTION_MATRIX * vec4(ndc_xy, depth, 1.0);
	surf_view.xyz /= surf_view.w;
	vec3 surf_world = (INV_VIEW_MATRIX * vec4(surf_view.xyz, 1.0)).xyz;
	vec3 ray = surf_world - near_world;
	float scene_len = max(length(ray), 1e-6);
	vec3 ray_dir = ray / scene_len;

	bool ray_valid = ray_dir.y < -1e-6 && band_top_altitude > band_bottom_altitude;
	if (ray_valid)
	{
		// XZ at the band's top plane — anchor for the base-offset noise.
		float t_band_top = (band_top_altitude - near_world.y) / ray_dir.y;
		vec3 p_top = near_world + ray_dir * max(t_band_top, 0.0);

		// Base-offset noise: per-XZ placement of the cloud's BOTTOM
		// inside the band. Higher base_n → cloud sits higher (thinner
		// column); lower base_n → cloud reaches down toward band_bottom
		// (taller column → more opacity from the path-length term).
		vec2 base_uv = p_top.xz * cloud_base_tiling + cloud_offset * 0.3;
		float base_n = texture(cloud_noise_tex, base_uv).r;
		float effective_base_y = mix(band_bottom_altitude, band_top_altitude, base_n);

		// Slab intersection with the per-XZ cloud column [effective_base, band_top].
		// path_length is the WORLD-METER traversal of the cloud at this pixel —
		// what drives the Beer-Lambert opacity. Ray entirely above column →
		// full thickness path. Ray inside column → partial path. Ray below →
		// no path (camera below cloud bottom for this XZ).
		float t_base = (effective_base_y - near_world.y) / ray_dir.y;
		float t_top = t_band_top;
		float t_enter = max(0.0, min(t_base, t_top));
		float t_exit = min(scene_len, max(t_base, t_top));
		float path_length = max(0.0, t_exit - t_enter);

		if (path_length > 0.0)
		{
			// Sample the cloud's density at the MIDPOINT of the column
			// crossing — gives a representative single-noise read for
			// the cloud at this pixel. Avoids edge-case sampling at
			// t_enter or t_exit when the camera grazes the band.
			float t_mid = (t_enter + t_exit) * 0.5;
			vec3 p_mid = near_world + ray_dir * t_mid;

			float band = max(1e-4, 1.0 - cloud_sharpness);

			// Two-octave noise. Base = smooth blob shape. Detail =
			// higher-frequency erosion at cloud_detail_scale × the base
			// frequency. Subtract `(1 - detail) * strength` so detail
			// VALLEYS carve into the base, but detail peaks don't add
			// new clouds where the base is empty.
			vec2 cov_uv = p_mid.xz * cloud_tex_tiling + cloud_offset;
			float c_base = texture(cloud_noise_tex, cov_uv).r;
			vec2 detail_uv = p_mid.xz * cloud_tex_tiling * cloud_detail_scale + cloud_offset * 0.7;
			float c_detail = texture(cloud_noise_tex, detail_uv).r;
			float c = c_base - (1.0 - c_detail) * cloud_detail_strength;

			float density = smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
			density = pow(density, cloud_mask_power);

			if (density > 0.0)
			{
				// Beer-Lambert opacity from path length × density.
				// `alpha` fades from 0 (zero crossing) toward 1 as the
				// camera rises through the cloud layer.
				float sigma = cloud_extinction * density;
				float alpha = 1.0 - exp(-sigma * path_length);

				// Final edge sharpener — smoothstep around 0.5 to push
				// partial-coverage pixels toward solid/empty. Tight
				// width = pixel-hard outlines; 0 disables.
				float edge_lo = clamp(0.5 - cloud_edge_sharpness, 0.0, 1.0);
				float edge_hi = clamp(0.5 + cloud_edge_sharpness, edge_lo + 1e-4, 1.0);
				alpha = smoothstep(edge_lo, edge_hi, alpha);

				// Cheap large-scale self-shadow: probe coverage at an XZ
				// offset along the sun's projection. High coverage there
				// = cloud at p is in the shadow of another puff toward
				// the sun. Drives the diffuse/specular attenuation
				// alongside the local Lambertian term.
				vec3 to_sun = -normalize(sun_world_dir);
				vec2 shadow_uv = (p_mid.xz + to_sun.xz * cloud_sun_shadow_distance) * cloud_tex_tiling + cloud_offset;
				float shadow_c = texture(cloud_noise_tex, shadow_uv).r;
				float shadow_cov = smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), shadow_c);
				float self_lit = exp(-cloud_sun_shadow_strength * shadow_cov);

				// Fake surface normal from the noise gradient. Two extra
				// noise reads at neighbor UV offsets, finite-differenced
				// against the center sample to get the XZ gradient. The
				// normal points up by `cloud_normal_up` and tilts away
				// from denser noise (gradient direction) by
				// `cloud_normal_strength`. Crisp clouds → strong tilt
				// near edges → clear shading on each puff's "shoulders".
				float c_xp = texture(cloud_noise_tex, cov_uv + vec2(cloud_normal_uv_offset, 0.0)).r;
				float c_zp = texture(cloud_noise_tex, cov_uv + vec2(0.0, cloud_normal_uv_offset)).r;
				vec2 grad = vec2(c_xp - c_base, c_zp - c_base) * cloud_normal_strength;
				vec3 normal = normalize(vec3(-grad.x, cloud_normal_up, -grad.y));

				// Blinn-Phong. n·l is the diffuse term, n·h is the
				// specular angle (h is the half-vector between view and
				// sun). Both gated by self_lit so a puff in another's
				// shadow can't catch direct light. cloud_ambient gives
				// a soft fill so deep shadow pockets aren't pure black.
				vec3 view_dir = -ray_dir;
				vec3 half_vec = normalize(to_sun + view_dir);
				float n_dot_l = max(0.0, dot(normal, to_sun));
				float n_dot_h = max(0.0, dot(normal, half_vec));
				float diffuse_term = n_dot_l * self_lit;
				float spec_term = pow(n_dot_h, cloud_shininess) * cloud_specular_strength * self_lit;

				// Compose. Lit color (cloud_color × sun_color) is mixed
				// against shadow_color by the diffuse term, plus the
				// specular highlight added on top, plus ambient fill.
				vec3 lit = cloud_color * sun_color;
				vec3 albedo = mix(cloud_shadow_color, lit, diffuse_term);
				albedo += sun_color * spec_term;
				albedo += cloud_ambient * 0.3;

				ALBEDO = albedo;
				ALPHA = alpha;
			}
		}
	}
}
