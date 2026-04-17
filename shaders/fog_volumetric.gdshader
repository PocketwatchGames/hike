shader_type spatial;
render_mode unshaded, depth_test_disabled, depth_draw_never, blend_mix, cull_disabled;
// The clip-cap pipeline writes 1 to the stencil buffer for pixels that
// represent cut-away ceilings (see voxel_backface_stencil.gdshader); the
// cap plane then draws only on those pixels. We want the inverse: fog
// should skip cap-stencil pixels entirely so the cutaway view stays clean.
stencil_mode read, compare_not_equal, 1;

// Screen-space volumetric fog + single-scattering light transport. Fullscreen
// quad in NDC that reconstructs each pixel's world position from DEPTH_TEXTURE,
// raymarches from the camera's near plane to that surface, accumulates fog
// density AND in-scattered sunlight / block light sampled from the voxel
// LightMap, and composites the result over the scene.
//
// Light shafts ("god rays") emerge because the sun-visibility channel of the
// LightMap is 0 inside terrain shadow volumes and >0 where sun reaches — so
// the inscatter integral brightens fog along the ray only where it's in
// direct sun, producing the diagonal beams that arc through gaps in the
// terrain. Point light halos fall out of the same loop via the block light
// channels.
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

// Voxel LightMap (registered by ChunkManager). R = sun visibility [0..1],
// GBA = accumulated block light RGB (torches, fill lights, etc.). Sampled
// at each raymarch step to drive in-scattering.
global uniform sampler3D light_map : filter_linear;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;

// Sun direction / color / intensity (shared with sky + voxel shaders, set by
// SkyController and ChunkManager). sun_world_dir points FROM the sun toward
// the ground, so -sun_world_dir is "toward the sun".
global uniform vec3 sun_world_dir;
global uniform vec3 sun_color;
global uniform float sun_intensity;

// Cloud occlusion parameters (shared with sky + terrain + sprite + water
// shaders, registered by SkyController). cloud_shadow.gdshaderinc reads
// these to compute projective cloud shadows cast along the sun direction.
global uniform vec2 cloud_scroll;
global uniform float cloud_scale;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
#include "res://shaders/cloud_shadow.gdshaderinc"

uniform vec3 fog_color : source_color = vec3(0.85, 0.88, 0.95);
// Defaults below are the actual tuned values for this game — if the
// material (.tres) lacks shader_parameter overrides, these produce the
// intended look. Previously the default was 4.0 (a dev placeholder) that
// only worked because the .tres overrode it to 0.05; editing the .tres
// in the Godot inspector can strip those overrides, revealing the
// mis-matched default as a fully-white screen.
uniform float fog_density : hint_range(0.0, 1.0) = 0.05;
uniform int fog_steps : hint_range(1, 64) = 48;
uniform float fog_max_distance = 100.0;
uniform bool fog_enabled = true;

// Atmospheric baseline density added to every raymarch sample on top of the
// fog_map value. The authored fog_map covers a thin band of ground mist
// above water, so outside that band `fog_map` is zero and no inscatter
// accumulates — which means no god rays anywhere except right above water.
// This uniform fills the air with a dust layer so shafts form wherever the
// cloud occlusion varies along the ray.
//
// ambient_fog_height is the linear falloff range (matches WorldGen's
// FOG_FALLOFF_HEIGHT for the authored mist): at water_level the ambient
// contribution is `ambient_density`, at water_level + ambient_fog_height
// it is 0. Without this falloff, `ambient_density` would apply at ALL
// elevations, so distant hilltops / treetops / cliff faces far above the
// water mist layer would read as fogged in — not what the authored design
// intends.
uniform float ambient_density : hint_range(0.0, 1.0) = 0.15;
uniform float ambient_fog_height = 8.0;

// In-scattering tuning.
// sun_shaft_intensity: multiplier on the sunlight inscatter term (raise to
//   make beams more dramatic; 0 disables shafts).
// block_halo_intensity: multiplier on the block-light inscatter term (raise
//   to make torch/lamp halos more visible; 0 disables halos).
// scatter_anisotropy: Henyey-Greenstein g. 0 = isotropic (uniform in all
//   directions), positive = forward-peaked (dramatic when looking toward
//   the sun, effectively invisible when looking away — ~25x dimmer at
//   g=0.55 for a camera pointing away from the sun).
// Default anisotropy is 0 because this game's isometric camera has a fixed
// pitch and usually does NOT face the sun: a forward-peaked phase would
// kill shaft visibility. Default shaft intensity is high (8) to compensate
// for the flat phase — isotropic scattering distributes energy over the
// full 4π steradians (factor 1/(4π) ≈ 0.08), so we need ~8x more raw
// intensity to reach the same brightness as a forward peak.
uniform float sun_shaft_intensity : hint_range(0.0, 32.0) = 8.0;
uniform float block_halo_intensity : hint_range(0.0, 32.0) = 6.0;
uniform float scatter_anisotropy : hint_range(-0.95, 0.95) = 0.0;

