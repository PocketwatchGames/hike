shader_type spatial;
// Transparent queue, no depth test, no depth write — the reflection
// geometry extends below the lake floor in world space; we rely on the
// stencil mask (written by voxel_water.gdshader at every visible water
// pixel) to clip rendering to ONLY those pixels where the water surface
// draws. depth_test_disabled bypasses the lake-floor occlusion that
// killed every previous attempt; render_priority on the material
// guarantees we draw AFTER water so the stencil mark is already laid
// down by the time we sample it. Water no longer composites its blue
// tint over us via screen_tex (we sit on top in the queue), so the
// fragment manually mixes toward water_deep_tint with depth — matches
// the look the per-instance LitSprite reflection got from water's
// blend_mix when reflections were children of Sprite3Ds rendered before
// water.
render_mode blend_mix, depth_draw_always, cull_disabled, ambient_light_disabled, skip_vertex_transform, unshaded;
stencil_mode read, compare_equal, 4;

// Multimesh-instanced water reflection for prop sprites. Forked from
// sprite_reflection.gdshader; per-instance values come from MultiMesh data
// the same way sprite_prop_multimesh.gdshader unpacks them. Three channels
// repurposed for this pass:
//   - COLOR.b : water_y (world Y of the reflecting water surface, baked at
//               MultimeshPropSprite._Ready by a one-shot voxel search down
//               from the sprite). The visible-pass shader uses this slot
//               for forward_offset; same byte position, different meaning
//               per pass — WorldPropScatter writes the right value when
//               filling each bucket.
//   - COLOR.a : forward_offset (same value the visible bucket carries in
//               COLOR.b). The vertex shader applies the same forward push
//               the visible sprite uses so the reflection's XZ anchor
//               lines up with where the visible sprite sits, not the bare
//               prop transform.
//   - MODEL_MATRIX[3].xyz : SOURCE world position (same transform the
//               visible/shadow buckets use; the shader computes the
//               mirrored reflection anchor on its own — see vertex()).
//
// Why the source position lives in the transform (rather than the mirrored
// anchor): MultimeshPropSprite registers ONE snapshot for all three passes,
// and the visible/shadow buckets want the source position. The reflection
// shader does the geometric mirror. This keeps every bucket's per-instance
// data byte-identical except for COLOR.b's payload.

uniform sampler2D sprite_texture : source_color, filter_nearest;

// --- Lighting / sky globals (verbatim from sprite_reflection) ---
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

global uniform float camera_clip;
global uniform float sprite_stretch;

// Ripple jitter — same globals voxel_water and the per-instance reflection
// shader use, set by SkyController.
global uniform float ripple_scale_a;
global uniform float ripple_scale_b;
global uniform vec2 ripple_offset_a;
global uniform vec2 ripple_offset_b;
global uniform float ripple_strength;
global uniform float reflection_tint;
global uniform float reflection_pixel_jitter_max;

// Coarse wind-factor 3D texture (see voxel_water.gdshader). Sampled at
// the reflection's water-surface intersection so prop reflections in
// caves / indoors stop shimmering — same gating as voxel_water and
// sprite_reflection so the three layers stay coherent.
global uniform sampler3D wind_map : filter_linear;
global uniform vec3 wind_map_origin;
global uniform vec3 wind_map_inv_size;

// Water-tint globals (already declared + set by SkyController for
// voxel_water.gdshader). We sit on top of water in the transparent queue
// now — water's blend_mix doesn't get to tint us anymore — so the
// fragment shader does its own depth-into-water mix toward
// water_deep_tint to recover the look the per-instance reflection had
// when water composited over it.
global uniform vec3 water_shallow_tint : source_color;
global uniform vec3 water_deep_tint : source_color;
global uniform float water_depth_scale;
global uniform float water_alpha_max;
global uniform float water_edge_opacity;
// Clarity / muddiness, driven from RegionData.WaterColor.a in
// SkyController.Apply (clearer on the regional-clear end, opaque on
// the muddy/swamp end). Drives reflection ALPHA so muddy water lets
// its own dim/turbid surface dominate instead of stamping the
// reflection sprite over it.
global uniform float water_muddiness;

// Debug visualizer mode — see CVars.cs `reflection_debug` for mode docs.
// 0 = off (normal rendering); 1..7 replace ALBEDO with diagnostics so we
// can see what's happening to ripple_tilt, surface_xz, path_len, jitter,
// etc. Driven by SkyController-registered global uniform.
global uniform int reflection_debug_mode;

