shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, blend_premul_alpha, cull_disabled;

// Screen-space volumetric fog + single-scattering light transport. Fullscreen
// quad in NDC that reconstructs each pixel's world position from DEPTH_TEXTURE,
// raymarches from the camera's near plane to that surface, accumulates fog
// density AND in-scattered sunlight / block light sampled from the voxel
// LightMap, and composites the result over the scene.
//
// Light shafts ("god rays") emerge because the sun-visibility channel of the
// LightMap is 0 inside terrain shadow volumes and >0 where sun reaches — so
// the inscatter integral brightens fog along the ray only where it's in
// direct sun, producing the diagonal beams that arc through gaps in the
// terrain. Point light halos fall out of the same loop via the block light
// channels.
//
// Uses near-plane reconstruction as the ray origin (rather than the camera
// focal point) so the same code handles perspective AND orthographic
// projections — ortho rays are parallel and start on the near plane.
//
// Godot's own volumetric fog pipeline is skipped because it requires a
// perspective camera; this shader is projection-agnostic.

// Per-material uniforms (not globals) — this shader is the only consumer of
// the fog texture, so scoping it locally keeps the editor's material preview
// working: global uniforms declared in a shader but registered at runtime
// error at editor load, which blocks inspector access to the material.
// ChunkManager sets all three from C# once the FogMap is built.
uniform sampler3D fog_map : repeat_enable, filter_linear;
uniform vec3 fog_map_origin;
uniform vec3 fog_map_inv_size;

// DEPTH_TEXTURE is no longer a built-in in Godot 4.x — it's a uniform hint.
uniform sampler2D depth_tex : hint_depth_texture;

// Voxel LightMap (registered by ChunkManager). R = sun visibility [0..1],
// GBA = accumulated block light RGB (torches, fill lights, etc.). Sampled
// at each raymarch step to drive in-scattering.
global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
// sky_exposure_map (+ origin / inv_size) is declared by cloud_shadow.gdshaderinc
// below, which this shader includes — do not redeclare it here. R = how much open
// sky is directly above a voxel [0..1]: 1 under open sky, 0 under solid overhead
// cover. The sun shaft samples it at the CLIP PLANE over the backdrop's column
// (not at the surface) to detect floor revealed by THIS cutaway: a low reading at
// the cut means the clip removed solid that sits right above this floor.
// Sampling at the clip height (rather than the surface) ignores cover BELOW the
// clip — tree canopy, distant terrain — so only the genuinely clipped interior fades.
// Ceiling cutaway altitude + animation. Voxel/sprite shaders discard
// fragments above camera_clip (the cut-away ceiling) so the player sees into
// indoor spaces from above. We treat above-clip air as occluded (the cut
// voxels are still solid terrain). The cutaway doesn't move camera_clip
// smoothly — it SNAPS, and animates the dissolve via camera_clip_prev /
// camera_clip_blend + a player-centered growth iris. clip_dither.gdshaderinc
// turns those into a smooth clip_visibility_smooth() so the shafts dissolve
// in sync with the ceiling instead of popping. Default 1e+20 = outdoor.
//
// camera_clip is only the BASE plane, so nothing here compares a height against
// it directly: the iris cuts a second, lower plane inside a player-centred
// region, and clip_height_at(p) is the height actually in force at a point.
// Reading the base alone left the air the iris had opened classified as sealed
// interior, so shafts and haze kept their outdoor strength over a room the player
// could already see into and snapped the instant the base latched down behind
// them.
global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"

// Sun direction / intensity (shared with sky + voxel shaders, set by
// SkyController and ChunkManager). sun_world_dir points FROM the sun toward
// the ground, so -sun_world_dir is "toward the sun". Shaft radiance uses
// the per-material `shaft_color` below rather than the `sun_color` global —
// shafts track the authored moon shaft color at night independent of the
// primary-light color the rest of the scene uses.
global uniform vec3 sun_world_dir;

// Bird's-eye overlook reveal. While the player is perched in a climbable tree
// the camera zooms far out and the backdrop chunks stream in over the fly-up;
// this radial curtain fades any ground beyond overlook_reveal_radius (XZ
// distance from overlook_reveal_center) to full fog_color haze, so chunks that
// aren't meshed yet are hidden behind atmosphere. GameClient rides the radius
// outward along the chunk-load frontier, so the haze burns off exactly as fast
// as the world fills. Default radius (1e20, set in project.godot / ChunkManager)
// keeps the curtain disabled for all normal play. Sky + not-yet-loaded pixels
// reconstruct to the far plane and read as "far" → curtained, which is the
// mechanism that masks the un-streamed ring.
global uniform vec3 overlook_reveal_center;
global uniform float overlook_reveal_radius;
global uniform float overlook_reveal_softness;

// Cloud occlusion parameters (shared with sky + terrain + sprite + water
// shaders, registered by SkyController). cloud_shadow.gdshaderinc reads
// these to compute projective cloud shadows cast along the sun direction.
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

uniform vec3 fog_color : source_color = vec3(0.85, 0.88, 0.95);
// Defaults below are the actual tuned values for this game — if the
// material (.tres) lacks shader_parameter overrides, these produce the
// intended look. Previously the default was 4.0 (a dev placeholder) that
// only worked because the .tres overrode it to 0.05; editing the .tres
// in the Godot inspector can strip those overrides, revealing the
// mis-matched default as a fully-white screen.
uniform float fog_density : hint_range(0.0, 1.0) = 0.05;
// Uniform whole-scene haze on TOP of the voxel fog_map — unlike fog_density
// above (which scales the per-voxel authored fog_map), this adds a
// constant density everywhere in the raymarch. Lets overcast / foggy /
// stormy weathers feel enclosed in atmosphere even where WorldGen didn't
// paint authored fog. Folds into both sigma_t (so it darkens distant
// geometry via extinction → visible haze tint) and sigma_s (so shafts
// still pick up the medium for inscatter).
uniform float ambient_fog_density : hint_range(0.0, 0.2) = 0.0;
uniform int fog_steps : hint_range(1, 128) = 48;
// Target world-space spacing between raymarch samples. The march always
// takes AT LEAST fog_steps, but ADDS samples for deeper dust bands so the
// actual step size never exceeds this. Bounding the step SIZE (not just
// the count) is what keeps the Riemann-sum "platter" banding sub-pixel
// regardless of how deep the band is. Lower = smoother beams, more fill
// cost. (V1 only.)
uniform float shaft_step_size : hint_range(0.05, 2.0, 0.01) = 0.35;
uniform float fog_max_distance = 100.0;
// Toggles the AUTHORED voxel fog contribution only. Dust, shafts, and
// halos keep working when this is off — they come from dust_density
// and block-light accumulation which are independent of the fog_map.
// Useful to isolate "just the shafts" visually without touching any
// other values. To disable ALL volumetric contribution, set dust_density
// to 0 AND fog_enabled to false.
uniform bool fog_enabled = true;

// Master kill-switch for the entire volumetric fog pass (haze AND shafts
// AND halos AND dust). When false the fragment returns transparent before
// any of the raymarching / texture sampling work runs — the scene draws
// unmodified. Use for low-spec machines or to isolate volumetric cost
// during profiling.
uniform bool fog_volumetric_enabled = true;

// Uniform atmospheric dust — the scattering medium that god rays need in
// order to be visible. Dustiness is a weather/region property, not tied
// to altitude directly, but the dust layer IS height-bounded relative to
// the LOCAL GROUND (surface_world.y for each pixel): dust exists within
// dust_band_height meters of the ground and fades to 0 above that.
//
// Keeping dust near the ground is what makes shafts read as grounded
// beams instead of a flat screen-space overlay — Tessellator-style
// beams only appear in the air column close to the terrain the ray
// terminates at, not throughout the whole view volume.
//
// Distinct from the authored fog_map (per-voxel volumetric fog, placed
// by WorldGen / editor, weather-scaled via fog_density). Authored fog
// is NOT height-gated — it lives wherever it's placed.
//
// Generic distance haze (screen-depth-based, atmosphere-color tint) is
// handled by Godot's built-in Environment fog; we don't try to replicate
// that here, and built-in fog doesn't interact with our shafts.
uniform float dust_density : hint_range(0.0, 1.0) = 0.005;
// Thickness of the dust band. Within this many meters above the
// reference Y, dust_density is full; it ramps linearly to 0 above.
uniform float dust_band_height : hint_range(1.0, 64.0, 0.1) = 10.0;

