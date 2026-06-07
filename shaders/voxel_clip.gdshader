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

// Zone/weather water look, shared with the water shader (set by SkyController).
// Pooled standing water on terrain reuses these so puddles match real water:
//   water_deep_tint — the zone water body color (murky pools take this on).
//   water_muddiness — 0 = clear (mirrors the sky), 1 = murky (shows body color,
//                     little reflection). Driven by zone palette + weather.
global uniform vec3 water_deep_tint : source_color;
global uniform float water_muddiness;

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

// Base color atlas (sRGB). source_color decodes the gamma-authored PNG to
// linear on sample. Nearest filtering — the chosen terrain look.
uniform sampler2DArray tile_array : source_color, filter_nearest_mipmap, repeat_enable;
// Packed per-tile detail: RGB = tangent-space normal, A = height/displacement.
// Linear (NOT source_color). Drives the per-tile normal perturbation and the
// height-aware blend between flat/wall and overlay tiles.
uniform sampler2DArray tile_nrm_height : filter_nearest_mipmap, repeat_enable;
// How strongly the per-tile normal map perturbs the shading normal.
uniform float tile_normal_strength : hint_range(0.0, 1.0) = 0.6;
// Height-blend sharpness in [0,1]: HIGHER = sharper/tighter interlock, LOWER =
// softer ramp (converted to a blend depth = 1 - sharpness in fragment()). Three
// regimes, by the cliff/ground class of the tiles meeting:
//   wall_blend_sharpness   — cliff ↔ ground (SHARPEST; the slope cliff/ground seam)
//   cliff_blend_sharpness  — cliff ↔ cliff  (soft; two rock walls meeting)
//   ground_blend_sharpness — ground ↔ ground & overlay-on-ground (soft)
uniform float wall_blend_sharpness : hint_range(0.0, 1.0) = 0.65;
uniform float cliff_blend_sharpness : hint_range(0.0, 1.0) = 0.4;
uniform float ground_blend_sharpness : hint_range(0.0, 1.0) = 0.4;

// Per-atlas-layer class flag: 1.0 = cliff/rock, 0.0 = ground. Uploaded from
// BlockData.IsCliff. Routes each tile into the cliff or ground accumulator.
const int TILE_CLASS_TABLE_SIZE = 64;
uniform float tile_is_cliff[TILE_CLASS_TABLE_SIZE];
// Per-atlas-layer wetness porosity [0,1] (BlockData.Porosity). LOW = rock
// (water beads on top → reflective), HIGH = soil (water soaks in → darkens).
uniform float tile_porosity[TILE_CLASS_TABLE_SIZE];

// Wetness model tuning (material uniforms; live via CVars).
//   wet_displacement   — how much the per-tile height map adds micro-pit pooling.
//   wet_pool_strength  — strength of the standing-water (pooled) state.
//   wet_roughness_min  — synthesized wet roughness floor (0 = mirror smooth).
//   wet_chroma         — extra saturation on wet, porous (soil) surfaces.
uniform float wet_displacement : hint_range(0.0, 1.0) = 0.5;
uniform float wet_pool_strength : hint_range(0.0, 1.0) = 1.0;
uniform float wet_roughness_min : hint_range(0.0, 1.0) = 0.25;
uniform float wet_chroma : hint_range(0.0, 1.0) = 0.2;

// Tile layer indices — must match the AtlasBaseIndex authored on the
// matching BlockData in resources/data/blocks/. Each block is now exactly one
// atlas layer (the elevation-band / variant system was removed), so the layer
// index equals the block's AtlasBaseIndex directly. BlockCatalog.ValidateOrLog
// asserts the two named slots referenced here ("Stone" at 0, "GrassTop" at 1).
const float TILE_STONE = 0.0;
const float TILE_GRASS_TOP = 1.0;
// "Auto" sentinel — when a vertex carries this id (TILE_AUTO = 255), the
// fragment picks the real tile from per-kit flat/wall via a smoothstep on
// surface normal.y. Must match VoxelTypeInfo.TILE_AUTO.
const float TILE_AUTO_THRESHOLD = 254.5;

