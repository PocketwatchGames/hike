shader_type spatial;
render_mode blend_mix, unshaded, depth_draw_never, cull_disabled, skip_vertex_transform;

// Floating dust-mote particle shader. Replaces the old in-shader fog motes
// (formerly in shaders/fog_volumetric.gdshader) with a real GPU particle
// system modeled on rain (scripts/client/RainEffect.cs + rain_drop.gdshader).
//
// A volumetric/cellular shader couldn't deliver the look — pixel specks that
// travel large distances, each sparkling independently — so motes are now
// real particles (scenes/fx/motes.tscn) and this shader just draws +
// gates each one:
//   - Each particle is a fixed-pixel-size speck laid out in SCREEN PIXELS
//     (like rain's streaks) so it stays 1-2 px regardless of the
//     orthographic zoom.
//   - Gated per-fragment to the intense sunbeams, the same way rain is gated
//     to open sky: sample the voxel sun mask (light_map.r) AND the projective
//     cloud shadow at the particle's world position, and discard/fade where
//     lit = light_map.r * (1 - cloud) falls below beam_threshold. Discard
//     above camera_clip so motes never paint on the cutaway-hidden interior
//     ceiling when the player is inside.
//   - Each speck sparkles INDEPENDENTLY via a per-particle position-hash
//     phase (high spatial frequency → neighbours decorrelate, so the field
//     never pulses in regional waves), and balances its colour between a
//     dust-coloured base and the sun/moon-tinted glint rather than adding
//     white (the old additive glint blew the motes out).
//
// Editor-preview fallback: light_map_inv_size is vec3(0) until ChunkManager
// registers the LightMap; gate the clips on that so motes render in the
// material preview.

global uniform sampler3D light_map : filter_linear, repeat_enable;
global uniform vec3 light_map_origin;
global uniform vec3 light_map_inv_size;
global uniform float camera_clip;
// Primary sky-light colour, already blended sun→moon→sunset by SkyController
// (= palette SunTint, the same global the voxel / rain shaders light with).
// The mote specular glint reads this directly so it always matches the active
// light without any local sun/moon math.
global uniform vec3 sun_color;

// Cloud-shadow globals (registered by SkyController) + the shared include
// that provides cloud_shadow_shafts(). Same set the fog shader declares
// before including this file.
global uniform vec3 sun_world_dir;
global uniform vec2 cloud_offset;
global uniform float cloud_threshold;
global uniform float cloud_sharpness;
global uniform sampler2D cloud_noise_tex : filter_linear, repeat_enable;
global uniform float cloud_altitude;
#include "res://shaders/cloud_shadow.gdshaderinc"