// Ripple normal-map textures — same pair voxel_water samples for surface
// perturbation. We sample them at the reflected fragment's world XZ to
// drive UV displacement so the reflection shimmers in lockstep with the
// water surface above it.
uniform sampler2D ripple_tex_a : filter_linear, repeat_enable, hint_normal;
uniform sampler2D ripple_tex_b : filter_linear, repeat_enable, hint_normal;

varying flat vec2 anchor_px;
varying flat vec3 source_world_pos;
varying flat float water_y_var;
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
varying flat ivec2 sprite_size;
varying flat ivec2 sprite_region_origin;
varying flat int sprite_mirror_var;
varying flat float world_w_var;
varying flat float world_h_var;
varying vec3 vertex_world_var;

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

float refl_hash13(vec3 p) {
    p = fract(p * 0.3183099 + 0.1);
    p *= 17.0;
    return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
}
float refl_vnoise(vec3 p) {
    vec3 i = floor(p);
    vec3 f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    float n000 = refl_hash13(i); float n100 = refl_hash13(i + vec3(1,0,0));
    float n010 = refl_hash13(i + vec3(0,1,0)); float n110 = refl_hash13(i + vec3(1,1,0));
    float n001 = refl_hash13(i + vec3(0,0,1)); float n101 = refl_hash13(i + vec3(1,0,1));
    float n011 = refl_hash13(i + vec3(0,1,1)); float n111 = refl_hash13(i + vec3(1,1,1));
    return mix(mix(mix(n000,n100,f.x), mix(n010,n110,f.x), f.y),
               mix(mix(n001,n101,f.x), mix(n011,n111,f.x), f.y), f.z);
}

