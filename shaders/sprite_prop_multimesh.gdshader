shader_type spatial;
// Alpha-tested opaque (matches detail_sprite). depth_prepass_alpha is the
// key requirement: with MultiMesh we can't reorder per-frame for
// transparent-queue back-to-front sort, so we rely on the alpha-tested
// depth prepass to write correct cut-out depth before the color pass, and
// then sorts come for free out of the depth buffer.
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, ambient_light_disabled, skip_vertex_transform;

// Multimesh-instanced prop sprite. Forked from sprite_lit.gdshader for the
// prop migration. The visual quality target (texelFetch from a FRAGCOORD-
// anchor projection, exact source-pixel mapping) matches the prior per-
// instance LitSprite path, so a multimesh-rendered shrub looks identical
// to the same shrub authored as a Sprite3D.
//
// Per-instance channel allocation (see WorldPropScatter / MultimeshPropSprite):
//   - MODEL_MATRIX[3].xyz    : world position
//   - length(MODEL_MATRIX[0]): world width  (texture aspect × scale)
//   - length(MODEL_MATRIX[1]): world height (texture aspect × scale)
//   - INSTANCE_CUSTOM.xyz    : terrain_normal (world space) — for align>0
//   - INSTANCE_CUSTOM.w      : align_to_terrain in [0, 1]
//   - COLOR.r                : packed region_origin (origin.x*4096 + origin.y)
//   - COLOR.g                : packed sprite_size   (size.x*4096   + size.y)
//   - COLOR.b                : forward_offset (signed float, world meters)
//   - COLOR.a                : unused
//
// What forks a bucket (and thus a draw call): texture binding (sampler2D
// can't be per-instance in Godot 4), and the pass (visible vs. shadow vs.
// reflection — different shaders). Everything else is per-instance, so
// atlases-with-N-archetypes collapse to N×0 = 0 extra draws beyond the
// one bucket per (atlas, pass).
//
// Mirror is per-instance via a hash of MODEL_MATRIX[3].xz. Authors who
// want unmirrored art draw symmetric textures; the per-instance flip
// then has no visible effect. Cheaper than a uniform gate.

uniform sampler2D sprite_texture : source_color, filter_linear;
uniform bool subpixel_aa = true;

// --- Lighting / sky globals (verbatim from sprite_lit) ---
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float light_falloff_exp;
global uniform float sun_intensity;
global uniform vec3 sun_color;
global uniform float sun_ambient;
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform float cloud_shadow_strength;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
global uniform bool debug_white_albedo;
#include "res://shaders/cloud_shadow.gdshaderinc"

global uniform float camera_clip;
global uniform float camera_clip_prev;
global uniform float camera_clip_blend;
#include "res://shaders/clip_dither.gdshaderinc"
global uniform float sprite_stretch;

// Block-light shadow projector — see shaders/block_light_shadow.gdshaderinc.
global uniform sampler2D block_light_shadow_tex : filter_linear;
global uniform vec3 block_light_shadow_origin;
global uniform vec3 block_light_shadow_right;
global uniform vec3 block_light_shadow_up;
global uniform float block_light_shadow_size;
global uniform float block_light_shadow_strength;
global uniform float block_light_shadow_blur_uv;
global uniform bool block_light_shadow_enabled;
#include "res://shaders/block_light_shadow.gdshaderinc"

// Per-instance unpacked from MultiMesh data, passed via flat varyings to
// the fragment stage so the integer cast / hash happens once per vertex.
varying flat vec2 anchor_px;
varying flat vec3 world_origin;
varying flat vec2 sprite_right_screen;
varying flat vec2 sprite_up_screen;
varying flat ivec2 sprite_size;
varying flat ivec2 sprite_region_origin;
varying flat int sprite_mirror_var;
varying float cloud_dim;
varying vec3 frag_world_pos;