// Base dust albedo, pushed from SkyController every frame (palette DustColor).
// The glint colour is NOT a material param — it's the global sun_color above,
// so it always matches the active sky light (sun by day / moon by night).
uniform vec3 dust_color : source_color = vec3(0.8, 0.72, 0.55);
// lit = light_map.r * (1 - cloud) must clear this for the speck to show;
// it fades in over beam_softness just above the threshold.
uniform float beam_threshold : hint_range(0.0, 1.0) = 0.45;
uniform float beam_softness : hint_range(0.01, 1.0) = 0.2;
// --- Shaft occlusion gate -------------------------------------------------
// Matches the fog god-rays: a mote only shows where there's actual occlusion
// STRUCTURE (cloud cover, or terrain/foliage casting shadow nearby), not across
// uniformly-open lit sky. Without this a clear flat desert reads as "lit
// everywhere" and motes appear over the whole scene even with no shafts.
//   - terrain/foliage: contrast (max-min) of the voxel sun mask sampled at four
//     horizontal neighbours shaft_sample_radius metres out — high at a shadow
//     edge, ~0 over open flat ground.
//   - cloud: the global cloud cover (pushed from SkyController) so overcast /
//     broken-cloud days light motes up in the sunlit gaps.
// occlusion = max(terrain contrast, cloud cover), remapped through
// [occlusion_min, occlusion_min + occlusion_soft]. Block-lit motes bypass it.
uniform float shaft_sample_radius : hint_range(0.25, 8.0) = 2.0;
uniform float shaft_occlusion_min : hint_range(0.0, 1.0) = 0.15;
uniform float shaft_occlusion_soft : hint_range(0.01, 1.0) = 0.3;
uniform float cloud_cover = 0.0;
// Animated dust-noise structuring — the SAME term the fog uses to sculpt beams
// into streaks (sampled sun-projected so it streaks along the sun direction),
// so motes sit inside the same noisy beam shape and their edges track the
// shafts. These mirror the fog's dust_noise_* uniforms and are pushed from
// SkyController with identical values + scroll offsets. strength 0 = no noise.
uniform float dust_noise_scale = 0.062;
uniform float dust_noise_threshold : hint_range(0.0, 1.0) = 0.2;
uniform float dust_noise_sharpness : hint_range(0.0, 1.0) = 0.5;
uniform float dust_noise_strength : hint_range(0.0, 1.0) = 0.7;
uniform vec2 dust_noise_offset_a = vec2(0.0);
uniform vec2 dust_noise_offset_b = vec2(0.0);
// --- Local air thickness (authored fog volumes + roof interior dust) -------
// Same per-voxel field the fog raymarch reads, bound per-material rather than
// as a global for the reason fog_volumetric documents (a runtime-registered
// global sampler errors at editor load and locks the inspector out of the
// material). MoteEffect binds all three from ChunkManager.FogVolume.
//
// STRICTLY A BOOST. Every term below folds back to the un-dusty expression at
// dust 0, so clean air renders exactly as it did before this existed — which
// is also what keeps stepping into a dusty room from changing the air outside
// it, since the emission box spans far more than the room.
uniform sampler3D fog_map : filter_linear, repeat_enable;
uniform vec3 fog_map_origin;
uniform vec3 fog_map_inv_size;
// Air thinner than this contributes no boost at all — keeps the faint ambient
// fog most of the world carries from lifting motes everywhere. The window
// [min, full] is deliberately narrow and sits LOW: the same channel drives the
// fog raymarch's extinction, so an author who has to reach dust 1.0 to get a
// good mote field gets a washed-out room along with it. Saturating the mote
// response early is what lets interiorDust / caveDustAmount stay modest.
uniform float dust_boost_min : hint_range(0.0, 1.0) = 0.05;
uniform float dust_boost_full : hint_range(0.05, 1.0) = 0.5;
uniform float dust_boost_strength : hint_range(0.0, 1.0) = 1.0;
// --- Density reconciliation ----------------------------------------------
// The particle COUNT is one global number for the whole emission box, sized by
// MoteEffect from whichever is thicker: the regional weather air, or the air at
// the player. That makes standing in a dusty room raise the count everywhere in
// the box — including the open ground outside, where the gates are at their
// most permissive (a roof's edge in the sun mask is maximum shadow contrast, so
// the shaft gate passes hardest right outside the walls).
//
// So the shader culls each speck back to the density its OWN position earns:
// budget is the density the count was sized for, weather is the regional
// baseline, and a fragment keeps specks in proportion to what it deserves out
// of that budget. Standing in dust therefore raises the count and the room
// spends it, while the air outside stays at the density it always had.
uniform float mote_budget_density : hint_range(0.0, 1.0) = 1.0;
uniform float mote_weather_density : hint_range(0.0, 1.0) = 1.0;
uniform float local_dust_density_scale : hint_range(0.0, 2.0) = 1.0;
// Fraction of beam_threshold that survives in full dust. Thick air scatters
// enough that dimmer light still picks specks out, so the light gate relaxes.
uniform float dust_threshold_relax : hint_range(0.05, 1.0) = 0.35;
// Extra opacity in full dust. >0 makes a dusty room read thicker, not just
// more populated.
uniform float dust_alpha_gain : hint_range(0.0, 2.0) = 0.6;
// --- Block light (torch / campfire / lantern) -----------------------------
// The light_map's GBA channels carry accumulated block-light RGB at fairly
// small magnitudes (the fog multiplies them ~6x for its halos), so both terms
// below want large multipliers to read.
//
// REVEAL and APPEARANCE are deliberately separate responses to the same field.
// They used to share one sqrt, which forced the two to move together: a light's
// deposit falls ~29:1 from its core to the edge of its reach, but sqrt
// compressed that to ~5:1, so even the faintest tail cleared the beam gate and
// motes filled the light's ENTIRE radius. Tightening it via the shared term
// then blew the core out to flat white. Split, the reveal can fall off far
// faster than the light does — which is what lets a lantern light a cave at
// range while its motes stay close in.
//
// Appearance: how strongly block light tints/brightens the speck. Gentle (sqrt)
// so a mote near the source doesn't saturate. Past ~1 the glint blooms.
uniform float block_light_strength : hint_range(0.0, 32.0) = 8.0;
// Reveal: how far from a light motes appear at all. Higher curve = tighter
// pool. Because the light's own falloff is pinned to 1% of core at its authored
// `distance` (LightEngine.ResolveTuning), the reveal edge lands well inside the
// lit radius rather than tracking it. Retune the pair together — raising the
// curve without raising the strength shrinks the pool from both ends.
uniform float block_light_reveal_curve : hint_range(0.5, 4.0) = 1.5;
uniform float block_light_reveal_strength : hint_range(0.0, 64.0) = 13.0;
// Speck size in SubViewport pixels (the visual core; the raster footprint is
// inflated 1 px for analytical AA).
uniform float mote_size_px : hint_range(0.5, 8.0) = 1.5;
// Tumble rate of each fleck's normal (radians/sec). The specular glint flashes
// as the spinning normal sweeps through the light/eye half-vector, so this is
// effectively the sparkle rate.
uniform float spin_rate : hint_range(0.0, 12.0) = 2.0;
// Specular sharpness — higher = tighter, briefer, rarer glints.
uniform float spec_power : hint_range(1.0, 64.0) = 8.0;
// Specular flash brightness (in the light colour). >1 lets a catching fleck
// bloom briefly.
uniform float spec_intensity : hint_range(0.0, 4.0) = 1.0;
// Pre-integrated time for the spin phase (MoteEffect accumulates dt on the C#
// side) so it stays precise over long sessions instead of riding raw TIME.
uniform float mote_time = 0.0;
// Near-ground concentration: brightness falls off as
// exp(-(world_y - ground_reference_y) / near_ground_height). MoteEffect
// pushes the player's Y as the ground reference each frame.
uniform float ground_reference_y = 0.0;
uniform float near_ground_height : hint_range(0.5, 32.0) = 6.0;
// Overall speck opacity before gating.
uniform float mote_alpha : hint_range(0.0, 1.0) = 0.9;