void vertex() {
    source_world_pos = MODEL_MATRIX[3].xyz;
    float world_w = length(MODEL_MATRIX[0].xyz);
    float world_h = length(MODEL_MATRIX[1].xyz);
    world_w_var = world_w;
    world_h_var = world_h;

    sprite_mirror_var = (hash12(source_world_pos.xz + vec2(7.31, 13.71)) > 0.5) ? 1 : 0;
    int packed_origin = int(COLOR.r);
    sprite_region_origin = ivec2(packed_origin >> 12, packed_origin & 0xFFF);
    int packed_size = int(COLOR.g);
    sprite_size = ivec2(packed_size >> 12, packed_size & 0xFFF);
    water_y_var = COLOR.b;
    float forward_offset = COLOR.a;
    float align = clamp(INSTANCE_CUSTOM.w, 0.0, 1.0);
    vec3 normal_in = INSTANCE_CUSTOM.xyz;

    // Reflection anchor:
    //   - Source above the water surface (shore props, trees on a cliff,
    //     anything not actively in the water): flip the sprite about its
    //     OWN BASE. The reflection's top edge sits at the prop's base
    //     (source_world_pos.y) — completely ignoring water elevation —
    //     and the body hangs world_h DOWN from there. This produces the
    //     "reflection extends from the source's feet" look the screen-
    //     space stencil clip wants. Inaccurate optically (water_y plays
    //     no role) but reads as a reflection because the stencil mask
    //     guarantees only fragments overlapping the water surface get
    //     drawn.
    //   - Source already in / partly under water (above_water <= 0):
    //     true geometric mirror across the water plane (2*(water_y -
    //     src.y)). Half-submerged props need the bottom half visibly
    //     continuous with their above-water sprite, which only the real
    //     mirror gives.
    float above_water = source_world_pos.y - water_y_var;
    float local_y;
    if (above_water <= 0.0) {
        local_y = 2.0 * (water_y_var - source_world_pos.y);
    } else {
        // Flip about the sprite's base. Quad encoding: VERTEX.y in [0,1],
        // up_flipped has a NEGATIVE world_up component, so the corner at
        // VERTEX.y = 0 is the visual TOP of the reflection (world_origin
        // itself) and VERTEX.y = 1 is the bottom (world_origin - h). For
        // the top to land at the source's base, world_origin.y must equal
        // source_world_pos.y → local_y = 0. (An earlier iteration shifted
        // by -world_h thinking the top was at world_origin + h; that
        // produced a world_h vertical gap between source and reflection.)
        local_y = 0.0;
    }
    vec3 world_origin = source_world_pos + vec3(0.0, local_y, 0.0);

    // Same yaw + roll-toward-terrain-normal billboard math as the visible
    // shader; the only difference is final Y-flip on the rolled axes (the
    // mirror-across-water-plane that makes this a reflection).
    vec3 world_up = vec3(0.0, 1.0, 0.0);
    vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
    vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
    vec3 sprite_forward = cross(sprite_right_yaw, world_up);

    // Visible bucket pushes the sprite forward by `+sprite_forward *
    // forward_offset` (so the sprite's billboard sits in front of the
    // prop's logical XZ). For the reflection we want the sprite to
    // extend FROM origin in the opposite direction — so add along
    // sprite_forward in the same direction the visible sprite extended,
    // not the reverse. (An earlier iteration subtracted; that put the
    // reflection on the wrong side of origin.)
    world_origin -= sprite_forward * forward_offset;

    vec3 n = length(normal_in) > 1e-4 ? normalize(normal_in) : world_up;
    vec3 n_in_plane = n - dot(n, sprite_forward) * sprite_forward;
    float right_comp = dot(n_in_plane, sprite_right_yaw);
    float up_comp = dot(n_in_plane, world_up);
    float roll = atan(right_comp, up_comp) * align;
    float cr = cos(roll);
    float sr = sin(roll);

    vec3 sprite_right_rolled = cr * sprite_right_yaw - sr * world_up;
    vec3 sprite_up_rolled = sr * sprite_right_yaw + cr * world_up;

    float stretch = max(sprite_stretch, 1e-4);
    vec3 right_flipped = vec3(sprite_right_rolled.x, -sprite_right_rolled.y * stretch, sprite_right_rolled.z);
    vec3 up_flipped = vec3(sprite_up_rolled.x, -sprite_up_rolled.y * stretch, sprite_up_rolled.z);

    vec3 vertex_world = world_origin
        + VERTEX.x * world_w * right_flipped
        + VERTEX.y * world_h * up_flipped;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

    // Override Z with a NEAR-PLANE SLAB depth that's always in front of any
    // scene geometry, with closer-source reflections placed slightly closer
    // to the camera within that slab so they win depth-test against farther
    // sources. Two reasons we can't just project a real world position:
    //
    //   1. The reflection geometry sits BELOW the lake floor in world Y, so
    //      its natural projected depth is behind the lake floor → the
    //      depth-test would reject every fragment.
    //   2. Even projecting (source.x, water_y, source.z) — the source's XZ
    //      dropped onto the water plane — gives a constant depth per
    //      reflection, which at any given screen pixel may still be FARTHER
    //      than the lake-floor depth at THAT pixel (lake floor varies per
    //      pixel, source-anchor depth doesn't). Result: chunks of the
    //      reflection get z-occluded by underwater terrain.
    //
    // So instead: project the surface-anchor to get a relative source
    // ordering [0..1] in NDC z (Godot 4 reverse-Z: 1=near, 0=far), then
    // REMAP that into [0.999, 1.0]. Every reflection lands in a thin slab
    // right at the near plane — guaranteed in front of any scene geometry
    // (which lives in [0, 0.999]). Within the slab, closer source → larger
    // ndc.z → wins reflection-vs-reflection depth test. Tiny per-source
    // hash epsilon breaks ties when two anchors land at the same projected
    // depth.
    //
    // Trade-off: depth buffer at water-visible pixels carries near-plane
    // depth values after reflections draw, which post-processes that read
    // depth (volumetric fog at priority 64, post_process at 127) will see
    // as "very close to camera" at those pixels. Acceptable for now;
    // volumetric fog reading near-plane depth at water pixels just means
    // it computes minimal thickness there, which matches the visual.
    vec3 surface_anchor = vec3(source_world_pos.x, water_y_var, source_world_pos.z);
    vec4 src_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(surface_anchor, 1.0);
    float surface_ndc_z = src_clip.z / src_clip.w;
    float ndc_z = mix(0.999, 1.0, clamp(surface_ndc_z, 0.0, 1.0));
    float src_eps = (hash12(source_world_pos.xz + vec2(0.137, 0.293)) - 0.5) * 1e-5;
    ndc_z += src_eps;
    POSITION.z = ndc_z * POSITION.w;

    vertex_world_var = vertex_world;

    float src_px_w = world_w / max(float(sprite_size.x), 1.0);
    float src_px_h = world_h / max(float(sprite_size.y), 1.0);
    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
    vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + src_px_w * right_flipped, 1.0);
    vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + src_px_h * up_flipped, 1.0);
    vec2 origin_ndc = origin_clip.xy / origin_clip.w;
    vec2 right_ndc = right_clip.xy / right_clip.w;
    vec2 up_ndc = up_clip.xy / up_clip.w;
    anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
    sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
    sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}

