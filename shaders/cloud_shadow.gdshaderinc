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
// see res://assets/textures/skybox/cloud_noise.tres). One texture() call per
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
//   global uniform vec3 sun_world_dir;                         // SkyController
//   global uniform vec2 cloud_offset;                          // SkyController (pre-integrated XZ offset — C# accumulates per frame, shader doesn't multiply by time)
//   global uniform float cloud_threshold;                      // noise value above which = cloud
//   global uniform float cloud_sharpness;                      // 0 = soft edges, 1 = hard step
//   global uniform sampler2D cloud_noise_tex                   // baked seamless FBM (owned by sky_common;
//       : filter_linear, repeat_enable;                        //  if you don't include sky_common, declare locally)
//   global uniform float cloud_altitude;                       // owned by sky_common; declare locally if not included

// Vertical (top-down, non-leaky) sky-reach field, registered by ChunkManager.
// R = how much open sky is directly above a voxel: 1 under open sky, 0 under
// any solid cover, with canopy folded in. Owned by THIS include (unlike the
// cloud globals above) because every gated consumer needs it and none of them
// declare it themselves — a shader that includes this file must NOT redeclare
// these three. rain_drop declares its own set; it doesn't include this file.
global uniform sampler3D sky_exposure_map : filter_linear, repeat_enable;
global uniform vec3 sky_exposure_map_origin;
global uniform vec3 sky_exposure_map_inv_size;
// Sky exposure at or above which a fragment tracks the live cloud pattern;
// below it, it reads as fully shadowed. Pushed by SkyController.
global uniform float cloud_shadow_sky_cover;

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
    vec2 cuv = cloud_xz * CLOUD_TEX_TILING + cloud_offset;
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

// Ground cloud shadow — samples the cloud texture directly at world XZ,
// with NO sun-direction projection. Physically this is "clouds cast
// shadows straight down"; stylistically it's a 2D pattern draped across
// the world that drifts purely with wind via cloud_offset. Apparent
// movement speed on the ground is therefore CONSTANT regardless of sun
// elevation — avoids the `to_sun.xz * cloud_altitude / to_sun.y` term
// whose magnitude and direction both sweep quickly at grazing sun,
// making shadows race across the ground at dawn/dusk.
//
// Surface lighting wants the SKY-GATED wrapper below, not this — bare, it
// drapes over cave and interior surfaces too. Left ungated for the one caller
// that must opt out: distant backdrop geometry, whose sky-exposure probe would
// wrap onto unrelated world. The fog raymarch needs real sun-direction
// projection for shaft ray-coherence — it uses cloud_shadow() /
// cloud_shadow_shafts().
float cloud_shadow_ground(vec3 world_pos, float time)
{
    return cs_sample(world_pos.xz, time);
}

// Sky-exposure gate for the ground shadow. The cloud pattern is a 2D drape
// with no vertical extent, so on its own it reaches a cave floor at full
// contrast; every consumer only hides it by multiplying into the sun term,
// and the sun term is NOT zero indoors (the sunlight BFS spreads laterally
// ~MAX_LIGHT / sunFalloffPerVoxel voxels in from any opening, and the light
// volume is trilinear so it bleeds through thin rock). Result: drifting cloud
// blotches on cave and interior surfaces. sky_exposure_map is the field that
// actually answers "is there cover straight up", so gate on it.
//
// `probe_pos` MUST be the same AIR-voxel position the caller samples the light
// map at (surface + half a voxel along the normal, typically). A fragment's own
// world position sits inside the solid it belongs to, where exposure is 0 — probe
// there and cloud shadows vanish from open ground too.
float cs_sky_gate(vec3 probe_pos)
{
    // Editor preview / first frames: ChunkManager hasn't registered the real
    // volume yet and the placeholder samples black, which would read as "fully
    // covered" and erase every cloud shadow. Same globals_ready idiom flat_lit
    // uses for the light map.
    if (length(sky_exposure_map_inv_size) < 1e-6)
    {
        return 1.0;
    }
    // Toroidal window with a zero origin — sample directly and let the repeat
    // sampler wrap. Don't add a [0,1] guard: uvw is only in range near the world
    // origin, so a guard would switch the gate off across most of the map.
    // Geometry past the window wraps onto unrelated world; callers that draw a
    // distant backdrop (voxel_clip's baked-sun path) opt out of the gate instead.
    vec3 uvw = (probe_pos - sky_exposure_map_origin) * sky_exposure_map_inv_size;
    float exposure = texture(sky_exposure_map, uvw).r;
    return smoothstep(0.0, max(cloud_shadow_sky_cover, 1e-4), exposure);
}

// Ground cloud shadow that goes fully shadowed under overhead cover. The
// default for all surface lighting — prefer this over bare
// cloud_shadow_ground().
//
// Cover is just another occluder, so it resolves to the SAME single shadow
// value a cloud gives — the darkest-occluder-wins rule the light() stages
// already apply between this and ATTENUATION. Two things fall out of that:
// there's no third brightness level between lit and shadowed, and the gate can
// only ever darken. Fading the shadow out toward 1.0 instead (the obvious
// reading of "no clouds reach in here") leaves a bright strip under a cave
// overhang or a house eave — the sheltered fragment keeps its leaked sun at
// full brightness while a passing cloud darkens the open ground beside it.
//
// Consequence worth knowing: on a CLEAR day a sheltered fragment still takes
// this shadow, so cloud_shadow_strength doubles as how hard an overhang
// shadows. That reads correctly (an eave casts a shadow in full sun) but if it
// ever needs to be dialled separately, add a cover strength — don't soften it
// back toward an average.
float cloud_shadow_ground_gated(vec3 world_pos, vec3 probe_pos, float time)
{
    float cloud = cs_sample(world_pos.xz, time);
    return mix(1.0, cloud, cs_sky_gate(probe_pos));
}

// Same, probing half a voxel up. For shaders whose light() stage recomputes the
// shadow and has only the fragment's world position in scope — the lift matches
// the one those shaders already apply to their light-map sample in fragment().
float cloud_shadow_ground_gated_up(vec3 world_pos, float time)
{
    return cloud_shadow_ground_gated(world_pos, world_pos + vec3(0.0, 0.5, 0.0), time);
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
    vec2 cuv = cloud_xz * CLOUD_TEX_TILING + cloud_offset;
    float c = texture(cloud_noise_tex, cuv).r;
    float band = max(1e-4, 1.0 - shaft_sharpness);
    return smoothstep(cloud_threshold, min(cloud_threshold + band, 1.0), c);
}