// flat: a 1-2 px speck wants identical values across all its fragments.
varying flat vec3 world_pos;
varying flat vec3 view_dir;
varying flat vec2 quad_center_px;
varying flat float quad_half_px;
varying flat float seed;
// Stable per-particle draw in [0,1) for the density cull — keyed on the
// particle's buffer slot, so it holds for the particle's whole life.
varying flat float cull_draw;
// Per-particle COLOR from the process material's color_ramp (lifetime fade).
// Defaults to white opaque when no ramp is authored, so it's a no-op until you
// set one — primarily used to fade ALPHA in/out over each mote's lifetime.
varying flat vec4 particle_color;

// Cheap non-periodic 3D hash -> [0,1). Same construction the fog motes used,
// so neighbouring particles decorrelate at high spatial frequency.
float hash13(vec3 p)
{
    p = fract(p * 0.1031);
    p += dot(p, p.zyx + 31.32);
    return fract((p.x + p.y) * p.z);
}

// Scalar hash, for the per-particle survival draw. Must key off something
// STABLE for the particle's life — the position-derived `seed` above changes as
// a speck drifts, which is what we want for decorrelating sparkle phases and
// exactly what we can't have for a cull (a speck would blink as it crossed hash
// cells rather than holding its verdict).
float hash11(float p)
{
    p = fract(p * 0.1031);
    p *= p + 33.33;
    return fract((p + p) * p);
}