// Animated dust motes drifting through the shafts. The shaft geometry
// itself is anchored to the (static) sun occlusion in the LightMap, so
// without this the beams would look frozen. Noise modulates ONLY the sun
// inscatter contribution — haze stays smooth and doesn't crawl.
//   dust_strength: 0 disables dust entirely, 1 means beams vary between
//     0 and 2x intensity as motes pass through them.
//   dust_scale: world-space frequency of motes (higher = finer).
//   dust_scroll: world-space velocity of the mote field (units/sec).
uniform float dust_strength : hint_range(0.0, 1.0) = 0.55;
uniform float dust_scale = 0.18;
uniform vec3 dust_scroll = vec3(0.35, 0.12, -0.25);

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
// 3 = visualize accumulated in-scatter only (haze disabled). Shafts should
//     appear as bright diagonal bands; halos as colored blobs around lights.
// 4 = visualize the AVERAGE sun mask along the march ray. Uniform gray =
//     no shadow variation (no shafts possible); stripes/bands = sun-shadow
//     contrast along the ray (shafts will form here).
// 5 = visualize cloud_shadow() sampled directly at the reconstructed surface
//     position (no march). Black = cloud-free, white = fully shadowed.
//     Should show a bold drifting noise pattern. If uniform black / gray,
//     the cloud globals aren't reaching the shader (suspect: stale shader
//     cache — delete .godot/shader_cache/ and relaunch).
uniform int debug_mode : hint_range(0, 5) = 0;

const float INV_4PI = 0.0795774715459;

// Henyey-Greenstein phase function. g > 0 is forward-scattering; the shaft
// lighting gets a strong boost when the view ray is aligned with the inbound
// sun direction (i.e. looking toward the sun through dust).
float phase_hg(float cos_theta, float g)
{
    float g2 = g * g;
    float denom = 1.0 + g2 - 2.0 * g * cos_theta;
    // max() guards against a negative base under the fractional power when
    // g * cos_theta slightly overshoots 1 due to fp error.
    return INV_4PI * (1.0 - g2) / (pow(max(denom, 1e-4), 1.5));
}

