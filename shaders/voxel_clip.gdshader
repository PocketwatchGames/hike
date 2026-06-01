shader_type spatial;
// Opaque queue: the clip transition uses pure `discard` (alpha clip), not
// actual alpha blending, so blend_mix was buying us nothing and silently
// putting terrain into the transparent depth-write path where
// depth_draw_opaque resolves to "no write" — that's why the stencil
// writer (alpha queue, priority 2) was depth-testing against opaque
// sprite content instead of terrain. Now terrain lives in the opaque
// queue, writes depth normally, and the stencil writer correctly gates
// the cap region against terrain front-faces.
render_mode depth_draw_opaque, cull_back, ambient_light_disabled, specular_disabled;

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"
global uniform int clip_debug_mode;
global uniform vec3 debug_solid; // (0,0,0) = disabled, otherwise forces ALBEDO.
// Debug toggles — shared with detail_sprite so terrain and sprites behave
// identically under each flag and you can isolate shading divergences.
global uniform bool debug_normals;        // RGB = shaded_normal * 0.5 + 0.5
global uniform bool debug_unlit;          // raw tex only, no lighting
global uniform bool debug_no_tint;        // skip fill_a_tint / fill_b_tint
global uniform bool debug_no_cloud;       // force cloud_dim = 1
global uniform bool debug_no_detail_normal; // skip detail_normal perturbation
global uniform bool debug_white_albedo;   // force tex_color.rgb = white (lighting-only view)
global uniform bool debug_wet_spec;        // render only the wetness specular contribution as grayscale
global uniform float light_falloff_exp; // power applied to lightmap value (>1 = darker mids).

// RGBA8 light map: R = sunlight visibility mask 0..1 (single source, max-fill,
// pow'd in this shader). GBA = per-color-channel block light, post-pow at
// deposit time so overlapping lights add their perceptual brightness directly.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
// Day/night-driven scalars for the sun. sun_intensity is overall brightness
// (0..1+); sun_color is the RGB tint (warm dawn, cool noon, etc.). sun_ambient
// is the fraction of BFS sun mask treated as sky-bounce ambient that survives
// the directional shadow — see fragment() / light() and GetPerceivedLight in
// WorldState.cs (kept in sync so visual and gameplay agree).
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;

// Sun direction for the cloud-shadow include. Declared BEFORE the include
// because the include's function body references sun_world_dir, and GLSL
// requires identifiers to be declared before use. Other sun-direction uses
// below (reverse directional shading) pick up this same declaration.
global uniform vec3 sun_world_dir;

// Lingering surface wetness in [0, 1]. Driven by SkyController from the
// recent rain / fog / humidity history. 0 = bone dry, 1 = thoroughly wet.
// Modulated per-fragment by sun_mask (cave/overhang voxels stay dry) and
// by surface slope (water sheds off vertical faces) before composing the
// specular highlight + slight albedo darken.
global uniform float wetness_level;
// Amplitude of the wetness specular highlight added in light(). Higher =
// brighter glint; values past ~4 push the peak past the bloom HDR
// threshold so the glint blooms outward.
global uniform float wet_spec_strength;
// Albedo multiplier when fully wet on a sky-exposed flat face. 1.0 = no
// darkening, 0.0 = solid black. Lower values give wet surfaces stronger
// contrast against the highlight at the cost of looking very saturated.
global uniform float wet_albedo_floor;
// Strength of the Fresnel SKY reflection that makes wet ground read as a
// partial mirror — the primary wet cue. 0 = old look (darken + sun glint
// only), ~1 = strong grazing-angle sky sheen on flat wet ground.
global uniform float wet_reflect_strength;
// Schlick base reflectance for the wet Fresnel — the head-on reflectivity
// floor. Small (~0.02–0.08); the grazing-angle term supplies most of the
// sheen, this just keeps a faint reflection when looking straight down.
global uniform float wet_fresnel_base;
// Sky gradient endpoints (also fed by SkyController to the sky shader) —
// reused here to tint the wet Fresnel reflection so the sheen matches the
// current time-of-day sky rather than a fixed color.
global uniform vec3 horizon_color;
global uniform vec3 zenith_color;

// Cloud occlusion parameters shared with the sky + fog + sprite + water
// shaders (registered by SkyController). cloud_shadow.gdshaderinc uses
// these to project cloud shadows along the sun direction.
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
// How much of the sun's total brightness a full cloud blocks on terrain.
// 1.0 = full cloud leaves block light only; 0.6 ≈ old ATTENUATION-only
// behaviour (direct sun killed, ambient sky bounce survives).
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

// source_color decodes the sRGB-authored PNG to linear on sample. Without
// it, Godot reads the gamma-encoded bytes as raw linear values and every
// shaded pixel ends up ~2× brighter than the authored texture — which is
// what made terrain look washed out under debug_unlit while detail sprites
// (declared with source_color) matched their sources.
uniform sampler2DArray tile_array : source_color, filter_nearest_mipmap, repeat_enable;