// Voxel sun-visibility (light_map.r) at a world position. Used by the shaft
// occlusion gate to measure local shadow contrast around a mote. The light map
// is a toroidal window — sample directly and let the repeat sampler wrap. The
// old [0,1] guard returned 1.0 (full sun) everywhere more than one window from
// the world origin, breaking the contrast gate away from spawn.
float sample_sun_r(vec3 p)
{
    vec3 uvw = (p - light_map_origin) * light_map_inv_size;
    return texture(light_map, uvw).r;
}

void vertex()
{
    vec3 origin = MODEL_MATRIX[3].xyz;
    float particle_scale = length(MODEL_MATRIX[0].xyz);

    // Project the particle origin to screen pixels and build a fixed-pixel
    // square around it (orthographic-safe constant size, like rain).
    vec4 origin_clip = PROJECTION_MATRIX * VIEW_MATRIX * vec4(origin, 1.0);
    vec2 origin_ndc = origin_clip.xy / max(abs(origin_clip.w), 1e-6);
    vec2 origin_px = (origin_ndc * 0.5 + 0.5) * VIEWPORT_SIZE;

    quad_center_px = origin_px;
    quad_half_px = mote_size_px * 0.5 * particle_scale;
    float rast_half = quad_half_px + 1.0;  // +1 px AA margin each side

    // Normalize VERTEX corners to ±0.5 regardless of QuadMesh orientation
    // (FACE_Y vs FACE_Z); a flat quad has one of y/z at zero.
    float vx_unit = VERTEX.x > 0.0 ? 0.5 : -0.5;
    float vy_unit = (VERTEX.y + VERTEX.z) > 0.0 ? 0.5 : -0.5;
    vec2 offset_px = vec2(vx_unit, vy_unit) * 2.0 * rast_half;
    vec2 offset_ndc = offset_px * 2.0 / VIEWPORT_SIZE;
    POSITION = vec4((origin_ndc + offset_ndc) * origin_clip.w, origin_clip.z, origin_clip.w);

    world_pos = origin;
    // Orthographic view-ray direction = camera forward in world space.
    view_dir = normalize(mat3(INV_VIEW_MATRIX) * vec3(0.0, 0.0, -1.0));
    // Stable-ish per-particle seed from a high-frequency hash of the world
    // position. Neighbours land in different hash cells → independent sparkle
    // phases, so the field never pulses in regional waves.
    seed = hash13(floor(origin * 13.0));
    // Slot index, not position — see hash11. Slots recycle as particles respawn,
    // which is fine: each new particle spawns somewhere unrelated, so the
    // surviving subset stays spatially uniform.
    cull_draw = hash11(float(INSTANCE_ID) + 0.5);
    // Carry the particle's lifetime color/alpha (process material color_ramp)
    // into the fragment so it can fade the speck over its life.
    particle_color = COLOR;
}

