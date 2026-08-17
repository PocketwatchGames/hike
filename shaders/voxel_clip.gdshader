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
// Architecture: participates in the visibility-fan cut. See clip_dither.
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
// Term-isolation switches. Each neutralises ONE multiplier of the final
// composite so a banding artefact can be bisected to the term that carries it,
// instead of inferred from how the finished pixel looks.
global uniform bool debug_no_ao;           // ao_factor = 1
global uniform bool debug_no_sun;          // sun_lit flat (drops the baked sun mask)
global uniform bool debug_no_block_light;  // block_lit = 0
global uniform bool debug_no_wet;          // no wetness / puddle / ripple terms
global uniform bool debug_no_eye_adapt;    // ea_gain = 1
// A/B the sun source, both kept so the three can be compared in place without a
// rebuild. Default (both false) = live volume sun x static per-vertex openness.
//   debug_sun_from_volume — raw volume fetch, no openness term.
//   debug_sun_legacy      — the pre-dynamic per-vertex bake, everywhere.
global uniform bool debug_sun_from_volume;
global uniform bool debug_sun_legacy;
// Set per chunk mesh (see ChunkMesh) for backdrop geometry loaded outside the
// light-map window: 1 = shade from the per-vertex bake instead of the volume.
instance uniform float use_baked_sun;
global uniform float light_falloff_exp; // power applied to lightmap value (>1 = darker mids).
// How far along the surface normal the lightmap is sampled, in voxels.
// Sunlight is baked into AIR voxels only, so a sample too close to the surface
// has unlit solid texels inside its trilinear footprint; the share of solid in
// that footprint varies with where the surface sits in the voxel grid, which is
// what makes a smooth slope band. Too far and cave walls read the brighter air
// further out. Diagnostic knob for finding the balance.
global uniform float light_sample_offset;

// RGBA8 light map: R = sunlight visibility mask 0..1 (single source, max-fill,
// pow'd in this shader). GBA = per-color-channel block light, post-pow at
// deposit time so overlapping lights add their perceptual brightness directly.
global uniform sampler3D light_map : filter_linear, repeat_enable;
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
// Standing-water (puddle) shape + slope falloff, all driven by SkyController.
// Puddles are a world-space NOISE field, not the per-vertex concavity bake
// (which interpolated across triangles → triangular pools). Coverage grows with
// wetness_level; only near-flat ground pools (slopes shed to the damp sheen).
//   wet_pool_strength — max puddle coverage cap on flat ground (≤1, so the
//                       ground never fully floods).
//   puddle_scale      — world-space noise frequency; LOWER = larger puddles.
//   puddle_edge       — rim softness in noise-value space; smaller = harder edge.
//   puddle_ramp       — coverage-vs-wetness exponent. <1 ramps puddles in faster
//                       as it starts to wet (1 = linear).
//   pool_flatness     — min smooth_n.y for standing water (cos of the steepest
//                       slope that can still pool). ~0.96 sheds above ~16°.
global uniform float wet_pool_strength;
global uniform float puddle_scale;
global uniform float puddle_edge;
global uniform float puddle_ramp;
global uniform float pool_flatness;
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
// softer ramp (converted to a blend depth = 1 - sharpness in fragment()). Four
// regimes, by the cliff/ground class of the tiles meeting:
//   wall_blend_sharpness    — cliff ↔ ground (SHARPEST; the slope cliff/ground seam)
//   cliff_blend_sharpness   — cliff ↔ cliff  (soft; two rock walls meeting)
//   ground_blend_sharpness  — ground ↔ ground (soft)
//   overlay_blend_sharpness — the overlay pass, on ground and cliff alike. Its
//     own knob because a wide ramp rounds off whatever SHAPE the overlay was
//     stamped in — moss strands need a tight edge that the moss tile's own
//     height map can then fray, while a road tread wants the soft ground ramp.
uniform float wall_blend_sharpness : hint_range(0.0, 1.0) = 0.65;
uniform float cliff_blend_sharpness : hint_range(0.0, 1.0) = 0.4;
uniform float ground_blend_sharpness : hint_range(0.0, 1.0) = 0.4;
uniform float overlay_blend_sharpness : hint_range(0.0, 1.0) = 0.4;

// How hard a tile's texture relief pushes the boundary around, against the
// transition width the sharpnesses above set. 0 = ignore the height maps and
// blend on geometry alone (a straight ramp); higher = the interlocking, eroded
// edge. Needs a gain because relief is a tile's deviation from its OWN mean and
// authored maps only deviate by ~0.05-0.07, which against a 0.6-wide ground
// transition would be an invisible wobble. Raise this OR the sharpnesses to
// tighten an edge — this keeps the soft ramp and adds bite, they narrow the ramp.
uniform float height_relief_strength : hint_range(0.0, 8.0) = 3.0;

// Tile CLASS is no longer a per-layer flag. It comes from which face slot a
// candidate was drawn from — top/bottom = ground, side = cliff — so one texture
// reads as ground on a floor and as cliff on the wall below it.
const int TILE_CLASS_TABLE_SIZE = 64;
// Per-atlas-layer wetness porosity [0,1] (BlockSurfaceData.porosity). LOW = rock
// (water beads on top → reflective), HIGH = soil (water soaks in → darkens).
uniform float tile_porosity[TILE_CLASS_TABLE_SIZE];
// Per-atlas-layer mean of the height channel, measured from the baked atlas at
// load (ChunkMesh.UploadTerrainTables). Cavity darkening subtracts this so the
// term is brightness-neutral: source height maps sit at wildly different means
// (grass 0.23, cobblestone 0.53), and multiplying by raw height would dim each
// tile by a different amount — a vendor authoring artifact, not depth.
uniform float tile_height_mid[TILE_CLASS_TABLE_SIZE];
// 1 = this layer, used as an overlay, also creeps onto cliff faces; 0 = it
// stops at flat ground. Moss breaks up a bare wall; a road tread must not run
// up one. From BlockSurfaceData.overlayOnCliffs.
uniform float tile_overlay_cliff[TILE_CLASS_TABLE_SIZE];
// Texture-space cavity: darkens pits and lifts peaks around tile_height_mid.
// Relief per tile scales with that tile's height CONTRAST, so flat height maps
// stay flat instead of going uniformly dark. 0 = off.
uniform float cavity_strength : hint_range(0.0, 2.0) = 0.0;

// Wetness model tuning (material uniforms; live via CVars). The standing-water
// (puddle) shape lives in the SkyController-driven globals up top, alongside the
// rest of the wet look — only the per-material knobs are here.
//   wet_displacement   — how much the per-tile height map adds micro-pit pooling.
//   wet_roughness_min  — synthesized wet roughness floor (0 = mirror smooth).
//   wet_chroma         — extra saturation on wet, porous (soil) surfaces.
uniform float wet_displacement : hint_range(0.0, 1.0) = 0.5;
uniform float wet_roughness_min : hint_range(0.0, 1.0) = 0.25;
uniform float wet_chroma : hint_range(0.0, 1.0) = 0.2;

