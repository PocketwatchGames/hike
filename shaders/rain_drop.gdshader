shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_opaque, cull_disabled, skip_vertex_transform;

// Rain-drop particle shader (also used for splashes via a sibling material).
// Responsibilities:
//   - Y-axis billboard (quad's local Y stays world-vertical, local X rotates
//     around Y to face the camera). Matches sprite_lit's billboard math.
//   - Per-fragment clip against the ceiling cutaway (camera_clip) so drops
//     don't paint on hidden interior ceilings.
//   - Per-fragment clip against the voxel sun mask so drops under a roof/
//     overhang/cave vanish.
//   - Cheap lighting: sun_color * (ambient + mask * (1-ambient)) * intensity.
//     These globals are the EFFECTIVE primary-light values SkyController
//     blends each frame (sun↔moon↔sunset), so rain inherits the current
//     time-of-day palette automatically.
//   - Orientation-agnostic QuadMesh handling. Godot's editor keeps writing
//     orientation = 1 (FACE_Y) onto QuadMesh_splash; we map VERTEX to
//     billboard-local (vx, vy) using (VERTEX.x, VERTEX.y + VERTEX.z) so
//     FACE_Z and FACE_Y both work.
//
// Sub-pixel antialiasing via analytical pixel coverage:
//   The chunky-pixel SubViewport (480×270) rasterizes thin drops to a single
//   pixel — a drop straddling two pixels snaps into one, causing flicker.
//   To fix: in the vertex stage, inflate the quad's VISUAL half-extent to a
//   minimum of `min_screen_width_px / 2` pixels (so sub-pixel drops read as
//   1-pixel visuals at full alpha), then inflate the RASTERIZATION envelope
//   another 0.5 px so fragments fire on neighbor pixels. In the fragment
//   stage, compute the exact pixel↔quad overlap area and use it as ALPHA.
//   - Drop centered on a pixel → that pixel gets full alpha, neighbors zero.
//   - Drop straddling 50/50 → both pixels get half alpha.
//   - Drop moving sub-pixel across a boundary → linear split-alpha handoff.
//   Handles streak-length fades the same way: top/bottom pixel rows get
//   partial coverage when the drop falls across row boundaries, so tall
//   streaks read continuously rather than ladder-stepping.
//
// Editor-preview fallback. When rain.tscn is opened standalone, no
// SkyController / LightMap has pushed the globals. light_map_inv_size = 0
// is the sentinel — gating the discards on length(inv_size) > 0 lets the
// preview render drops without a running game.

global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float camera_clip;
global uniform vec3 sun_color;
global uniform float sun_intensity;
global uniform float sun_ambient;

uniform vec4 albedo : source_color = vec4(0.72, 0.78, 0.88, 0.45);
uniform float sun_mask_threshold : hint_range(0.0, 1.0) = 0.7;
// Minimum on-screen width (in SubViewport pixels) the drop gets inflated to
// before coverage AA runs. 1.0 gives crisp single-pixel drops that cleanly
// split alpha when straddling; 2.0 gives softer 2-pixel drops.
uniform float min_screen_width_px : hint_range(0.0, 8.0) = 0.25;

varying vec3 world_pos;
// TRUE/visual quad screen-space AABB in viewport pixels: (xmin, ymin, xmax, ymax).
// Flat because every vertex of a given particle computes the same value
// (abs of VERTEX is identical at each of the four corners).
varying flat vec4 quad_bounds_px;
varying vec2 uv_for_trail;
// World Y of the first solid (sun_mask ≈ 0) voxel below the particle's
// origin. Walked once per particle in the vertex stage. Fragment uses it as
// the drop's "impact point" and clips drops whose impact is above camera_clip
// (rain falling onto a cut-away roof rather than open ground).
varying flat float impact_y;