void fragment()
{
    bool globals_ready = length(light_map_inv_size) > 1e-6;

    // Light gate. A mote shows where it's lit by EITHER the sky (voxel sun mask
    // × cloud gap) OR local block light (torches / campfires / lamps, carried
    // in the light_map's GBA channels). Discard above the ceiling cutaway so
    // motes never show on the hidden interior ceiling when the player's inside.
    //
    // light_col is the effective light colour reaching the mote — sun/moon plus
    // the block-light colour added in. It drives both the dust base hue and the
    // specular glint, so a fleck inside torch glow glints warm orange while one
    // in a sunbeam glints with the sky light.
    float beam = 1.0;
    float dust_gain = 1.0;
    vec3 light_col = sun_color;
    if (globals_ready)
    {
        if (world_pos.y > camera_clip)
        {
            discard;
        }
        // Toroidal light map: sample directly (repeat sampler wraps). The old
        // [0,1] guard defaulted to full-sun / no-block-light away from the world
        // origin (same windowed-volume-map refactor bug as fog_volumetric).
        vec3 luvw = (world_pos - light_map_origin) * light_map_inv_size;
        vec4 lm = texture(light_map, luvw);
        float cloud = cloud_shadow_shafts(world_pos, mote_time, cloud_sharpness);
        float sun_lit = lm.r * (1.0 - cloud);
        // Block light (GBA), split into the two independent responses described
        // at block_light_strength: a gentle one for how the speck LOOKS, and a
        // steep one for how far out specks appear at all.
        vec3 block_raw = max(lm.gba, vec3(0.0));
        vec3 block_light = sqrt(block_raw) * block_light_strength;
        float block_peak = max(block_raw.r, max(block_raw.g, block_raw.b));
        float block_lit = pow(block_peak, block_light_reveal_curve) * block_light_reveal_strength;
        float lit = max(sun_lit, block_lit);
        light_col = sun_color * sun_lit + block_light;

        // Local air thickness — authored fog volumes plus any roof interior
        // dust, the same single channel the fog raymarch reads. 0 in clean air,
        // where every use of dust_boost below folds back to the expression it
        // replaced; that is what keeps walking into a dusty room from altering
        // the air outside it, since the emission box spans far more than a room.
        float dust_boost = 0.0;
        float dust_here = 0.0;
        bool fog_ready = length(fog_map_inv_size) > 1e-6;
        if (fog_ready)
        {
            vec3 fuvw = (world_pos - fog_map_origin) * fog_map_inv_size;
            dust_here = texture(fog_map, fuvw).r;
            dust_boost = smoothstep(dust_boost_min, max(dust_boost_full, dust_boost_min + 1e-3), dust_here) * dust_boost_strength;
        }
        dust_gain = 1.0 + dust_alpha_gain * dust_boost;

        // Cull back to locally-earned density (see mote_budget_density). Mirrors
        // the max() MoteEffect sized the buffer with, so the fraction kept is 1
        // wherever the budget came from this fragment's own air and falls to the
        // weather baseline everywhere else.
        //
        // Stochastic rather than proportional: dropping whole specks keeps the
        // survivors at full brightness, so thin air reads as a few clear motes.
        // Fading all of them instead matches total emitted light but turns the
        // same budget into a dim wash, which is the thing that looked wrong.
        if (fog_ready && mote_budget_density > 1e-4)
        {
            float earned = max(mote_weather_density, dust_here * local_dust_density_scale);
            if (cull_draw > clamp(earned / mote_budget_density, 0.0, 1.0))
            {
                discard;
            }
        }

        // Shaft occlusion gate: require cloud cover OR local terrain/foliage
        // shadow contrast, so motes track the actual god-rays instead of
        // blanketing open clear-sky areas (e.g. a flat desert at noon).
        float r = shaft_sample_radius;
        float c0 = lm.r;
        float c1 = sample_sun_r(world_pos + vec3(r, 0.0, 0.0));
        float c2 = sample_sun_r(world_pos + vec3(-r, 0.0, 0.0));
        float c3 = sample_sun_r(world_pos + vec3(0.0, 0.0, r));
        float c4 = sample_sun_r(world_pos + vec3(0.0, 0.0, -r));
        float vis_max = max(c0, max(max(c1, c2), max(c3, c4)));
        float vis_min = min(c0, min(min(c1, c2), min(c3, c4)));
        float occlusion = max(vis_max - vis_min, cloud_cover);
        float occ_gate = smoothstep(shaft_occlusion_min, shaft_occlusion_min + shaft_occlusion_soft, occlusion);

        // Same animated dust-noise the fog uses to sculpt beams into streaks,
        // sampled sun-projected (points along a sun ray share the projected XZ),
        // so motes fall inside the SAME noisy beam shape and track shaft edges.
        vec3 to_sun_n = -normalize(sun_world_dir);
        float proj_t = (cloud_altitude - world_pos.y) / max(to_sun_n.y, 0.15);
        vec2 dust_xz = world_pos.xz + to_sun_n.xz * proj_t;
        vec2 dn_uv_a = dust_xz * dust_noise_scale + dust_noise_offset_a;
        vec2 dn_uv_b = dust_xz * dust_noise_scale * 1.3 + dust_noise_offset_b;
        float dn = (texture(cloud_noise_tex, dn_uv_a).r + texture(cloud_noise_tex, dn_uv_b).r) * 0.5;
        float dn_band = max(1e-4, 1.0 - dust_noise_sharpness);
        float dust_pattern = smoothstep(dust_noise_threshold, min(dust_noise_threshold + dn_band, 1.0), dn);
        float noise_gate = mix(1.0, dust_pattern, dust_noise_strength);

        // Block-lit motes (torch glow) bypass the sun-shaft occlusion + noise.
        // Thick air bypasses it too: dust IS the scattering medium, so a speck
        // in it doesn't need a shaft edge or a noise pocket to justify showing.
        // This is what lifts motes inside authored fog banks and under dusty
        // roofs, where there is no terrain shadow contrast to gate on.
        float block_present = smoothstep(0.0, 0.15, block_lit);
        float shaft_struct = max(max(occ_gate * noise_gate, block_present), dust_boost);

        // Dust also lets dimmer light reveal a speck at all — more of the air
        // is scattering, so the sun/moon picks specks out further into shadow.
        float thr = mix(beam_threshold, beam_threshold * dust_threshold_relax, dust_boost);
        beam = smoothstep(thr, thr + beam_softness, lit) * shaft_struct;
        if (beam < 1e-3)
        {
            discard;
        }
    }

    // Square speck coverage with adaptive AA (sub-pixel safe), mirroring the
    // rain streak's analytical coverage so a 1 px speck reads as ~1 px instead
    // of flickering on/off across pixel boundaries.
    vec2 d = abs(FRAGCOORD.xy - quad_center_px);
    float aa = min(1.0, quad_half_px * 2.0);
    float cov_x = 1.0 - smoothstep(quad_half_px, quad_half_px + aa, d.x);
    float cov_y = 1.0 - smoothstep(quad_half_px, quad_half_px + aa, d.y);
    float coverage = cov_x * cov_y;
    if (coverage < 1e-4)
    {
        discard;
    }

    // Each mote is a tiny tumbling fleck: build a per-particle normal that
    // spins over time, with a per-particle phase from the seed so every speck
    // tumbles independently (never in sync). As the normal sweeps through the
    // light/eye half-vector the fleck throws a real specular glint — that
    // flashing IS the sparkle, physically, instead of a faked oscillation.
    float sp = mote_time * spin_rate;
    vec3 n = normalize(vec3(
        sin(sp + seed * 6.2831853),
        sin(sp * 1.3 + seed * 11.0),
        cos(sp * 0.7 + seed * 17.0)));

    // Blinn-style specular against the active light. Under the orthographic
    // camera the light + eye directions are constant across the screen, so the
    // only variable is the tumbling normal — each fleck glints on its own.
    vec3 to_sun = -normalize(sun_world_dir);
    vec3 to_view = -view_dir;
    vec3 half_vec = normalize(to_sun + to_view);
    float spec = pow(max(dot(n, half_vec), 0.0), spec_power);

    // Dust base = dust albedo lit by the effective light colour (sun/moon +
    // block light), so it stays dust-hued and shifts toward torch colour near
    // a flame. The specular adds a flash in that same light colour as the fleck
    // catches it — so glints read warm in torchlight, cool in moonlit sun.
    vec3 base = dust_color * light_col;
    vec3 color = base + light_col * (spec * spec_intensity);

    // Near-ground concentration: dim with height above the player's ground.
    float h_above = max(world_pos.y - ground_reference_y, 0.0);
    float ground_fade = exp(-h_above / max(near_ground_height, 0.1));

    // Fold in the per-particle lifetime ramp: rgb tints the speck, a fades it.
    // Both default to 1 (white/opaque) when no color_ramp is authored.
    ALBEDO = color * particle_color.rgb;
    ALPHA = min(mote_alpha * dust_gain, 1.0) * coverage * beam * ground_fade * particle_color.a;
}
