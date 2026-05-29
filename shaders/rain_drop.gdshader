shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_opaque, cull_disabled, skip_vertex_transform;

// Rain-drop particle shader (also used for splashes via a sibling material).
// Screen-space streak orientation: each particle's quad is laid out in SCREEN
// PIXELS, with its long axis along the projected velocity direction and a
// fixed pixel length/width. This keeps streak length constant regardless of
// camera angle — essential for an orthographic iso camera where foreshortened
// streaks read as a jarring "shrink-and-rotate" effect that doesn't convey
// any real forward/backward motion information.
//
// Anti-aliasing via analytical pixel coverage: the rasterized quad is
// inflated by 1 pixel on each side, and the fragment shader computes the
// current pixel's distance-from-quad-center projected onto the velocity /
// perpendicular axes (in SubViewport pixels). Coverage smoothsteps over the
// 1-pixel AA zone past each edge, giving clean sub-pixel transitions even
// for thin streaks. Without this a 1-2 px streak would flicker on/off as
// the center crossed a pixel boundary.
//
// Responsibilities:
//   - Per-fragment clip against camera_clip using a per-particle impact-Y
//     (walked in vertex via the voxel sun mask). Rain over a cut-away roof
//     vanishes; rain over open ground keeps rendering.
//   - Per-fragment clip against the non-leaky vertical sky-exposure field so
//     drops under roofs / overhangs / caves / dense canopy don't render, and
//     the visible rain edge matches where the player actually gets wet (the
//     BFS sun mask was leaking drops sideways into cave mouths).
//   - Cheap lighting: sun_color * (ambient + mask * (1-ambient)) * intensity.
//     Tracks time of day through SkyController's effective-light globals.
//   - Asymmetric trail fade: leading edge (head) opaque with AA, linear
//     motion-blur taper to the trailing tail. With particle_flag_align_y on
//     the process material the particle's +Y is velocity → the "leading"
//     side of the quad is along +velocity, physically correct for both
//     falling rain and splash sprays.
//   - particle_color.a carries the process material's color_ramp so splashes
//     fade out over lifetime; falling rain leaves color default (1,1,1,1) so
//     it's a no-op.
//
// Editor-preview fallback: light_map_inv_size is still vec3(0) when opened
// standalone (no LightMap registered); gate the clips on that so drops render
// in the preview.

global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
// Non-leaky vertical sky-exposure field (same UVW convention as light_map).
// Used to clip drops at the true overhead-cover line instead of the BFS sun
// mask, which bleeds sideways into cave mouths. See ChunkState.SkyExposure.
global uniform sampler3D sky_exposure_map : filter_linear;
global uniform vec3 sky_exposure_map_origin;
global uniform vec3 sky_exposure_map_inv_size;
global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
global uniform vec3 camera_clip_growth_center;
global uniform float camera_clip_growth_max_radius;
global uniform float camera_clip_growth_edge_softness;
#include "res://shaders/clip_dither.gdshaderinc"
global uniform vec3 sun_color;
global uniform float sun_intensity;
global uniform float sun_ambient;

uniform vec4 albedo : source_color = vec4(0.72, 0.78, 0.88, 0.45);
// Drops only render where vertical sky exposure clears this — i.e. where the
// player would actually get rained on. Mid-range to match the wetness shelter
// threshold so the visible rain edge tracks the dry/wet line under canopy and
// at cave mouths. Distinct from sun_mask_threshold (which now only shades).
uniform float sky_exposure_threshold : hint_range(0.0, 1.0) = 0.5;
// Streak dimensions in SubViewport pixels. The rasterization envelope is
// inflated by 1 pixel on each axis beyond these so analytical coverage AA
// can fade smoothly past the edge; the visual "solid core" is still this size.
uniform float streak_length_px : hint_range(1.0, 64.0) = 8.0;
uniform float streak_width_px : hint_range(0.5, 16.0) = 0.25;