// Global world-to-UV scale shared by every tile. One authored PNG spans
// 1/tile_uv_scale world units on each axis (the tiling frequency). Uniform
// across tiles so the same camera distance produces the same texel density
// for every material; exposed live via the `terrain_tile_scale` CVar.
uniform float tile_uv_scale = 0.3;

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

// Ground-stain projector — flat ground marks (scorch / footprints / blood)
// composited into BASE before the lighting split so they read in every
// lighting condition. See shaders/ground_stain.gdshaderinc.
global uniform sampler2D ground_stain_tex : filter_linear;
global uniform vec3 ground_stain_origin;
global uniform vec3 ground_stain_right;
global uniform vec3 ground_stain_up;
global uniform float ground_stain_size;
global uniform float ground_stain_strength;
global uniform bool ground_stain_enabled;
#include "res://shaders/ground_stain.gdshaderinc"

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

// Triplanar sample of one tile layer. `wp` is the world position already
// scaled by tile_uv_scale; `blend` is the axis weight vector from the shading
// normal. Returns base color, a world-space normal (decoded + remapped from
// the packed tangent-space normal map), and the height/displacement scalar.
void sample_tile(float layer, vec3 wp, vec3 blend,
		out vec3 color, out vec3 wnormal, out float height) {
	// Fast path: near-flat ground is dominated by the XZ plane, so skip the two
	// vertical projections — 1 fetch instead of 3, across the bulk of the ground.
	if (blend.y > 0.995) {
		color = texture(tile_array, vec3(wp.x, wp.z, layer)).rgb;
		vec4 nh = texture(tile_nrm_height, vec3(wp.x, wp.z, layer));
		height = nh.a;
		vec3 n = nh.xyz * 2.0 - 1.0;
		wnormal = normalize(vec3(n.x, n.z, n.y));
		return;
	}
	vec3 c_xy = texture(tile_array, vec3(wp.x, -wp.y, layer)).rgb;
	vec3 c_xz = texture(tile_array, vec3(wp.x,  wp.z, layer)).rgb;
	vec3 c_yz = texture(tile_array, vec3(wp.z, -wp.y, layer)).rgb;
	vec4 nh_xy = texture(tile_nrm_height, vec3(wp.x, -wp.y, layer));
	vec4 nh_xz = texture(tile_nrm_height, vec3(wp.x,  wp.z, layer));
	vec4 nh_yz = texture(tile_nrm_height, vec3(wp.z, -wp.y, layer));
	color = c_xz * blend.y + c_xy * blend.z + c_yz * blend.x;
	height = nh_xz.a * blend.y + nh_xy.a * blend.z + nh_yz.a * blend.x;
	// Decode each plane's tangent-space normal and remap into world space
	// using that plane's natural frame (same mapping as the old detail
	// normal): xy(Z-facing)→(x,y,z), xz(Y-facing)→(x,z,y), yz(X-facing)→(z,y,x).
	vec3 n_xy = nh_xy.xyz * 2.0 - 1.0;
	vec3 n_xz = nh_xz.xyz * 2.0 - 1.0;
	vec3 n_yz = nh_yz.xyz * 2.0 - 1.0;
	wnormal = normalize(
		vec3(n_xy.x, n_xy.y, n_xy.z) * blend.z +
		vec3(n_xz.x, n_xz.z, n_xz.y) * blend.y +
		vec3(n_yz.z, n_yz.y, n_yz.x) * blend.x);
}

// Height-aware blend weight toward tile B. `t` is the geometric weight [0,1]
// toward B (e.g. the wall fraction, or overlay coverage); ha/hb are the two
// height samples [0,1]; `depth` sets the transition width — small = tight
// interlock (cliffs), large = smooth ramp (ground overlays). Returns the
// resolved A→B weight, used identically for color and normal so they agree.
float height_blend_weight(float t, float ha, float hb, float depth) {
	float wa = (1.0 - t) + ha;
	float wb = t + hb;
	float m = max(wa, wb) - depth;
	float ba = max(wa - m, 0.0);
	float bb = max(wb - m, 0.0);
	return bb / (ba + bb + 1e-5);
}

