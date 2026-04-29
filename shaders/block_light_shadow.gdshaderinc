// Block-light shadow projector sampling.
//
// BlockLightShadowProjector renders sprite silhouettes from above into a
// SubViewport, exposing the result as `block_light_shadow_tex`. Lit
// shaders sample it at each fragment's world position to dim the
// `block_lit` term where a sprite stands above — gives sprites a soft
// grounding shadow on torchlit/cave/indoor surfaces without dimming
// areas that aren't actually under any sprite.
//
// World → UV uses the projector camera's basis directly: project the
// world-relative offset onto the camera's right/up axes and divide by
// the orthographic full extent. Avoids declaring a mat4 global, which
// Godot 4.6 declines to register from project.godot's [shader_globals]
// section (declaration parses, but `RenderingServer.GlobalShaderParameterSet`
// then errors with `variables.has(p_name)==false`).
//
// Caller is responsible for declaring these globals before including
// this file (other shaders may already own them as well, so we don't
// redeclare to avoid multi-decl errors):
//
//   global uniform sampler2D block_light_shadow_tex;       // BlockLightShadowProjector
//   global uniform vec3 block_light_shadow_origin;         // projector camera position
//   global uniform vec3 block_light_shadow_right;          // projector camera +X (world)
//   global uniform vec3 block_light_shadow_up;             // projector camera +Y (world, V-flipped)
//   global uniform float block_light_shadow_size;          // orthographic full extent (world units)
//   global uniform float block_light_shadow_strength;      // 0..1 dim multiplier
//   global uniform float block_light_shadow_blur_uv;       // blur radius in UV units (= texels / textureSize)
//   global uniform bool block_light_shadow_enabled;        // CVar block_light_shadow

// Returns block_lit attenuation in [1 - strength, 1]:
//   1.0           = no silhouette covers this fragment from above
//   1 - strength  = silhouette fully covers (block_lit dimmed by `strength`)
//
// When the CVar is off the projector stops rendering and the global is
// false, so this returns 1.0 unconditionally — rendering is byte-
// identical to pre-feature. The branch is on a uniform bool so the GPU
// can hoist it out of per-fragment work.
//
// Soft-edge blur: when `block_light_shadow_blur_uv > 0`, sample a 5-tap
// cross around the center texel and average. Keeps cost low (5 fetches,
// uniform-radius branch) while softening the alpha-cut edges of the
// proxy silhouette into a readable drop-shadow.
float block_light_shadow_factor(vec3 world_pos)
{
    if (!block_light_shadow_enabled) {
        return 1.0;
    }
    vec3 offset = world_pos - block_light_shadow_origin;
    float inv_size = 1.0 / max(block_light_shadow_size, 1e-4);
    vec2 uv = vec2(
        dot(offset, block_light_shadow_right) * inv_size + 0.5,
        dot(offset, block_light_shadow_up) * inv_size + 0.5
    );
    // Out-of-frustum fragments (the projector covers a finite radius
    // around the player) have no coverage data — treat as unshadowed.
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0) {
        return 1.0;
    }
    float coverage;
    if (block_light_shadow_blur_uv > 0.0) {
        // 5-tap cross blur. Center weighted 4×, neighbors 1× each. Sums to
        // 8; weights are tuned for a soft gaussian-ish look without doing
        // the full 9-tap.
        float bu = block_light_shadow_blur_uv;
        float c = texture(block_light_shadow_tex, uv).r;
        float l = texture(block_light_shadow_tex, uv + vec2(-bu, 0.0)).r;
        float r = texture(block_light_shadow_tex, uv + vec2( bu, 0.0)).r;
        float u = texture(block_light_shadow_tex, uv + vec2(0.0, -bu)).r;
        float d = texture(block_light_shadow_tex, uv + vec2(0.0,  bu)).r;
        coverage = (c * 4.0 + l + r + u + d) * 0.125;
    } else {
        coverage = texture(block_light_shadow_tex, uv).r;
    }
    return 1.0 - coverage * block_light_shadow_strength;
}