void vertex() {
    // MODEL_MATRIX is the per-particle transform for GPUParticles3D draw
    // passes. Column 0 length carries per-particle uniform scale.
    vec3 origin = MODEL_MATRIX[3].xyz;
    float particle_scale = length(MODEL_MATRIX[0].xyz);

    // Y-axis billboard: rotate right-axis around world Y to face camera.
    vec3 world_up = vec3(0.0, 1.0, 0.0);
    vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
    vec3 right = normalize(cross(world_up, cam_back));

    // Map VERTEX to billboard (horizontal, vertical) regardless of QuadMesh
    // orientation. FACE_Z → (VERTEX.x, VERTEX.y); FACE_Y → (VERTEX.x, VERTEX.z).
    // A flat quad always has one of VERTEX.y/VERTEX.z equal to zero, so the
    // sum picks the live axis without conditionals.
    float vx = VERTEX.x;
    float vy = VERTEX.y + VERTEX.z;

    // Mesh-authored half-extents of this quad in world space.
    float half_w_world = abs(vx) * particle_scale;
    float half_h_world = abs(vy) * particle_scale;

    // Project origin + world-axis tips to screen pixels, so we know how many
    // pixels the un-inflated quad would cover. Under orthographic projection
    // w=1 so the divide is trivial; covered here in case the scene switches
    // to perspective (e.g. the editor preview camera).
    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin, 1.0);
    vec4 w_tip_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin + half_w_world * right, 1.0);
    vec4 h_tip_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin + half_h_world * world_up, 1.0);
    vec2 origin_px = (origin_clip.xy / max(abs(origin_clip.w), 1e-6) * 0.5 + 0.5) * VIEWPORT_SIZE;
    vec2 w_tip_px = (w_tip_clip.xy / max(abs(w_tip_clip.w), 1e-6) * 0.5 + 0.5) * VIEWPORT_SIZE;
    vec2 h_tip_px = (h_tip_clip.xy / max(abs(h_tip_clip.w), 1e-6) * 0.5 + 0.5) * VIEWPORT_SIZE;
    float half_w_px = abs(w_tip_px.x - origin_px.x);
    float half_h_px = abs(h_tip_px.y - origin_px.y);

    // VISUAL half-extent (used for coverage): at least min_screen_width_px / 2
    // so sub-pixel drops read as a 1-pixel visual and get proper split alpha
    // when straddling.
    float min_half_px = min_screen_width_px * 0.5;
    float visual_half_w_px = max(half_w_px, min_half_px);
    float visual_half_h_px = max(half_h_px, min_half_px);
    quad_bounds_px = vec4(
        origin_px.x - visual_half_w_px, origin_px.y - visual_half_h_px,
        origin_px.x + visual_half_w_px, origin_px.y + visual_half_h_px
    );

    // RASTERIZATION half-extent: add 0.5 px on each side so fragments fire
    // on the pixels adjacent to the visual quad too — needed so the straddle
    // pixels actually get a shader invocation and their coverage can be
    // computed (analytical AA is useless if the rasterizer already snapped
    // the quad to one pixel).
    float raster_half_w_px = visual_half_w_px + 0.5;
    float raster_half_h_px = visual_half_h_px + 0.5;
    // Convert pixel half-extents back to a world-scale multiplier for VERTEX.
    // The VERTEX offset in world space is vx * particle_scale along right;
    // in pixels it maps to ±half_w_px. Multiplying VERTEX.x's world offset
    // by (raster_half_w_px / half_w_px) stretches the rasterized quad to
    // the desired pixel size.
    float scale_x_factor = raster_half_w_px / max(half_w_px, 1e-6);
    float scale_y_factor = raster_half_h_px / max(half_h_px, 1e-6);

    vec3 vertex_world = origin
        + vx * particle_scale * scale_x_factor * right
        + vy * particle_scale * scale_y_factor * world_up;

    world_pos = vertex_world;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);
    uv_for_trail = UV;

    // Walk the sun mask down from the drop, 1 m per step, until the first
    // voxel that's solid (sun BFS didn't reach it). That voxel's Y is the
    // drop's impact point — where it would hit ground if unobstructed. The
    // fragment uses this to cull drops falling onto a cut-away roof:
    // impact_y > camera_clip means the drop column lands on something the
    // cutaway has hidden, so drawing the drop would put it in empty air
    // above a removed ceiling.
    //
    // Computed in vertex (once per particle, shared to the fragment via a
    // flat varying) rather than per-fragment, so the 24-sample loop runs
    // 5000×/frame, not 5000×(fragment count)×/frame.
    impact_y = origin.y - 32.0;  // fallback: "impact well below drop" = don't clip
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

    // Impact-point clip (user's suggested heuristic). Rather than hiding every
    // drop above camera_clip globally (which also hides outdoor drops 40m
    // away), hide only drops whose vertical "raycast" — computed in vertex
    // via a sun-mask walk — hits a surface above camera_clip. Drops over
    // open ground have impact_y ≈ terrain Y (well below camera_clip) and
    // render; drops over a cut-away roof have impact_y = roof Y (above
    // camera_clip) and get discarded.
    if (globals_ready && impact_y > camera_clip) {
        discard;
    }

    float sun_mask = 1.0;
    if (globals_ready) {
        vec3 uvw = (world_pos - light_map_origin) * light_map_inv_size;
        sun_mask = pow(texture(light_map, uvw).r, light_falloff_exp);
        if (sun_mask < sun_mask_threshold) {
            discard;
        }
    }

    // Analytical pixel-coverage AA. FRAGCOORD.xy is the current pixel's
    // center in SubViewport pixels; its 1×1 square is (center ± 0.5).
    // Overlap the pixel square with the visual quad bounds to get the
    // exact fraction of this pixel covered by the drop.
    vec2 pixel_min = FRAGCOORD.xy - 0.5;
    vec2 pixel_max = FRAGCOORD.xy + 0.5;
    vec2 overlap_min = max(pixel_min, quad_bounds_px.xy);
    vec2 overlap_max = min(pixel_max, quad_bounds_px.zw);
    vec2 overlap = max(vec2(0.0), overlap_max - overlap_min);
    float coverage = overlap.x * overlap.y;
    if (coverage < 1e-4) {
        discard;
    }

    // Mild Y-fade on the trailing ends of the streak — coverage AA already
    // smooths the row-to-row transition, but fading the streak's head/tail
    // softens the "hard end of the line" look, which will matter more once
    // drops fall at an angle and the streak tips sweep across pixels.
    float edge_y = abs(uv_for_trail.y - 0.5) * 2.0;
    float trail_fade = 1.0 - smoothstep(0.85, 1.0, edge_y);

    float lit = sun_ambient + sun_mask * (1.0 - sun_ambient);
    ALBEDO = albedo.rgb * sun_color * lit * sun_intensity;
    ALPHA = albedo.a * coverage * trail_fade;
}
