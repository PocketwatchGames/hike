shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, blend_mix, cull_disabled;
// The clip-cap pipeline writes 1 to the stencil buffer for pixels that
// represent cut-away ceilings (see voxel_backface_stencil.gdshader); the
// cap plane then draws only on those pixels. We want the inverse: fog
// should skip cap-stencil pixels entirely so the cutaway view stays clean.
stencil_mode read, compare_not_equal, 1;

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
uniform sampler3D fog_map : repeat_disable, filter_linear;
uniform vec3 fog_map_origin;
uniform vec3 fog_map_inv_size;

// DEPTH_TEXTURE is no longer a built-in in Godot 4.x — it's a uniform hint.
uniform sampler2D depth_tex : hint_depth_texture;

// Voxel LightMap (registered by ChunkManager). R = sun visibility [0..1],
// GBA = accumulated block light RGB (torches, fill lights, etc.). Sampled
// at each raymarch step to drive in-scattering.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;

// Sun direction / color / intensity (shared with sky + voxel shaders, set by
// SkyController and ChunkManager). sun_world_dir points FROM the sun toward
// the ground, so -sun_world_dir is "toward the sun".
global uniform vec3 sun_world_dir;
global uniform vec3 sun_color;
global uniform float sun_intensity;

// Cloud occlusion parameters (shared with sky + terrain + sprite + water
// shaders, registered by SkyController). cloud_shadow.gdshaderinc reads
// these to compute projective cloud shadows cast along the sun direction.
global uniform vec2 cloud_scroll;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
#include "res://shaders/cloud_shadow.gdshaderinc"

uniform vec3 fog_color : source_color = vec3(0.85, 0.88, 0.95);
// Defaults below are the actual tuned values for this game — if the
// material (.tres) lacks shader_parameter overrides, these produce the
// intended look. Previously the default was 4.0 (a dev placeholder) that
// only worked because the .tres overrode it to 0.05; editing the .tres
// in the Godot inspector can strip those overrides, revealing the
// mis-matched default as a fully-white screen.
uniform float fog_density : hint_range(0.0, 1.0) = 0.05;
uniform int fog_steps : hint_range(1, 64) = 48;
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

// In-scattering tuning.
// sun_shaft_intensity: multiplier on the sunlight inscatter term (raise to
//   make beams more dramatic; 0 disables shafts).
// block_halo_intensity: multiplier on the block-light inscatter term (raise
//   to make torch/lamp halos more visible; 0 disables halos).
// scatter_anisotropy: Henyey-Greenstein g. 0 = isotropic (uniform in all
//   directions), positive = forward-peaked (dramatic when looking toward
//   the sun, effectively invisible when looking away — ~25x dimmer at
//   g=0.55 for a camera pointing away from the sun).
// Default anisotropy is 0 because this game's isometric camera has a fixed
// pitch and usually does NOT face the sun: a forward-peaked phase would
// kill shaft visibility. Default shaft intensity is high (8) to compensate
// for the flat phase — isotropic scattering distributes energy over the
// full 4π steradians (factor 1/(4π) ≈ 0.08), so we need ~8x more raw
// intensity to reach the same brightness as a forward peak.
uniform float sun_shaft_intensity : hint_range(0.0, 32.0) = 8.0;
uniform float block_halo_intensity : hint_range(0.0, 32.0) = 6.0;
uniform float scatter_anisotropy : hint_range(-0.95, 0.95) = 0.0;

// Animated mote shimmer drifting through the shafts. The shaft geometry
// itself is anchored to the (static) sun occlusion in the LightMap +
// cloud pattern, so without this the beams would look frozen. Modulates
// ONLY the sun inscatter contribution — haze stays smooth and doesn't
// crawl. Carries no scattering density of its own (that's dust_density);
// this is pure visual noise on top of the shaft brightness.
//   mote_strength: 0 = no shimmer, 1 = beams vary 0..2x as motes pass through.
//   mote_scale: world-space frequency of motes (higher = finer).
//   mote_scroll: world-space velocity of the mote field (units/sec).
uniform float mote_strength : hint_range(0.0, 1.0) = 0.55;
uniform float mote_scale = 0.18;
uniform vec3 mote_scroll = vec3(0.35, 0.12, -0.25);