void fragment() {
    // Master disable — when SkyController pushes reflection_tint == 0
    // (CVar `sprite_reflections_disabled`, fully muddy/dim conditions).
    if (reflection_tint <= 0.0) {
        discard;
    }

    // Ceiling cutaway — match the source against camera_clip, not the
    // reflection anchor (which sits below water).
    if (source_world_pos.y > camera_clip) {
        discard;
    }

    // Discard fragments that would render above the water plane —
    // GEOMETRIC-MIRROR branch only. For partially-submerged sources the
    // mirror puts the reflection's above-water portion in world space
    // above water_y; we don't want to see the source's underwater half
    // mirrored back into the air. The stencil mask handles screen-space
    // clipping to water-visible pixels, but the world-Y check filters
    // out the unwanted above-water mirror geometry.
    //
    // For ABOVE-WATER sources (above_water > 0) we use the "flip at
    // base" anchor — the entire reflection geometry sits BETWEEN
    // source.y (top) and source.y - world_h*stretch (bottom), which for
    // a tall tree on a high hill is entirely above water_y. The world-Y
    // discard would kill the whole reflection in that case (you'd see
    // only a tiny slice near the water line) — so we skip it. The
    // stencil already guarantees we only paint where water is visible.
    float above_water = source_world_pos.y - water_y_var;
    if (above_water <= 0.0 && vertex_world_var.y > water_y_var + 0.001) {
        discard;
    }

    vec2 d = FRAGCOORD.xy - anchor_px;
    float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
    float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
    vec2 sxy = vec2(
        (sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
        (sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
    );

    // Ripple-driven UV jitter. Sample at the WATER-SURFACE XZ that
    // corresponds to this screen pixel — NOT at vertex_world_var.xz.
    // The reflection geometry's XZ sits roughly under the source for
    // every fragment (we only shift Y when computing world_origin), so
    // a tall reflection that extends deep down the screen would sample
    // ripples from the SAME XZ at every fragment if we used
    // vertex_world_var.xz — every pixel of the reflection would shift
    // by the same amount, which reads as no shimmer at all.
    //
    // The actual water surface a viewer "sees" the reflection through
    // is at the camera ray's intersection with the water plane (Y =
    // water_y_var). Compute that XZ here so each fragment samples the
    // ripple field where the surface IS, and a tall reflection picks
    // up many different ripple normals along its screen-space length.
    // Ripple-driven UV displacement, derived physically. Reflection ray
    // travels from the surface intersection point UP to the source-sprite
    // plane; if the surface normal is tilted by Δn from +Y, the ray's
    // landing point shifts by ~2 * Δn.xz * (path length) world units. The
    // path length is (h_above_water / |cam_fwd.y|) where h_above_water is
    // the world-Y height of the SOURCE pixel this fragment represents
    // above the water surface — sxy.y in source-pixel units mapped back
    // to world meters via world_h / sprite_size.y, plus the source's own
    // above-water offset. Top of the source (sxy.y near sprite_size.y)
    // is far from water → big shift; the source's base (sxy.y near 0)
    // sits near water → tiny shift. That's what makes the ripple produce
    // visible shimmer instead of a uniform whole-sprite jiggle.
    //
    // After computing the world-XZ shift we convert to SOURCE-PIXEL
    // jitter via the sprite basis: project the shift onto sprite_right
    // (horizontal axis in world XZ) for the U component and onto
    // cam-forward (the screen-vertical axis under iso projection) for
    // the V component, then divide by per-pixel world width / height to
    // land back in source-pixel space.
    // Intermediates captured for the debug visualizer so we can see what
    // each step of the ripple math produces. Defaulted to safe zeros for
    // the ripple_strength==0 / vy≈0 short-circuit cases.
    vec2 dbg_ripple_tilt = vec2(0.0);
    vec2 dbg_surface_xz = vec2(0.0);
    float dbg_source_above_water = 0.0;
    float dbg_path_len = 0.0;
    vec2 dbg_shift_world_xz = vec2(0.0);

    vec2 jitter = vec2(0.0);
    if (ripple_strength > 1e-4) {
        vec3 cam_fwd_world = -normalize(mat3(INV_VIEW_MATRIX) * vec3(0.0, 0.0, 1.0));
        float vy = cam_fwd_world.y;
        if (abs(vy) > 1e-4) {
            float t_surf = (water_y_var - vertex_world_var.y) / vy;
            vec2 surface_xz = vertex_world_var.xz + cam_fwd_world.xz * t_surf;
            dbg_surface_xz = surface_xz;

            vec2 uv_a = surface_xz * ripple_scale_a + ripple_offset_a;
            vec2 uv_b = surface_xz * ripple_scale_b + ripple_offset_b;
            vec3 na_raw = texture(ripple_tex_a, uv_a).rgb * 2.0 - 1.0;
            vec3 nb_raw = texture(ripple_tex_b, uv_b).rgb * 2.0 - 1.0;
            vec3 na = vec3(na_raw.r, na_raw.b, na_raw.g);
            vec3 nb = vec3(nb_raw.r, nb_raw.b, nb_raw.g);
            vec3 n_combined = normalize(na + nb);
            // Wind-subgrid gate at the reflection's water-surface point —
            // matches voxel_water.gdshader's smooth trilinear sampling.
            vec3 wind_sample_pos = vec3(surface_xz.x, water_y_var, surface_xz.y);
            vec3 wind_uvw = (wind_sample_pos - wind_map_origin) * wind_map_inv_size;
            float wind_factor = texture(wind_map, wind_uvw).r;
            vec2 ripple_tilt = mix(vec2(0.0), n_combined.xz, ripple_strength * wind_factor);
            dbg_ripple_tilt = ripple_tilt;

            float pixel_world_w = world_w_var / max(float(sprite_size.x), 1.0);
            float pixel_world_h = world_h_var / max(float(sprite_size.y), 1.0);
            float source_pixel_h = sxy.y * pixel_world_h;
            // Treat the source as if its base sits AT the waterline for
            // ripple amplitude — matches the "flip-at-base" anchor we use
            // for above-water sources. Including the real (source.y -
            // water_y) offset would make a tree on a 5m cliff produce
            // huge path_len → ripple shifts large enough to obliterate
            // the reflection sprite. Bounded by sprite's own world_h.
            float source_above_water = source_pixel_h;
            dbg_source_above_water = source_above_water;

            float path_len = source_above_water / max(abs(vy), 0.05);
            dbg_path_len = path_len;

            vec2 shift_world_xz = 2.0 * ripple_tilt * path_len;
            dbg_shift_world_xz = shift_world_xz;

            vec2 sprite_right_xz = vec2(INV_VIEW_MATRIX[0].x, INV_VIEW_MATRIX[0].z);
            float right_len = length(sprite_right_xz);
            if (right_len > 1e-4) {
                sprite_right_xz /= right_len;
            }
            float jitter_u = dot(shift_world_xz, sprite_right_xz) / pixel_world_w;

            vec2 cam_fwd_xz = cam_fwd_world.xz;
            float cam_fwd_xz_len = length(cam_fwd_xz);
            float jitter_v = 0.0;
            if (cam_fwd_xz_len > 1e-4) {
                cam_fwd_xz /= cam_fwd_xz_len;
                jitter_v = dot(shift_world_xz, cam_fwd_xz) / pixel_world_h;
            }

            jitter = vec2(jitter_u, jitter_v);
        }
    }

    // Debug visualizer — set ALBEDO to a diagnostic color and skip the
    // remainder of the fragment so we see the math even on transparent /
    // out-of-bounds pixels of the source sprite. GLSL `return` isn't
    // allowed inside Godot's fragment processor, so we use an `if/else`
    // around the rest of the body instead.
    if (reflection_debug_mode > 0) {
        vec3 dbg = vec3(0.0);
        if (reflection_debug_mode == 1) {
            dbg = vec3(dbg_ripple_tilt * 0.5 + 0.5, 0.0);
        } else if (reflection_debug_mode == 2) {
            dbg = vec3(fract(dbg_surface_xz.x * 0.1), fract(dbg_surface_xz.y * 0.1), 0.0);
        } else if (reflection_debug_mode == 3) {
            dbg = vec3(clamp(dbg_source_above_water / 16.0, 0.0, 1.0), 0.0, 0.0);
        } else if (reflection_debug_mode == 4) {
            dbg = vec3(clamp(dbg_path_len / 64.0, 0.0, 1.0), 0.0, 0.0);
        } else if (reflection_debug_mode == 5) {
            float mag = length(dbg_shift_world_xz);
            dbg = vec3(clamp(mag / 4.0, 0.0, 1.0), 0.0, 0.0);
        } else if (reflection_debug_mode == 6) {
            dbg = vec3(clamp(jitter.x / 32.0 + 0.5, 0.0, 1.0),
                       clamp(jitter.y / 32.0 + 0.5, 0.0, 1.0),
                       0.0);
        } else if (reflection_debug_mode == 7) {
            dbg = vec3(ripple_strength, 0.0, 0.0);
        }
        ALBEDO = dbg;
        ALPHA = 1.0;
    } else {

    // Reflections sample the source image top-to-bottom flipped:
    //   sxy.y near 0           → tex_coord.y = sprite_size.y - 1 (feet)
    //   sxy.y near sprite_size.y → tex_coord.y = 0               (head)
    ivec2 tex_coord = ivec2(
        int(floor(sxy.x + jitter.x)) + sprite_size.x / 2,
        sprite_size.y - 1 - int(floor(sxy.y + jitter.y))
    );
    bool sprite_mirror = sprite_mirror_var > 0;
    if (sprite_mirror) {
        tex_coord.x = sprite_size.x - 1 - tex_coord.x;
    }
    if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
        || tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
        discard;
    }

    vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
    if (tex.a < 0.5) {
        discard;
    }

    // Light from the SOURCE position, not the reflection's. Same push-up
    // half-a-voxel as the visible shader so the lightmap read lands in air.
    vec3 light_sample_pos = source_world_pos + vec3(0.0, 0.5, 0.0);
    vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
    vec4 lm = textureLod(light_map, uvw, 0.0);
    float sun_mask = pow(lm.r, light_falloff_exp);
    float cloud = cloud_shadow_ground(source_world_pos, TIME);
    float cloud_sun = 1.0 - cloud * cloud_shadow_strength;
    vec3 sun_lit = sun_mask * sun_intensity * sun_color * cloud_sun;
    vec3 block_lit = lm.gba;
    vec3 light = min(vec3(1.0), sun_lit + block_lit);

    vec3 reflected = tex.rgb * light;

    // Manual water-color tint. We sit on top of water in the transparent
    // queue, so water's blend_mix doesn't get to tint us — recreate it
    // here. Two-component mix:
    //   - Surface tint: a constant water-edge tint applied to the entire
    //     reflection (matches what water_alpha at the shoreline /
    //     water_edge_opacity does to the per-instance LitSprite). This is
    //     what gives the player's reflection its overall blue cast even
    //     when the reflection is right at the waterline.
    //   - Depth tint: ramps the target color from shallow→deep tint with
    //     depth below the surface, and bumps the mix factor up toward
    //     water_alpha_max so deep reflections sit close to the deep-water
    //     color (matching what water's blend_mix would do over a sprite
    //     visible through deep water).
    float depth_below = max(0.0, water_y_var - vertex_world_var.y);
    float depth_t = clamp(depth_below / max(water_depth_scale, 0.01), 0.0, 1.0);
    vec3 tint_target = mix(water_shallow_tint, water_deep_tint, depth_t) * light;
    float surface_blend = clamp(water_edge_opacity, 0.0, 1.0);
    float blend = mix(surface_blend, water_alpha_max, depth_t);
    vec3 tinted = mix(reflected, tint_target, blend);

        ALBEDO = tinted * reflection_tint;
        // Fade reflection opacity by water clarity so muddy water lets its
        // own surface dominate instead of stamping the reflection sprite
        // over it. Clear water → ALPHA=1 (full reflection); swamp /
        // muddy water → ALPHA→0 (water surface shows through; the
        // reflection still contributes faintly via blend_mix). Power
        // curve so the falloff stays subtle in the middle range and
        // bites only as muddiness saturates.
        ALPHA = pow(clamp(1.0 - water_muddiness, 0.0, 1.0), 1.5);
    }
}