varying vec3 world_pos;
varying flat float impact_y;
varying flat vec4 particle_color;
// Screen-space quad info (flat so every fragment of the same quad reads the
// same values; vertices all compute identical data).
varying flat vec2 quad_center_px;
varying flat vec2 vel_dir_px;
varying flat float quad_half_l_px;
varying flat float quad_half_w_px;

void vertex() {
    vec3 origin = MODEL_MATRIX[3].xyz;
    float particle_scale = length(MODEL_MATRIX[0].xyz);

    // Particle's local +Y axis. particle_flag_align_y makes this = velocity
    // direction, which is what we want: streaks lean along motion on screen.
    vec3 vel_dir_world = normalize(MODEL_MATRIX[1].xyz);

    // Project origin and (origin + vel_dir) to find velocity direction in
    // screen pixels. Fall back to straight-down when the projected length
    // collapses (drop moving exactly into/out of the view axis).
    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin, 1.0);
    vec4 tip_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin + vel_dir_world, 1.0);
    vec2 origin_ndc = origin_clip.xy / max(abs(origin_clip.w), 1e-6);
    vec2 tip_ndc = tip_clip.xy / max(abs(tip_clip.w), 1e-6);
    vec2 vel_px_raw = (tip_ndc - origin_ndc) * VIEWPORT_SIZE * 0.5;
    vec2 vel_dir_px_local = length(vel_px_raw) > 1e-4 ? normalize(vel_px_raw) : vec2(0.0, -1.0);
    vec2 perp_dir_px = vec2(-vel_dir_px_local.y, vel_dir_px_local.x);

    // Publish core (un-inflated) quad data for the fragment stage's coverage
    // calc. origin_px converts from NDC back to the same pixel space the
    // fragment reads via FRAGCOORD.
    vec2 origin_px = (origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE;
    quad_center_px = origin_px;
    vel_dir_px = vel_dir_px_local;
    quad_half_l_px = streak_length_px * 0.5 * particle_scale;
    quad_half_w_px = streak_width_px * 0.5 * particle_scale;

    // Rasterized footprint = core + 1 pixel AA margin on each axis. This
    // guarantees fragments fire on the pixels adjacent to the visual quad
    // so analytical coverage can fade them smoothly instead of popping.
    float rast_half_l = quad_half_l_px + 1.0;
    float rast_half_w = quad_half_w_px + 1.0;

    // Normalize VERTEX to ±0.5 corner factors regardless of mesh size /
    // QuadMesh orientation (FACE_Y vs FACE_Z). A flat quad always has one
    // of VERTEX.y/VERTEX.z at zero, so summing picks the non-zero axis.
    float vx_unit = VERTEX.x > 0.0 ? 0.5 : -0.5;
    float vy_unit = (VERTEX.y + VERTEX.z) > 0.0 ? 0.5 : -0.5;

    vec2 offset_px = vx_unit * 2.0 * rast_half_w * perp_dir_px
                   + vy_unit * 2.0 * rast_half_l * vel_dir_px_local;
    vec2 offset_ndc = offset_px * 2.0 / VIEWPORT_SIZE;
    POSITION = vec4((origin_ndc + offset_ndc) * origin_clip.w, origin_clip.z, origin_clip.w);

    // world_pos at the particle origin is "good enough" for light_map
    // sampling — voxels are 1 m, drops are tiny, and the quad is laid out
    // in screen space so interpolated per-fragment world position wouldn't
    // carry meaningful geometric info anyway.
    world_pos = origin;
    particle_color = COLOR;

    // Walk the sun mask down from the drop to find the first solid voxel.
    // That voxel's Y is the drop's "impact point" — where rain would hit
    // ground if nothing else stopped it. Fragment uses this to clip drops
    // whose impact sits above camera_clip (rain falling onto a cut-away
    // roof), without globally hiding drops over distant outdoor ground.
    impact_y = origin.y - 32.0;  // fallback: "well below drop" = don't clip
    float start_y = floor(origin.y) - 0.5;
    for (int i = 0; i < 24; i++) {
        float sy = start_y - float(i);
        vec3 suvw = (vec3(origin.x, sy, origin.z) - light_map_origin) * light_map_inv_size;
        if (textureLod(light_map, suvw, 0.0).r < 0.1) {
            impact_y = sy;
            break;
        }
    }
}