// Cheap 2D hash — same coefficients detail_sprite uses, so adjacent prop
// instances flip differently.
float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
    world_origin = MODEL_MATRIX[3].xyz;
    float world_w = length(MODEL_MATRIX[0].xyz);
    float world_h = length(MODEL_MATRIX[1].xyz);

    // Always-hash per-instance mirror coin from the world XZ. Authors who
    // want a non-mirroring archetype use symmetric art; the flip is then a
    // visual no-op and we save a bucket fork.
    sprite_mirror_var = (hash12(world_origin.xz + vec2(7.31, 13.71)) > 0.5) ? 1 : 0;

    // Unpack 12-bit-per-axis atlas coords from COLOR. The packing
    // (origin.x*4096 + origin.y) fits exactly in float32's 24-bit mantissa
    // for atlas sizes up to 4096 per axis. shift-by-12 / mask-low-12 in
    // GLSL recovers the components without precision loss. forward_offset
    // is now per-instance too (was a per-bucket uniform; moving it freed
    // the bucket key from forking on per-archetype offset values).
    int packed_origin = int(COLOR.r);
    sprite_region_origin = ivec2(packed_origin >> 12, packed_origin & 0xFFF);
    int packed_size = int(COLOR.g);
    sprite_size = ivec2(packed_size >> 12, packed_size & 0xFFF);
    float forward_offset = COLOR.b;

    float align = clamp(INSTANCE_CUSTOM.w, 0.0, 1.0);
    vec3 normal_in = INSTANCE_CUSTOM.xyz;
    // Default-safe: a zero-length normal (e.g. an instance with align=0
    // and no normal computed) collapses to upright via the world-up
    // fallback below.

    // Two-stage billboard (matches sprite_lit / detail_sprite):
    //   1) Yaw — rotate the sprite plane around world Y to face the camera.
    //   2) Roll — rotate within the plane around sprite_forward by the
    //      angle of the per-instance terrain normal projected into the
    //      plane. Lerped by `align`, so 0 = upright, 1 = full slope-lean.
    vec3 world_up = vec3(0.0, 1.0, 0.0);
    vec3 cam_back = INV_VIEW_MATRIX[2].xyz;
    vec3 sprite_right_yaw = normalize(cross(world_up, cam_back));
    vec3 sprite_forward = cross(sprite_right_yaw, world_up);

    // Camera-following sprite shift along the horizontal billboard-
    // forward axis (which from any point in the scene reads as "toward
    // the camera" since cam_back is the camera's behind-the-lens axis).
    world_origin += sprite_forward * forward_offset;

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
    // Stretch only the world-Y component of each rolled axis — same iso-
    // pitch compensation sprite_lit uses so a row of source pixels
    // projects to one screen-pixel row.
    vec3 right_stretched = vec3(sprite_right_rolled.x, sprite_right_rolled.y * stretch, sprite_right_rolled.z);
    vec3 up_stretched = vec3(sprite_up_rolled.x, sprite_up_rolled.y * stretch, sprite_up_rolled.z);

    // VERTEX.x in [-0.5, 0.5], VERTEX.y in [0, 1] — unit quad. World dims
    // come from MODEL_MATRIX column lengths; the shader recovers per-
    // source-pixel step in world space by dividing by sprite_size.
    vec3 vertex_world = world_origin
        + VERTEX.x * world_w * right_stretched
        + VERTEX.y * world_h * up_stretched;
    frag_world_pos = vertex_world;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

    // Project anchor + a one-source-pixel step along each axis into screen
    // space, so the fragment stage can invert the basis to recover the
    // source pixel. Step magnitude = world_w / sprite_size.x (and h / .y),
    // because `world_w` already encodes "sprite_size.x source pixels' worth
    // of world distance" — divide back out for one-pixel.
    float src_px_w = world_w / max(float(sprite_size.x), 1.0);
    float src_px_h = world_h / max(float(sprite_size.y), 1.0);
    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin, 1.0);
    vec4 right_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + src_px_w * right_stretched, 1.0);
    vec4 up_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world_origin + src_px_h * up_stretched, 1.0);
    vec2 origin_ndc = origin_clip.xy / origin_clip.w;
    vec2 right_ndc = right_clip.xy / right_clip.w;
    vec2 up_ndc = up_clip.xy / up_clip.w;
    anchor_px = floor((origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE + 0.5);
    sprite_right_screen = (right_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
    sprite_up_screen = (up_ndc - origin_ndc) * 0.5 * VIEWPORT_SIZE;
}

void fragment() {
    if (clip_dither_should_discard(world_origin.y, FRAGCOORD.xy)) {
        discard;
    }

    vec2 d = FRAGCOORD.xy - anchor_px;
    float basis_det = sprite_right_screen.x * sprite_up_screen.y - sprite_up_screen.x * sprite_right_screen.y;
    float inv_det = 1.0 / (abs(basis_det) < 1e-6 ? 1e-6 : basis_det);
    vec2 sxy = vec2(
        (sprite_up_screen.y * d.x - sprite_up_screen.x * d.y) * inv_det,
        (sprite_right_screen.x * d.y - sprite_right_screen.y * d.x) * inv_det
    );
    float half_w = float(sprite_size.x / 2);
    vec2 src_px = vec2(sxy.x + half_w, -sxy.y + float(sprite_size.y));

    ivec2 tex_coord = ivec2(int(floor(src_px.x)), int(floor(src_px.y)));
    bool sprite_mirror = sprite_mirror_var > 0;
    if (sprite_mirror) {
        tex_coord.x = sprite_size.x - 1 - tex_coord.x;
    }
    if (tex_coord.x < 0 || tex_coord.x >= sprite_size.x
        || tex_coord.y < 0 || tex_coord.y >= sprite_size.y) {
        discard;
    }

    vec2 px_aa;
    if (subpixel_aa) {
        vec2 seam = floor(src_px) + 0.5;
        vec2 dp = max(fwidth(src_px), vec2(1e-6));
        px_aa = seam + clamp((src_px - seam) / dp, -0.5, 0.5);
    } else {
        px_aa = floor(src_px) + 0.5;
    }
    px_aa = clamp(px_aa, vec2(0.5), vec2(sprite_size) - vec2(0.5));
    if (sprite_mirror) {
        px_aa.x = float(sprite_size.x) - px_aa.x;
    }

    vec2 atlas_size = vec2(textureSize(sprite_texture, 0));
    vec2 atlas_uv = (vec2(sprite_region_origin) + px_aa) / atlas_size;
    vec4 tex = texture(sprite_texture, atlas_uv);
    if (tex.a < 0.5) {
        discard;
    }

    // Lighting model identical to sprite_lit's split-EMISSION path so a
    // multimesh prop and a per-instance LitSprite under the same lightmap
    // sample shade the same.
    vec3 light_sample_pos = world_origin + vec3(0.0, 0.5, 0.0);
    vec3 uvw = (light_sample_pos - light_map_origin) * light_map_inv_size;
    vec4 lm = textureLod(light_map, uvw, 0.0);
    float sun_mask = pow(lm.r, light_falloff_exp);
    float cloud = cloud_shadow_ground(frag_world_pos, TIME);
    cloud_dim = 1.0 - cloud * cloud_shadow_strength;
    vec3 sun_lit = sun_mask * sun_intensity * sun_color;
    // Per-prop block_lit dim — sample at the anchor (one position for the
    // whole prop), so a tree under another tree's silhouette reads as
    // dimmed without cross-fading inside its own footprint.
    vec3 block_lit = lm.gba * block_light_shadow_factor(world_origin);

    NORMAL = vec3(0.0, 1.0, 0.0);
    if (debug_white_albedo) {
        tex.rgb = vec3(0.0);
    }
    ALBEDO = tex.rgb * sun_lit * (1.0 - sun_ambient);
    EMISSION = tex.rgb * (block_lit + sun_lit * sun_ambient * cloud_dim);
}

void light() {
    // ATTENUATION (directional shadow atlas) and cloud_dim compose as min()
    // — see sprite_lit / voxel_clip light() for the energy-gate rationale.
    const float INV_PI = 0.31830989;
    float light_energy = LIGHT_COLOR.r * INV_PI;
    DIFFUSE_LIGHT += ALBEDO * light_energy * min(ATTENUATION, cloud_dim);
}