// World Y of the water surface. The water shader uses depth_draw_never, so
// DEPTH_TEXTURE at water pixels returns the depth of the opaque terrain
// BEHIND the water rather than the water surface itself. Without clamping,
// the fog raymarch continues past the water surface and its alpha layer
// masks the water's depth-tint, flattening the visible water. Rays that
// cross this Y going downward get their march clamped to stop at the
// water surface. Default -1e20 effectively disables the clamp.
uniform float water_level = -1e20;

// Screen-space sun shadow raymarch — shapes the shafts by the ACTUAL
// scene geometry the camera can see (trees, terrain, sprites — anything
// that writes to the depth buffer). Without this, shafts are only gated
// by the voxel LightMap's top-down BFS sun mask, which doesn't know
// about tree-sprite silhouettes, so beams never stream through canopies
// the way Tessellator-style pixel-art forests do.
//
// For each fog march sample p, step K times along to_sun, project each
// step to screen space, and compare that projected depth against the
// scene depth at the same screen position. If scene_depth shows geometry
// IN FRONT OF our step, the sun ray at p is occluded — no shaft there.
//
// Cost: K depth samples per fog march step (K*48 total per pixel at
// defaults). Keep K small and sun_shadow_distance short unless the
// scene has many tall occluders.
uniform bool sun_shadow_enabled = true;
uniform int sun_shadow_steps : hint_range(1, 16) = 6;
uniform float sun_shadow_distance : hint_range(1.0, 64.0) = 16.0;
// Reverse-Z depth comparison bias. Prevents the raymarch from "hitting"
// the occluder at the projected step itself due to FP precision near
// the ground.
uniform float sun_shadow_bias : hint_range(0.0, 0.01, 0.0001) = 0.0005;

// How much cloud occlusion contributes to the shaft gating. 0 disables
// the "shafts from clouds" effect (beams are purely shaped by scene
// geometry via the raymarch above) while leaving cloud shadows intact
// on terrain / sprite / water via cloud_shadow_ground(). 1 composes
// cloud mask with geometry occlusion multiplicatively (both block sun).
uniform float cloud_shaft_weight : hint_range(0.0, 1.0) = 1.0;

// Voxel sun-visibility threshold for shaft contribution. The LightMap's
// sun channel (lm.r) is a top-down BFS with lateral propagation at
// FALLOFF_PER_VOXEL=4, so air voxels up to ~15 voxels inside a cave
// mouth still receive partial sun via lateral BFS bleed — values like
// 0.2-0.5 in shallow cave air. Multiplied by sunShaftIntensity those
// become visible "shafts underground." This threshold remaps lm.r via
// smoothstep(threshold, 1.0, lm.r) so only voxels whose lightmap value
// is near full sun contribute to shafts. Terrain lighting still uses
// the raw lm.r — this only affects the fog shader's shaft accumulation.
uniform float shaft_sun_threshold : hint_range(0.0, 1.0, 0.01) = 0.7;

// Sharpness of the cloud mask SPECIFICALLY when used for shaft gating.
// Decoupled from the scene-wide cloud_sharpness so you can crank shaft
// contrast high (crisp beams through narrow cloud gaps) without making
// the sky dome / terrain cloud shadows hard-edged too. 0 = soft gradient,
// 1 = hard step.
uniform float cloud_shaft_sharpness : hint_range(0.0, 1.0) = 0.95;

// Half-angle (degrees from the sun axis) at which view-aligned shafts
// start fading. When the view ray is close to parallel with the sun
// direction, beams collapse into foreshortened dots / radial rays
// instead of side-on streaks — physically correct, but often reads as
// a rendering artifact to the player. Fade shaft intensity and dust
// noise structuring smoothly to zero as |dot(ray_dir, to_sun)| crosses
// cos(threshold). 90 disables the fade.
uniform float shaft_camera_fade_degrees : hint_range(0.0, 90.0, 0.1) = 60.0;

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
// 6 = visualize AVERAGE screen-space sun occlusion along the march ray.
//     Black = fully sunlit, white = fully shadowed. Tree/terrain shadow
//     silhouettes should be visible as darker bands in the air.
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
uniform int debug_mode : hint_range(0, 8) = 0;

const float INV_4PI = 0.0795774715459;