// Animated fine-scale noise multiplied onto dust_density within the
// band. Parallels the cloud_* uniforms above but at ~10x higher spatial
// frequency, so dust forms localized dense pockets and sparse gaps —
// which read as additional narrow beam structure on top of whatever the
// cloud mask and geometry occlusion have already gated out.
//   strength : 0 = uniform dust (off), 1 = fully modulated (can zero
//              dust in sparse regions, double it in dense pockets).
//   scale    : world-unit → UV multiplier; higher = finer noise.
//   threshold / sharpness : same cloud-noise smoothstep remap — low
//              threshold / high sharpness produces crisp dense pockets.
//   scroll   : base drift vector. A perpendicular secondary scroll is
//              derived from this to break directional motion, so the
//              pattern evolves rather than sliding in one direction.
uniform float dust_noise_strength : hint_range(0.0, 1.0) = 0.7;
uniform float dust_noise_scale = 0.12;
uniform float dust_noise_threshold : hint_range(0.0, 1.0) = 0.4;
uniform float dust_noise_sharpness : hint_range(0.0, 1.0) = 0.5;
uniform vec2 dust_noise_scroll = vec2(0.05, 0.03);
// Pre-integrated dust-noise scroll positions (SkyController accumulates
// `dust_noise_scroll * dt` and the perpendicular companion at 0.7x each
// frame). Using a position directly avoids the `TIME * scroll` form
// that slowly loses float precision over long sessions and teleports
// the pattern whenever the scroll speed is retuned.
uniform vec2 dust_noise_offset_a = vec2(0.0);
uniform vec2 dust_noise_offset_b = vec2(0.0);

// --- Sun-shaft streak carve -----------------------------------------
// A second, COARSER + SHARPER sun-projected dust pattern multiplied into the
// per-step beam visibility (`lit`), so the shafts EVOLVE in linear streaks
// running along the shaft instead of only drifting rigidly with the wind. The
// cloud mask that shapes the shafts is a single rigidly-scrolling layer (moves
// but never reshapes); this layer's two perpendicular scrolls (offset_a vs
// offset_b drift apart) make the streaks form and dissolve.
//
// Folded in per-march-step via mix(1, pattern, occlusion) — a pure DARKEN
// (capped at 1 → no reverse-shadow blowout). The KEY to it reading as streaks
// rather than a faint uniform dampening: the pattern must be coarse enough
// (large contiguous regions) and sharp enough (hard edges) that it survives
// the band-average along the view ray. dust_streak_scale well below the halo
// dust_noise_scale (0.12) is what does that; fewer raymarch steps (lower
// fog_steps / larger shaft_step_size) sharpen it further by averaging less.
//   occlusion : carve strength. 0 = off, 1 = dense regions fully erase the beam.
//   scale     : world→UV multiplier. LOWER = larger contiguous streaks.
//   threshold / sharpness : smoothstep remap. High threshold = sparser, more
//               dramatic dark gaps; high sharpness = hard streak edges.
uniform float dust_shaft_occlusion : hint_range(0.0, 1.0) = 0.85;
uniform float dust_streak_scale = 0.04;
uniform float dust_streak_threshold : hint_range(0.0, 1.0) = 0.5;
uniform float dust_streak_sharpness : hint_range(0.0, 1.0) = 0.9;

// In-scattering tuning.
// sun_shaft_intensity: the global sun-wash intensity (= dust × cloudCover²,
//   pushed by SkyController) — how strongly shadowed air columns are darkened.
// block_halo_intensity: multiplier on the block-light halo inscatter (raise
//   to make torch/lamp halos more visible; 0 disables halos).
uniform float sun_shaft_intensity : hint_range(0.0, 32.0) = 8.0;
uniform float block_halo_intensity : hint_range(0.0, 32.0) = 6.0;
// Per-voxel authored-fog → shaft boost. The band-averaged fog_map density is
// multiplied by this and ADDED to the global wash, so painted fog volumes
// (mist over water, dusty hollows) get locally denser shafts. 0 = off.
uniform float fog_shaft_gain : hint_range(0.0, 4.0) = 1.0;

// Skylight carve-out for the above-clip suppression below.
//
// Above-clip air is normally treated as fully occluded, because it is usually
// the solid the cutaway removed. But a hole in a roof (or a cave mouth) is air
// that genuinely sees sky, and killing it erases a shaft exactly where it
// should be strongest — at its source — the moment the player steps indoors and
// the clip snaps down to the ceiling. sky_exposure_map already distinguishes
// the two: it is a top-down, non-leaky field, 0 under solid cover and high
// under a real opening, and roofs write into the column scan that fills it.
// 0 = old behaviour (suppress everything above clip).
uniform float indoor_shaft_sky_gain : hint_range(0.0, 1.0) = 1.0;
// Day↔night fade for the shafts (0 at the day/night boundary, up to 1 with the
// sun/moon high). SkyController already folds this into sun_shaft_intensity for
// the weather-driven wash; it's passed separately so the authored-fog boost
// above fades on the same curve instead of carving beams through the night.
uniform float shaft_day_factor : hint_range(0.0, 1.0) = 1.0;

// --- Sun-wash (carve-by-darkening) controls -------------------------
// TEST gate (CVar shaft_light_floor): fade the darkening out where the air
// column has near-zero sunlight, so genuinely lightless areas get no wash.
// The darkening is multiplied by smoothstep(0, this, lit_frac). 0 = off.
uniform float shaft_light_floor : hint_range(0.0, 1.0) = 0.0;
// How dark fully-shadowed air gets, at full global wash. This is the main
// CONTRAST knob: the scene is the lit baseline and shadowed columns are
// multiplied down by up to this much. Expands value range, never blows out.
uniform float wash_shadow_darkness : hint_range(0.0, 1.0) = 0.5;
// Bounded warm tint ADDED in the lit gaps (sun/dust color in the beams).
// Keep small — this is the only additive term, so it's the one that could
// brighten. 0 = beams are pure un-darkened scene; ~0.15 = subtly colored.
uniform float wash_tint_strength : hint_range(0.0, 1.0) = 0.15;
// Clip-revealed-floor fade for the sun shaft. Sky exposure is sampled at the
// CLIP PLANE above the backdrop's column and remapped through
// smoothstep(shaft_sky_cover_min, shaft_sky_cover_max, .): a covered reading
// (solid above the clip) fades the beam over that floor, an open reading leaves
// it. Per-pixel on the backdrop, so only the part of a beam over revealed floor
// vanishes. Sky exposure is TOP-DOWN but beams arrive ANGLED, so keep `max` low
// — beams persist into partial cover and only die as cover nears total.
// min = exposure at/below which the beam is gone; max = at/above which it's full.
uniform float shaft_sky_cover_min : hint_range(0.0, 1.0, 0.01) = 0.0;
uniform float shaft_sky_cover_max : hint_range(0.0, 1.0, 0.01) = 0.25;

// Color tint on shaft inscatter. SkyController crossfades between the
// day sun color and weather.moonShaftColor based on which source is
// currently above the horizon, so this tracks the "real" shaft source
// rather than the primary-light global `sun_color` (which lighting
// keys off and which may itself already be blended with sunset). Same
// geometry, swapped palette: day beams read warm, moon beams read
// whatever mood the weather author picked.
uniform vec3 shaft_color : source_color = vec3(1.0, 0.96, 0.88);