// Footstep ripples in standing-water puddles. The shared ripple buffer is tuned
// for open-water entity wakes; puddles want their own speed plus thinner,
// smaller, subtler rings, and a puddle's low-contrast sky reflection barely shows
// a normal tilt — so we also draw an explicit bright rim where the ring crest is.
// All pure feel.
//   puddle_ripple_speed   — ring expansion rate (m/s), independent of the
//                           open-water wake speed (water_ripple_speed global).
//   puddle_ripple_amp     — normal-tilt amplitude (vs the open-water wake amount).
//   puddle_ripple_thinness— ring falloff multiplier; HIGHER = thinner ring.
//   puddle_ripple_size    — ring radius multiplier; LOWER = smaller/tighter ring.
//   puddle_ripple_rim     — brightness of the sky-tinted crest rim (the main cue).
uniform float puddle_ripple_speed : hint_range(0.1, 10.0) = 1.5;
uniform float puddle_ripple_amp : hint_range(0.0, 3.0) = 0.5;
uniform float puddle_ripple_thinness : hint_range(0.5, 20.0) = 2.5;
uniform float puddle_ripple_size : hint_range(0.2, 1.0) = 0.5;
uniform float puddle_ripple_rim : hint_range(0.0, 5.0) = 1.2;

// Puddle sky-reflection gains. Both sit OUTSIDE the Fresnel term (see fragment):
// the camera pitch is fixed and a pool's normal is forced level, so Fresnel there
// is pinned near wet_fresnel_base (~4%) and cannot carry either of these.
//   pool_mirror_strength — sky mirrored by the pool interior; the "this is water" cue.
//   waterline_strength   — the brighter rim at the pool edge. Keep it near or below
//                          the interior gain, else the puddle reads as an outline
//                          drawn around nothing.
uniform float pool_mirror_strength : hint_range(0.0, 1.0) = 0.25;
uniform float waterline_strength : hint_range(0.0, 1.0) = 0.25;

// Ids arriving per-vertex are BLOCK ids (BlockData.blockId), resolved to atlas
// layers here via block_faces[]. There is no auto/literal split any more — every
// block picks its face by slope, and one that repeats a single surface across
// its slots simply blends between identical tiles.
//
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

// Direct-sun NdotL remap window (see sun_lit in fragment()). Sun-averted
// surfaces get sun_ndl_floor; fully sun-facing get floor + range. The gap
// between them is what makes normal-map relief visible at all, so widening it
// is the primary knob for lighting response; narrowing toward 1.0/0.0 flattens
// terrain back to unshaded.
uniform float sun_ndl_floor : hint_range(0.0, 2.0) = 0.55;
uniform float sun_ndl_range : hint_range(0.0, 2.0) = 0.9;

// Concavity is baked per-vertex into CUSTOM2.w by the DC mesher (signed voxels;
// positive = a dip/bowl, negative = a bump). It is NO LONGER the puddle shape
// (that is now world-space noise — see puddle_scale above); instead it BIASES
// where puddles prefer to sit: a baked dip nudges up the local pool coverage so
// real bowls pool a little sooner, but the visible pool edge comes from the
// noise, not the interpolated vertex value. Scaled by wetness, so dry = dry.
//   concavity_wetness_strength: bias weight. 0 = noise-only (ignore dips),
//     1 = authored, >1 makes bowls pool much sooner than flat ground.
//   concavity_threshold: dip depth (voxels) at which the bias is full. Smaller =
//     even shallow dips bias; larger = only deep bowls.
uniform float concavity_wetness_strength : hint_range(0.0, 4.0) = 1.0;
uniform float concavity_threshold : hint_range(0.001, 1.0) = 0.15;
// Debug: paint the baked concavity directly via EMISSION. Red = concave dip
// (pools water), blue = convex bump/ridge, black ≈ flat. Scaled up so the
// sub-voxel magnitudes are visible. Verification aid — see CVars.debugConcavity.
uniform bool debug_concavity = false;

// --- Climbable-ledge mark ------------------------------------------------
// The mesher marks the lip of every wall the player can mantle in CUSTOM3.z
// (ClimbLedgeMarker); that scalar is the COVERAGE of a real atlas layer, so the
// mark is a textured lichen crust with its own normal + height, not a shader
// effect painted over the rock.
//   climb_mark_layer     — atlas layer to sample, pushed from
//     BlockCatalog.climbLedgeSurface by ChunkMesh. -1 disables the mark, so
//     clearing that reference turns it off without editing this shader.
//   climb_mark_strength  — master coverage scale. 0 = off.
//   climb_mark_sharpness — height-blend interlock, same meaning as the
//     overlay/cliff sharpnesses above: HIGHER = tighter, more frayed edge where
//     the crust meets bare rock; LOWER = a softer ramp.
//   climb_mark_gloss     — small sun sheen so the crust catches light against
//     matte rock. Composed in light() beside the wetness glint. 0 = matte.
//   climb_band_width     — half-width of the strip in METRES, measured from the
//     lip edge. This is the knob for "how much ledge is marked"; it governs the
//     reach down the wall AND onto the floor above at once, because the mesher
//     bakes distance from the EDGE rather than from either surface. Keep it
//     under ChunkMesherDC.CLIMB_MAX_DIST or the band clips flat.
//   climb_band_feather   — how far past that the crust fades out, in metres.
//   climb_band_noise_*   — world-space wobble on the band edge, so the strip
//     wanders instead of running parallel to the lip. Amount is in metres.
uniform int climb_mark_layer = -1;
// Held below 1.0 on purpose: at full coverage the crust wins everywhere along
// the lip and reads as a painted line again. Leaving headroom lets the tile's
// own height decide, so bare rock shows between colonies even at the edge.
uniform float climb_mark_strength : hint_range(0.0, 1.0) = 0.75;
uniform float climb_mark_sharpness : hint_range(0.0, 1.0) = 0.5;
// How opaque the crust is where it DOES win the height blend. Distinct from
// strength, which is coverage: lowering strength shrinks the colonies, lowering
// this lets the rock read through the ones that are there. 1.0 replaces the rock
// outright, which is what made the orange read as a solid decal.
uniform float climb_mark_opacity : hint_range(0.0, 1.0) = 0.5;
uniform float climb_mark_gloss : hint_range(0.0, 8.0) = 0.6;
// Hue for the crust, applied as a LUMINANCE-PRESERVING recolor rather than a
// multiply: the baked tile is an orange lichen, and multiplying that by green
// lands on mud, not on green. The tile keeps carrying the colony detail; this
// only carries the hue. Amount 0 restores the tile's own color exactly.
uniform vec3 climb_mark_tint : source_color = vec3(0.55, 0.55, 0.25);
uniform float climb_mark_tint_amount : hint_range(0.0, 1.0) = 1.0;
const vec3 LUMA = vec3(0.2126, 0.7152, 0.0722);
uniform float climb_band_width : hint_range(0.0, 2.0) = 0.207;
uniform float climb_band_feather : hint_range(0.0, 2.0) = 0.22;
// Amount is deliberately kept near the band width: much larger and the wobble
// swings the edge clear past the strip, cutting it into islands that no longer
// read as one ledge. Raise the SCALE to break it up more finely, not this.
uniform float climb_band_noise : hint_range(0.0, 2.0) = 0.35;
uniform float climb_band_noise_scale : hint_range(0.1, 16.0) = 3.0;
// Debug: paint the raw baked mark (CUSTOM3.z) green via EMISSION, alongside the
// coverage that actually resolved, so the bake can be confirmed independently of
// the blend. See CVars.debugClimbLedge.
uniform bool debug_climb_ledge = false;