// Henyey-Greenstein phase function. g > 0 is forward-scattering; the shaft
// lighting gets a strong boost when the view ray is aligned with the inbound
// sun direction (i.e. looking toward the sun through dust).
float phase_hg(float cos_theta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cos_theta;
    // max() guards against a negative base under the fractional power when
    // g * cos_theta slightly overshoots 1 due to fp error.
    return INV_4PI * (1.0 - g2) / (pow(max(denom, 1e-4), 1.5));
}

// Screen-space sun occlusion raymarch. Returns fraction [0..1] of sun-march
// samples that land behind visible scene geometry (trees, terrain, sprites
// — anything in depth_tex). 0 = fully sunlit, 1 = fully shadowed.
//
// The projection/view matrices are fragment-stage built-ins in Godot's
// spatial shader, so this helper takes them as parameters instead of
// reading them at file scope.
float sun_occlusion(vec3 p, vec3 to_sun, mat4 proj, mat4 view)
{
    if (!sun_shadow_enabled) { return 0.0; }
    float step_len = sun_shadow_distance / float(sun_shadow_steps);
    float occluded = 0.0;
    for (int i = 1; i <= sun_shadow_steps; i++)
    {
        vec3 sp = p + to_sun * step_len * float(i);
        vec4 clip = proj * view * vec4(sp, 1.0);
        if (clip.w <= 0.0) { continue; }
        vec3 ndc = clip.xyz / clip.w;
        vec2 uv = ndc.xy * 0.5 + 0.5;
        if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0))))
        {
            continue;
        }
        float scene_z = texture(depth_tex, uv).r;
        // Reverse-Z: 1 = near plane, 0 = far plane. If scene_z > sp's
        // ndc.z, the scene's visible surface at that screen location is
        // closer to the camera than our sun-ray sample — approximates
        // "something opaque sits between p and the sun along this ray
        // segment", so p is shadowed.
        if (scene_z > ndc.z + sun_shadow_bias)
        {
            occluded += 1.0;
        }
    }
    return occluded / float(sun_shadow_steps);
}