// Tile layer indices — must match the AtlasBaseIndex authored on the
// matching BlockData in resources/data/blocks/. BlockCatalog.ValidateOrLog
// asserts the two named slots referenced here ("Stone" at 0, "GrassTop" at
// 1) at boot. Values are the BASE layer of a block's contiguous variant
// range; the real sampled layer is resolved per-fragment in `resolve_layer`
// from the base, the elevation band (world Y), and a position hash.
const float TILE_STONE = 0.0;
// Grass top's base is 1; variants/bands occupy layers 1..16 — resolved in
// resolve_layer() from the tile_variants[] entry at index 1.
const float TILE_GRASS_TOP = 1.0;
// "Auto" sentinel — when a vertex carries this id (TILE_AUTO = 255), the
// fragment picks the real tile from per-kit flat/wall via a smoothstep on
// surface normal.y. Must match VoxelTypeInfo.TILE_AUTO.
const float TILE_AUTO_THRESHOLD = 254.5;

// Per base-tile variant config indexed by base layer id:
//   .x = num elevation bands
//   .y = variants per band
//   .z, .w = reserved
// Entries we never index may be stale; keep TILE_VARIANT_TABLE_SIZE in sync
// with VoxelTypeInfo.TILE_VARIANT_TABLE_SIZE. A (1,1,_,_) entry means "no
// variation" — the effective layer equals the base.
const int TILE_VARIANT_TABLE_SIZE = 64;
uniform vec4 tile_variants[TILE_VARIANT_TABLE_SIZE];

// Global world-to-UV scale shared by every tile. One authored PNG spans
// 1/tile_uv_scale world units on each axis. Keeping this uniform across
// tiles means the same camera distance produces the same texel density for
// all materials — no per-tile tuning and no surprises when a tile's base
// layer is reused as an overlay. Also drives the variant-pick hash so all
// fragments in one PNG-sized region agree on which variant to show.
uniform float tile_uv_scale = 0.25;
uniform float band_origin_y = 0.0;
uniform float band_height = 32.0;
// Crossfade width across an elevation-band seam, expressed as a fraction of
// band_height. 0 = hard step (original behavior); larger values smooth the
// transition between a tile's adjacent band layers. Both bands share the
// variant hash so the crossfade doesn't also flip splatter variant.
uniform float band_blend : hint_range(0.0, 0.5) = 0.15;

// Low-frequency grayscale overlay multiplied into albedo. Breaks up obvious
// tiling in large stretches of the same material without needing multi-tile
// variants. Sampled on world_vertex.xz so top-facing ground gets the most
// benefit; sides read the same pattern projected straight down, which is
// acceptable for an isometric view.
uniform sampler2D detail_noise : filter_linear, repeat_enable;
uniform float detail_noise_freq = 0.03125;  // ~1/32 world units per cycle.
uniform float detail_noise_strength = 0.2;

// Detail normal perturbation on the ground. Sampled as a tangent-space
// normal map in the XZ plane; its influence is scaled by how much the
// surface faces up, so cliffs stay clean and ground gets the break-up.
uniform sampler2D detail_normal : filter_linear, repeat_enable, hint_normal;
uniform float detail_normal_freq = 0.5;  // ~2 world units per cycle.
uniform float detail_normal_strength = 0.35;

// Ambient-occlusion bake strength. AO is baked per-vertex into COLOR.a by the
// DC mesher (0 = open, 1 = fully sheltered) and applied here as a multiplicative
// darken on the diffuse lighting (direct sun + ambient sky-bounce + block
// light), leaving the wet specular / sky reflection untouched. 0 = AO off.
// This is OCCLUSION/shelter darkening; the separate concavity bake (CUSTOM2.w)
// drives wetness, not shading.
uniform float ao_strength : hint_range(0.0, 1.0) = 1.0;

// Concavity-driven wetness/pooling. Concavity is baked per-vertex into CUSTOM2.w
// by the DC mesher (signed voxels; positive = a dip/bowl, negative = a bump).
// Together with surface flatness it modulates the gameplay wetness_level: a
// concave + flat + sky-exposed spot collects MORE of the current surface
// moisture (wets first, dries last), while bumps and steep faces shed. It is a
// multiplier on wetness_level, not a constant — when the surface is dry, dips
// are dry too.
//   concavity_wetness_strength: 0 = off, 1 = authored, >1 exaggerates.
//   concavity_threshold: dip depth (voxels) at which pooling is full. Smaller =
//     even shallow dips pool; larger = only deep bowls.
uniform float concavity_wetness_strength : hint_range(0.0, 4.0) = 1.0;
uniform float concavity_threshold : hint_range(0.001, 1.0) = 0.15;
// Debug: paint the baked concavity directly via EMISSION. Red = concave dip
// (pools water), blue = convex bump/ridge, black ≈ flat. Scaled up so the
// sub-voxel magnitudes are visible. Verification aid — see CVars.debugConcavity.
uniform bool debug_concavity = false;

// Per-terrain palette uploaded by ChunkMesh.SetTerrains at world start.
//   terrain_tiles[i] = (flat, wall, _, _) tile_array layer indices. .zw are
//                   reserved (overlay is no longer a terrain property — it
//                   is authored per-voxel as a direct tile_array base index).
//   terrain_bands[i] = (wall_lo, wall_hi, _, _). One smoothstep: y<wall_lo = wall,
//                   y>wall_hi = flat. No intermediate slope tile — authored
//                   dirt/field/clover/etc. appearance comes from the global
//                   overlay pass below.
// Index = the per-vertex TerrainId picked via barycentric argmax (parallel to
// tile_ids picking). MAX_TERRAINS sized for headroom; only the first
// (deduplicated palette length) entries are meaningful — see
// WorldGen.ExtractTerrainPalette and ChunkMesh.SetTerrains.
const int MAX_TERRAINS = 16;
uniform vec4 terrain_tiles[MAX_TERRAINS];
uniform vec4 terrain_bands[MAX_TERRAINS];

