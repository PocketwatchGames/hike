shader_type spatial;
// Visible-only multimesh sibling of sprite_prop_shadow_multimesh.gdshader,
// dedicated to the BlockLightShadowProjector's color pass. Same billboard
// math but writes ALBEDO=1; cast shadow is OFF. The original
// sprite_prop_shadow_multimesh proxy keeps casting sun/moon shadows
// untouched. See block_light_shadow_caster.gdshader for the full
// rationale on why we use two proxies instead of one.
//
// `ambient_light_disabled` so AMBIENT_LIGHT doesn't bleed into the
// unshaded ALBEDO output and saturate the projector texture.
render_mode depth_draw_opaque, cull_disabled, unshaded, ambient_light_disabled, skip_vertex_transform;

uniform sampler2D sprite_texture : source_color, filter_nearest;

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

    vec3 vertex_world = world_origin
        + VERTEX.x * world_w * sprite_right_rolled
        + VERTEX.y * world_h * sprite_up_rolled;
    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(vertex_world, 1.0);

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
    ALBEDO = vec3(1.0);
}