// NOTE: the per-class height-blend (ground / cliff) is written inline in
// fragment() rather than as a helper — Godot's shader language has no
// array-parameter functions, and the blend needs the candidate id/weight
// arrays. See the two inlined blend blocks after the candidate-build loop.
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

	// Per-tile normal-map detail now replaces the old global detail_normal —
	// it's sampled below alongside each tile's color/height and perturbs
	// shaded_normal after the tile pick.

	// Per-corner barycentric weights (COLOR.rgb interpolates them for free),
	// perturbed by 3D noise so material boundaries are irregular instead of a
	// straight triangle bisector. blend_amp = 0 (man-made) → un-jittered weights.
	vec3 bary = COLOR.rgb / max(COLOR.r + COLOR.g + COLOR.b, 1e-5);
	vec3 np = world_vertex * BLEND_NOISE_FREQ;
	float n0 = vnoise(np);
	float n1 = vnoise(np + vec3(13.7, 0.0, 0.0));
	float n2 = vnoise(np + vec3(0.0, 17.3, 7.1));
	vec3 jitter = (vec3(n0, n1, n2) - 0.5) * blend_amp;
	vec3 weights = bary + jitter;

	// Triplanar axis weights from the geometric shading normal (before per-tile
	// normal perturbation) so tile weighting stays coherent across a face.
	vec3 blend = pow(abs(shaded_normal), vec3(4.0));
	blend /= (blend.x + blend.y + blend.z + 1e-5);

	vec3 wp = world_vertex * tile_uv_scale;
	float s = clamp(shaded_normal.y, 0.0, 1.0);
	// Sharpness (higher = sharper) → height-blend depth (lower = tighter).
	float wall_depth = 1.0 - wall_blend_sharpness;
	float cliff_depth = 1.0 - cliff_blend_sharpness;
	float ground_depth = 1.0 - ground_blend_sharpness;

	// Build deduped ground/cliff candidate sets across the triangle's 3 corners.
	// Each AUTO corner contributes its flat tile (weight·w_flat, ground) and
	// wall tile (weight·w_wall, cliff); a concrete corner contributes one tile.
	// Duplicate ids merge (per class), so a single-material interior collapses
	// to one sample per class. Class comes from tile_is_cliff[].
	float g_ids[3];
	float g_wts[3];
	int g_n = 0;
	float c_ids[3];
	float c_wts[3];
	int c_n = 0;
	float ov_id = 0.0;
	float ov_w = 0.0;
	float ov_cov = 0.0;
	for (int i = 0; i < 3; i++) {
		float w = max(weights[i], 0.0);
		if (w <= 0.001) {
			continue;
		}
		// Up to two (id, weight) entries from this corner.
		float ent_id[2];
		float ent_w[2];
		int cnt;
		float tid = tile_ids[i];
		if (tid >= TILE_AUTO_THRESHOLD) {
			int ti = clamp(int(terrain_ids[i]), 0, MAX_TERRAINS - 1);
			vec4 tiles = terrain_tiles[ti];
			vec4 band = terrain_bands[ti];
			float wf = smoothstep(band.x, band.y, s);
			ent_id[0] = tiles.x;
			ent_w[0] = w * wf;
			ent_id[1] = tiles.y;
			ent_w[1] = w * (1.0 - wf);
			cnt = 2;
			if (overlay_ids[i] > 0.5 && w * wf > ov_w) {
				ov_id = overlay_ids[i];
				ov_w = w * wf;
				ov_cov = wf;
			}
		} else {
			ent_id[0] = tid;
			ent_w[0] = w;
			ent_id[1] = 0.0;
			ent_w[1] = 0.0;
			cnt = 1;
		}
		// Route + dedup-append each entry into its class list.
		for (int j = 0; j < cnt; j++) {
			float id = ent_id[j];
			float ew = ent_w[j];
			if (ew <= 0.001) {
				continue;
			}
			if (tile_is_cliff[clamp(int(id), 0, TILE_CLASS_TABLE_SIZE - 1)] > 0.5) {
				bool merged = false;
				for (int k = 0; k < c_n; k++) {
					if (c_ids[k] == id) { c_wts[k] += ew; merged = true; break; }
				}
				if (!merged && c_n < 3) { c_ids[c_n] = id; c_wts[c_n] = ew; c_n += 1; }
			} else {
				bool merged = false;
				for (int k = 0; k < g_n; k++) {
					if (g_ids[k] == id) { g_wts[k] += ew; merged = true; break; }
				}
				if (!merged && g_n < 3) { g_ids[g_n] = id; g_wts[g_n] = ew; g_n += 1; }
			}
		}
	}

	// Soft height-blend within each class. Ground and cliff blocks are identical
	// apart from arrays/depth — inlined twice since the shader language has no
	// array-parameter functions. Each samples its unique tiles once and weights
	// them by (normalized geometric weight + height) with a max-cutoff = depth.
	vec3 g_col = vec3(0.0);
	vec3 g_nrm = vec3(0.0, 1.0, 0.0);
	float g_h = 0.0;
	float g_tw = 0.0;
	float g_por = 0.0;
	{
		vec3 cols[3];
		vec3 nrms[3];
		float hs[3];
		float gw[3];
		for (int k = 0; k < g_n; k++) {
			vec3 cc;
			vec3 nn;
			float hh;
			sample_tile(g_ids[k], wp, blend, cc, nn, hh);
			cols[k] = cc;
			nrms[k] = nn;
			hs[k] = hh;
			g_tw += g_wts[k];
		}
		float gmax = -1.0;
		for (int k = 0; k < g_n; k++) {
			gw[k] = g_wts[k] / max(g_tw, 1e-5) + hs[k];
			gmax = max(gmax, gw[k]);
		}
		float mm = gmax - ground_depth;
		float sum = 0.0;
		for (int k = 0; k < g_n; k++) {
			gw[k] = max(gw[k] - mm, 0.0);
			sum += gw[k];
		}
		for (int k = 0; k < g_n; k++) {
			float wk = gw[k] / max(sum, 1e-5);
			g_col += cols[k] * wk;
			g_nrm += nrms[k] * wk;
			g_h += hs[k] * wk;
			g_por += tile_porosity[clamp(int(g_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)] * wk;
		}
		if (g_n > 0) {
			g_nrm = normalize(g_nrm);
		}
	}

	vec3 c_col = vec3(0.0);
	vec3 c_nrm = vec3(0.0, 1.0, 0.0);
	float c_h = 0.0;
	float c_tw = 0.0;
	float c_por = 0.0;
	{
		vec3 cols[3];
		vec3 nrms[3];
		float hs[3];
		float gw[3];
		for (int k = 0; k < c_n; k++) {
			vec3 cc;
			vec3 nn;
			float hh;
			sample_tile(c_ids[k], wp, blend, cc, nn, hh);
			cols[k] = cc;
			nrms[k] = nn;
			hs[k] = hh;
			c_tw += c_wts[k];
		}
		float gmax = -1.0;
		for (int k = 0; k < c_n; k++) {
			gw[k] = c_wts[k] / max(c_tw, 1e-5) + hs[k];
			gmax = max(gmax, gw[k]);
		}
		float mm = gmax - cliff_depth;
		float sum = 0.0;
		for (int k = 0; k < c_n; k++) {
			gw[k] = max(gw[k] - mm, 0.0);
			sum += gw[k];
		}
		for (int k = 0; k < c_n; k++) {
			float wk = gw[k] / max(sum, 1e-5);
			c_col += cols[k] * wk;
			c_nrm += nrms[k] * wk;
			c_h += hs[k] * wk;
			c_por += tile_porosity[clamp(int(c_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)] * wk;
		}
		if (c_n > 0) {
			c_nrm = normalize(c_nrm);
		}
	}

	// Overlay (dirt/mud/field) feathers onto the ground result with the SMOOTH
	// depth where authored — coverage follows its flat-ground fraction.
	if (ov_id > 0.5 && g_n > 0) {
		vec3 oc, on;
		float oh;
		sample_tile(ov_id, wp, blend, oc, on, oh);
		float ob = height_blend_weight(ov_cov, g_h, oh, ground_depth);
		g_col = mix(g_col, oc, ob);
		g_nrm = normalize(mix(g_nrm, on, ob));
		g_h = mix(g_h, oh, ob);
	}

	// TIGHT cliff↔ground blend, by the slope-aggregated class weights. cliff↔cliff
	// and ground↔ground were already blended softly within their classes above.
	vec4 tex_color;
	vec3 tile_normal;
	float surface_h;
	float surface_porosity;
	if (c_n == 0) {
		tex_color = vec4(g_col, 1.0);
		tile_normal = g_nrm;
		surface_h = g_h;
		surface_porosity = g_por;
	} else if (g_n == 0) {
		tex_color = vec4(c_col, 1.0);
		tile_normal = c_nrm;
		surface_h = c_h;
		surface_porosity = c_por;
	} else {
		float t = c_tw / max(c_tw + g_tw, 1e-5);
		float wb = height_blend_weight(t, g_h, c_h, wall_depth);
		tex_color = vec4(mix(g_col, c_col, wb), 1.0);
		tile_normal = normalize(mix(g_nrm, c_nrm, wb));
		surface_h = mix(g_h, c_h, wb);
		surface_porosity = mix(g_por, c_por, wb);
	}

	// Per-tile normal map perturbs the shading normal (replaces the old global
	// detail_normal). Strength is tunable.
	shaded_normal = normalize(mix(shaded_normal, tile_normal, tile_normal_strength));

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
	// Displacement: the per-tile height map's micro-pits (low height) collect
	// water. Folds into the concavity pooling as a [0,1] multiply; wet_displacement
	// tunes how strongly (0 = ignore the height map, the old weather-only look).
	float displacement_wetness = mix(1.0, 1.0 - surface_h, wet_displacement);
	// Concavity pooling (macro dips) × displacement (micro pits), scaled by the
	// real weather wetness (WorldState.WetnessLevel) so pooling is always
	// proportional to the current dampness — bone-dry weather = dry dips.
	float pool_boost = smoothstep(0.0, concavity_threshold, concavity) * displacement_wetness * concavity_wetness_strength;
	float local_wetness = clamp(wetness_level * (1.0 + pool_boost), 0.0, 1.0);
	// saturation: how wet this fragment is — weather × sky-exposure × slope ×
	// pooling. The single "is it wet here" scalar every wet look keys off.
	float saturation = sun_mask * wet_slope * local_wetness;

	// Porosity splits the wet LOOK: porous soil (high) soaks water → darkens with
	// little glint; non-porous rock (low) beads water → reflects. reflectivity is
	// the "water sits on top" amount.
	float porosity = clamp(surface_porosity, 0.0, 1.0);
	float reflectivity = 1.0 - porosity;

	// Standing-water (pooled) state: where the baked concavity is deep, the
	// surface is flat (wet_slope), it's wet (local_wetness) and non-porous, water
	// STANDS — a puddle. Plugs the concavity bake into a distinct water look.
	float pool_depth = smoothstep(concavity_threshold, concavity_threshold * 2.5, concavity);
	// Sky occlusion: pools are rain-fed, so they only form / mirror the sky
	// where the sky actually reaches — sun_mask is the BFS sky-exposure, so
	// caves and overhangs read ~0 and stay dry (no sky mirror in a cave).
	float pool_mask = clamp(pool_depth * wet_slope * local_wetness * sun_mask * mix(0.4, 1.0, reflectivity) * wet_pool_strength, 0.0, 1.0);
	// Water clarity from the zone/weather muddiness: clear pools mirror the sky
	// strongly; murky pools mostly show their (zone-tinted) body color.
	float water_clarity = 1.0 - clamp(water_muddiness, 0.0, 1.0);
	// Waterline: a thin brighter sky-tinted rim at the pool edge (band on pool_mask).
	float waterline = smoothstep(0.02, 0.18, pool_mask) * (1.0 - smoothstep(0.18, 0.4, pool_mask)) * saturation;

	// Roughness synthesized from wetness — no static map. Dry = rough (1); wetting
	// drops it toward wet_roughness_min; a pool goes to ~mirror. This one term
	// drives BOTH the glint tightness and the reflection strength/sharpness below.
	float wet_roughness = mix(1.0, wet_roughness_min, saturation);
	wet_roughness = mix(wet_roughness, 0.02, pool_mask);
	float smoothness = 1.0 - wet_roughness;

	// In a pool, flatten the reflecting normal toward level water for a clean
	// mirror; elsewhere it's the smooth geometric normal (coherent across a face).
	vec3 wet_n = normalize(mix(smooth_n, vec3(0.0, 1.0, 0.0), pool_mask));

	vec3 wet_view = normalize(INV_VIEW_MATRIX[3].xyz - world_vertex);
	// Sun glint: hotspot tightness from smoothness (smoother = tighter), gated by
	// saturation and by reflectivity (non-porous beads glint; pools glint hard).
	vec3 wet_h = normalize(wet_view + (-sun_world_dir));
	float wet_ndoth = max(dot(wet_n, wet_h), 0.0);
	float spec_power = mix(24.0, 400.0, smoothness);
	wet_spec = pow(wet_ndoth, spec_power) * saturation * mix(reflectivity, 1.0, pool_mask);
	// Fresnel SKY reflection — wet ground as a partial mirror of the sky gradient.
	// Strength scales with smoothness (sharper when smoother) and reflectivity,
	// and is forced near-full inside pools. Added to EMISSION (a reflection, not
	// diffuse, so NOT multiplied by albedo) and cloud-dimmed below.
	float wet_fresnel = wet_fresnel_base + (1.0 - wet_fresnel_base) * pow(1.0 - max(dot(wet_view, wet_n), 0.0), 5.0);
	vec3 wet_refl_dir = reflect(-wet_view, wet_n);
	float wet_sky_t = clamp(wet_refl_dir.y * 0.5 + 0.5, 0.0, 1.0);
	vec3 wet_sky = mix(horizon_color, zenith_color, wet_sky_t);
	// Pool reflection floor scales with clarity (clear mirrors, murk doesn't).
	// Clouds (cloud_dim) and canopy/shelter sky-occlusion (ao_factor) are applied
	// where wet_reflect lands in EMISSION; sun_mask already gated pool_mask above.
	float pool_reflect = pool_mask * mix(0.15, 1.0, water_clarity);
	float reflect_amount = max(saturation * smoothness * mix(reflectivity, 1.0, pool_mask), pool_reflect);
	vec3 wet_reflect = wet_sky * (wet_fresnel * wet_reflect_strength * reflect_amount);
	wet_reflect += wet_sky * (waterline * wet_reflect_strength * 0.5);
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

	// Wet albedo: porous soil darkens & saturates a lot, non-porous rock barely
	// (the dark-saturated-ground half of the split). Gated by saturation × porosity;
	// pools force at least some darkening so standing water reads dark under its
	// sky reflection. wet_albedo_floor (SkyController) sets the full-wet darkness.
	float wet_dark = max(saturation * porosity, pool_mask * 0.6);
	// Slight chroma lift so wet ground reads richer, not just darker.
	float wet_luma = dot(tex_color.rgb, vec3(0.299, 0.587, 0.114));
	tex_color.rgb = mix(tex_color.rgb, mix(vec3(wet_luma), tex_color.rgb, 1.0 + wet_chroma), wet_dark);
	tex_color.rgb *= mix(1.0, wet_albedo_floor, wet_dark);
	// Pooled water body: murky pools take on the zone water color; clear pools
	// keep the darkened ground (the sky reflection — clarity/cloud/AO gated — is
	// layered over this in EMISSION).
	tex_color.rgb = mix(tex_color.rgb, water_deep_tint, pool_mask * water_muddiness);

	vec3 base = tex_color.rgb * fill_a_tint * fill_b_tint;
	// Ground stains (scorch / footprints / blood) composite into base BEFORE
	// the ALBEDO/EMISSION split so they survive the always-on EMISSION term —
	// a no-op when the stain projector is off or this fragment is unstained.
	base = apply_ground_stain(base, world_vertex);

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