// Cheap triple-sine value noise for the animated mote shimmer. Not a true
// noise (has visible periodicity at low frequencies), but inexpensive
// per-sample and reads as drifting motes once multiple axes are combined.
// Keeps the march loop affordable at 24-48 steps per pixel.
float mote_noise(vec3 p)
{
    float n1 = sin(p.x * 1.3) * sin(p.y * 1.7 + 0.5) * sin(p.z * 1.1 - 0.3);
    float n2 = sin(p.x * 2.7 + 1.1) * sin(p.y * 2.3 - 0.7) * sin(p.z * 3.1 + 1.9);
    return 0.5 + 0.5 * (n1 * 0.65 + n2 * 0.35);
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

    // Compute the dust band extent in world Y and clip the march to the
    // segment of the ray that actually passes through it. Without this,
    // the 48 march steps span ~march_dist worth of world (often 80+ m)
    // with a step size of ~2 m — step aliasing at dust-gate edges, cloud
    // transitions, and geometry-occlusion boundaries becomes visible as
    // banding inside beams. With the band clip, the same 48 steps only
    // cover the band (typically 10 m), so step size shrinks to ~0.2 m
    // and the banding effectively disappears.
    //
    // Trade-off: authored fog OUTSIDE the band no longer contributes to
    // extinction or inscatter for the haze integral. In practice, the
    // authored fog_map that WorldGen places is in the water-mist band
    // already, so this usually coincides. If you later author fog above
    // the dust band, we'd need a two-pass march to preserve it.
    float band_bot = effective_surface_y;
    float band_top = band_bot + dust_band_height;
    float t_band_enter;
    float t_band_exit;
    if (abs(ray_dir.y) < 1e-6)
    {
        // Horizontal view ray — in the band for its whole length if
        // near_world.y is inside [band_bot, band_top], otherwise never.
        if (near_world.y >= band_bot && near_world.y <= band_top)
        {
            t_band_enter = 0.0;
            t_band_exit = march_dist;
        }
        else
        {
            t_band_enter = 0.0;
            t_band_exit = -1.0;
        }
    }
    else
    {
        float t_bot = (band_bot - near_world.y) / ray_dir.y;
        float t_top = (band_top - near_world.y) / ray_dir.y;
        t_band_enter = min(t_bot, t_top);
        t_band_exit = max(t_bot, t_top);
    }
    t_band_enter = max(t_band_enter, 0.0);
    t_band_exit = min(t_band_exit, march_dist);
    float band_range = max(t_band_exit - t_band_enter, 0.0);
    float step_size = (band_range > 0.0) ? band_range / float(fog_steps) : 0.0;

    // Phase values are view-direction-dependent but not per-step, so compute
    // once per fragment. cos_theta is the angle between the ray direction
    // and the direction TOWARD the sun (-sun_world_dir is the vector from
    // the receiver to the sun).
    vec3 to_sun = -normalize(sun_world_dir);
    float cos_theta = dot(ray_dir, to_sun);
    float phase_sun = phase_hg(cos_theta, scatter_anisotropy);

    // View-alignment fade: when the view ray is close to the sun axis
    // (either facing sun or anti-facing), beams foreshorten into radial
    // dots instead of streaks. Fade the shaft + dust-noise contribution
    // smoothly from full (at the threshold angle) to zero (at 0°
    // alignment, i.e. looking directly along the sun ray). Uses
    // |cos_theta| so both facing-sun and facing-away directions fade.
    float shaft_camera_fade_cos = cos(radians(shaft_camera_fade_degrees));
    float align = abs(cos_theta);
    float align_fade = smoothstep(shaft_camera_fade_cos, 1.0, align);
    float shaft_align_mod = 1.0 - align_fade;
    float eff_shaft_intensity = sun_shaft_intensity * shaft_align_mod;
    float eff_dust_noise_strength = dust_noise_strength * shaft_align_mod;
    // Block lights scatter isotropically — no preferred direction relative
    // to the view ray, so halos stay round regardless of camera facing.
    float phase_block = INV_4PI;

    float tau = 0.0;            // optical depth (integrated density * fog_density)
    float transmittance = 1.0;  // exp(-tau), tracked incrementally for inscatter weighting
    vec3 inscatter = vec3(0.0);
    // For debug mode 4: average sun mask seen along the ray.
    float sun_mask_sum = 0.0;
    float sun_mask_samples = 0.0;
    // For debug mode 6: average geometry-shadow occlusion along the ray.
    float sun_occ_sum = 0.0;
    float sun_occ_samples = 0.0;
    // For debug modes 7/8: track peak shaft contribution magnitude + where
    // (world Y) along the ray it occurred, so we can visualize which pixels
    // get shafts and at what altitude the band actually delivers.
    float peak_shaft = 0.0;
    float peak_shaft_y = 0.0;

    // No clip-based skip. Previous attempts to suppress above-clip fog
    // samples when the cutaway is active all produced a visible difference
    // between clip-on and clip-off states — either a horizontal fade-line
    // in the sky at the clip altitude, or outdoor views losing fog the
    // moment the player ducked under an overhang.
    //
    // The design constraint is "dust is weather-driven and uniform across
    // altitudes" AND "fog should read the same regardless of clip state."
    // Those two together forbid the skip: uniform dust above the clip is
    // real scattering medium that contributes equally whether or not the
    // clip happens to be active. With dust_density kept small (~0.005),
    // the subtle haze above-clip air adds to a cutaway-revealed interior
    // view reads as atmosphere rather than a visible boundary.

    // NOTE: no per-pixel jitter on the step offset. It would break step
    // aliasing the standard way (Monte Carlo) but with the low-res
    // SceneViewport (480x270) and no bilateral blur pass downstream, the
    // jitter becomes visible as chunky 4x4 noise blocks after upscale.
    // Rely on low moteStrength + ground fade to hide any stripe patterns
    // instead; reduces noise at the cost of mild aliasing we can't see
    // through the pixel-art quantization anyway.
    // fog_enabled now ONLY gates authored fog below, not the whole march.
    // Dust + shaft inscatter stays live so you can see beams on a
    // fog-free baseline.
    float authored_scale = fog_enabled ? fog_density : 0.0;

    if (band_range > 0.0)
    {
        for (int i = 0; i < fog_steps; i++)
        {
            // t is absolute along the full view ray (0 at near plane, up
            // to march_dist at surface). Keeping it in that coordinate
            // space means the downstream shaft_ground_fade math based on
            // t / march_dist still does the right thing for pixels whose
            // band exits at the terminating surface.
            float t = t_band_enter + (float(i) + 0.5) * step_size;
            vec3 p = near_world + ray_dir * t;
            vec3 uvw = (p - fog_map_origin) * fog_map_inv_size;
            if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
            {
                continue;
            }
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
            float h_above_ref = p.y - effective_surface_y;
            float dust_gate = 1.0 - clamp(h_above_ref / max(dust_band_height, 0.01), 0.0, 1.0);

            // Fine-scale dust noise — two 2D samples of the cloud noise
            // texture at perpendicular scrolls and slightly different
            // scales, averaged then remapped via threshold/sharpness. The
            // perpendicular-scroll pair gives "evolving" motion rather
            // than a single drift direction — closer to the ambient-air
            // turbulence feel than plain scrolling.
            //
            // SAMPLE XZ IS SUN-PROJECTED: we project p along the sun
            // direction to a fixed reference altitude (cloud_altitude)
            // and use that XZ. Points stacked along a sun ray share the
            // same projected XZ → dust pockets form columns aligned with
            // the sun instead of vertical air columns, so beams streak
            // in the sun direction rather than appearing as vertical
            // on-screen stripes in an iso view.
            float dust_proj_t = (cloud_altitude - p.y) / max(to_sun.y, 0.15);
            vec2 dust_xz = p.xz + to_sun.xz * dust_proj_t;
            vec2 scroll_a = dust_noise_scroll * TIME;
            vec2 scroll_b = vec2(-dust_noise_scroll.y, dust_noise_scroll.x) * TIME * 0.7;
            vec2 dust_uv_a = dust_xz * dust_noise_scale + scroll_a;
            vec2 dust_uv_b = dust_xz * dust_noise_scale * 1.3 + scroll_b;
            float dn = (texture(cloud_noise_tex, dust_uv_a).r + texture(cloud_noise_tex, dust_uv_b).r) * 0.5;
            float dust_band_width = max(1e-4, 1.0 - dust_noise_sharpness);
            float dust_pattern = smoothstep(dust_noise_threshold, min(dust_noise_threshold + dust_band_width, 1.0), dn);
            // Scale by 2 so at strength=1, dense pockets can double the
            // dust density while sparse gaps drop it to zero — preserves
            // average dust on the scene.
            float dust_mod = mix(1.0, 2.0 * dust_pattern, eff_dust_noise_strength);

            float authored = texture(fog_map, uvw).r * authored_scale;
            float sigma_t = authored;
            float sigma_s = authored + dust_density * dust_gate * dust_mod;
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
            vec3 L = vec3(0.0);
            vec3 luvw = (p - light_map_origin) * light_map_inv_size;
            if (all(greaterThanEqual(luvw, vec3(0.0))) && all(lessThanEqual(luvw, vec3(1.0))))
            {
                vec4 lm = texture(light_map, luvw);

                // Animated mote shimmer modulates the sun shaft term only.
                // Motes drift in world space so the beams come alive without
                // the ambient haze crawling with them. Centered at 1.0 so
                // the average shaft brightness stays independent of strength.
                vec3 mp = p * mote_scale + mote_scroll * TIME;
                float dust = mix(1.0, 2.0 * mote_noise(mp), mote_strength);

                // Sun gating: combine two occlusion sources.
                //   1. Cloud mask (atmospheric, low-frequency) — soft
                //      drifting variation from the cloud noise texture,
                //      weighted by cloud_shaft_weight so weather can
                //      disable it without removing cloud shadows from
                //      terrain lighting (those use cloud_shadow_ground
                //      on the other shaders).
                //   2. Geometry occlusion (screen-space raymarch toward
                //      the sun, samples depth_tex) — sharp beams shaped
                //      by trees / terrain / sprites actually blocking
                //      the sun along the ray at p.
                // Both combine multiplicatively — either can fully
                // shadow the sample.
                float cloud_mask = cloud_shadow_shafts(p, TIME, cloud_shaft_sharpness);
                float sun_through_clouds = mix(1.0, 1.0 - cloud_mask, cloud_shaft_weight);
                float sun_occ = sun_occlusion(p, to_sun, PROJECTION_MATRIX, VIEW_MATRIX);
                float sun_through_geom = 1.0 - sun_occ;
                float sun_through = sun_through_clouds * sun_through_geom;

                // Remap lm.r through the shaft threshold so partial-sun
                // cave voxels (lateral BFS bleed past cave mouths)
                // don't produce shafts. At threshold=0 this is a no-op
                // (raw lm.r used). At 1 only fully-lit voxels contribute.
                float voxel_sun = smoothstep(shaft_sun_threshold, 1.0, lm.r);
                vec3 sun_radiance = voxel_sun * sun_intensity * sun_color * sun_through;
                L += sun_radiance * phase_sun * eff_shaft_intensity * dust;
                L += lm.gba * phase_block * block_halo_intensity;

                sun_mask_sum += voxel_sun * sun_through;
                sun_mask_samples += 1.0;
                sun_occ_sum += sun_occ;
                sun_occ_samples += 1.0;
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
            vec3 sample_inscatter = shaft_fade * transmittance * L * sigma_s * step_size;
            inscatter += sample_inscatter;
            // Debug: track this ray's peak shaft contribution for debug
            // modes 7 & 8. max-channel is close enough to luma for these.
            float sample_shaft = max(max(sample_inscatter.r, sample_inscatter.g), sample_inscatter.b);
            if (sample_shaft > peak_shaft)
            {
                peak_shaft = sample_shaft;
                peak_shaft_y = p.y;
            }
            transmittance *= exp(-seg_tau);
            tau += seg_tau;
        }
    }

    if (debug_mode == 1)
    {
        // Normalize surface Y across the fog grid's vertical extent.
        float ny = (surface_world.y - fog_map_origin.y) * fog_map_inv_size.y;
        ALBEDO = vec3(clamp(ny, 0.0, 1.0));
        ALPHA = 1.0;
    }
    else if (debug_mode == 2)
    {
        vec3 uvw = (surface_world - fog_map_origin) * fog_map_inv_size;
        if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
        {
            ALBEDO = vec3(0.0, 0.0, 0.5);
        }
        else
        {
            ALBEDO = vec3(texture(fog_map, uvw).r);
        }
        ALPHA = 1.0;
    }
    else if (debug_mode == 3)
    {
        // Show pure inscatter over black — easy to see shaft geometry and
        // halo placement without haze on top.
        ALBEDO = inscatter;
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
    else if (debug_mode == 6)
    {
        // Average screen-space geometry occlusion along the ray. Tree and
        // terrain silhouettes should appear as bright bands (shadowed air)
        // between dark bands (sunlit air). Uniform black = raymarch is
        // finding no occluders (check sun_shadow_enabled, depth_tex wiring).
        // Uniform white = everything reads as occluded (bias too low, or
        // depth reconstruction broken).
        float avg = sun_occ_samples > 0.0 ? sun_occ_sum / sun_occ_samples : 0.0;
        ALBEDO = vec3(avg);
        ALPHA = 1.0;
    }
    else if (debug_mode == 7)
    {
        // Shaft-intensity heatmap: total inscatter magnitude per pixel.
        // Black = no shaft, progressing through blue → cyan → green →
        // yellow → red → white as brightness grows. Quick-pick ramp, not
        // perceptually uniform, but good for eyeballing shaft coverage.
        float m = max(max(inscatter.r, inscatter.g), inscatter.b);
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
    else
    {
        // Composite haze + inscatter through blend_mix:
        //   output = scene * (1 - ALPHA) + ALBEDO * ALPHA
        //   we want: output = scene * transmittance + haze + inscatter
        //
        // Setting ALPHA = 1 - transmittance handles haze cleanly, but when
        // transmittance is near 1 (e.g. authored fog disabled, dust only
        // scattering without extinction), ALPHA → 0 and ALBEDO * 0 wipes
        // out the inscatter contribution — shafts disappear.
        //
        // Fix: also include inscatter luma in ALPHA so it survives the
        // multiply. The price is a slight over-attenuation of the scene
        // behind bright shafts (scene gets multiplied by (1 - inscatter_luma))
        // but for modest shaft magnitudes this reads as "shaft overlays
        // the scene" which is what we want anyway.
        float alpha_haze = 1.0 - transmittance;
        vec3 haze = fog_color * alpha_haze;
        float inscatter_luma = max(max(inscatter.r, inscatter.g), inscatter.b);
        float alpha = max(alpha_haze, inscatter_luma);
        vec3 total = haze + inscatter;
        ALBEDO = total / max(alpha, 1e-4);
        ALPHA = clamp(alpha, 0.0, 1.0);
    }

    } // end if (fog_volumetric_enabled)
}
