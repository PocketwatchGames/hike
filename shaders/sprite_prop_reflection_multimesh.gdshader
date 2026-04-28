shader_type spatial;
// Render mode matches sprite_reflection.gdshader exactly — opaque queue,
// depth-test enabled, depth-write enabled. Practical consequence today:
// only sources already IN the water (player wading in, mobs in water)
// cast a visible reflection. Sources on shore have their reflection
// extend below the lake floor in world space, where depth-test against
// the floor terrain discards every fragment. Same limitation the
// per-instance LitSprite path has had since reflections were added.
//
// Shore-prop reflections are a desired follow-up — see the TODO note at
// the top of MultimeshPropSprite.cs for the design directions explored
// (depth_test_disabled, transparent-queue blend_mix, DEPTH_TEXTURE
// stencil) and why each one didn't pan out in this project. The next
// likely path is real stencil support (Godot 4.5+ adds stencil_compare_*
// / stencil_write_* render_mode tokens) — water writes a stencil bit,
// reflection tests it, then we control queue + tinting independently.
render_mode depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform, unshaded;

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
//   - COLOR.a : lake_floor_y (world Y of the first non-water voxel below
//               the surface). Currently UNUSED by the shader; baked at
//               _Ready time and packed by the bucket so a follow-up
//               implementation (vertex clamp, stencil mask, etc.) can pick
//               it up without revisiting the per-instance layout.
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
global uniform vec2 ripple_offset_a;
global uniform float ripple_strength;
global uniform float reflection_tint;
global uniform float reflection_pixel_jitter_max;

// Cap on the above-water height that drives the geometric mirror. Within
// this distance above the water surface, the reflection uses a true mirror
// (2*(water_y - source.y)); above the cap, the anchor stops sinking and
// falls back to "feet at waterline" so a tall prop on a cliff doesn't
// anchor its reflection deep below the seabed. Hardcoded to match
// LitSprite.MaxReflectionAboveWater's default — exposing as a uniform
// would just fork buckets again with no real authoring benefit.
const float MAX_REFLECT_ABOVE_WATER = 2.0;

varying flat vec2 anchor_px;
varying flat vec3 source_world_pos;
varying flat float water_y_var;
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
varying flat ivec2 sprite_size;
varying flat ivec2 sprite_region_origin;
varying flat int sprite_mirror_var;
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

    sprite_mirror_var = (hash12(source_world_pos.xz + vec2(7.31, 13.71)) > 0.5) ? 1 : 0;
    int packed_origin = int(COLOR.r);
    sprite_region_origin = ivec2(packed_origin >> 12, packed_origin & 0xFFF);
    int packed_size = int(COLOR.g);
    sprite_size = ivec2(packed_size >> 12, packed_size & 0xFFF);
    water_y_var = COLOR.b;
    // COLOR.a carries lake_floor_y but no current consumer — see header.
    float align = clamp(INSTANCE_CUSTOM.w, 0.0, 1.0);
    vec3 normal_in = INSTANCE_CUSTOM.xyz;

    // Reflection anchor: mirror the sprite anchor across the water plane,
    // then cap to keep tall props on cliffs from sinking their reflection
    // far below the seabed. Same math LitSprite.UpdateReflection used,
    // moved into the shader since multimesh can't position per-instance
    // node transforms differently per pass.
    float above_water = source_world_pos.y - water_y_var;
    float local_y;
    if (above_water <= MAX_REFLECT_ABOVE_WATER) {
        local_y = 2.0 * (water_y_var - source_world_pos.y);
    } else {
        local_y = water_y_var - source_world_pos.y;
    }
    vec3 world_origin = source_world_pos + vec3(0.0, local_y, 0.0);

    // Same yaw + roll-toward-terrain-normal billboard math as the visible
    // shader; the only difference is final Y-flip on the rolled axes (the
    // mirror-across-water-plane that makes this a reflection).
    vec3 world_up = vec3(0.0, 1.0, 0.0);
    vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
    vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
    vec3 sprite_forward = cross(sprite_right_yaw, world_up);

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

    // Discard fragments that would render above the water plane (handles
    // partially-submerged sources whose reflection anchor lands above
    // water_y). Small epsilon so the row right at the water line still
    // renders — otherwise a sharp visible gap appears along the surface.
    if (vertex_world_var.y > water_y_var + 0.001) {
        discard;
    }

    vec2 d = FRAGCOORD.xy - anchor_px;
    float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
    float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
    vec2 sxy = vec2(
        (sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
        (sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
    );

    // Per-fragment ripple jitter — sampled at the fragment's world XZ so
    // adjacent fragments produce smoothly varying noise (internal shimmer).
    vec2 jitter = vec2(0.0);
    float jitter_amp = ripple_strength * reflection_pixel_jitter_max;
    if (jitter_amp > 1e-4) {
        vec3 jp = vec3(vertex_world_var.xz * ripple_scale_a + ripple_offset_a, 0.0);
        float jx = refl_vnoise(jp) - 0.5;
        float jy = refl_vnoise(jp + vec3(11.7, 5.3, 0.0)) - 0.5;
        jitter = vec2(jx, jy) * 2.0 * jitter_amp;
    }

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

    // No silhouette tint on the prop reflection — static props don't fade
    // in/out. The reflection-tint dim still applies so it reads as a
    // reflection rather than a second copy of the sprite.
    ALBEDO = tex.rgb * light * reflection_tint;
}