// Two off-axis fill directions + colors. Neither is aligned with the sun.
// Each darkens surfaces that face away from it; surfaces facing toward it
// are unaffected. Together they sculpt slope direction across sunlit AND
// shadowed regions (we don't fade them by sun_mask), which is how outdoor
// slopes read as distinct from flat ground. Set by SkyController.
global uniform vec3 fill_a_world_dir;
global uniform vec3 fill_b_world_dir;
global uniform vec3 fill_a_color;
global uniform vec3 fill_b_color;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
// Sampled in fragment() at world_vertex; the returned factor multiplies
// block_lit (lm.gba) so a sprite standing above this terrain fragment dims
// the torch/lamp/skylight contribution here without depressing block_lit
// elsewhere in the world. Direct sun (carried via ALBEDO) is untouched.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

const float MIN_AMBIENT = 0.0;

varying vec3 world_vertex;
varying vec3 world_normal;
// Tile ids for the 3 corners of this triangle (constant across the tri).
varying flat vec3 tile_ids;
// Per-vertex blend-noise amplitude, interpolated to fragments.
varying float blend_amp;
// Per-vertex baked ambient occlusion (COLOR.a). 0 = open, 1 = sheltered.
// Interpolated to fragments; applied as a multiplicative diffuse darken.
varying float ao;
// Per-vertex baked concavity (CUSTOM2.w). Signed voxels: + = dip/bowl,
// - = bump/ridge. Interpolated to fragments; drives the wetness pooling term.
varying float concavity;
// Per-vertex sharpness in [0,1]. 0 = use interpolated smooth NORMAL,
// 1 = use dFdx/dFdy-derived face normal (hard flat-shaded crease). Authored
// per-cell by the DC mesher: 1 for authored-sharp materials (Stone/Wood),
// 0 for soft terrain. Interpolates across mixed quads so creases soften.
varying float sharpness;
// Fragment→light varying. 1 = unclouded, 0 = fully overcast.
// Written in fragment(), read in light() so cloud shadow composes with
// the directional shadow atlas as min() rather than multiply — see light().
varying float cloud_dim;
// Fragment→light varying. Pre-multiplied wetness specular term:
//   pow(N·H, k) * wetness_level * sun_mask * slope.
// Composed in light() with sun_color × sun_intensity and the same shadow
// gates as DIFFUSE_LIGHT so cast shadows kill the highlight cleanly.
varying float wet_spec;
// Terrain ids for the 3 corners of this triangle (constant across the tri).
// Barycentric argmax picks which corner's terrain wins at each fragment
// (parallel to tile_ids), so terrain boundaries blend with the same
// noise-jittered edge as tile boundaries.
varying flat vec3 terrain_ids;
// Per-corner authored overlay ids. 0 = no overlay, paint the terrain's flat/wall
// tile as-is; non-zero = sample the terrain's edge-overlay tile and blend over.
// Picked by the same barycentric argmax as tile_ids and terrain_ids so boundaries
// inherit the shared noise-jittered edge.
varying flat vec3 overlay_ids;

// Tunes the spatial frequency of the per-tile-edge noise. Higher = finer
// jitter along the boundary; too high looks like static, too low looks like
// big linear smears.
const float BLEND_NOISE_FREQ = 1.7;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;
	tile_ids = CUSTOM0.xyz;
	blend_amp = CUSTOM0.w;
	ao = COLOR.a;
	sharpness = CUSTOM1.x;
	terrain_ids = CUSTOM1.yzw;
	overlay_ids = CUSTOM2.xyz;
	concavity = CUSTOM2.w;
}