// World Y of the water surface. The water shader uses depth_draw_never, so
// DEPTH_TEXTURE at water pixels returns the depth of the opaque terrain
// BEHIND the water rather than the water surface itself. Without clamping,
// the fog raymarch continues past the water surface and its alpha layer
// masks the water's depth-tint, flattening the visible water. Rays that
// cross this Y going downward get their march clamped to stop at the
// water surface. Default -1e20 effectively disables the clamp.
uniform float water_level = -1e20;

// Absolute world-Y at which the dust CEILING sits when > -1e9. Dust is
// at full density everywhere below (ceiling - dust_band_height) and fades
// linearly to 0 at the ceiling; there is no lower cutoff, so valleys,
// caves, and anything beneath the ceiling see dust (and therefore shafts).
// ALL pixels — tree, terrain, water — share the same ceiling in this
// mode, so their marches sample cloud_shadow_shafts at the same Y-range
// along a given sun ray and the tree-vs-terrain brightness pop goes
// away.
// Left at default (-1e20) the shader falls back to the per-pixel legacy
// behavior: ceiling = surface_y + dust_band_height (i.e. dust is a band
// just above each pixel's own surface).
uniform float dust_reference_y = -1e20;

// Sharpness of the cloud mask SPECIFICALLY when used for shaft gating.
// Decoupled from the scene-wide cloud_sharpness so you can crank shaft
// contrast high (crisp beams through narrow cloud gaps) without making
// the sky dome / terrain cloud shadows hard-edged too. 0 = soft gradient,
// 1 = hard step.
uniform float cloud_shaft_sharpness : hint_range(0.0, 1.0) = 0.95;

// Near-surface inscatter fade. Dust peaks at the surface (dust_gate = 1
// when h_above_ground = 0) — without this fade, the march samples right
// at the terminating ground contribute full brightness inscatter,
// blanketing the ground pixel with shaft color instead of letting the
// terrain shader's own lighting show through. Expressed as the fraction
// of march_dist over which inscatter fades to 0 as it approaches the
// surface. 0 = no fade (shafts paint onto ground); 0.5 = fades over the
// last half of the march.
uniform float shaft_ground_fade : hint_range(0.0, 0.5, 0.01) = 0.2;

// 0 = normal fog render.
// 1 = visualize reconstructed surface world Y as grayscale (black = lowest Y
//     in the fog grid, white = highest). Ground voxels should read as dark,
//     cliff tops as mid, sky as the brightest value (far plane).
// 2 = visualize the fog density directly sampled at the surface (no march).
//     Bright stripes should form over water/ground where fog lives.
// 3 = visualize accumulated in-scatter only (haze disabled). Shafts should
//     appear as bright diagonal bands; halos as colored blobs around lights.
// 4 = visualize the AVERAGE sun mask along the march ray. Uniform gray =
//     no shadow variation (no shafts possible); stripes/bands = sun-shadow
//     contrast along the ray (shafts will form here).
// 5 = visualize cloud_shadow() sampled directly at the reconstructed surface
//     position (no march). Black = cloud-free, white = fully shadowed.
//     Should show a bold drifting noise pattern. If uniform black / gray,
//     the cloud globals aren't reaching the shader (suspect: stale shader
//     cache — delete .godot/shader_cache/ and relaunch).
// 7 = shaft intensity per pixel as a HEATMAP of total inscatter magnitude
//     (black = no shaft, blue → green → yellow → red → white for
//     increasing brightness). Shows which screen pixels actually receive
//     shaft contribution and how strong it is — use to verify that dust
//     is confined where you expect (e.g. band-limited areas should be
//     black, valley areas should glow).
// 8 = peak-shaft world-Y along each ray, normalized to the dust band.
//     Red channel = (peak_y - dust_reference_y) / dust_band_height
//     clamped to [0,1]. Black pixels have no shaft. Within-band peaks
//     show as RED gradient from dark (near ref_y) to bright (near
//     ref_y + band_height). If you see RED pixels above where you
//     expected the band to end, the shaft IS in the band but the
//     screen projection is fooling your eye.
// 12 = MIN raw lm.r encountered along the march. Unthresholded,
//     unaveraged — pure darkest-voxel readout. Pixel goes fully
//     black if any voxel along the view ray has zero BFS sun. Use
//     to diagnose "is the fog seeing any truly dark voxels" without
//     the ray-average or shaft-threshold noise in mode 4.
// 13 = sun-wash coverage. GREEN = lit_frac (how sunlit this air column is:
//     bright = lit beam, dark = shadowed). This is what the wash darkens
//     and tints.
uniform int debug_mode : hint_range(0, 13) = 0;

const float INV_4PI = 0.0795774715459;

// Sun-projected dust noise pattern in [0..1]. Two cloud-noise samples at
// perpendicular pre-integrated scrolls (offset_a / offset_b drift apart, so
// the field EVOLVES rather than rigidly sliding) and slightly different
// scales, averaged then remapped via threshold/sharpness. The XZ is projected
// along the sun to cloud_altitude so points stacked on one sun ray share a
// sample — pockets form sun-aligned columns (beam-aligned streaks), not
// vertical on-screen stripes. `to_sun` is passed in to avoid re-normalizing
// per march step.
//
// scale / threshold / sharpness are parameters (not the dust_noise_* globals)
// so the two callers tune independently: the halo-density modulation keeps the
// fine dust_noise_* values, while the beam-streak carve uses coarser + sharper
// dust_streak_* values — larger contiguous lit/dark regions with hard edges
// survive the band-average along the view ray and read as streaks instead of a
// faint uniform dampening.
float dust_pattern_at(vec3 world_pos, vec3 to_sun, float scale, float threshold, float sharpness)
{
    float proj_t = (cloud_altitude - world_pos.y) / max(to_sun.y, 0.15);
    vec2 dust_xz = world_pos.xz + to_sun.xz * proj_t;
    // Pre-integrated positions (SkyController side); equivalent to the old
    // `scroll * TIME` math but precision-stable across long sessions.
    vec2 uv_a = dust_xz * scale + dust_noise_offset_a;
    vec2 uv_b = dust_xz * scale * 1.3 + dust_noise_offset_b;
    float dn = (texture(cloud_noise_tex, uv_a).r + texture(cloud_noise_tex, uv_b).r) * 0.5;
    float band = max(1e-4, 1.0 - sharpness);
    return smoothstep(threshold, min(threshold + band, 1.0), dn);
}

