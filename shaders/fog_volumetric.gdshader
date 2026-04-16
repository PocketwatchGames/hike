shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, blend_mix, cull_disabled;
// The clip-cap pipeline writes 1 to the stencil buffer for pixels that
// represent cut-away ceilings (see voxel_backface_stencil.gdshader); the
// cap plane then draws only on those pixels. We want the inverse: fog
// should skip cap-stencil pixels entirely so the cutaway view stays clean.
stencil_mode read, compare_not_equal, 1;

// Screen-space volumetric fog. Fullscreen quad in NDC that reconstructs each
// pixel's world position from DEPTH_TEXTURE, raymarches from the camera's
// near plane to that surface, accumulates fog density sampled from a 3D
// density texture, and composites a tinted haze over the scene.
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

// World Y above which the voxel/sprite shaders discard pixels (ceiling
// cutaway, declared in project.godot, written each frame by GameCamera).
// Defaults to 1e20 when no clip is active. Fog samples above this Y belong
// to clipped-away geometry and must not contribute, or the interior view
// ends up foggy with atmosphere from the "outside" region overhead.
global uniform float camera_clip;

uniform vec3 fog_color : source_color = vec3(0.85, 0.88, 0.95);
uniform float fog_density : hint_range(0.0, 32.0) = 4.0;
uniform int fog_steps : hint_range(1, 64) = 24;
uniform float fog_max_distance = 150.0;
uniform bool fog_enabled = true;

// World Y of the water surface. The water shader uses depth_draw_never, so
// DEPTH_TEXTURE at water pixels returns the depth of the opaque terrain
// BEHIND the water rather than the water surface itself. Without clamping,
// the fog raymarch continues past the water surface and its alpha layer
// masks the water's depth-tint, flattening the visible water. Rays that
// cross this Y going downward get their march clamped to stop at the
// water surface. Default -1e20 effectively disables the clamp.
uniform float water_level = -1e20;

// 0 = normal fog render.
// 1 = visualize reconstructed surface world Y as grayscale (black = lowest Y
//     in the fog grid, white = highest). Ground voxels should read as dark,
//     cliff tops as mid, sky as the brightest value (far plane).
// 2 = visualize the fog density directly sampled at the surface (no march).
//     Bright stripes should form over water/ground where fog lives.
uniform int debug_mode : hint_range(0, 2) = 0;

void vertex()
{
    // QuadMesh(2,2) has VERTEX.xy in [-1, 1] at the origin; emitting those
    // as clip-space coordinates covers the whole screen regardless of where
    // the mesh sits in world space. w=1 disables perspective divide.
    POSITION = vec4(VERTEX.xy, 1.0, 1.0);
}

void fragment()
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

    float step_size = march_dist / float(fog_steps);
    float tau = 0.0;

    if (march_dist >= 0.001 && fog_enabled)
    {
        for (int i = 0; i < fog_steps; i++)
        {
            float t = (float(i) + 0.5) * step_size;
            vec3 p = near_world + ray_dir * t;
            // Skip samples above the ceiling-clip height — those voxels are
            // "cut away" by the voxel/sprite shaders, and fog from that band
            // would bleed into the cutaway interior view.
            if (p.y > camera_clip)
            {
                continue;
            }
            vec3 uvw = (p - fog_map_origin) * fog_map_inv_size;
            if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
            {
                continue;
            }
            float d = texture(fog_map, uvw).r;
            tau += d * step_size * fog_density;
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
    else
    {
        ALBEDO = fog_color;
        ALPHA = 1.0 - exp(-tau);
    }
}