// Hash-based 3D value noise. Good enough for breaking up tile-edge seams; we
// only need it to be locally varied and reproducible across chunk boundaries.
float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Resolve the two adjacent tile_array layers a fragment straddles (the
// lower band + the band above it, wrapped modulo `bands`) along with the
// crossfade weight `band_t` in [0,1] and the texture's world-to-UV scale.
// band_t ramps from 0 to 1 across the top `band_blend` fraction of each
// band so mid-band fragments return band_t=0 and sample only layer_lo,
// matching the pre-blend behavior.
//
// Tiles configured with (1,1,1,_) collapse to layer_lo == layer_hi so the
// crossfade is a no-op. The variant hash is position-based and shared
// across both layers so the crossfade doesn't also swap splatter variant.
void resolve_layer(
		float base_layer,
		vec3 world_pos,
		out float layer_lo,
		out float layer_hi,
		out float band_t) {
	int id = clamp(int(base_layer), 0, TILE_VARIANT_TABLE_SIZE - 1);
	vec4 cfg = tile_variants[id];
	float bands = max(1.0, cfg.x);
	float per_band = max(1.0, cfg.y);
	// Bands are a function of elevation (world Y), not texture scale —
	// terrain's geographic Y banding is independent of any overlay's
	// tiling scale. Modulus so bands cycle forever up and down (GLSL mod
	// handles negative Y correctly: mod(-1, 4) = 3).
	float b = (world_pos.y - band_origin_y) / band_height;
	float base_band = floor(b);
	float frac = b - base_band;
	float band_lo = mod(base_band, bands);
	float band_hi = mod(base_band + 1.0, bands);
	band_t = smoothstep(1.0 - band_blend, 1.0, frac);
	// Variant granularity follows the global texture scale so one
	// PNG-sized region picks one variant. At tile_uv_scale=0.25 that's
	// one variant per 4-voxel region across every tile.
	vec3 scaled = world_pos * tile_uv_scale;
	float h = hash13(floor(scaled) + vec3(37.2, 11.7, 91.3));
	float variant = clamp(floor(h * per_band), 0.0, per_band - 1.0);
	layer_lo = base_layer + band_lo * per_band + variant;
	layer_hi = base_layer + band_hi * per_band + variant;
}

// Triplanar sample of a tile with variant/band resolution. `blend` is the
// axis weight vector computed from the shading normal in fragment(). When
// a fragment falls inside the elevation-band crossfade window we sample
// both the lower and upper band layers and lerp; outside the window we
// skip the second sample entirely.
vec4 sample_tile(float base_layer, vec3 world_pos, vec3 blend) {
	float layer_lo;
	float layer_hi;
	float band_t;
	resolve_layer(base_layer, world_pos, layer_lo, layer_hi, band_t);
	vec3 wp = world_pos * tile_uv_scale;
	vec4 lo_xy = texture(tile_array, vec3(wp.x, -wp.y, layer_lo));
	vec4 lo_xz = texture(tile_array, vec3(wp.x,  wp.z, layer_lo));
	vec4 lo_yz = texture(tile_array, vec3(wp.z, -wp.y, layer_lo));
	vec4 tex_lo = lo_xz * blend.y + lo_xy * blend.z + lo_yz * blend.x;
	if (band_t > 0.0) {
		vec4 hi_xy = texture(tile_array, vec3(wp.x, -wp.y, layer_hi));
		vec4 hi_xz = texture(tile_array, vec3(wp.x,  wp.z, layer_hi));
		vec4 hi_yz = texture(tile_array, vec3(wp.z, -wp.y, layer_hi));
		vec4 tex_hi = hi_xz * blend.y + hi_xy * blend.z + hi_yz * blend.x;
		return mix(tex_lo, tex_hi, band_t);
	}
	return tex_lo;
}
float vnoise(vec3 p) {
	vec3 i = floor(p);
	vec3 f = fract(p);
	f = f * f * (3.0 - 2.0 * f);
	float n000 = hash13(i + vec3(0.0, 0.0, 0.0));
	float n100 = hash13(i + vec3(1.0, 0.0, 0.0));
	float n010 = hash13(i + vec3(0.0, 1.0, 0.0));
	float n110 = hash13(i + vec3(1.0, 1.0, 0.0));
	float n001 = hash13(i + vec3(0.0, 0.0, 1.0));
	float n101 = hash13(i + vec3(1.0, 0.0, 1.0));
	float n011 = hash13(i + vec3(0.0, 1.0, 1.0));
	float n111 = hash13(i + vec3(1.0, 1.0, 1.0));
	float nx00 = mix(n000, n100, f.x);
	float nx10 = mix(n010, n110, f.x);
	float nx01 = mix(n001, n101, f.x);
	float nx11 = mix(n011, n111, f.x);
	float nxy0 = mix(nx00, nx10, f.y);
	float nxy1 = mix(nx01, nx11, f.y);
	return mix(nxy0, nxy1, f.z);
}

void fragment() {
	// Per-fragment shading normal: lerp between the interpolated smooth NORMAL
	// (sharpness=0) and a dFdx/dFdy-derived face normal (sharpness=1). The
	// derivative form gives the current triangle's plane — same look as a
	// SurfaceTool flat-shade group, but without needing one, so we can vary
	// smoothness smoothly per vertex. Flip the derivative normal into the same
	// hemisphere as the smooth normal to stay invariant to rasterizer winding.
	vec3 face_n_raw = cross(dFdx(world_vertex), dFdy(world_vertex));
	vec3 smooth_n = normalize(world_normal);
	vec3 face_n = normalize(face_n_raw * sign(dot(face_n_raw, smooth_n) + 1e-6));
	vec3 shaded_normal = normalize(mix(smooth_n, face_n, clamp(sharpness, 0.0, 1.0)));

	// Triplanar axis weights from the pre-perturbation normal, used only by
	// the detail-normal triplanar blend below. A single XZ sample smeared
	// vertically on cliff faces; this lets the perturbation read from all
	// three planes. Tile sampling re-derives its own blend from the
	// post-perturbation normal further down so tile triplanar weights
	// still match the final shading normal (unchanged behavior).
	vec3 dn_blend = pow(abs(shaded_normal), vec3(4.0));
	dn_blend /= (dn_blend.x + dn_blend.y + dn_blend.z + 1e-5);

	// Detail normal map perturbation — triplanar. Each planar projection
	// is decoded to a world-space perturbation using that plane's natural
	// tangent frame, then blended by dn_blend.
	vec3 dn_xy = texture(detail_normal, world_vertex.xy * detail_normal_freq).xyz * 2.0 - 1.0;
	vec3 dn_xz = texture(detail_normal, world_vertex.xz * detail_normal_freq).xyz * 2.0 - 1.0;
	vec3 dn_yz = texture(detail_normal, world_vertex.zy * detail_normal_freq).xyz * 2.0 - 1.0;
	// Remap each tangent-space sample (tx, ty, tz) into world space for its
	// projection's natural frame:
	//   xy plane (Z-facing): T=+X, B=+Y, N=+Z → (tx, ty, tz)
	//   xz plane (Y-facing): T=+X, B=+Z, N=+Y → (tx, tz, ty)
	//   yz plane (X-facing): T=+Z, B=+Y, N=+X → (tz, ty, tx)
	// A flat (0,0,1) sample decodes to each plane's outward axis, so the
	// blended perturb equals shaded_normal when the map is unperturbed.
	vec3 dn_xy_world = vec3(dn_xy.x, dn_xy.y, dn_xy.z);
	vec3 dn_xz_world = vec3(dn_xz.x, dn_xz.z, dn_xz.y);
	vec3 dn_yz_world = vec3(dn_yz.z, dn_yz.y, dn_yz.x);
	vec3 dn_world = normalize(
		dn_xy_world * dn_blend.z +
		dn_xz_world * dn_blend.y +
		dn_yz_world * dn_blend.x);
	if (!debug_no_detail_normal) {
		shaded_normal = normalize(mix(shaded_normal, dn_world, detail_normal_strength));
	}

	// Pick which corner's tile this fragment shows. COLOR.rgb was set per
	// vertex to (1,0,0)/(0,1,0)/(0,0,1) so the rasterizer's linear interpolation
	// gives us the barycentric weights for free. Perturb each weight with a
	// 3D noise sample (different offset per corner) and take the argmax — this
	// is a hard pick (no smearing) but the boundary becomes irregular instead
	// of a straight triangle bisector. blend_amp = 0 (e.g. man-made walls)
	// collapses back to the un-perturbed bisector for a crisp seam.
	vec3 bary = COLOR.rgb / max(COLOR.r + COLOR.g + COLOR.b, 1e-5);
	vec3 np = world_vertex * BLEND_NOISE_FREQ;
	float n0 = vnoise(np);
	float n1 = vnoise(np + vec3(13.7, 0.0, 0.0));
	float n2 = vnoise(np + vec3(0.0, 17.3, 7.1));
	vec3 jitter = (vec3(n0, n1, n2) - 0.5) * blend_amp;
	vec3 weights = bary + jitter;
	float tile_index;
	float terrain_index;
	float overlay_index;
	if (weights.x >= weights.y && weights.x >= weights.z) {
		tile_index = tile_ids.x;
		terrain_index = terrain_ids.x;
		overlay_index = overlay_ids.x;
	} else if (weights.y >= weights.z) {
		tile_index = tile_ids.y;
		terrain_index = terrain_ids.y;
		overlay_index = overlay_ids.y;
	} else {
		tile_index = tile_ids.z;
		terrain_index = terrain_ids.z;
		overlay_index = overlay_ids.z;
	}

	// Triplanar blend — DC vertices have arbitrary normals, so a dominant-axis
	// pick produces visible seams. Sample the tile on all three planes and
	// blend by |normal|^k. Derived from the post-perturbation shaded_normal
	// so tile weighting follows the detail-normal wobble as well as the
	// geometric surface.
	vec3 blend = pow(abs(shaded_normal), vec3(4.0));
	blend /= (blend.x + blend.y + blend.z + 1e-5);

	vec4 tex_color;
	if (tile_index >= TILE_AUTO_THRESHOLD) {
		// AUTO terrain: per-kit flat↔wall via a single smoothstep on normal.y.
		// No intermediate slope tile — dirt/edge appearance is painted by the
		// overlay layer only where worldgen authored OverlayId != 0. Keeps
		// cliff tops sharp (no auto dirt ring) and makes dirt an intentional
		// marker for walkable slopes and small features. Each concrete tile
		// runs through sample_tile so kit tiles that happen to be variant-
		// capable bases (e.g. grass) get per-fragment band/variant resolution.
		int terrain_idx = clamp(int(terrain_index), 0, MAX_TERRAINS - 1);
		vec4 tiles = terrain_tiles[terrain_idx];
		vec4 bands = terrain_bands[terrain_idx];
		float s = clamp(shaded_normal.y, 0.0, 1.0);
		float w_flat = smoothstep(bands.x, bands.y, s);
		float w_wall = 1.0 - w_flat;
		vec4 t_flat = sample_tile(tiles.x, world_vertex, blend);
		vec4 t_wall = sample_tile(tiles.y, world_vertex, blend);
		tex_color = t_flat * w_flat + t_wall * w_wall;

		// Overlay pass: OverlayId is a direct tile_array base layer (0 = none,
		// non-zero = any overlay tile in the global catalog — dirt, field,
		// clover, moss, etc.). Decoupling from kits lets one kit carry mixed
		// overlays across voxels. Variants on the overlay base layer are
		// resolved automatically by sample_tile → resolve_layer, so overlays
		// with multiple art variants (e.g. 4 dirt splatters) just need the
		// right tile_variants entry.
		//
		// The overlay's own alpha channel drives blend strength, so authored
		// soft edges in the PNG read as partial coverage. Multiplied by w_flat
		// so overlays ride on top of flat ground and fade out on walls (a
		// cliff face carrying an overlay id would otherwise overwrite its
		// wall tile).
		if (overlay_index > 0.5) {
			vec4 t_overlay = sample_tile(overlay_index, world_vertex, blend);
			tex_color.rgb = mix(tex_color.rgb, t_overlay.rgb, w_flat * t_overlay.a);
		}
	} else {
		tex_color = sample_tile(tile_index, world_vertex, blend);
	}

	// Macro detail overlay: a low-frequency grayscale noise multiplied into
	// albedo as a small brightness modulation. Breaks up same-tile repetition
	// over large areas without adding any texture-array variants. Triplanar
	// so cliff faces don't inherit a vertically smeared XZ slice.
	float macro_xy = texture(detail_noise, world_vertex.xy * detail_noise_freq).r;
	float macro_xz = texture(detail_noise, world_vertex.xz * detail_noise_freq).r;
	float macro_yz = texture(detail_noise, world_vertex.zy * detail_noise_freq).r;
	float macro = macro_xy * blend.z + macro_xz * blend.y + macro_yz * blend.x;
	float overlay = mix(1.0 - detail_noise_strength, 1.0 + detail_noise_strength, macro);
	// NOTE: applied below, after wet_factor is known. Wetness flattens this
	// macro contrast toward 1.0 — water fills the surface and evens out the
	// dry-texture noise instead of letting the darkened albedo amplify it.

	// Sample lightmap per-pixel so it interpolates smoothly across the face.
	// Push 0.5 along the outward normal so the sample lands in the adjacent
	// air voxel — without this, DC vertices on cave walls would sample inside
	// the solid (or worse, near a sunlit-air neighbour) and caves would glow.
	vec3 light_sample_pos = world_vertex + shaded_normal * 0.5;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	float sun_mask = pow(lm.r, light_falloff_exp);
	// Wetness gate: only sky-exposed (sun_mask) and roughly upward-facing
	// surfaces wet out. Vertical cliffs shed water, caves/overhangs never
	// see rain. Slope exponent 3 gives a smooth falloff — fully flat reads
	// as fully wet, ~45° slopes still get a subtle sheen, vertical faces
	// are clean. Read the SMOOTH geometric normal (not the detail-perturbed
	// shaded_normal): the wet sheen must be coherent across a face, not
	// flicker on the normal-map noise. Driving the old specular through the
	// perturbed normal is exactly what made wet ground read as "crunchier
	// texture" instead of wet.
	float wet_slope = pow(max(smooth_n.y, 0.0), 3.0);
	// Concavity pooling, driven by the gameplay surface wetness. wetness_level
	// is WorldState.WetnessLevel — the weather-derived [0,1] dampness that wets
	// up in rain/fog and dries on a half-life curve (SkyController.UpdateWetness).
	// Concavity doesn't add water out of nowhere; it makes concave + flat spots
	// COLLECT more of whatever moisture is currently present, so dips wet up
	// first and stay damp longest, while bumps and steep faces shed. When the
	// surface is bone dry (wetness_level = 0) even dips read dry.
	// HOOK: displacement_wetness is reserved for the not-yet-implemented detail
	// displacement (height) map — it will fold per-texel micro-pits into the
	// pooling later (a multiply in [0,1]). Keep it 1.0 until that map exists.
	float displacement_wetness = 1.0;
	// Local wetness boost in concave spots: ramps 0→1 with dip depth, scaled by
	// strength. Multiplies the global wetness so pooling is always proportional
	// to the real surface wetness, never a constant.
	float pool_boost = smoothstep(0.0, concavity_threshold, concavity) * displacement_wetness * concavity_wetness_strength;
	float local_wetness = clamp(wetness_level * (1.0 + pool_boost), 0.0, 1.0);
	// concavity_wetness_strength = 0 → pool_boost = 0 → local_wetness =
	// wetness_level, collapsing this back to the original weather-only term.
	float wet_factor = sun_mask * wet_slope * local_wetness;
	vec3 wet_view = normalize(INV_VIEW_MATRIX[3].xyz - world_vertex);
	// Tight sun glint off the smooth normal. Exponent 40 (vs the old 16)
	// makes a compact glossy hotspot that reads as a wet specular rather
	// than a broad brightening. Folded through the shadow/cloud gate in
	// light() via the wet_spec varying.
	vec3 wet_h = normalize(wet_view + (-sun_world_dir));
	float wet_ndoth = max(dot(smooth_n, wet_h), 0.0);
	wet_spec = pow(wet_ndoth, 40.0) * wet_factor;
	// Fresnel SKY reflection — the primary wet cue. Wet ground becomes a
	// partial mirror: at grazing view angles it reflects the sky dome.
	// Reflect the view about the smooth normal and sample the same
	// horizon→zenith gradient the sky shader uses, so the sheen tints with
	// the current sky. Schlick Fresnel with a small base reflectance; gated
	// by wet_factor so it only shows on wet, sky-exposed, upward faces.
	// Added to EMISSION below (a reflection, not a diffuse term, so it is
	// NOT multiplied by albedo) and cloud-dimmed (clouds occlude the sky).
	float wet_fresnel = wet_fresnel_base + (1.0 - wet_fresnel_base) * pow(1.0 - max(dot(wet_view, smooth_n), 0.0), 5.0);
	vec3 wet_refl_dir = reflect(-wet_view, smooth_n);
	float wet_sky_t = clamp(wet_refl_dir.y * 0.5 + 0.5, 0.0, 1.0);
	vec3 wet_sky = mix(horizon_color, zenith_color, wet_sky_t);
	vec3 wet_reflect = wet_sky * (wet_fresnel * wet_reflect_strength * wet_factor);
	// Cloud shadow: pass to light() via cloud_dim so it composes with
	// ATTENUATION as min() (darkest occluder wins) rather than multiplying
	// onto the direct-sun term. Ambient sky-bounce (EMISSION half) is still
	// cloud-dimmed below — clouds do occlude sky light.
	// cloud_shadow_ground is Y-independent to avoid faceted polygon-edge
	// artifacts on sloped triangles.
	float cloud = cloud_shadow_ground(world_vertex, TIME);
	cloud_dim = debug_no_cloud ? 1.0 : 1.0 - cloud * cloud_shadow_strength;
	// NdotL multiplier on direct sun. Slopes facing toward the sun get a lift
	// above the glow HDR threshold so bloom catches them; sun-averted slopes
	// get a mild cut (still gated by sun_mask, so underground/shadowed doesn't
	// darken further). Kept narrow (0.9..1.3) — this is a push over the bloom
	// knee, not a lighting overhaul.
	float sun_ndl = max(0.0, dot(shaded_normal, -sun_world_dir));
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * (0.9 + 0.4 * sun_ndl);
	// Per-fragment block_lit (torch/lamp + skylight) is dimmed by any
	// sprite silhouette projected from above by BlockLightShadowProjector.
	// Direct sun (sun_lit, carried via ALBEDO) is untouched.
	vec3 block_lit = lm.gba * block_light_shadow_factor(world_vertex);

	// "Reverse" directional shading: each fill darkens faces by its tint
	// color in proportion to how much they face away from it. Faces facing
	// the fill are unaffected (multiplied by 1). No sun_mask fade — both
	// fills are off-axis from the sun, so there's no double-darkening with
	// the BFS sun mask to worry about. Keeping them active outdoors is what
	// gives sunlit slopes their directional read.
	float fill_a_facing = max(0.0, dot(shaded_normal, -fill_a_world_dir));
	float fill_b_facing = max(0.0, dot(shaded_normal, -fill_b_world_dir));
	vec3 fill_a_tint = mix(fill_a_color, vec3(1.0), fill_a_facing);
	vec3 fill_b_tint = mix(fill_b_color, vec3(1.0), fill_b_facing);
	if (debug_no_tint) {
		fill_a_tint = vec3(1.0);
		fill_b_tint = vec3(1.0);
	}

	if (debug_white_albedo) {
		tex_color.rgb = vec3(0.0);
	}

	// Macro noise overlay (deferred from above), flattened toward 1.0 as the
	// surface wets — water fills the pores and evens out the dry-texture
	// contrast instead of the darkened albedo making it crunchier.
	tex_color.rgb *= mix(overlay, 1.0, wet_factor);
	// Wet surfaces look darker as well as shinier — same gate as wet_spec.
	// Floor tuned via SkyController's wet_albedo_floor (lower = more
	// darkening at full wet, more contrast with the highlight).
	tex_color.rgb *= mix(1.0, wet_albedo_floor, wet_factor);

	vec3 base = tex_color.rgb * fill_a_tint * fill_b_tint;

	// Baked AO: multiplicative shelter/contact darkening on the diffuse
	// lighting only. ao=0 (open) leaves lighting untouched; ao=1 (buried)
	// scales the diffuse down by ao_strength. The wet sky reflection
	// (wet_reflect, a specular term) is intentionally NOT occluded here.
	float ao_factor = 1.0 - ao * ao_strength;

	// Split the lighting across ALBEDO and EMISSION so light() can shadow
	// just the direct portion of the sun via ATTENUATION. The BFS sun mask
	// is treated as part-direct, part-ambient-skylight (see sun_ambient):
	//   ALBEDO    = direct sun color  (scaled by ATTENUATION in light())
	//   EMISSION  = ambient sky bounce + block light (always-on)
	// With ambient_light_disabled, ALBEDO contributes only via DIFFUSE_LIGHT,
	// so the final pixel = ALBEDO * ATTENUATION + EMISSION.
	// Block-light shadow projector dims block_lit per-fragment via
	// `block_light_shadow_factor(world_vertex)` above — the dim is baked
	// into block_lit before it lands in EMISSION, so no light()-stage
	// machinery is needed here.
	ALBEDO = base * sun_lit * (1.0 - sun_ambient) * ao_factor;
	// wet_reflect is the mirrored sky (specular), layered on top of the
	// diffuse composite rather than multiplied by albedo — so even dark wet
	// ground shows the bright sky sheen ("wet asphalt" look). Gated by
	// sky-occlusion: cloud_dim (clouds occlude the sky being reflected) AND
	// ao_factor (baked vertex AO — a vertex sheltered under canopy or against a
	// vertical face sees less sky, so it reflects less of it). ao_factor also
	// darkens the diffuse above.
	EMISSION = base * (block_lit + sun_lit * sun_ambient * cloud_dim) * ao_factor + wet_reflect * cloud_dim * ao_factor;

	if (dot(debug_solid, vec3(1.0)) > 0.001) {
		ALBEDO = vec3(0.0);
		EMISSION = debug_solid;
		wet_spec = 0.0;
	}
	if (debug_concavity) {
		// Red = concave dip (positive), blue = convex bump (negative), black ≈
		// flat. ×4 so sub-voxel concavity magnitudes are visible.
		ALBEDO = vec3(0.0);
		EMISSION = vec3(max(concavity, 0.0), 0.0, max(-concavity, 0.0)) * 4.0;
		wet_spec = 0.0;
	}
	if (debug_normals) {
		ALBEDO = vec3(0.0);
		EMISSION = shaded_normal * 0.5 + 0.5;
		wet_spec = 0.0;
	}
	if (debug_unlit) {
		ALBEDO = vec3(0.0);
		EMISSION = tex_color.rgb;
		wet_spec = 0.0;
	}
	if (debug_wet_spec) {
		// RGB channel breakdown of the wetness pipeline:
		//   R = wetness_level         (global weather drive; uniform across world)
		//   G = sun_mask * wet_slope  (per-fragment gate: 0 in caves/walls, ~1 on lit flat tops)
		//   B = pow(N·H, 16)          (camera/sun half-vector glint, independent of weather)
		// Final spec contribution ≈ R*G*B. So:
		//   black  = nothing contributing
		//   red    = wet weather, but masked or wrong angle
		//   green  = sky-exposed flat, but dry or wrong angle
		//   blue   = perfect glint angle, but dry or in shade
		//   white  = all three high → the spec actually pops here
		// Zero wet_spec afterwards so light() doesn't add on top.
		float wet_geom = pow(max(dot(smooth_n, wet_h), 0.0), 40.0);
		ALBEDO = vec3(0.0);
		EMISSION = vec3(wetness_level, sun_mask * wet_slope, wet_geom);
		wet_spec = 0.0;
	}
	if (clip_dither_should_discard(world_vertex, FRAGCOORD.xy)) {
		discard;
	}
	// Mode 13: paint terrain via EMISSION (bypasses lighting) so it shows
	// BRIGHT BLUE regardless of cave/dim conditions. Earlier ALBEDO+lighting
	// version dimmed to ~black underground, hiding the diagnostic. Anything
	// not in {blue terrain, green stencil-1, cyan stencil-2, red cap,
	// magenta water cap} is some other shader (sky, sprite, water front
	// face, fog) — rule those out separately.
	if (clip_debug_mode == 13 || clip_debug_mode == 14) {
		ALBEDO = vec3(0.0);
		EMISSION = vec3(0.0, 0.0, 0.4);
	}
}

void light() {
	// ATTENUATION is Godot's directional shadow atlas — terrain self-shadow
	// plus sprite shadow proxies, already composed correctly in the atlas.
	// No LIGHT_COLOR / NdotL: sun color/direction stylization is baked into
	// ALBEDO via sun_color and the fill tints in fragment().
	//
	// Cloud shadow (cloud_dim from fragment()) and the shadow atlas both
	// occlude direct sun. Compose as min(): whichever is darker wins,
	// instead of multiplying them — standing in a cast shadow on a cloudy
	// day should read as one shadow, not two stacked. Ambient sky-bounce
	// is still cloud-dimmed in EMISSION (see fragment()).
	//
	// Gate by each light's energy so the sun and moon DirectionalLight3Ds
	// (driven by SkyController) crossfade cleanly — each call contributes
	// only its source's fraction of the direct light. LIGHT_ENERGY isn't a
	// builtin in Godot 4's light() stage; energy is pre-baked into
	// LIGHT_COLOR as `light_color * light_energy * PI`. Both SunLight and
	// MoonLight are authored white (color = (1,1,1)) so LIGHT_COLOR.r =
	// light_energy * PI; dividing by PI yields the scalar energy factor.
	// Sun energy peaks during day / fades to 0 as the sun sets; moon
	// energy is the mirror. During the dawn/dusk crossover both are
	// partial and the scene receives blended shadows from both directions.
	const float INV_PI = 0.31830989;
	float light_energy = LIGHT_COLOR.r * INV_PI;
	float light_gate = light_energy * min(ATTENUATION, cloud_dim);
	DIFFUSE_LIGHT += ALBEDO * light_gate;
	// Wetness specular. Sun-tinted highlight gated by the same shadow
	// composition as the diffuse direct contribution — cast shadows kill
	// the glint, cloud cover dims it. Added into DIFFUSE_LIGHT (not
	// SPECULAR_LIGHT) so it composes with the rest of the stylized
	// lighting model under specular_disabled. Amplitude tuned via
	// SkyController's wet_spec_strength.
	DIFFUSE_LIGHT += wet_spec_strength * sun_color * sun_intensity * wet_spec * light_gate;
}