// Cheap triple-sine value noise for animated dust. Not a true noise (has
// visible periodicity at low frequencies), but inexpensive per-sample and
// reads as drifting motes once multiple axes are combined. Keeps the march
// loop affordable at 24-48 steps per pixel.
float dust_noise(vec3 p)
{
    float n1 = sin(p.x * 1.3) * sin(p.y * 1.7 + 0.5) * sin(p.z * 1.1 - 0.3);
    float n2 = sin(p.x * 2.7 + 1.1) * sin(p.y * 2.3 - 0.7) * sin(p.z * 3.1 + 1.9);
    return 0.5 + 0.5 * (n1 * 0.65 + n2 * 0.35);
}

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

    // Phase values are view-direction-dependent but not per-step, so compute
    // once per fragment. cos_theta is the angle between the ray direction
    // and the direction TOWARD the sun (-sun_world_dir is the vector from
    // the receiver to the sun).
    vec3 to_sun = -normalize(sun_world_dir);
    float cos_theta = dot(ray_dir, to_sun);
    float phase_sun = phase_hg(cos_theta, scatter_anisotropy);
    // Block lights scatter isotropically — no preferred direction relative
    // to the view ray, so halos stay round regardless of camera facing.
    float phase_block = INV_4PI;

    float tau = 0.0;            // optical depth (integrated density * fog_density)
    float transmittance = 1.0;  // exp(-tau), tracked incrementally for inscatter weighting
    vec3 inscatter = vec3(0.0);
    // For debug mode 4: average sun mask seen along the ray.
    float sun_mask_sum = 0.0;
    float sun_mask_samples = 0.0;

    // When the camera is ABOVE the ceiling-clip (player's view is looking
    // DOWN through a cutaway that removed ceiling voxels to reveal interior
    // space), skip fog samples above the clip so the "outside" atmosphere
    // doesn't bleed into the revealed interior. This is constant per frame
    // because it keys off CAMERA_POSITION_WORLD, not per-pixel surface Y —
    // using surface Y here was wrong before, because a tall tree sprite's
    // top pixel reconstructed above clip while the same tree's ground
    // pixel reconstructed below, giving inconsistent per-pixel fog.
    //
    // With ambient density now falling off to 0 above water_level +
    // ambient_fog_height, outdoor rays pass through mostly-empty air at
    // the clip altitude anyway, so the earlier false-positive fog "fade
    // line" on outdoor views goes away — skipping above-clip contributes
    // nothing meaningful except in the cutaway-interior case.
    //
    // When the clip isn't active (camera_clip defaults to 1e20), the
    // camera is always below it, so apply_clip is false and the branch
    // is a no-op.
    bool apply_clip = CAMERA_POSITION_WORLD.y > camera_clip;

    if (march_dist >= 0.001 && fog_enabled)
    {
        for (int i = 0; i < fog_steps; i++)
        {
            float t = (float(i) + 0.5) * step_size;
            vec3 p = near_world + ray_dir * t;
            if (apply_clip && p.y > camera_clip)
            {
                continue;
            }
            vec3 uvw = (p - fog_map_origin) * fog_map_inv_size;
            if (any(lessThan(uvw, vec3(0.0))) || any(greaterThan(uvw, vec3(1.0))))
            {
                continue;
            }
            // Ambient density falls off linearly from full at water_level
            // to 0 at water_level + ambient_fog_height, matching the
            // authored fog_map's falloff shape. Keeps high-elevation air
            // clear so distant hilltops and treetops don't smear with fog.
            float h = (p.y - water_level) / max(ambient_fog_height, 1e-4);
            float ambient_falloff = clamp(1.0 - h, 0.0, 1.0);
            float d = texture(fog_map, uvw).r + ambient_density * ambient_falloff;
            if (d <= 0.0)
            {
                continue;
            }

            float sigma_t = d * fog_density;
            float seg_tau = sigma_t * step_size;

            // Sample the voxel LightMap for this world position. The sun
            // channel is already a shadow-aware mask (0 inside shadow volumes,
            // >0 where direct sun reaches), which is exactly what makes
            // shafts appear: the inscatter integrates bright only along ray
            // segments that are simultaneously in dust AND in sun.
            vec3 L = vec3(0.0);
            vec3 luvw = (p - light_map_origin) * light_map_inv_size;
            if (all(greaterThanEqual(luvw, vec3(0.0))) && all(lessThanEqual(luvw, vec3(1.0))))
            {
                vec4 lm = texture(light_map, luvw);

                // Animated dust motes modulate the sun shaft term only. Motes
                // drift in world space so the beams come alive without the
                // ambient haze crawling with them. Centered at 1.0 so the
                // average shaft brightness stays independent of dust_strength.
                vec3 dp = p * dust_scale + dust_scroll * TIME;
                float dust = mix(1.0, 2.0 * dust_noise(dp), dust_strength);

                // Projective cloud shadow: occludes the sun contribution
                // only. Block lights (torches) are unaffected. This is the
                // term that actually creates distinct shafts — the BFS sun
                // mask is nearly uniform in open air, so without cloud
                // variation the inscatter just produces a uniform glow.
                float cloud_mask = cloud_shadow(p, TIME);
                float sun_through_clouds = 1.0 - cloud_mask;

                vec3 sun_radiance = lm.r * sun_intensity * sun_color * sun_through_clouds;
                L += sun_radiance * phase_sun * sun_shaft_intensity * dust;
                L += lm.gba * phase_block * block_halo_intensity;

                sun_mask_sum += lm.r * sun_through_clouds;
                sun_mask_samples += 1.0;
            }

            // Single-scattering integral for this segment. sigma_s == sigma_t
            // (single-scatter albedo of 1); transmittance weights the
            // contribution so far-away samples are attenuated by the fog
            // between them and the camera.
            inscatter += transmittance * L * sigma_t * step_size;
            transmittance *= exp(-seg_tau);
            tau += seg_tau;
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
    else if (debug_mode == 3)
    {
        // Show pure inscatter over black — easy to see shaft geometry and
        // halo placement without haze on top.
        ALBEDO = inscatter;
        ALPHA = 1.0;
    }
    else if (debug_mode == 4)
    {
        // Average sun mask along the ray. Uniform gray = no shadow variation
        // (no shafts possible); dark bands = shadowed air that blocks shafts;
        // bright bands = sunlit air that produces shafts.
        float avg = sun_mask_samples > 0.0 ? sun_mask_sum / sun_mask_samples : 0.0;
        ALBEDO = vec3(avg);
        ALPHA = 1.0;
    }
    else if (debug_mode == 5)
    {
        // Cloud shadow sampled directly at the surface. Diagnoses the cloud
        // projection in isolation — the pattern should read as a bold drifting
        // noise field. Uniform black = cloud math returning 0 unconditionally
        // (suspect sun_world_dir or cloud_scale reading as 0, or stale cache).
        float cloud = cloud_shadow(surface_world, TIME);
        ALBEDO = vec3(cloud);
        ALPHA = 1.0;
    }
    else
    {
        // Composite haze + inscatter into blend_mix's non-premultiplied
        // expected form: output = scene * (1 - ALPHA) + ALBEDO * ALPHA, so
        // we want ALBEDO * ALPHA == haze * ALPHA + inscatter and
        // (1 - ALPHA) == transmittance. Divide is guarded for near-zero
        // alpha — inscatter also scales with sigma_t, so when alpha is tiny
        // inscatter is tiny too, keeping the guarded result well-behaved.
        float alpha = 1.0 - transmittance;
        vec3 haze = fog_color * alpha;
        vec3 total = haze + inscatter;
        ALBEDO = total / max(alpha, 1e-4);
        ALPHA = alpha;
    }
}