// Per-block tables uploaded by ChunkMesh.SetBlocks at startup. GLOBAL, not
// per-world: a block id means the same thing in every file.
//   block_faces[i] = (top, side, bottom, _) tile_array layer indices. A block
//                    authoring one surface repeats it, so the blend below
//                    collapses to a single sample.
//   block_bands[i] = (band_lo, band_hi, _, _). One smoothstep on |normal.y|:
//                    below band_lo = 100% side, above band_hi = 100% top (or
//                    bottom, under the horizontal). No intermediate slope tile
//                    — dirt/field/clover appearance comes from the overlay pass.
// Index = the per-vertex block id picked via barycentric argmax. Must match
// BlockCatalog.MAX_BLOCKS.
const int MAX_BLOCKS = 64;
uniform vec4 block_faces[MAX_BLOCKS];
uniform vec4 block_bands[MAX_BLOCKS];

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

// Eye adaptation — local "night eyes" tone curve, driven by the eye_adaptation
// global. The dilation state is owned by the player sim (Player.EyeDilation) and
// pushed by GameClient. Applied to the lit output below; exact no-op when
// eye_adaptation == 0. See shaders/eye_adaptation.gdshaderinc.
#include "res://shaders/eye_adaptation.gdshaderinc"

// Dynamic radial ripples (footsteps in puddles) — the same ring buffer the
// open-water shader reads. Declares the water_ripple_* globals and the
// dynamic_ripple_core helper; applied to standing-water pools below.
#include "res://shaders/water_dynamic_ripple.gdshaderinc"

const float MIN_AMBIENT = 0.0;

varying vec3 world_vertex;
varying vec3 world_normal;
// Block ids for the 3 corners of this triangle (constant across the tri).
varying flat vec3 block_ids;
// Per-vertex blend-noise amplitude, interpolated to fragments.
varying float blend_amp;
// Per-vertex baked ambient occlusion (COLOR.a). 0 = open, 1 = sheltered.
// Interpolated to fragments; applied as a multiplicative diffuse darken.
varying float ao;
// Per-vertex baked concavity (CUSTOM2.w). Signed voxels: + = dip/bowl,
// - = bump/ridge. Interpolated to fragments; drives the wetness pooling term.
varying float concavity;

// Per-vertex static sky openness (CUSTOM3.x) — the geometry-only hemisphere
// fraction, multiplied by the live volume sun in fragment().
varying float openness;
// Per-vertex baked sun (CUSTOM3.y): openness already weighted by the sunlight
// each ray reached, frozen at mesh-build time. Only backdrop geometry outside
// the light-map window shades with it.
varying float baked_sun;
// Per-vertex distance in metres to the nearest mantleable lip edge (CUSTOM3.z),
// clamped at the mesher's CLIMB_MAX_DIST. A distance field rather than a flag so
// the band below can be narrower than the 1m vertex spacing.
varying float climb_dist;
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
// Fragment→light varying. The climbable-ledge band's sheen, composed in light()
// under the same shadow gates as wet_spec. Its own varying rather than a term
// folded into wet_spec: this one must show on dry rock, which is the whole
// point, and wet_spec is multiplied through by wetness.
varying float climb_spec;
// Per-corner authored overlay ids. 0 = no overlay, paint the block's own faces
// as-is; non-zero = sample that tile and blend over. Picked by the same
// barycentric argmax as block_ids so boundaries inherit the shared
// noise-jittered edge.
varying flat vec3 overlay_ids;

// Tunes the spatial frequency of the per-tile-edge noise. Higher = finer
// jitter along the boundary; too high looks like static, too low looks like
// big linear smears.
const float BLEND_NOISE_FREQ = 1.7;

void vertex() {
	world_vertex = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
	world_normal = NORMAL;
	block_ids = CUSTOM0.xyz;
	blend_amp = CUSTOM0.w;
	ao = COLOR.a;
	sharpness = CUSTOM1.x;
	overlay_ids = CUSTOM2.xyz;
	concavity = CUSTOM2.w;
	openness = CUSTOM3.x;
	baked_sun = CUSTOM3.y;
	climb_dist = CUSTOM3.z;
}

// Hash-based 3D value noise. Good enough for breaking up tile-edge seams; we
// only need it to be locally varied and reproducible across chunk boundaries.
float hash13(vec3 p) {
	p = fract(p * 0.3183099 + 0.1);
	p *= 17.0;
	return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}

