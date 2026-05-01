shader_type spatial;
// depth_prepass_alpha: required for the multimesh shadow path even though
// the per-instance LitSprite shadow_caster.gdshader doesn't use it. The
// difference is sortability — Godot can per-instance-sort separate Sprite3D
// shadow proxies into the shadow atlas, but inside one MultiMesh the
// instance order is fixed at upload time, so overlapping alpha-cut
// fragments z-fight in the atlas without an explicit alpha-tested depth
// prepass. Symptom of leaving this off: shadows visibly shimmer as the
// camera moves. Same reason detail_sprite.gdshader has it.
render_mode depth_prepass_alpha, depth_draw_opaque, cull_disabled, unshaded, skip_vertex_transform;

// Multimesh-instanced shadow caster for prop sprites. Forked from
// shadow_caster.gdshader; per-instance values come from MultiMesh data the
// same way sprite_prop_multimesh.gdshader unpacks them. Identical sun-
// billboard math: during the shadow atlas pass, INV_VIEW_MATRIX is the
// sun's camera so the silhouette is correct from the sun's POV.
//
// Per-instance channel allocation matches the visible shader exactly so
// scatter contributions are byte-identical between the two buckets:
//   - MODEL_MATRIX[3].xyz    : world position
//   - length(MODEL_MATRIX[0]): world width
//   - length(MODEL_MATRIX[1]): world height
//   - INSTANCE_CUSTOM.xyz    : terrain_normal
//   - INSTANCE_CUSTOM.w      : align_to_terrain
//   - COLOR.r                : packed region_origin (origin.x*4096 + origin.y)
//   - COLOR.g                : packed sprite_size   (size.x*4096   + size.y)
//   - COLOR.b                : forward_offset (cylinder radius in meters —
//                              same value the visible bucket uses; here it
//                              drives the head→feet shadow nudge so the
//                              silhouette starts at the prop's sun-facing
//                              edge instead of bisecting the cylinder)
//   - COLOR.a                : unused

uniform sampler2D sprite_texture : source_color, filter_nearest;
// World-space direction sunlight TRAVELS — owned by SkyController. Used
// below to nudge the silhouette toward the sun by `forward_offset`, mirroring
// the per-instance shadow_caster.gdshader path that LitSprite shadow proxies
// take. Without this shift the silhouette quad's base sits at the model
// origin (= cylinder centerline), so the cast shadow's near edge starts at
// the prop's center instead of its sun-facing perimeter — visually reads
// as a disconnected shadow several voxels past the prop, especially on
// large rocks/trees with cylinder radii > 1m.
global uniform vec3 sun_world_dir;

varying vec2 src_uv;
varying flat int sprite_mirror_var;
varying flat ivec2 sprite_size;
varying flat ivec2 sprite_region_origin;

float hash12(vec2 p) {
    vec3 p3 = fract(vec3(p.xyx) * 0.1031);
    p3 += dot(p3, p3.yzx + 33.33);
    return fract((p3.x + p3.y) * p3.z);
}

void vertex() {
    vec3 world_origin = MODEL_MATRIX[3].xyz;
    float world_w = length(MODEL_MATRIX[0].xyz);
    float world_h = length(MODEL_MATRIX[1].xyz);
    sprite_mirror_var = (hash12(world_origin.xz + vec2(7.31, 13.71)) > 0.5) ? 1 : 0;
    int packed_origin = int(COLOR.r);
    sprite_region_origin = ivec2(packed_origin >> 12, packed_origin & 0xFFF);
    int packed_size = int(COLOR.g);
    sprite_size = ivec2(packed_size >> 12, packed_size & 0xFFF);
    float align = clamp(INSTANCE_CUSTOM.w, 0.0, 1.0);
    vec3 normal_in = INSTANCE_CUSTOM.xyz;

    // Sun-aligned yaw + roll-toward-terrain-normal — same math as
    // shadow_caster.gdshader. cam_forward during the shadow pass is the
    // sun's forward, so horizontalising it gives a vertical sprite plane
    // facing the sun even at low elevation.
    vec3 world_up = vec3(0.0, 1.0, 0.0);
    vec3 cam_forward = -INV_VIEW_MATRIX[2].xyz;
    vec3 light_horiz = normalize(vec3(cam_forward.x, 0.0, cam_forward.z));
    vec3 sprite_right_yaw = normalize(cross(world_up, light_horiz));
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

    // Head→feet shadow nudge: same logic as shadow_caster.gdshader. Read
    // forward_offset (cylinder radius) from COLOR.b and shift world_origin
    // toward the sun along the ground plane so the silhouette quad's base
    // sits at the sun-facing edge of the prop's collider rather than its
    // centerline. sun_world_dir points from the sun toward the scene, so
    // -horizontal(sun_world_dir) is the toward-sun direction.
    float forward_offset = COLOR.b;
    vec3 sun_horiz = vec3(sun_world_dir.x, 0.0, sun_world_dir.z);
    float sun_horiz_len = length(sun_horiz);
    if (sun_horiz_len > 1e-4 && forward_offset != 0.0) {
        vec3 head_to_feet = -sun_horiz / sun_horiz_len;
        world_origin += head_to_feet * forward_offset;
    }

    vec3 vertex_world = world_origin
        + VERTEX.x * world_w * sprite_right_rolled
        + VERTEX.y * world_h * sprite_up_rolled;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

    // Unit-quad UV: VERTEX.x in [-0.5, 0.5] → 0..1; VERTEX.y in [0, 1]
    // gets flipped to 1..0 so src_uv.y=0 reads the top row of the sprite.
    src_uv = vec2(VERTEX.x + 0.5, 1.0 - VERTEX.y);
}

void fragment() {
    if (src_uv.x < 0.0 || src_uv.x > 1.0 || src_uv.y < 0.0 || src_uv.y > 1.0) {
        discard;
    }
    ivec2 tex_coord = ivec2(
        int(floor(src_uv.x * float(sprite_size.x))),
        int(floor(src_uv.y * float(sprite_size.y)))
    );
    tex_coord = clamp(tex_coord, ivec2(0), sprite_size - ivec2(1));
    if (sprite_mirror_var > 0) {
        tex_coord.x = sprite_size.x - 1 - tex_coord.x;
    }

    vec4 tex = texelFetch(sprite_texture, tex_coord + sprite_region_origin, 0);
    if (tex.a < 0.5) {
        discard;
    }
    // Godot writes depth automatically into the shadow atlas; ALBEDO is
    // unused for shadow-only meshes.
    ALBEDO = vec3(0.0);
}
