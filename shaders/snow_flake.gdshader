shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_opaque, cull_disabled, skip_vertex_transform;

// Snowflake particle shader — the snow half of RainEffect, sibling to
// rain_drop.gdshader. It shares that shader's two clips verbatim (they are the
// hard part, and a second derivation of them would drift): the per-fragment
// sky-exposure test that stops precipitation rendering under roofs, canopy and
// cave mouths, and the camera_clip dither so flakes over a cut-away roof vanish
// with it.
//
// What it does NOT share is the streak. A raindrop is a motion-blurred line
// whose length carries its speed; a flake is a slow tumbling speck, and drawing
// one as an oriented streak reads as fast rain in white. So the quad here is a
// screen-space SQUARE of fixed pixel size — the same reason the drop lays itself
// out in pixels (an iso/ortho camera foreshortens world-space quads into an
// unreadable shrink-and-rotate), but with no velocity axis at all.
//
// Fixed PIXEL size is also what keeps snow legible at this game's pixel-art
// resolution: a world-space flake small enough to read as a flake up close
// disappears into sub-pixel shimmer at the far end of a 480x270 viewport.
// Per-particle scale variety still comes from the process material.
//
// Flutter is deliberately NOT here. Godot's ParticleProcessMaterial has
// turbulence, which moves the particle itself — so its screen position, its
// lighting sample and its occlusion test all agree. A shader-side wobble would
// move only the drawn quad, leaving a flake lit and clipped as though it were
// somewhere it is not.

global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
// Non-leaky vertical sky-exposure field (same UVW convention as light_map).
// See ChunkState.SkyExposure and the note in rain_drop.gdshader.
global uniform sampler3D sky_exposure_map : filter_linear, repeat_enable;
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

uniform vec4 albedo : source_color = vec4(0.95, 0.97, 1.0, 0.85);
// Flakes only render where vertical sky exposure clears this. Same value and
// same purpose as the drop's, so the snow line and the rain line agree.
uniform float sky_exposure_threshold : hint_range(0.0, 1.0) = 0.5;
// Flake size in SubViewport pixels, before the per-particle scale. The
// rasterized envelope is inflated 1 px past this so the coverage falloff has
// somewhere to land.
uniform float flake_size_px : hint_range(0.5, 32.0) = 2.0;
// How much of the flake's radius is soft edge. 0 = a hard pixel square (most
// pixel-art-correct for a 1-2 px flake, where any softness is most of the
// sprite); toward 1 = a round out-of-focus puff, which is what sells the large
// near flakes of heavy snow. Applied as a fraction of the radius so it means
// the same thing at every size.
uniform float flake_softness : hint_range(0.0, 1.0) = 0.45;
// Ambient floor specific to snow. Falling snow is a dense cloud of scatterers
// and stays visibly bright in weather too overcast to cast any sun at all —
// gating it on the sun mask alone (as rain is) turns a blizzard invisible under
// exactly the heavy cloud that produces one.
uniform float min_ambient : hint_range(0.0, 1.0) = 0.35;

varying vec3 world_pos;
varying flat float impact_y;
varying flat vec4 particle_color;
// Screen-space quad info, flat so every fragment of a quad agrees.
varying flat vec2 quad_center_px;
varying flat float quad_half_px;

void vertex() {
    vec3 origin = MODEL_MATRIX[3].xyz;
    float particle_scale = length(MODEL_MATRIX[0].xyz);

    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin, 1.0);
    vec2 origin_ndc = origin_clip.xy / max(abs(origin_clip.w), 1e-6);

    vec2 origin_px = (origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE;
    quad_center_px = origin_px;
    quad_half_px = flake_size_px * 0.5 * particle_scale;

    // Core + 1 px AA margin, so fragments still fire on the pixels the soft
    // edge needs to fade across.
    float rast_half = quad_half_px + 1.0;

    // Normalize VERTEX to +-0.5 corner factors regardless of mesh size /
    // QuadMesh orientation (FACE_Y vs FACE_Z): a flat quad always has one of
    // VERTEX.y / VERTEX.z at zero, so summing picks the non-zero axis.
    float vx_unit = VERTEX.x > 0.0 ? 0.5 : -0.5;
    float vy_unit = (VERTEX.y + VERTEX.z) > 0.0 ? 0.5 : -0.5;

    // Screen-axis-aligned: no velocity direction to orient to, and an aligned
    // square is what keeps a 1-2 px flake landing on whole pixels.
    vec2 offset_px = vec2(vx_unit, vy_unit) * 2.0 * rast_half;
    vec2 offset_ndc = offset_px * 2.0 / VIEWPORT_SIZE;
    POSITION = vec4((origin_ndc + offset_ndc) * origin_clip.w, origin_clip.z, origin_clip.w);

    world_pos = origin;
    particle_color = COLOR;

    // Walk the sun mask down to the first solid voxel — the flake's landing
    // point. The fragment stage clips against camera_clip using THAT rather
    // than the flake's own Y, so snow falling onto a cut-away roof disappears
    // with the roof while snow over open ground beyond it keeps drawing.
    impact_y = origin.y - 32.0;  // fallback: "well below" = never clip
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
        vec3 se_uvw = (world_pos - sky_exposure_map_origin) * sky_exposure_map_inv_size;
        if (texture(sky_exposure_map, se_uvw).r < sky_exposure_threshold) {
            discard;
        }
        vec3 uvw = (world_pos - light_map_origin) * light_map_inv_size;
        sun_mask = pow(texture(light_map, uvw).r, light_falloff_exp);
    }

    // Radial coverage in pixels. The AA zone is at least 1 px, widened by
    // flake_softness toward the full radius; clamping the floor to 1 px is what
    // keeps a large soft flake from developing a hard rim.
    vec2 offset_px = FRAGCOORD.xy - quad_center_px;
    float dist = length(offset_px);
    float edge_aa = max(1.0, quad_half_px * flake_softness);
    float coverage = 1.0 - smoothstep(quad_half_px - edge_aa, quad_half_px + 1.0, dist);
    if (coverage < 1e-4) {
        discard;
    }

    float ambient = max(sun_ambient, min_ambient);
    float lit = ambient + sun_mask * (1.0 - ambient);
    ALBEDO = albedo.rgb * sun_color * lit * sun_intensity;
    ALPHA = albedo.a * coverage * particle_color.a;
}