void fragment() {
    bool globals_ready = length(light_map_inv_size) > 1e-6;

    if (globals_ready && clip_dither_should_discard(vec3(world_pos.x, impact_y, world_pos.z), FRAGCOORD.xy)) {
        discard;
    }

    float sun_mask = 1.0;
    if (globals_ready) {
        // Cover clip: discard drops that aren't under enough open sky, using
        // the non-leaky vertical sky-exposure field. This is what stops rain
        // from rendering inside a cave mouth (where the BFS sun mask leaks
        // bright) and makes the visible rain edge match the dry/wet line.
        vec3 se_uvw = (world_pos - sky_exposure_map_origin) * sky_exposure_map_inv_size;
        float sky_exposure = texture(sky_exposure_map, se_uvw).r;
        if (sky_exposure < sky_exposure_threshold) {
            discard;
        }
        // Shading still tracks actual light (BFS sun mask), so drops dim
        // correctly under dusk / partial shade without affecting the clip.
        vec3 uvw = (world_pos - light_map_origin) * light_map_inv_size;
        sun_mask = pow(texture(light_map, uvw).r, light_falloff_exp);
    }

    // Pixel's offset from the quad center, projected onto the streak's
    // oriented axes (velocity direction and its perpendicular). Both are
    // in SubViewport pixels since FRAGCOORD and quad_center_px are both
    // expressed there.
    vec2 offset_px = FRAGCOORD.xy - quad_center_px;
    float along = dot(offset_px, vel_dir_px);
    float perp = abs(dot(offset_px, vec2(-vel_dir_px.y, vel_dir_px.x)));

    // Analytical coverage. The quad's "core" is ±quad_half_{l,w}_px; we fade
    // over an AA zone past each edge.
    //   - width_cov: symmetric fade both sides.
    //   - lead_cov: fade past the leading edge (along = +half_l). Trailing
    //     side of the quad stays covered here; the trail_cov below drives
    //     its own motion-blur taper.
    //   - trail_cov: linear 1.0 at leading (along = +half_l) to 0.0 at
    //     trailing (along = -half_l). This is the motion-blur aesthetic,
    //     not an AA edge — the trailing tip naturally reaches alpha 0
    //     without needing a separate fade.
    //
    // AA zone scales with streak dimension: at full pixel+ widths the zone
    // is a full 1 px (standard AA), but for sub-pixel widths the zone shrinks
    // to match the core size so the AA doesn't dominate the visible footprint.
    // A 0.25 px-wide streak would otherwise read as 2-3 px because the 1 px
    // AA zone extends past it both sides; with this it reads as ~1 px.
    float width_aa = min(1.0, quad_half_w_px * 2.0);
    float lead_aa = min(1.0, quad_half_l_px * 2.0);
    float width_cov = 1.0 - smoothstep(quad_half_w_px, quad_half_w_px + width_aa, perp);
    float lead_cov = 1.0 - smoothstep(quad_half_l_px, quad_half_l_px + lead_aa, along);
    float trail_cov = clamp((along + quad_half_l_px) / (2.0 * quad_half_l_px), 0.0, 1.0);
    float coverage = width_cov * lead_cov * trail_cov;
    if (coverage < 1e-4) {
        discard;
    }

    float lit = sun_ambient + sun_mask * (1.0 - sun_ambient);
    ALBEDO = albedo.rgb * sun_color * lit * sun_intensity;
    ALPHA = albedo.a * coverage * particle_color.a;
}