// Triplanar sample of one tile layer. `wp` is the world position already
// scaled by tile_uv_scale; `dwp_dx`/`dwp_dy` are its screen-space derivatives
// (see fragment(): they MUST be computed in uniform control flow and passed in);
// `blend` is the axis weight vector from the shading normal. Returns base color,
// a world-space normal (decoded + remapped from the packed tangent-space normal
// map), and the height/displacement scalar.
//
// Every fetch is textureGrad, never texture(). Both atlases are mipmapped, and
// every call site here sits in divergent control flow — the flat/triplanar
// branch below, the per-class candidate loops, and the optional overlay fetch
// all execute a different set of samples per fragment. Implicit-derivative
// texture() in divergent flow reads neighbor lanes that never ran that fetch, so
// the mip level came out as garbage and speckled/flickered wherever the divergence
// lived: the ~0.5m band around cliff and slope edges where the normal tilts past
// the fast-path cutoff and ground/cliff tiles both have weight.
void sample_tile(float layer, vec3 wp, vec3 dwp_dx, vec3 dwp_dy, vec3 blend,
		out vec3 color, out vec3 wnormal, out float height) {
	// Per-plane UV gradients. Pure swizzles of the hoisted derivatives — no
	// derivative op here, so these stay valid however divergent this call is.
	vec2 g_xz_x = vec2(dwp_dx.x,  dwp_dx.z);
	vec2 g_xz_y = vec2(dwp_dy.x,  dwp_dy.z);
	// Fast path: near-flat ground is dominated by the XZ plane, so skip the two
	// vertical projections — 1 fetch instead of 3, across the bulk of the ground.
	if (blend.y > 0.995) {
		color = textureGrad(tile_array, vec3(wp.x, wp.z, layer), g_xz_x, g_xz_y).rgb;
		vec4 nh = textureGrad(tile_nrm_height, vec3(wp.x, wp.z, layer), g_xz_x, g_xz_y);
		height = nh.a;
		vec3 n = nh.xyz * 2.0 - 1.0;
		wnormal = normalize(vec3(n.x, n.z, n.y));
		return;
	}
	vec2 g_xy_x = vec2(dwp_dx.x, -dwp_dx.y);
	vec2 g_xy_y = vec2(dwp_dy.x, -dwp_dy.y);
	vec2 g_yz_x = vec2(dwp_dx.z, -dwp_dx.y);
	vec2 g_yz_y = vec2(dwp_dy.z, -dwp_dy.y);
	vec3 c_xy = textureGrad(tile_array, vec3(wp.x, -wp.y, layer), g_xy_x, g_xy_y).rgb;
	vec3 c_xz = textureGrad(tile_array, vec3(wp.x,  wp.z, layer), g_xz_x, g_xz_y).rgb;
	vec3 c_yz = textureGrad(tile_array, vec3(wp.z, -wp.y, layer), g_yz_x, g_yz_y).rgb;
	vec4 nh_xy = textureGrad(tile_nrm_height, vec3(wp.x, -wp.y, layer), g_xy_x, g_xy_y);
	vec4 nh_xz = textureGrad(tile_nrm_height, vec3(wp.x,  wp.z, layer), g_xz_x, g_xz_y);
	vec4 nh_yz = textureGrad(tile_nrm_height, vec3(wp.z, -wp.y, layer), g_yz_x, g_yz_y);
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
// tiles' RELIEF — height minus that tile's own mean, so a tile authored bright
// can't win the comparison everywhere (see the class blends); `depth` sets the
// transition width — small = tight interlock (cliffs), large = smooth ramp
// (ground overlays). Returns the resolved A→B weight, used identically for
// color and normal so they agree.
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
	// Tile-UV derivatives, taken HERE in uniform control flow and threaded into
	// every sample_tile below. All the tile fetches live in divergent branches
	// (flat vs triplanar, varying candidate counts, optional overlay), where
	// implicit derivatives are undefined — see sample_tile.
	vec3 dwp_dx = dFdx(wp);
	vec3 dwp_dy = dFdy(wp);
	// Signed for the top/bottom pick, magnitude for the slope blend — a ceiling
	// is as "flat" as a floor, it just wears the other horizontal face.
	float sy = shaded_normal.y;
	float s = abs(sy);
	// Sharpness (higher = sharper) → height-blend depth (lower = tighter).
	float wall_depth = 1.0 - wall_blend_sharpness;
	float cliff_depth = 1.0 - cliff_blend_sharpness;
	float ground_depth = 1.0 - ground_blend_sharpness;
	float overlay_depth = 1.0 - overlay_blend_sharpness;

	// Build deduped ground/cliff candidate sets across the triangle's 3 corners.
	// Every corner contributes its block's horizontal face (weight·wf, ground)
	// and its side face (weight·(1-wf), cliff). Duplicate ids merge per class,
	// so a single-material interior collapses to one sample per class and a
	// single-surface block costs no more than a concrete tile used to.
	float g_ids[3];
	float g_wts[3];
	int g_n = 0;
	float c_ids[3];
	float c_wts[3];
	int c_n = 0;
	// Overlays get the same deduped treatment as the two classes. Coverage has
	// to be the overlay corners' SHARE of the triangle, so it needs the total
	// corner weight as a denominator — using the slope fraction alone made a
	// single overlay corner paint the whole triangle at full strength, which is
	// what read as a hard un-feathered dirt/road edge on flat ground.
	float ov_ids[3];
	float ov_wts[3];
	int ov_n = 0;
	float w_total = 0.0;
	for (int i = 0; i < 3; i++) {
		float w = max(weights[i], 0.0);
		if (w <= 0.001) {
			continue;
		}
		w_total += w;
		// Two (id, weight) entries from every corner: the horizontal face
		// (top above the equator, bottom below) and the side, split by slope.
		// A block repeating one surface across its slots yields two identical
		// ids, which the dedup below merges back into a single sample.
		float ent_id[2];
		float ent_w[2];
		// 1.0 = cliff class, 0.0 = ground. The face slot IS the class.
		float ent_cliff[2];
		int cnt;
		int bi = clamp(int(block_ids[i]), 0, MAX_BLOCKS - 1);
		vec4 faces = block_faces[bi];
		vec4 band = block_bands[bi];
		float wf = smoothstep(band.x, band.y, s);
		ent_id[0] = sy >= 0.0 ? faces.x : faces.z;
		ent_w[0] = w * wf;
		ent_cliff[0] = 0.0;
		ent_id[1] = faces.y;
		ent_w[1] = w * (1.0 - wf);
		ent_cliff[1] = 1.0;
		cnt = 2;
		if (overlay_ids[i] > 0.5) {
			// ONE weight spanning both faces. wf is the flat share; the wall
			// share is admitted only for layers authored to climb (moss yes,
			// road tread no). Splitting this into separate ground and cliff
			// overlays is what put a visible seam along every wall/floor join:
			// two coverages resolved at two different depths cannot agree
			// where they meet.
			float climb = tile_overlay_cliff[clamp(int(overlay_ids[i]), 0, TILE_CLASS_TABLE_SIZE - 1)];
			float ow = w * (wf + (1.0 - wf) * climb);
			if (ow > 0.001) {
				bool merged = false;
				for (int k = 0; k < ov_n; k++) {
					if (ov_ids[k] == overlay_ids[i]) { ov_wts[k] += ow; merged = true; break; }
				}
				if (!merged && ov_n < 3) { ov_ids[ov_n] = overlay_ids[i]; ov_wts[ov_n] = ow; ov_n += 1; }
			}
		}
		// Route + dedup-append each entry into its class list.
		for (int j = 0; j < cnt; j++) {
			float id = ent_id[j];
			float ew = ent_w[j];
			if (ew <= 0.001) {
				continue;
			}
			if (ent_cliff[j] > 0.5) {
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

	// Winning overlay, and its share of the triangle.
	float ov_id = 0.0;
	float ov_cov = 0.0;
	for (int k = 0; k < ov_n; k++) {
		if (ov_wts[k] > ov_cov) { ov_cov = ov_wts[k]; ov_id = ov_ids[k]; }
	}
	ov_cov = ov_cov / max(w_total, 1e-5);

	// Soft height-blend within each class. Ground and cliff blocks are identical
	// apart from arrays/depth — inlined twice since the shader language has no
	// array-parameter functions. Each samples its unique tiles once and weights
	// them by (normalized geometric weight + RELIEF) with a max-cutoff = depth.
	//
	// Relief is height MINUS that tile's own mean (tile_height_mid), never raw
	// height. Authored maps sit at wildly different mean levels — grass averages
	// 0.23, forest soil 0.40 — and against a per-tile relief of only ~0.06 a raw
	// comparison is decided by that constant offset, so the higher-mean tile wins
	// everywhere and the interlock flattens into a plain ramp. The two happened to
	// match in the swamp (marsh 0.376 vs mud 0.399), which is the one place the
	// interlock was visible.
	vec3 g_col = vec3(0.0);
	vec3 g_nrm = vec3(0.0, 1.0, 0.0);
	float g_h = 0.0;
	float g_tw = 0.0;
	float g_por = 0.0;
	float g_hmid = 0.0;
	{
		vec3 cols[3];
		vec3 nrms[3];
		float hs[3];
		float mids[3];
		float gw[3];
		for (int k = 0; k < g_n; k++) {
			vec3 cc;
			vec3 nn;
			float hh;
			sample_tile(g_ids[k], wp, dwp_dx, dwp_dy, blend, cc, nn, hh);
			cols[k] = cc;
			nrms[k] = nn;
			hs[k] = hh;
			mids[k] = tile_height_mid[clamp(int(g_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)];
			g_tw += g_wts[k];
		}
		float gmax = -1.0;
		for (int k = 0; k < g_n; k++) {
			gw[k] = g_wts[k] / max(g_tw, 1e-5) + (hs[k] - mids[k]) * height_relief_strength;
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
			g_hmid += tile_height_mid[clamp(int(g_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)] * wk;
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
	float c_hmid = 0.0;
	{
		vec3 cols[3];
		vec3 nrms[3];
		float hs[3];
		float mids[3];
		float gw[3];
		for (int k = 0; k < c_n; k++) {
			vec3 cc;
			vec3 nn;
			float hh;
			sample_tile(c_ids[k], wp, dwp_dx, dwp_dy, blend, cc, nn, hh);
			cols[k] = cc;
			nrms[k] = nn;
			hs[k] = hh;
			mids[k] = tile_height_mid[clamp(int(c_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)];
			c_tw += c_wts[k];
		}
		float gmax = -1.0;
		for (int k = 0; k < c_n; k++) {
			gw[k] = c_wts[k] / max(c_tw, 1e-5) + (hs[k] - mids[k]) * height_relief_strength;
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
			c_hmid += tile_height_mid[clamp(int(c_ids[k]), 0, TILE_CLASS_TABLE_SIZE - 1)] * wk;
		}
		if (c_n > 0) {
			c_nrm = normalize(c_nrm);
		}
	}

	// TIGHT cliff↔ground blend, by the slope-aggregated class weights. cliff↔cliff
	// and ground↔ground were already blended softly within their classes above.
	vec4 tex_color;
	vec3 tile_normal;
	float surface_h;
	float surface_porosity;
	// Mean of surface_h's own tile(s), carried through every blend alongside it
	// so the cavity term always subtracts the mid of whatever mix it landed on.
	float surface_h_mid;
	if (c_n == 0) {
		tex_color = vec4(g_col, 1.0);
		tile_normal = g_nrm;
		surface_h = g_h;
		surface_porosity = g_por;
		surface_h_mid = g_hmid;
	} else if (g_n == 0) {
		tex_color = vec4(c_col, 1.0);
		tile_normal = c_nrm;
		surface_h = c_h;
		surface_porosity = c_por;
		surface_h_mid = c_hmid;
	} else {
		float t = c_tw / max(c_tw + g_tw, 1e-5);
		float wb = height_blend_weight(t, (g_h - g_hmid) * height_relief_strength, (c_h - c_hmid) * height_relief_strength, wall_depth);
		tex_color = vec4(mix(g_col, c_col, wb), 1.0);
		tile_normal = normalize(mix(g_nrm, c_nrm, wb));
		surface_h = mix(g_h, c_h, wb);
		surface_porosity = mix(g_por, c_por, wb);
		surface_h_mid = mix(g_hmid, c_hmid, wb);
	}

	// Overlay (moss / dirt / road tread) feathers onto the RESOLVED surface,
	// after cliff and ground have already been combined. Applying it once here
	// rather than separately per class is what keeps a wall and the floor it
	// meets continuous: one coverage, one depth, one height comparison, so the
	// moss crossing the join is the same moss on both sides of it.
	if (ov_id > 0.5) {
		vec3 oc, on;
		float oh;
		sample_tile(ov_id, wp, dwp_dx, dwp_dy, blend, oc, on, oh);
		float o_mid = tile_height_mid[clamp(int(ov_id), 0, TILE_CLASS_TABLE_SIZE - 1)];
		float ob = height_blend_weight(
			ov_cov,
			(surface_h - surface_h_mid) * height_relief_strength,
			(oh - o_mid) * height_relief_strength,
			overlay_depth);
		tex_color = vec4(mix(tex_color.rgb, oc, ob), tex_color.a);
		tile_normal = normalize(mix(tile_normal, on, ob));
		surface_porosity = mix(surface_porosity, tile_porosity[clamp(int(ov_id), 0, TILE_CLASS_TABLE_SIZE - 1)], ob);
		surface_h = mix(surface_h, oh, ob);
		surface_h_mid = mix(surface_h_mid, o_mid, ob);
	}

	// --- Climbable-ledge mark -----------------------------------------------
	// Applied AFTER the overlay pass, on the resolved surface, so a mantleable
	// ledge is marked whatever it is made of and whatever is already growing on
	// it — the affordance has to survive moss and road tread, not queue behind
	// them. Blended through the same relief-aware weight as the overlay, so the
	// crust interlocks with the rock's own height instead of ending on a ruled
	// line, and contributes its normal map to the shading below.
	//
	// Deliberately NOT gated to wall-facing normals. At this camera a ledge
	// facing away shows only its top surface, so the strip has to straddle the
	// lip — a little down the wall and a little onto the floor above — or it
	// disappears exactly when the player most needs it. Measuring from the edge
	// gets both halves from one number.
	float climb_cov = 0.0;
	if (climb_mark_layer >= 0) {
		// Wobble the band edge in world space so the strip wanders instead of
		// running perfectly parallel to the lip. Perturbing the DISTANCE rather
		// than masking the result keeps the crust connected along the ledge.
		float band_n = vnoise(world_vertex * climb_band_noise_scale) - 0.5;
		float d = climb_dist + band_n * climb_band_noise;
		climb_cov = (1.0 - smoothstep(climb_band_width, climb_band_width + climb_band_feather, d))
			* climb_mark_strength;
	}
	if (climb_cov > 0.001) {
		vec3 lichen_col, lichen_nrm;
		float lichen_h;
		sample_tile(float(climb_mark_layer), wp, dwp_dx, dwp_dy, blend, lichen_col, lichen_nrm, lichen_h);
		// Normalizing the tint by its own luminance makes it a pure hue
		// direction, so the recolored crust keeps the tile's brightness instead
		// of darkening wherever the tint is saturated.
		float lichen_lum = dot(lichen_col, LUMA);
		vec3 climb_hue = climb_mark_tint / max(dot(climb_mark_tint, LUMA), 1e-4);
		lichen_col = mix(lichen_col, climb_hue * lichen_lum, climb_mark_tint_amount);
		int li = clamp(climb_mark_layer, 0, TILE_CLASS_TABLE_SIZE - 1);
		float lichen_mid = tile_height_mid[li];
		float lb = height_blend_weight(
			climb_cov,
			(surface_h - surface_h_mid) * height_relief_strength,
			(lichen_h - lichen_mid) * height_relief_strength,
			1.0 - climb_mark_sharpness);
		// The height blend above chose WHERE; this scales HOW MUCH, so the crust
		// tints the rock rather than replacing it. Applied to every channel below
		// (not just color) so a half-opacity crust also perturbs the normal and
		// shifts porosity by half — otherwise it would light like solid lichen
		// while looking translucent.
		lb *= climb_mark_opacity;
		tex_color = vec4(mix(tex_color.rgb, lichen_col, lb), tex_color.a);
		tile_normal = normalize(mix(tile_normal, lichen_nrm, lb));
		surface_porosity = mix(surface_porosity, tile_porosity[li], lb);
		surface_h = mix(surface_h, lichen_h, lb);
		surface_h_mid = mix(surface_h_mid, lichen_mid, lb);
		// Carry forward what actually landed, not the requested coverage — the
		// sheen below must follow the crust's frayed edge, not the bake's.
		climb_cov = lb;
	} else {
		climb_cov = 0.0;
	}

	// Per-tile normal map perturbs the shading normal (replaces the old global
	// detail_normal). Strength is tunable.
	shaded_normal = normalize(mix(shaded_normal, tile_normal, tile_normal_strength));

	// Sample lightmap per-pixel so it interpolates smoothly across the face.
	// Push 0.5 along the outward normal so the sample lands in the adjacent
	// air voxel — without this, DC vertices on cave walls would sample inside
	// the solid (or worse, near a sunlit-air neighbour) and caves would glow.
	vec3 light_sample_pos = world_vertex + shaded_normal * light_sample_offset;
	vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
	vec4 lm = texture(light_map, uvw);
	// Sun = the live volume field x a static per-vertex openness term.
	//   The VOLUME carries everything that can change at runtime — a door
	//   opening, canopy, the propagated flood — so terrain relights without a
	//   re-mesh. It is dilated into solid voxels at encode time (see LightMap)
	//   so sampling near a surface no longer pulls the ground's own black texels
	//   into the trilinear footprint and bands every slope.
	//   OPENNESS carries what one volume sample cannot express: the cosine-
	//   weighted sky hemisphere, which is what keeps a wall darker than the
	//   ground it meets instead of reading a flat 1.0 like level terrain.
	// Block light (lm.gba) comes from the volume unchanged.
	float sun_raw = clamp(lm.r * openness, 0.0, 1.0);
	if (use_baked_sun > 0.5 || debug_sun_legacy) {
		// Backdrop geometry past the light-map window: the window is toroidal,
		// so a sample from outside it wraps onto unrelated world. The frozen
		// bake is correct enough for a distant panorama.
		sun_raw = clamp(baked_sun, 0.0, 1.0);
	} else if (debug_sun_from_volume) {
		sun_raw = lm.r;
	}
	float sun_mask = pow(sun_raw, light_falloff_exp);
	// Wetness gate: only sky-exposed (sun_mask) and roughly upward-facing
	// surfaces wet out. Vertical cliffs shed water, caves/overhangs never
	// see rain. Slope exponent 3 gives a smooth falloff — fully flat reads
	// as fully wet, ~45° slopes still get a subtle sheen, vertical faces
	// are clean. Read the SMOOTH geometric normal (not the detail-perturbed
	// shaded_normal): the wet sheen must be coherent across a face, not
	// flicker on the normal-map noise. Driving the old specular through the
	// perturbed normal is exactly what made wet ground read as "crunchier
	// texture" instead of wet.
	// General surface wetness ("slickness") — a damp sheen on ALL sky-exposed
	// upward-facing ground, flat and sloped alike. wet_slope falls off with
	// incline (full on flat, subtle by ~45°, gone on vertical faces) but never
	// pools; standing water (below) is a separate, flat-only term.
	float wet_slope = pow(max(smooth_n.y, 0.0), 3.0);
	float local_wetness = wetness_level;
	// saturation: how wet this fragment is — weather × sky-exposure × slope. The
	// single "is it wet here" scalar every wet look keys off.
	float saturation = sun_mask * wet_slope * local_wetness;

	// Porosity splits the wet LOOK: porous soil (high) soaks water → darkens with
	// little glint; non-porous rock (low) beads water → reflects. reflectivity is
	// the "water sits on top" amount.
	float porosity = clamp(surface_porosity, 0.0, 1.0);
	float reflectivity = 1.0 - porosity;

	// Standing water (puddles): a world-space NOISE field thresholded by coverage,
	// NOT the per-vertex concavity bake (that interpolated across triangles and
	// made pools look triangular). Two octaves of value noise in world XZ — static
	// in world space, so puddles sit still as the camera moves.
	vec2 puddle_uv = world_vertex.xz * puddle_scale;
	float puddle_field = vnoise(vec3(puddle_uv, 0.0)) * 0.65
		+ vnoise(vec3(puddle_uv * 2.7, 19.0)) * 0.35;
	// Coverage = how much of the noise field is underwater. Grows with weather
	// wetness (dry weather → no pools), capped by wet_pool_strength, biased up in
	// micro-pits (per-tile height map) and in real baked dips (concavity — a soft
	// nudge on WHERE pools sit, not their shape). The ×local_wetness keeps dips dry
	// when the weather is dry.
	float micro_pit = mix(1.0, 1.0 - surface_h, wet_displacement);
	float concavity_bias = smoothstep(0.0, concavity_threshold, concavity) * concavity_wetness_strength;
	// puddle_ramp (<1) curves coverage up faster as the ground starts to wet, so
	// puddles appear with less rain; at full wetness it still caps at wet_pool_strength.
	float pool_wetness = pow(clamp(local_wetness, 0.0, 1.0), puddle_ramp);
	float pool_coverage = clamp(pool_wetness * (wet_pool_strength * micro_pit + concavity_bias), 0.0, 1.0);
	// Threshold the noise: more coverage → lower threshold → more ground pools.
	float pool_thresh = 1.0 - pool_coverage;
	float pool_shape = smoothstep(pool_thresh, pool_thresh + puddle_edge, puddle_field);
	// Slope falloff: standing water needs near-level ground — a much sharper gate
	// than wet_slope, so gentle slopes shed to the slickness sheen above instead
	// of pooling. sun_mask (BFS sky-exposure) keeps caves/overhangs dry.
	float pool_flat = smoothstep(pool_flatness, 1.0, smooth_n.y);
	float pool_depth = pool_shape * pool_flat;
	float pool_mask = clamp(pool_depth * sun_mask * mix(0.4, 1.0, reflectivity), 0.0, 1.0);
	// Water clarity from the zone/weather muddiness: clear pools mirror the sky
	// strongly; murky pools mostly show their (zone-tinted) body color.
	float water_clarity = 1.0 - clamp(water_muddiness, 0.0, 1.0);
	// Waterline: a thin brighter sky-tinted rim at the pool edge. Banded on
	// pool_shape, NOT pool_mask — pool_mask carries the porosity scale, so banding on
	// it makes the rim ~1.8x wider on marsh than on rock and the gain below stops
	// meaning one thing. Re-apply the flatness / sky gates pool_mask would have.
	float waterline = smoothstep(0.02, 0.18, pool_shape) * (1.0 - smoothstep(0.18, 0.4, pool_shape))
		* pool_flat * sun_mask * saturation;

	// Roughness synthesized from wetness — no static map. Dry = rough (1); wetting
	// drops it toward wet_roughness_min; a pool goes to ~mirror. This one term
	// drives BOTH the glint tightness and the reflection strength/sharpness below.
	float wet_roughness = mix(1.0, wet_roughness_min, saturation);
	wet_roughness = mix(wet_roughness, 0.02, pool_mask);
	float smoothness = 1.0 - wet_roughness;

	// In a pool, flatten the reflecting normal toward level water for a clean
	// mirror; elsewhere it's the smooth geometric normal (coherent across a face).
	vec3 wet_n = normalize(mix(smooth_n, vec3(0.0, 1.0, 0.0), pool_mask));
	// Footstep ripples on the pool surface. Gated by pool_mask so only standing
	// water reacts (the slope sheen / dry ground stay calm). The normal tilt
	// (thinner/smaller/subtler than an open-water wake via the puddle_ripple_*
	// muls) flows through the Fresnel sky reflection + sun glint below; `ripple_rim`
	// is the crest envelope, drawn as an explicit bright rim later since a
	// puddle's flat sky reflection barely shows the tilt on its own.
	float ripple_rim = 0.0;
	if (pool_mask > 0.001) {
		float rim;
		vec2 dyn_tilt = dynamic_ripple_core(world_vertex.xz, puddle_ripple_speed,
			puddle_ripple_amp, puddle_ripple_thinness, puddle_ripple_size, rim) * pool_mask;
		wet_n = normalize(wet_n + vec3(dyn_tilt.x, 0.0, dyn_tilt.y));
		ripple_rim = rim * pool_mask;
	}

	vec3 wet_view = normalize(INV_VIEW_MATRIX[3].xyz - world_vertex);
	// Sun glint: hotspot tightness from smoothness (smoother = tighter), gated by
	// saturation and by reflectivity (non-porous beads glint; pools glint hard).
	vec3 wet_h = normalize(wet_view + (-sun_world_dir));
	float wet_ndoth = max(dot(wet_n, wet_h), 0.0);
	float spec_power = mix(24.0, 400.0, smoothness);
	wet_spec = pow(wet_ndoth, spec_power) * saturation * mix(reflectivity, 1.0, pool_mask);
	// Lichen sheen. Same half-vector, but no wetness gate — the crust should
	// catch a little sun against matte dry rock. Broad rather than tight: this is
	// a slightly waxy surface, not a polished one.
	climb_spec = pow(max(dot(shaded_normal, wet_h), 0.0), 16.0) * climb_cov * climb_mark_gloss;
	// Fresnel SKY reflection — wet ground as a partial mirror of the sky gradient.
	// Strength scales with smoothness (sharper when smoother) and reflectivity.
	// Added to EMISSION (a reflection, not diffuse, so NOT multiplied by albedo)
	// and cloud-dimmed below. This term carries the sheen on tilted wet ground; the
	// pool interior and its waterline are added separately after it, because a pool
	// forces a level normal and so has no Fresnel response left to modulate.
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
	// Pool interior and its rim, both outside the Fresnel multiply above — at this
	// camera's fixed pitch against a level pool normal, Schlick sits at ~4% and
	// would scale pool_reflect away to nothing, leaving the puddle a hole inside
	// its own waterline. Rim gain is separate so it can stay near the interior's.
	wet_reflect += wet_sky * (pool_reflect * wet_reflect_strength * pool_mirror_strength);
	wet_reflect += wet_sky * (waterline * wet_reflect_strength * waterline_strength
		* mix(0.15, 1.0, water_clarity));
	if (debug_no_wet) {
		wet_reflect = vec3(0.0);
		ripple_rim = 0.0;
	}
	// Cloud shadow: pass to light() via cloud_dim so it composes with
	// ATTENUATION as min() (darkest occluder wins) rather than multiplying
	// onto the direct-sun term. Ambient sky-bounce (EMISSION half) is still
	// cloud-dimmed below — clouds do occlude sky light.
	// cloud_shadow_ground is Y-independent to avoid faceted polygon-edge
	// artifacts on sloped triangles. Gated by sky exposure at the same air
	// voxel the light map was sampled at, so cave / interior surfaces that
	// still carry lateral sun spill don't get cloud blotches drifting over them.
	// Backdrop geometry past the volume window takes the ungated drape: its
	// sky-exposure probe would wrap onto unrelated world, same reason that
	// path reads baked_sun instead of the live sun volume.
	float cloud = (use_baked_sun > 0.5)
		? cloud_shadow_ground(world_vertex, TIME)
		: cloud_shadow_ground_gated(world_vertex, light_sample_pos, TIME);
	cloud_dim = debug_no_cloud ? 1.0 : 1.0 - cloud * cloud_shadow_strength;
	// NdotL multiplier on direct sun, remapped into [floor, floor+range] rather
	// than the raw 0..1 Lambert — sun-facing slopes lift above the glow HDR
	// threshold so bloom catches them, averted slopes cut (still gated by
	// sun_mask, so underground/shadowed doesn't darken further).
	//
	// The width of that window is the ceiling on how much per-tile normal-map
	// detail can ever show: at the original 0.9..1.3 a bump big enough to swing
	// true Lambert 1.0→0.6 moved this only 1.30→1.14 (12%), which is why the
	// normal maps read as flat. Defaults are chosen to cross the old curve at
	// sun_ndl≈0.7 (typical sunlit ground) so overall exposure is preserved while
	// the spread roughly doubles. Widening further pushes more of the range past
	// the bloom knee — retune glow alongside it.
	float sun_ndl = max(0.0, dot(shaded_normal, -sun_world_dir));
	vec3 sun_lit = sun_mask * sun_intensity * sun_color * (sun_ndl_floor + sun_ndl_range * sun_ndl);
	// Flat sun: keeps intensity/colour but discards the BAKED sun mask, so a
	// band that survives this is not coming from the sunlight volume.
	if (debug_no_sun) {
		sun_lit = sun_intensity * sun_color;
	}
	// Per-fragment block_lit (torch/lamp + skylight) is dimmed by any
	// sprite silhouette projected from above by BlockLightShadowProjector.
	// Direct sun (sun_lit, carried via ALBEDO) is untouched.
	vec3 block_lit = lm.gba * block_light_shadow_factor(world_vertex);
	if (debug_no_block_light) {
		block_lit = vec3(0.0);
	}

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
	// Texture-space cavity. Mean-centred, so it redistributes brightness (pits
	// down, peaks up) instead of dimming — average output is unchanged at any
	// strength. Unlike the normal-map term this is view- and light-independent,
	// so crevices stay legible at every sun angle. Applied before ground stains
	// so a scorch mark isn't darkened a second time by the ground under it.
	base *= max(0.0, 1.0 + cavity_strength * (surface_h - surface_h_mid));
	// Ground stains (scorch / footprints / blood) composite into base BEFORE
	// the ALBEDO/EMISSION split so they survive the always-on EMISSION term —
	// a no-op when the stain projector is off or this fragment is unstained.
	base = apply_ground_stain(base, world_vertex, shaded_normal.y);

	// Baked AO: multiplicative shelter/contact darkening on the diffuse
	// lighting only. ao=0 (open) leaves lighting untouched; ao=1 (buried)
	// scales the diffuse down by ao_strength. The wet sky reflection
	// (wet_reflect, a specular term) is intentionally NOT occluded here.
	float ao_factor = debug_no_ao ? 1.0 : 1.0 - ao * ao_strength;

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
	// Footstep ripple crest — a thin sky-tinted rim, the main visible cue since a
	// flat puddle's reflection barely bends with the normal tilt. Sky-occlusion
	// gated (cloud_dim, ao_factor) like the rest of the wet reflection above.
	EMISSION += wet_sky * (ripple_rim * puddle_ripple_rim * cloud_dim * ao_factor);

	// Eye adaptation: lift this fragment when it sits in low local light and the
	// player is dark-adapted, push it toward blowout in bright light. ea_light is
	// the AREA illuminance here (direct sun + block light, pre-shadow — NOT
	// albedo, so dark ground in bright light isn't lifted like a genuinely dim
	// spot). Scaling ALBEDO + EMISSION equally scales the final composite
	// (ALBEDO*ATTENUATION + EMISSION) cleanly. No-op when eye_adaptation == 0.
	// Runs BEFORE the debug blocks so diagnostics stay un-adapted.
	float ea_light = dot(sun_lit + block_lit, vec3(0.299, 0.587, 0.114));
	float ea_gain = debug_no_eye_adapt ? 1.0 : eye_adaptation_gain(ea_light);
	ALBEDO *= ea_gain;
	EMISSION *= ea_gain;

	if (dot(debug_solid, vec3(1.0)) > 0.001) {
		ALBEDO = vec3(0.0);
		EMISSION = debug_solid;
		wet_spec = 0.0;
		climb_spec = 0.0;
	}
	if (debug_concavity) {
		// Red = concave dip (positive), blue = convex bump (negative), black ≈
		// flat. ×4 so sub-voxel concavity magnitudes are visible.
		ALBEDO = vec3(0.0);
		EMISSION = vec3(max(concavity, 0.0), 0.0, max(-concavity, 0.0)) * 4.0;
		wet_spec = 0.0;
		climb_spec = 0.0;
	}
	if (debug_normals) {
		ALBEDO = vec3(0.0);
		EMISSION = shaded_normal * 0.5 + 0.5;
		wet_spec = 0.0;
		climb_spec = 0.0;
	}
	if (debug_unlit) {
		ALBEDO = vec3(0.0);
		EMISSION = tex_color.rgb;
		wet_spec = 0.0;
		climb_spec = 0.0;
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
		climb_spec = 0.0;
	}
	if (debug_climb_ledge) {
		// Green = the baked distance field (bright AT the lip edge), red = the
		// coverage the band + height blend actually resolved. Split so a missing
		// crust can be attributed to the bake, the band, or the blend.
		ALBEDO = vec3(0.0);
		EMISSION = vec3(climb_cov, max(0.0, 1.0 - climb_dist / 2.0), 0.0);
		wet_spec = 0.0;
		climb_spec = 0.0;
	}
	// Mode 21 keeps cut fragments alive so they can be painted below — the cut
	// VOLUME is otherwise invisible by construction.
	bool clip_cut = clip_dither_should_discard(world_vertex, FRAGCOORD.xy);
	if (clip_cut && clip_debug_mode != 21) {
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
	// Mode 20: paint the iris's footprint onto the ground, so its SHAPE is
	// directly readable. Sampling coverage at the terrain surface gives the iris
	// as it lands on the world — for the disk shape, a circle on screen and so an
	// ellipse stretched away from the camera once it falls on the ground; for the
	// fan, the blob itself. Green inside, fading across the dithered edge. Via
	// EMISSION so it survives dim/interior lighting like mode 13 does.
	if (clip_debug_mode == 20 && clip_iris_active()) {
		float iris_debug = clip_iris_coverage(world_vertex);
		ALBEDO = vec3(0.0);
		EMISSION = mix(EMISSION, vec3(0.05, 0.9, 0.3), iris_debug * 0.8);
	}
	// Mode 21: paint the cut VOLUME in red rather than removing it. Mode 20
	// gives the plan view; this gives the elevation, which is the half you
	// cannot infer from a footprint — red climbs a wall from the clip plane
	// upward, so the cut reads as a solid. The dither stipples the edge too.
	if (clip_debug_mode == 21 && clip_cut) {
		ALBEDO = vec3(0.0);
		EMISSION = vec3(0.9, 0.05, 0.15);
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
	// Worn-ledge sheen, on the same shadow gate. Amplitude is already folded
	// into climb_spec by climb_mark_gloss, so there is no second strength here.
	DIFFUSE_LIGHT += sun_color * sun_intensity * climb_spec * light_gate;
}