void vertex()
{
    // QuadMesh(2,2) has VERTEX.xy in [-1, 1] at the origin; emitting those
    // as clip-space coordinates covers the whole screen regardless of where
    // the mesh sits in world space. w=1 disables perspective divide.
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment()
{
    // Default to transparent so the master kill-switch below can skip the
    // whole pass without writing ALBEDO / ALPHA at the exits. Godot's
    // fragment stage doesn't allow `return`, so we gate the entire body
    // of the function on fog_volumetric_enabled.
    ALBEDO = vec3(0.0);
    ALPHA = 0.0;
    if (fog_volumetric_enabled)
    {

    // Helpers called from fragment don't inherit the fragment-stage built-in
    // matrices, so reconstruction is inlined. Reconstructs both near-plane
    // and surface world positions from the scene depth buffer.
    float depth = texture(depth_tex, SCREEN_UV).x;
    // Godot 4's INV_PROJECTION_MATRIX expects NDC in the underlying Vulkan
    // convention (Y points down), which matches SCREEN_UV (top-left origin)
    // directly — no Y flip.
    //
    // Godot 4 also uses REVERSE-Z depth: NDC z = 1.0 is the near plane,
    // z = 0.0 is the far plane (and sky fragments read as 0.0 in depth_tex).
    // The raw depth value is already in this space, so it goes straight into
    // surface reconstruction. For the near-plane origin we have to use 1.0 —
    // not 0.0, which would grab the far plane and make the raymarch run
    // from the far plane backward toward the surface, missing the fog band
    // at normal camera altitudes.
    vec2 ndc_xy = SCREEN_UV * 2.0 - 1.0;

    vec4 near_view = INV_PROJECTION_MATRIX * vec4(ndc_xy, 1.0, 1.0);
    near_view.xyz /= near_view.w;
    vec3 near_world = (INV_VIEW_MATRIX * vec4(near_view.xyz, 1.0)).xyz;

    vec4 surface_view = INV_PROJECTION_MATRIX * vec4(ndc_xy, depth, 1.0);
    surface_view.xyz /= surface_view.w;
    vec3 surface_world = (INV_VIEW_MATRIX * vec4(surface_view.xyz, 1.0)).xyz;

    // Water is rendered with depth_draw_never, so depth_tex at water pixels
    // contains the OPAQUE UNDERWATER TERRAIN's depth — the reconstructed
    // surface_world.y can be tens of meters below sea level. Using that raw
    // value as the dust-band reference puts the band entirely underwater,
    // which keeps beams from rendering above water. Clamp the surface Y to
    // water_level so water pixels are treated as terminating at the water
    // surface for fog-shaft purposes (band above water, not under). Land
    // pixels where surface_world.y > water_level are unaffected.
    float effective_surface_y = max(surface_world.y, water_level);

    vec3 ray = surface_world - near_world;
    float ray_len = max(length(ray), 0.0001);
    float march_dist = min(ray_len, fog_max_distance);
    vec3 ray_dir = ray / ray_len;

    // Clamp the march at the water surface for downward rays that cross it.
    // The reconstructed "surface" for water pixels sits on the underwater
    // terrain; letting the march run through the water column overstates
    // fog coverage on water.
    if (near_world.y > water_level && ray_dir.y < 0.0)
    {
        float t_water = (water_level - near_world.y) / ray_dir.y;
        if (t_water > 0.0)
        {
            march_dist = min(march_dist, t_water);
        }
    }

    // Dust ceiling: dust is at full density everywhere at or below
    //   ceiling_y - dust_band_height
    // and fades linearly to 0 at ceiling_y. No lower bound — valleys,
    // caves, anywhere under the ceiling sees dust.
    //
    // Legacy per-pixel: ceiling = surface_y + dust_band_height, so the
    // pixel's dust still concentrates near its own surface (backward
    // compatible with the old band-based behavior for typical scenes).
    // Absolute: dust_reference_y > -1e9 overrides — all pixels share a
    // single global ceiling so cloud sampling is coherent across the
    // screen.
    float ceiling_y = (dust_reference_y > -1e9) ? dust_reference_y
                                                 : (effective_surface_y + dust_band_height);

    // Clip the march to the portion of the ray that sits below the
    // ceiling. Keeps the 48 steps concentrated in dust-relevant air
    // (avoids step aliasing from spreading them across a mostly-dustless
    // ray). Rays entirely above the ceiling get band_range = 0 and skip
    // the loop.
    float t_dust_enter;
    float t_dust_exit = march_dist;
    if (abs(ray_dir.y) < 1e-6)
    {
        // Horizontal ray: either always below the ceiling or always above.
        t_dust_enter = (near_world.y <= ceiling_y) ? 0.0 : -1.0;
    }
    else
    {
        float t_cross = (ceiling_y - near_world.y) / ray_dir.y;
        if (ray_dir.y < 0.0)
        {
            // Descending: if we start above the ceiling, enter at crossing.
            // Otherwise we're already below — march from near plane.
            t_dust_enter = (near_world.y > ceiling_y) ? t_cross : 0.0;
        }
        else
        {
            // Ascending: if below ceiling now, exit at crossing.
            // If already above, no below-ceiling portion.
            if (near_world.y < ceiling_y)
            {
                t_dust_enter = 0.0;
                t_dust_exit = min(t_dust_exit, t_cross);
            }
            else
            {
                t_dust_enter = -1.0;
            }
        }
    }
    t_dust_enter = max(t_dust_enter, 0.0);
    t_dust_exit = min(t_dust_exit, march_dist);
    float band_range = max(t_dust_exit - t_dust_enter, 0.0);
    // Per-pixel step count derived from a TARGET step size, so step size is
    // capped no matter how deep the band is. fog_steps is the floor; deep
    // bands add samples up to a hard cap. This bounds the "platter" banding
    // to sub-pixel without per-pixel jitter (which goes chunky at low res).
    int v1_steps = fog_steps;
    if (band_range > 0.0)
    {
        v1_steps = int(ceil(band_range / max(shaft_step_size, 0.01)));
        v1_steps = clamp(v1_steps, fog_steps, 256);
    }
    float step_size = (band_range > 0.0) ? band_range / float(v1_steps) : 0.0;
    float t_band_enter = t_dust_enter;  // name kept for downstream use

    // Direction toward the sun (-sun_world_dir points from receiver to sun),
    // used for the dust-noise sun projection below. The sun wash is a uniform
    // tint, so there's no view-direction phase function for it.
    vec3 to_sun = -normalize(sun_world_dir);
    // Block lights scatter isotropically — halos stay round.
    float phase_block = INV_4PI;

    // Two-zone accumulators. Above-clip ("outdoor" — air or
    // cutaway-clipped rock between camera and the visible scene)
    // and below-clip ("indoor" — the visible interior / cave the
    // camera is looking into) each track their own inscatter, lit
    // haze, and transmittance. Each zone's transmittance starts
    // fresh at 1.0 so its inscatter is weighted only by extinction
    // within that zone — outdoor extinction does NOT pre-attenuate
    // indoor inscatter, which is the property that lets a tall
    // foggy cave still read as fully foggy when the camera is
    // looking through cutaway-clipped rock above it.
    //
    // Composite at end of fragment: outdoor zone gets multiplied by
    // `clip_atten = exp(-rock_thickness * ROCK_OPACITY)` so its
    // contribution dies as the ray traverses cutaway rock; indoor
    // zone passes through unchanged. Then the two zones layer
    // (outdoor on top of indoor) via standard premul-alpha.
    // Shaft (sun god-ray) and halo (block-light) inscatter are kept in
    // SEPARATE accumulators per zone: only the shaft term is scaled by the
    // partial-shadow contrast gate at composite. Block-light halos are round
    // and always visible regardless of sun contrast, so they bypass the gate.
    float outdoor_tau = 0.0;
    float outdoor_transmittance = 1.0;
    vec3 outdoor_halo = vec3(0.0);
    vec3 outdoor_lit_haze = vec3(0.0);
    float indoor_tau = 0.0;
    float indoor_transmittance = 1.0;
    vec3 indoor_halo = vec3(0.0);
    vec3 indoor_lit_haze = vec3(0.0);
    // Sun-wash model: accumulate the LIT FRACTION of the air column (the
    // un-shadowed coverage), weighted by the dust band. The global wash
    // intensity/color is applied once at composite as a value-preserving tint
    // carved by this fraction — NOT additive per-step radiance.
    // lit_weighted / band_weight = average lit fraction.
    float lit_weighted = 0.0;
    float band_weight = 0.0;
    // Authored per-voxel fog density (fog_map.r) accumulated along the band,
    // band-weighted like lit. Lets WorldGen/editor-painted fog volumes drive
    // LOCAL shaft density on top of the global humidity/dust wash.
    float fog_weighted = 0.0;
    // For debug mode 4: average sun mask seen along the ray.
    float sun_mask_sum = 0.0;
    float sun_mask_samples = 0.0;
    // For debug modes 7/8: track peak shaft contribution magnitude + where
    // (world Y) along the ray it occurred, so we can visualize which pixels
    // get shafts and at what altitude the band actually delivers.
    float peak_shaft = 0.0;
    float peak_shaft_y = 0.0;
    // Average cloud_mask along the march, isolated from voxel_sun. Pure
    // cloud-projection diagnostic accumulator.
    float cloud_mask_sum = 0.0;
    float cloud_mask_samples = 0.0;
    // For debug mode 12: minimum raw lm.r along the march (unthresholded).
    // Starts at 1.0 so an in-bounds sample of 0.3 correctly sets min=0.3;
    // rays that never hit the lightmap keep min=1.0 (no dark voxels seen).
    float min_lm_r = 1.0;
    // Above-clip rock-thickness. Integrated along the march wherever the sample
    // is above the cut in force AT THAT SAMPLE (clip_height_at, so the iris's
    // lower plane counts too), weighted by `(1 - lm_r_soft) * step_size` —
    // outdoor sunlit air above the clip contributes 0 (lm ≈ 1), solid
    // cutaway-clipped rock contributes 1 per step (lm = 0), lateral
    // BFS bleed at a cave mouth contributes a fractional value.
    //
    // After the march, `clip_atten = exp(-rock_thickness * ROCK_OPACITY)`
    // multiplies the FINAL ALBEDO and ALPHA. Effect: pre-rock outdoor
    // air contributions (haze, shafts, halos) accumulated before the
    // ray entered the rock get retroactively dampened in proportion
    // to how much rock the ray traversed. Deep cave pixels have
    // ray-crossed-thick-rock → clip_atten ≈ 0 → fog goes inky black.
    // Cave-mouth pixels graze the rock edge → small rock_thickness
    // → clip_atten ≈ 1 → shafts still visible. Outdoor scenes have
    // camera_clip = +∞ so nothing is ever above clip and the
    // accumulator stays at 0 → clip_atten = 1 → unchanged.
    //
    // Replaces both an earlier binary `past_opaque` latch (reset
    // transmittance + accumulators on first lit→dark crossing — gave
    // a hard ALPHA step at the BFS termination edge) and a per-step
    // `vis = lm_r_soft` gate (didn't catch outdoor air above the
    // clipped rock, leaving sun shafts leaking onto cave fragments).
    float rock_thickness = 0.0;
    // Per-voxel "opaqueness" for the above-clip rock — exp(-1) ≈ 0.37
    // per voxel of rock, exp(-4) ≈ 0.018 after 4 voxels, so a typical
    // ceiling kills above-clip contribution to ~zero while a 1-voxel
    // graze still passes most of it.
    const float ROCK_OPACITY = 1.0;

    // NOTE: no per-pixel jitter on the step offset. It would break step
    // aliasing the standard way (Monte Carlo) but with the low-res
    // SceneViewport (480x270) and no bilateral blur pass downstream, the
    // jitter becomes visible as chunky 4x4 noise blocks after upscale.
    // Rely on the ground fade + pixel-art quantization to hide any stripe
    // patterns instead; reduces noise at the cost of mild aliasing we can't
    // see through the pixel-art quantization anyway.
    // fog_enabled now ONLY gates authored fog below, not the whole march.
    // Dust + shaft inscatter stays live so you can see beams on a
    // fog-free baseline.
    float authored_scale = fog_enabled ? fog_density : 0.0;

    if (band_range > 0.0)
    {
        for (int i = 0; i < v1_steps; i++)
        {
            // t is absolute along the full view ray (0 at near plane, up
            // to march_dist at surface). Keeping it in that coordinate
            // space means the downstream shaft_ground_fade math based on
            // t / march_dist still does the right thing for pixels whose
            // band exits at the terminating surface.
            float t = t_band_enter + (float(i) + 0.5) * step_size;
            vec3 p = near_world + ray_dir * t;
            // The cut height AT THIS SAMPLE, not the world-wide base plane. Inside
            // the iris the cut sits lower, so the air the iris opened is above the
            // clip here and below it a metre to the side — which is the whole point
            // of resolving it per sample. Falls back to camera_clip for free while
            // the iris is closed.
            float clip_here = clip_height_at(p);
            bool above_clip = p.y > clip_here;
            // Windowed fog map is addressed TOROIDALLY: sample directly and let
            // the repeat sampler do the mod. No [0,1] guard — fog_map_origin is 0
            // and fog_map_inv_size spans only the player-centric window, so a
            // guard would reject every sample more than one window from the world
            // origin, skipping the whole march and hiding all authored fog away
            // from spawn. (The bug from the windowed-volume-map refactor.)
            // model_lit samples light_map the same way. See WindowedVolumeMap.
            vec3 uvw = (p - fog_map_origin) * fog_map_inv_size;
            // Separate extinction (sigma_t) from scattering (sigma_s):
            //   sigma_t = only the authored voxel fog extinguishes the
            //             scene behind it — this is what produces the
            //             milky haze tint via transmittance.
            //   sigma_s = authored fog + ground-gated dust — BOTH scatter
            //             sunlight into the view, so dust enables shafts
            //             without tinting the scene.
            //
            // Dust is height-gated relative to the pixel's terminating
            // surface: full strength within a thin band just above the
            // ground, fading linearly to 0 at dust_band_height. This
            // concentrates shafts in the near-ground air column (the
            // Tessellator look) rather than smearing them across the
            // entire view ray.
            //
            // Physically this whole split is non-physical (scattering
            // albedo > 1, height-gated scattering medium) but visually
            // it gives "dust reveals beams near the ground" without
            // "dust makes everything look foggy".
            // Dust density as a function of altitude BELOW the ceiling.
            // p.y = ceiling: gate = 0 (no dust right at the top edge).
            // p.y = ceiling - dust_band_height: gate = 1 (full dust).
            // p.y below that: still 1 — there's no floor, dust extends
            // down indefinitely. Caps above 1 via clamp.
            float h_below_ceiling = ceiling_y - p.y;
            float dust_gate = clamp(h_below_ceiling / max(dust_band_height, 0.01), 0.0, 1.0);

            // Fine-scale dust noise for HALO density: modulates the scattering
            // medium (sigma_s) so block-light halos get denser/sparser pockets.
            float dust_pattern = dust_pattern_at(p, to_sun, dust_noise_scale, dust_noise_threshold, dust_noise_sharpness);
            // Scale by 2 so at strength=1, dense pockets can double the
            // dust density while sparse gaps drop it to zero — preserves
            // average dust on the scene.
            float dust_mod = mix(1.0, 2.0 * dust_pattern, dust_noise_strength);
            // Coarser + sharper sun-projected pattern that carves linear streaks
            // into the beam below (see dust_shaft_occlusion). Multiplied into
            // `lit` per step so the streaks are genuinely volumetric (float in
            // the air column and parallax) rather than pinned to a surface.
            float dust_streak = dust_pattern_at(p, to_sun, dust_streak_scale, dust_streak_threshold, dust_streak_sharpness);

            // Raw per-voxel authored fog density [0..1] (before the weather
            // fog_density scale) — used to boost LOCAL shaft density in fog
            // volumes. `authored` keeps the extinction-scaled value for haze.
            float fog_raw = texture(fog_map, uvw).r;
            float authored = fog_raw * authored_scale;
            // Ambient haze adds uniform extinction AND scattering — acts
            // like diluted fog everywhere the raymarch visits, letting
            // overcast / foggy / stormy feel enclosed in atmosphere
            // without needing authored fog_map volumes.
            float dust_amount_here = dust_density * dust_gate * dust_mod;
            float sigma_t = authored + ambient_fog_density;
            float sigma_s = authored + ambient_fog_density + dust_amount_here;
            if (sigma_s <= 0.0)
            {
                continue;
            }
            float seg_tau = sigma_t * step_size;

            // Sample the voxel LightMap for this world position. The sun
            // channel is already a shadow-aware mask (0 inside shadow volumes,
            // >0 where direct sun reaches), which is exactly what makes
            // shafts appear: the inscatter integrates bright only along ray
            // segments that are simultaneously in dust AND in sun.
            vec3 L_block = vec3(0.0);
            // Voxel-sun gate for haze accumulation below: haze_gate = lm_r_soft
            // (linear-filtered BFS sun mask) so unlit voxels produce zero haze.
            // Windowed light map is toroidal — always sample (the repeat sampler
            // wraps). The old [0,1] guard defaulted haze_gate to 1.0 for "out of
            // window" rays so distant haze still rendered; that's obsolete under
            // windowing (every coordinate wraps into the resident window, and the
            // march is bounded to fog_max_distance anyway). The same guard also
            // suppressed ALL shaft/halo inscatter away from the world origin
            // (same refactor bug as the fog_map guard above). Bare scope kept so
            // the diff stays minimal.
            float haze_gate = 1.0;
            vec3 luvw = (p - light_map_origin) * light_map_inv_size;
            {
                // Single linear-filtered sample. The previously-snapped
                // (nearest-neighbor) sampling collapsed the BFS sun
                // field to per-voxel steps, which read as a hard line
                // at the BFS termination edge under overhangs that
                // didn't move with time of day. Linear gives a
                // half-voxel blend; the rock_thickness clip_atten at
                // composite time does the rest of the work.
                vec4 lm = texture(light_map, luvw);
                float lm_r_soft = lm.r;

                // Accumulate above-clip rock thickness (see declaration
                // above). Outdoor air above clip has lm_r_soft ≈ 1 so
                // adds 0; rock has lm_r_soft = 0 so adds step_size per
                // step. Multiplied at composite time onto ALBEDO/ALPHA
                // so pre-rock contributions get retroactively dampened
                // when the ray ends up behind hidden rock.
                if (above_clip)
                {
                    rock_thickness += (1.0 - lm_r_soft) * step_size;
                }

                haze_gate = lm_r_soft;
                min_lm_r = min(min_lm_r, lm.r);

                // Sun gating: cloud mask (atmospheric, drifting) only. Terrain
                // AND foliage occlusion both come from the voxel light_map.R
                // (voxel_sun) below — the BFS sun mask, which lets sun leak
                // horizontally into a cave mouth / under a canopy. The old
                // screen-space depth raymarch toward the sun is gone (it was a
                // separate, expensive, view-dependent foliage approximation).
                float cloud_mask = cloud_shadow_shafts(p, TIME, cloud_shaft_sharpness);
                float sun_through = 1.0 - cloud_mask;

                cloud_mask_sum += cloud_mask;
                cloud_mask_samples += 1.0;

                // S-curve remap of the BFS sun mask. smoothstep(0, 1, x)
                // = x^2 * (3 - 2x) — same shape as the previous tunable
                // smoothstep(threshold, 1, x) at threshold=0, just with
                // the threshold knob removed since it's no longer
                // serving any tuning purpose. The retroactive
                // rock_thickness clip_atten handles cave/cutaway
                // suppression now.
                float voxel_sun = smoothstep(0.0, 1.0, lm_r_soft);
                // Sun-wash: accumulate the lit FRACTION (coverage), not added
                // radiance. lit = terrain × cloud-gap × foliage-gap, weighted
                // by the dust band and streak noise. A tree
                // here lowers `lit` (a shadow in the wash) instead of adding a
                // beam. The wash color/intensity is applied once at composite.
                // Shaft SHAPE comes from real shadows (terrain × cloud ×
                // foliage), animated by the drifting clouds — NOT the dust
                // noise, which (a) pushed lit_frac above 1 → "reverse shadow"
                // bright blobs and (b) smears into blobs once band-averaged
                // instead of reading as streaks. Dust noise stays on the haze
                // (sigma_s) only.
                // Above-clip samples are cut-away rock (still physically solid
                // terrain) — treat them as occluded so caves / indoor columns
                // read as shadowed: no shaft leaks in from the sky-lit rock the
                // cutaway hides. clip_visibility_smooth follows the cutaway's
                // growth iris, so this dissolves in sync with the ceiling
                // instead of snapping when camera_clip changes (was a pop).
                float clip_occ = clip_visibility_smooth(p);
                // Spare above-clip air that genuinely has open sky above it, so
                // a shaft through a roof hole survives the player walking in
                // under it. Cut-away rock reads 0 here, so the case this
                // suppression exists for is untouched.
                if (indoor_shaft_sky_gain > 0.0 && clip_occ < 1.0)
                {
                    vec3 se_uvw = (p - sky_exposure_map_origin) * sky_exposure_map_inv_size;
                    float p_sky = texture(sky_exposure_map, se_uvw).r;
                    clip_occ = max(clip_occ, p_sky * indoor_shaft_sky_gain);
                }
                // Carve the coarse+sharp streak pattern into beam visibility.
                // Pure darken (mix capped at 1) so it can only thin/erase a
                // beam, never brighten it (no reverse-shadow blowout).
                float lit = voxel_sun * sun_through * clip_occ * mix(1.0, dust_streak, dust_shaft_occlusion);
                lit_weighted += lit * dust_gate;
                band_weight += dust_gate;
                fog_weighted += fog_raw * dust_gate;

                L_block += lm.gba * phase_block * block_halo_intensity;

                // Diagnostic accumulators (debug modes 4 / 9).
                sun_mask_sum += voxel_sun * sun_through;
                sun_mask_samples += 1.0;
            }

            // Near-surface fade on inscatter. Dust is maximum at the
            // surface (dust_gate = 1 when h_above_ground = 0), so without
            // this the last march samples would paint the shaft color
            // directly over the ground pixel, washing out the terrain
            // lighting underneath. Fading the last `shaft_ground_fade`
            // fraction of the march keeps beams visually floating just
            // above the ground rather than blanketing it.
            //
            // Camera-side fade isn't needed anymore because `dust_gate`
            // already attenuates the near-camera portion of downward
            // ground rays (h_above_ground is large there). Sky rays get
            // full dust_gate but don't have a surface to worry about,
            // so the near-surface fade naturally idles for them.
            float t_norm = t / max(march_dist, 1e-4);
            float fade_edge = max(shaft_ground_fade, 1e-4);
            float shaft_fade = smoothstep(0.0, fade_edge, 1.0 - t_norm);

            // Inscatter uses sigma_s (scattering) weighted by transmittance
            // (attenuation of the scattered light on its way to the camera).
            // Transmittance itself only decays by sigma_t (extinction) — this
            // is the split that lets dust reveal shafts without adding haze.
            //
            // Route this step's contribution into the right zone (outdoor
            // above the cut / indoor below). Each zone uses its OWN
            // transmittance for the inscatter weight so extinction stays
            // bounded within the zone — indoor inscatter is not
            // pre-attenuated by outdoor fog, so a tall foggy cave keeps
            // its full fog density even when the camera is looking
            // through cutaway-clipped rock above it. clip_atten at
            // composite time only dampens the outdoor zone.
            float zone_transmittance = above_clip ? outdoor_transmittance : indoor_transmittance;
            // Shared inscatter weight (transmittance * scattering * length,
            // with the near-surface fade). Applied to the sun-shaft and
            // block-halo radiances separately so they can be composited with
            // different gates (shaft is contrast-gated, halo is not).
            float inscatter_weight = shaft_fade * zone_transmittance * sigma_s * step_size;
            // Block-light halos stay additive radiance (they ARE emitters).
            // The sun shaft is no longer accumulated here — it's a coverage
            // wash applied at composite via lit_frac.
            vec3 sample_halo_v = inscatter_weight * L_block;
            // Accumulate light-gated haze. sigma_t * transmittance *
            // step_size is the "per-step extinction weight" that sums
            // across the march to (1 - transmittance); multiplying by
            // haze_gate kills haze in unlit voxels. In a fully-lit
            // outdoor scene this matches the old `fog_color *
            // (1 - transmittance)` term exactly.
            vec3 sample_haze = haze_gate * fog_color * sigma_t * zone_transmittance * step_size;
            if (above_clip)
            {
                outdoor_halo += sample_halo_v;
                outdoor_lit_haze += sample_haze;
                outdoor_transmittance *= exp(-seg_tau);
                outdoor_tau += seg_tau;
            }
            else
            {
                indoor_halo += sample_halo_v;
                indoor_lit_haze += sample_haze;
                indoor_transmittance *= exp(-seg_tau);
                indoor_tau += seg_tau;
            }
        }
    }

    // --- Sun-wash coverage ------------------------------------------
    // Average lit fraction of the air column along this ray (band-weighted).
    // 0 = fully shadowed (deep canopy / cave / cloud), 1 = fully sunlit.
    float lit_frac = band_weight > 0.0 ? clamp(lit_weighted / band_weight, 0.0, 1.0) : 0.0;

    // Inscatter now carries ONLY the block-light halos; the sun shaft is a
    // coverage wash applied as a value-preserving tint at composite time.
    vec3 outdoor_inscatter = outdoor_halo;
    vec3 indoor_inscatter = indoor_halo;

    if (debug_mode == 1)
    {
        // Normalize surface Y across the fog grid's vertical extent.
        float ny = (surface_world.y - fog_map_origin.y) * fog_map_inv_size.y;
        ALBEDO = vec3(clamp(ny, 0.0, 1.0));
        ALPHA = 1.0;
    }
    else if (debug_mode == 2)
    {
        // Toroidal fog map: sample directly (repeat sampler wraps); there is no
        // out-of-bounds case under windowing.
        vec3 uvw = (surface_world - fog_map_origin) * fog_map_inv_size;
        ALBEDO = vec3(texture(fog_map, uvw).r);
        ALPHA = 1.0;
    }
    else if (debug_mode == 3)
    {
        // Show pure inscatter over black — easy to see shaft geometry and
        // halo placement without haze on top. Outdoor + indoor summed,
        // outdoor pre-attenuated by clip_atten so the diagnostic matches
        // what the live composite shows.
        float clip_atten_dbg = exp(-rock_thickness * ROCK_OPACITY);
        ALBEDO = clip_atten_dbg * outdoor_inscatter + indoor_inscatter;
        ALPHA = 1.0;
    }
    else if (debug_mode == 4)
    {
        // Average sun mask along the ray. Uniform gray = no shadow variation
        // (no shafts possible); dark bands = shadowed air that blocks shafts;
        // bright bands = sunlit air that produces shafts.
        float avg = sun_mask_samples > 0.0 ? sun_mask_sum / sun_mask_samples : 0.0;
        ALBEDO = vec3(avg);
        ALPHA = 1.0;
    }
    else if (debug_mode == 5)
    {
        // Cloud shadow sampled directly at the surface. Diagnoses the cloud
        // projection in isolation — the pattern should read as a bold drifting
        // noise field. Uniform black = cloud math returning 0 unconditionally
        // (suspect sun_world_dir or cloud_scale reading as 0, or stale cache).
        float cloud = cloud_shadow(surface_world, TIME);
        ALBEDO = vec3(cloud);
        ALPHA = 1.0;
    }
    else if (debug_mode == 7)
    {
        // Shaft-intensity heatmap: total inscatter magnitude per pixel.
        // Black = no shaft, progressing through blue → cyan → green →
        // yellow → red → white as brightness grows. Quick-pick ramp, not
        // perceptually uniform, but good for eyeballing shaft coverage.
        float clip_atten_dbg = exp(-rock_thickness * ROCK_OPACITY);
        vec3 inscatter_dbg = clip_atten_dbg * outdoor_inscatter + indoor_inscatter;
        float m = max(max(inscatter_dbg.r, inscatter_dbg.g), inscatter_dbg.b);
        float x = clamp(m, 0.0, 1.0);
        vec3 c0 = vec3(0.0, 0.0, 0.0);
        vec3 c1 = vec3(0.0, 0.0, 1.0);
        vec3 c2 = vec3(0.0, 1.0, 1.0);
        vec3 c3 = vec3(0.0, 1.0, 0.0);
        vec3 c4 = vec3(1.0, 1.0, 0.0);
        vec3 c5 = vec3(1.0, 0.0, 0.0);
        vec3 c6 = vec3(1.0, 1.0, 1.0);
        vec3 col;
        if (x < 0.166)       { col = mix(c0, c1, x / 0.166); }
        else if (x < 0.333)  { col = mix(c1, c2, (x - 0.166) / 0.166); }
        else if (x < 0.500)  { col = mix(c2, c3, (x - 0.333) / 0.166); }
        else if (x < 0.666)  { col = mix(c3, c4, (x - 0.500) / 0.166); }
        else if (x < 0.833)  { col = mix(c4, c5, (x - 0.666) / 0.166); }
        else                 { col = mix(c5, c6, (x - 0.833) / 0.166); }
        ALBEDO = col;
        ALPHA = 1.0;
    }
    else if (debug_mode == 8)
    {
        // Peak-shaft Y — where along each ray the inscatter was strongest,
        // normalized to the dust band. Helps answer "is the shaft actually
        // in the band?" directly, without eyeballing on-screen projection.
        // Red channel: (peak_y - effective_surface_y) / dust_band_height
        // clamped to [0,1]. Black = no shaft here.
        float has_shaft = step(1e-4, peak_shaft);
        float y_norm = clamp((peak_shaft_y - effective_surface_y) / max(dust_band_height, 0.01), 0.0, 1.0);
        ALBEDO = vec3(y_norm * has_shaft, 0.0, 0.0);
        ALPHA = 1.0;
    }
    else if (debug_mode == 12)
    {
        // Minimum raw lm.r along the view ray (unaveraged). A black
        // pixel means the ray passed through at least one truly-dark
        // voxel; a white pixel means the ray stayed entirely in lit
        // space. Diagnostic for "is the cave air actually dark in the
        // lightmap, or does BFS lateral bleed reach it?"
        ALBEDO = vec3(min_lm_r);
        ALPHA = 1.0;
    }
    else if (debug_mode == 13)
    {
        // Sun-wash coverage. GREEN = lit_frac (how sunlit this air column is:
        // bright = lit beam, dark = shadowed). This is what the wash darkens
        // and tints.
        ALBEDO = vec3(0.0, clamp(lit_frac, 0.0, 1.0), 0.0);
        ALPHA = 1.0;
    }
    else
    {
        // Two-zone layered composite. Outdoor (above-clip) zone is on
        // top, indoor (below-clip) zone is behind it; standard premul-
        // alpha layering treats them as two stacked translucent layers.
        //
        //   layer A (outdoor): albedo_A = clip_atten * (outdoor_lit_haze
        //                                               + outdoor_inscatter)
        //                      alpha_A  = clip_atten * (1 - outdoor_T)
        //   layer B (indoor):  albedo_B = indoor_lit_haze + indoor_inscatter
        //                      alpha_B  = 1 - indoor_T
        //
        //   ALBEDO = albedo_A + (1 - alpha_A) * albedo_B
        //   ALPHA  = 1 - (1 - alpha_A) * (1 - alpha_B)
        //
        // `clip_atten = exp(-rock_thickness * ROCK_OPACITY)` retro-
        // actively dampens ONLY the outdoor zone. Indoor accumulators
        // pass through unchanged so a tall foggy cave keeps its full
        // density even when the camera is looking down through cutaway-
        // clipped rock. clip_atten varies smoothly with how much rock
        // the ray traversed, so adjacent pixels grazing the overhang
        // silhouette differ by a fractional voxel — no hard edge.
        //
        // Each zone weighted its own inscatter by its own transmittance
        // during the march, so dropping outdoor extinction here doesn't
        // re-introduce outdoor fog behind the rock — the indoor zone
        // already reads as if outdoor fog never existed.
        float clip_atten = exp(-rock_thickness * ROCK_OPACITY);
        vec3 albedo_a = clip_atten * (outdoor_lit_haze + outdoor_inscatter);
        float alpha_a = clip_atten * clamp(1.0 - outdoor_transmittance, 0.0, 1.0);
        vec3 albedo_b = indoor_lit_haze + indoor_inscatter;
        float alpha_b = clamp(1.0 - indoor_transmittance, 0.0, 1.0);
        vec3 haze_albedo = albedo_a + (1.0 - alpha_a) * albedo_b;
        float haze_alpha = clamp(1.0 - (1.0 - alpha_a) * (1.0 - alpha_b), 0.0, 1.0);

        // --- Sun-wash composite (carve by darkening, not brightening) ---
        // The scene IS the fully-lit atmosphere; we DARKEN the shadowed air
        // columns rather than add light to the lit ones. Darkening cannot
        // blow out, EXPANDS value contrast (deeper shadows) instead of
        // depressing it, and being a neutral multiply it preserves color/
        // saturation. The lit gaps stay at full scene value and pop as beams;
        // a small bounded warm tint is added ONLY in those lit gaps for sun/
        // dust color. Both are gated by the global wash intensity
        // (sun_shaft_intensity = dust × cloudCover², from SkyController), so
        // clear skies stay clean.
        //
        // No screen read needed: under premultiplied-alpha the hardware does
        // result = ALBEDO + (1 - ALPHA)·scene. Setting ALPHA = darken makes
        // (1 - ALPHA) attenuate the scene (the darkening); ALBEDO carries the
        // additive tint. Composited over the haze layer below.
        // Local authored-fog boost: the global wash (humidity + dust + cloud +
        // cave) PLUS the per-voxel painted fog density along this pixel's band,
        // so WorldGen / editor fog volumes (mist over water, dusty hollows)
        // thicken the shafts right where the author placed them.
        float fog_frac = band_weight > 0.0 ? fog_weighted / band_weight : 0.0;
        // sun_shaft_intensity already carries the day/night fade; gate the
        // authored-fog boost with the same factor so painted fog volumes don't
        // keep carving beams across the day/night boundary and into night.
        float wash = sun_shaft_intensity + fog_shaft_gain * fog_frac * shaft_day_factor;
        float shadow = 1.0 - clamp(lit_frac, 0.0, 1.0);
        // Coverage guard: only darken where the dust band was actually
        // sampled. Without this, distant pixels beyond the loaded lightmap
        // (band_weight == 0 -> lit_frac == 0) would read as "fully shadowed"
        // and darken the sky / far terrain incorrectly.
        float coverage = clamp(band_weight, 0.0, 1.0);
        // Three cut-away suppressors, handling DIFFERENT geometry (all per-pixel
        // on the backdrop / view ray, so only the affected part of a beam goes):
        //   clip_fade  : kills shafts whose ground DESTINATION is above the
        //                clip (cut-away exterior/ceiling surfaces). Follows
        //                the cutaway growth iris so it dissolves in sync with
        //                the ceiling instead of snapping. Outdoors it's 1.
        //   clip_atten : kills shafts that sit BEHIND cut-away rock — i.e. a
        //                cave floor below the clip whose view ray traverses
        //                the (discarded but physically solid) ceiling above
        //                it. clip_fade alone returns 1 for any below-clip
        //                surface, so without clip_atten the whole visible
        //                cave interior carries full-strength beams. The haze
        //                and block-halo layers already fold clip_atten in
        //                (albedo_a above); the sun-wash refactor that moved
        //                the shaft to this composite-time tint/darken pair
        //                dropped it for the shaft. Restoring it here matches
        //                the pre-refactor behavior where shafts lived in
        //                outdoor_inscatter and were attenuated by clip_atten.
        float clip_fade = clip_visibility_smooth(surface_world);
        //   sky_gate   : fades shafts over floor REVEALED by this cutaway. Probe
        //                the sky-exposure field at the CLIP PLANE above the
        //                backdrop's column: if solid sits just above the cut
        //                there, the clip removed that ceiling and this floor is
        //                interior. Probing at clip height (not the surface)
        //                ignores cover below the clip — canopy, distant terrain —
        //                so distant/unclipped covered ground keeps its beams.
        //                Outdoors (clip = +inf) it's a no-op. The 0.5 lifts the
        //                probe just inside the clipped (discarded) region so the
        //                linear filter doesn't blend with open air below.
        //                At the cut in force over THIS column, iris included:
        //                probing the base plane found open sky above a roof the
        //                iris had already cut through, so the beams stayed at full
        //                strength over the interior it opened and vanished the
        //                moment the player stepped in and the base latched down.
        float surface_clip = clip_height_at(surface_world);
        float sky_gate = 1.0;
        if (surface_clip < 1e9)
        {
            vec3 clip_probe = vec3(surface_world.x, surface_clip + 0.5, surface_world.z);
            // Toroidal sky-exposure map: sample directly (repeat sampler wraps).
            // The probe sits over the visible (resident) backdrop column, and a
            // [0,1] guard would leave sky_gate at 1.0 (no fade) away from origin.
            vec3 se_uvw = (clip_probe - sky_exposure_map_origin) * sky_exposure_map_inv_size;
            float sky_exposure = texture(sky_exposure_map, se_uvw).r;
            float cover_hi = max(shaft_sky_cover_max, shaft_sky_cover_min + 1e-3);
            sky_gate = smoothstep(shaft_sky_cover_min, cover_hi, sky_exposure);
        }
        // sky_gate gates the BEAM only, never the darkening. The darkening is
        // the "this air is unlit" wash, so cancelling it over cutaway-revealed
        // floor makes covered ground read BRIGHTER than the open ground beside
        // it — the opposite of what cover means. Harmless while only caves
        // tripped it (their floors are barely lit anyway), but a roof stamps
        // sky exposure to 0 across its whole footprint AND presents a flat
        // soffit, so GameCamera's upward clip ray lands camera_clip exactly on
        // that voxel: probe at +0.5 hits its texel centre, sky_gate reads 0,
        // and every wall and eave under the roof popped bright the instant the
        // cutaway engaged.
        float shaft_clip = clip_fade * clip_atten;
        float beam_clip = shaft_clip * sky_gate;
        // TEST: fade the darkening out where the column has near-zero light, so
        // genuinely lightless areas aren't washed. 0 floor = off (full effect).
        float light_present = shaft_light_floor > 0.0
            ? smoothstep(0.0, shaft_light_floor, clamp(lit_frac, 0.0, 1.0))
            : 1.0;
        float shaft_darken = clamp(wash * shadow, 0.0, wash_shadow_darkness) * coverage * shaft_clip * light_present;

        // Warm wash tint, faded at the clip. It's a PARTIAL-SHADOW term:
        // lit air that sits next to shadow (a beam). lit·shadow goes to ZERO
        // in fully-lit clear air (shadow = 0) AND in fully-shadowed air
        // (lit = 0), peaking where the column is ~half occluded — so the wash
        // vanishes when it's clear and fades in with occlusion instead of
        // tinting every lit pixel. ×4 normalises lit·(1-lit) (max 0.25) so the
        // peak reaches the full wash. (Floating dust motes are now a separate
        // GPU particle system — see MoteEffect / scenes/fx/motes.tscn.)
        float beam_lit = clamp(lit_frac, 0.0, 1.0) * shadow * 4.0 * wash;
        vec3 tint = shaft_color * (beam_lit * wash_tint_strength) * beam_clip;

        // Shaft layer (darken + tint) composited OVER the haze layer via
        // premultiplied alpha.
        ALBEDO = tint + (1.0 - shaft_darken) * haze_albedo;
        ALPHA = clamp(shaft_darken + (1.0 - shaft_darken) * haze_alpha, 0.0, 1.0);

        // Overlook reveal curtain (premultiplied-alpha). Fade to opaque
        // fog_color for any surface beyond the streaming frontier so chunks
        // still loading under the bird's-eye view are masked by haze. At
        // curtain = 1: ALBEDO = fog_color, ALPHA = 1 → result is solid fog;
        // at curtain = 0 the composite above is untouched. The default radius
        // (1e20) puts curtain at 0 everywhere, so this is inert in normal play.
        float reveal_d = distance(surface_world.xz, overlook_reveal_center.xz);
        float curtain = smoothstep(overlook_reveal_radius, overlook_reveal_radius + max(overlook_reveal_softness, 0.001), reveal_d);
        ALBEDO = ALBEDO * (1.0 - curtain) + fog_color * curtain;
        ALPHA = ALPHA * (1.0 - curtain) + curtain;
    }

    } // end if (fog_volumetric_enabled)
}
