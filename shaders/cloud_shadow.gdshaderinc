// Projective cloud shadow. Given a world-space point, projects along the
// sun direction up to a flat cloud plane at `cloud_altitude`, samples a
// seamless noise texture at the intersection XZ, and returns occlusion
// in [0..1] (0 = sun fully visible, 1 = fully shadowed).
//
// Points stacked along the sun direction hit the SAME cloud XZ, so they
// share a shadow value — this is exactly how a real directional light
// casts cloud shadows through the air and onto the ground. Perpendicular
// to the sun direction, the texture varies, producing bright/dark bands
// that become shafts when sampled through fog in the raymarcher.
//
// The noise comes from a pre-baked NoiseTexture2D (seamless FBM Perlin,
// see res://assets/textures/cloud_noise.tres). One texture() call per
// query is cheaper than the 5-octave procedural fbm this used to call,
// and the baked texture has no visible grid artifacts — procedural
// value-noise lattices show up as axis-aligned rectangular patches at
// the base octave's frequency, which is what made the old shadows look
// blocky at ~4 world units regardless of cloud_scale.
//
// Caller is responsible for declaring these globals before including
// this file (they are already owned by sky_common / SkyController, so we
// don't redeclare them here to avoid multi-decl errors):
//
//   global uniform vec3 sun_world_dir;    // SkyController
//   global uniform vec2 cloud_scroll;     // SkyController (units/sec in XZ)
//   global uniform float cloud_threshold; // noise value above which = cloud
//   global uniform float cloud_sharpness; // 0 = soft edges, 1 = hard step
//
// This file owns `cloud_altitude` and the cloud noise sampler.

global uniform float cloud_altitude;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;

// World-unit → texture-UV multiplier. Smaller values make cloud features
// span more world units (broader shadows). At 0.01 and a 512² texture
// baked from FastNoiseLite(frequency=0.01), a "cloud feature" is roughly
// 20 world units across, which reads as typical shadow patches on an
// isometric outdoor scene. Expose as a uniform / CVar if live tuning
// becomes useful; for now a const keeps the include lean.
const float CLOUD_TEX_TILING = 0.012;

float cs_sample(vec2 cloud_xz, float time)
{
    // Texture() wraps automatically via repeat_enable — the texture was
    // baked seamless so no tile seams are visible.
    vec2 cuv = cloud_xz * CLOUD_TEX_TILING + cloud_scroll * time;
    float c = texture(cloud_noise_tex, cuv).r;
    // Remap noise → cloud density. cloud_threshold is the low edge of the
    // transition (below this noise value = no cloud). cloud_sharpness
    // controls the band width: 0 = soft gradient across [threshold, 1],
    // 1 = hard step at threshold. Clamp the upper edge so smoothstep
    // stays well-defined when threshold is near 1.
    float band = max(1e-4, 1.0 - cloud_sharpness);
    return smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
}

// Y-dependent projective cloud shadow — the receiver's own Y is used when
// projecting along the sun direction, so points stacked on the same sun ray
// hit the SAME cloud XZ. This coherence is REQUIRED for the fog raymarch:
// air samples along a single sun ray must share a cloud sample or shafts
// decorrelate and stop reading as beams.
//
// Side effect: on sloped / vertical terrain, receiver Y varies per pixel
// across a single triangle, so the cloud sample sweeps across the texture
// within that triangle. Reads as faceted edges on terrain — correct for
// shaft coherence, ugly on solid surfaces. Use cloud_shadow_ground() for
// terrain lighting instead.
float cloud_shadow(vec3 world_pos, float time)
{
    // Vector from receiver TOWARD the sun. sun_world_dir points FROM the
    // sun, so to_sun = -sun_world_dir. max() below guards the divide when
    // the sun is near the horizon: without it, t blows up and the cloud
    // sample lands hundreds of units away, reading as noise.
    vec3 to_sun = -normalize(sun_world_dir);
    float up = max(to_sun.y, 0.15);
    float t = (cloud_altitude - world_pos.y) / up;
    vec2 cloud_xz = world_pos.xz + to_sun.xz * t;
    return cs_sample(cloud_xz, time);
}

// Y-independent cloud shadow — projects from a REFERENCE altitude (0) rather
// than the receiver's Y. Physically: clouds at infinity. Visually: adjacent
// pixels on a sloped face sample adjacent texels smoothly, so cloud shadows
// on terrain look like a uniform 2D pattern draped across the world XZ.
// Loses the ray-coherence that fog shafts need.
//
// Use this for ALL surface lighting (terrain, sprites, water). Use
// cloud_shadow() for the fog raymarch.
float cloud_shadow_ground(vec3 world_pos, float time)
{
    vec3 to_sun = -normalize(sun_world_dir);
    float up = max(to_sun.y, 0.15);
    float t = cloud_altitude / up;
    vec2 cloud_xz = world_pos.xz + to_sun.xz * t;
    return cs_sample(cloud_xz, time);
}

// Shaft-specific variant — re-projects along the sun (Y-coherent) and
// applies a caller-provided sharpness instead of the scene-wide
// cloud_sharpness. Lets the fog shader crank shaft contrast high
// without affecting sky dome or terrain-shadow softness, which
// typically want softer cloud edges.
float cloud_shadow_shafts(vec3 world_pos, float time, float shaft_sharpness)
{
    vec3 to_sun = -normalize(sun_world_dir);
    float up = max(to_sun.y, 0.15);
    float t = (cloud_altitude - world_pos.y) / up;
    vec2 cloud_xz = world_pos.xz + to_sun.xz * t;
    vec2 cuv = cloud_xz * CLOUD_TEX_TILING + cloud_scroll * time;
    float c = texture(cloud_noise_tex, cuv).r;
    float band = max(1e-4, 1.0 - shaft_sharpness);
    return smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
}
