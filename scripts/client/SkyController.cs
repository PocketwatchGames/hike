using Godot;

// Owns the runtime visual pipeline for the sky dome, sun / moon, fog,
// shafts, water ripples, cloud shadows, and precipitation. Every frame
// it:
//   1. Samples a blended ZoneData + WeatherData at the player's XZ
//      via ZoneBlend.
//   2. Recomputes sun / moon orbit from WorldState.TimeOfDay01.
//   3. Derives a full DerivedPalette from (zone, weather, sunElev,
//      SimData tuning) via WeatherDerivation — this is where the
//      day/sunset/night phase blend and all "look recipe" logic live.
//   4. Pushes the palette to:
//        - Global shader uniforms (sky dome, voxel, sprite, water)
//        - Per-material uniforms on fog_volumetric.tres
//        - sunLight / moonLight DirectionalLight3D properties
//        - RainEffect via ApplyPrecipitation
//
// SkyController itself owns only SCENE-STRUCTURAL tuning (cloudScale /
// altitude / shadow strength, SSR, fog step counts, water foam, shaft
// fade bands, etc.) — weather / zone-driven visuals come from the
// palette.
//
// [Tool] makes this run in the editor. When no World/Player exists, it
// falls back to `previewZone` so inspector edits produce live sky
// previews without entering the game.
[Tool]
[GlobalClass]
public partial class SkyController : Node3D
{
    // Static reference for consumers that need the current wind state
    // or palette (RainEffect reads GustedWindSpeed for tilt math; CVar
    // callbacks may mutate atmospheric exports directly). There's only
    // one SkyController per game scene.
    public static SkyController Current { get; private set; }

    [ExportGroup("Preview")]
    // Editor / pre-World fallback zone. Used for live sky preview
    // when no WorldState / player exists (pure inspector tweaking). At
    // runtime the world's zones take over via ZoneBlend.
    [Export] public ZoneData previewZone;
    // Stand-ins for the runtime ZoneState fields when previewing in
    // the editor — at runtime these come from WorldState.Zones[],
    // populated by WorldGen / the disk loader.
    [Export] public Vector3 previewWindDirection = new Vector3(0.7f, 0f, 0.7f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float previewElevation = 0.0f;

    [ExportGroup("TimeOfDay")]
    [ExportSubgroup("Lights")]
    // Wire to the scene's SunLight DirectionalLight3D. SkyController writes
    // this light's transform each frame to face the sun's actual position
    // (never flips to the moon). Its LightEnergy is scaled by the sun's
    // above-horizon factor so the sun's shadow fades out as it sets.
    [Export] public DirectionalLight3D sunLight;
    // Wire to the scene's MoonLight DirectionalLight3D. Mirror of sunLight
    // for the moon's side of the sky — oriented at the moon's position each
    // frame, LightEnergy scaled by the moon's above-horizon factor AND by
    // the palette's NightPrimaryIntensity so moonlight is physically
    // dimmer than daylight. Enables simultaneous sun+moon directional
    // shadows during dawn/dusk crossover.
    [Export] public DirectionalLight3D moonLight;

    [ExportSubgroup("Preview")]
    // Editor preview only — no WorldState exists in the editor, so the
    // orbit needs a manual parameter to preview nighttime / sunset looks.
    // At runtime this is ignored and WorldState.TimeOfDay01 drives the orbit.
    [Export(PropertyHint.Range, "0,1,0.001")] public float previewTimeOfDay = 0.5f;

    [ExportSubgroup("Fades")]
    // Each phenomenon's horizon fade is a PAIR:
    //   - FadeAngle   : degrees ABOVE SimData.SunsetAngleDegrees at which
    //                   the fade reaches its 0 value (source fully gone).
    //   - FadeRange   : width (degrees) of the fade band. fadeStart = end + range.
    // Above fadeStart the phenomenon is at full intensity; between
    // fadeStart and fadeEnd it smoothsteps down to 0; below fadeEnd it's 0.
    // Both fades pivot on SunsetAngleDegrees, so moving sunset up or down
    // carries the whole horizon transition with it.

    // Sun and moon DirectionalLight3D LightEnergy fade. fadeAngle=0 means
    // the light reaches 0 energy exactly at sunset; fadeRange is how many
    // degrees above that it spends ramping up to full.
    [Export(PropertyHint.Range, "0,30,0.5")] public float lightEnergyFadeAngleDegrees = 0f;
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float lightEnergyFadeRangeDegrees = 10f;

    // Shaft (god-ray) fade. Needs a positive fadeAngle so shafts are fully
    // gone by the time the primary direction sign-flips.
    [Export(PropertyHint.Range, "0,30,0.5")] public float shaftFadeAngleDegrees = 5f;
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float shaftFadeRangeDegrees = 10.1f;

    // Sun-wash intensity tuning (client-side visual — drives the global "how
    // lit is the atmosphere" scalar before per-pixel shadows carve it).
    // washIntensity = min(washMax, effectiveDust × (baseline + cloudGain ×
    // cloudCover²)). The shader darkens shadowed air by this; the per-pixel
    // shadow carve and the shaft COLOUR (zone-derived) live elsewhere.
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftWashBaseline = 0.15f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float shaftWashCloudGain = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftWashMax = 1.0f;
    // Per-voxel authored-fog → shaft boost (fog_shaft_gain). Painted fog_map
    // volumes locally thicken the shafts on top of the global wash. 0 = off.
    [Export(PropertyHint.Range, "0,4,0.01")] public float fogShaftGain = 1.0f;
    // Day↔night beam knob: moon-shaft intensity = sun wash × this. >1 makes
    // moon beams MORE prominent than daytime, <1 less.
    [Export(PropertyHint.Range, "0,4,0.01")] public float moonBeamScale = 0.5f;

    [ExportSubgroup("Shadows")]
    // Baseline DirectionalLight3D light_angular_distance (degrees) at noon in
    // clear air. Drives a PCSS-style penumbra whose width scales with caster
    // distance — unlike shadow_blur (texel-counted), this stays consistent
    // across PSSM cascades and camera angles. Overrides the scene-authored
    // value on both sunLight and moonLight each frame. The real sun is ~0.5°.
    [Export(PropertyHint.Range, "0,20,0.05")] public float shadowAngularBase = 0.5f;
    // Added to shadowAngularBase at the low-sun endpoint (primary body at
    // SunsetAngleDegrees), smoothstepped by primary elevation so noon
    // stays tight. Models the way low-angle light grazes through more
    // atmosphere and softens shadow edges.
    [Export(PropertyHint.Range, "0,20,0.05")] public float shadowAngularLowSunBoost = 1.5f;
    // Extra angular spread from humidity + dust (hazy clear-sky scatter).
    // Slight — soft always-on baseline on top of the elevation boost.
    [Export(PropertyHint.Range, "0,20,0.05")] public float shadowAngularAtmosphericBoost = 0.5f;

    [ExportSubgroup("Disks")]
    // Sun/moon DISK shape + intensity in the sky shader. The disk is drawn
    // as smoothstep(outer, inner, dot(dir, -sun)) — so `outer` is where
    // the disk starts fading IN (lower = wider angular radius) and `inner`
    // is where it reaches full brightness (higher = sharper edge).
    // Intensity is the peak brightness multiplier — >1 triggers bloom.
    //
    // Under an orthographic camera with flat water, ALL fragments share a
    // reflection direction, so if the disk is hit in one fragment it's hit
    // in all — the whole surface goes bright. Wider disks (lower outer)
    // make the sun findable at more camera facings but also mean more of
    // the water brightens when alignment hits. 0.80 is a reasonable starting
    // point for pixel-art iso cameras.
    // Sun + moon are drawn as textured sprites in the sky. The texture's
    // alpha channel is the sprite shape (circle, starburst, phased moon,
    // whatever the art asset contains). Angular size controls how much of
    // the sky dome the sprite covers — 2° matches real sun angular size,
    // ~6° reads as chunky pixel art. Intensity multiplies output color;
    // >1 pushes HDR so Godot's bloom pass gives the sprite a natural glow.
    [Export(PropertyHint.Range, "0.3,30,0.05")] public float sunAngularSizeDeg = 2f;
    [Export(PropertyHint.Range, "0,10,0.05")] public float sunDiskIntensity = 4.0f;
    [Export(PropertyHint.Range, "0.3,30,0.05")] public float moonAngularSizeDeg = 2.5f;
    [Export(PropertyHint.Range, "0,10,0.05")] public float moonDiskIntensity = 2.0f;
    // Swap these textures to change sun/moon shape without code changes.
    // Moon phases live here: author each phase as a separate texture
    // asset and swap via a gameplay controller over time.
    [Export] public Texture2D sunTexture;
    [Export] public Texture2D moonTexture;

    // Time-of-day fade for the disk glow. Feeds sun_disk_glow / moon_disk_glow
    // which SkyController further multiplies by the day/night factor —
    // authored as a ceiling that fades to 0 when the body is below horizon.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunDiskGlowStrength = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float moonDiskGlowStrength = 0.15f;

    // Disk intensity at the horizon endpoint of the sun's orbit. The
    // effective disk intensity lerps between sunDiskIntensity (noon) and
    // this value (sunrise/sunset) with sin(orbital phase) as the parameter.
    // The palette already warms SunTint toward amber through the sunset
    // band, but at full disk intensity the amber clips past 1.0 in every
    // channel and tonemaps back to near-white — pulling intensity down
    // near the horizon lets the warm tint actually read on the disk.
    [Export(PropertyHint.Range, "0,10,0.05")] public float sunsetDiskIntensity = 1.0f;

    // Wall-clock seconds over which the sun/moon disks fade in at rise
    // and fade out before set. Applied as a multiplier on top of the
    // elevation-based intensity lerp, so the disks smoothly appear and
    // disappear in both the sky and the water reflection rather than
    // popping on/off at the horizon. The fade is converted to a fraction
    // of SimData.DayLengthSeconds; if it exceeds half the active window
    // it's clamped so fade-in and fade-out meet at a peak < 1 instead
    // of overlapping.
    [Export(PropertyHint.Range, "0,600,1")] public float sunDiskFadeTime = 30f;

    // Atmospheric attenuation of the sun disk. Humidity (water vapor) and
    // dust (aerosols) both scatter direct sunlight — a humid jungle or
    // dusty desert sun reads softer than a clean-air alpine one. Each
    // knob is the FRACTION of intensity removed at full weather (1.0):
    // e.g. humidityDiskDim=0.5 means humidity=1 cuts disk intensity in
    // half; dustDiskDim=0.7 means dust=1 cuts it to 30%. Applied
    // multiplicatively on top of the sunset lerp.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidityDiskDim = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustDiskDim = 0.7f;

    // Atmospheric attenuation of the moon disk. Fog (dense low moisture)
    // washes out the moon most visibly — the classic "foggy night" look —
    // and dust does the same in hazy desert / volcanic air. Structure
    // mirrors the sun's humidity/dust dims; values are the FRACTION of
    // moonDiskIntensity removed at full weather.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fogMoonDim = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustMoonDim = 0.5f;

    [ExportSubgroup("Stars")]
    // Equirectangular panorama sampled by view direction — U wraps with
    // compass azimuth, V spans horizon (0.5) to zenith (1.0). Drop in a
    // painted PNG to place named constellations in specific compass
    // directions. The shader fades stars in with sky_night_factor and
    // occludes them with clouds, so no per-frame work is needed here
    // beyond pushing the texture + intensity.
    [Export] public Texture2D starTexture;
    [Export(PropertyHint.Range, "0,4,0.01")] public float starIntensity = 2.0f;

    // Max mip LOD bias for the water reflection's starfield at full
    // ripple_strength. The water shader multiplies this by ripple_strength
    // so flat water samples mip 0 (sharp stars) and fully rippled water
    // samples a coarser mip (stable, blurry stars instead of per-frame
    // flicker from the jittered reflection direction). 0 = no blur ever;
    // ~3–5 reads as diffuse glow on windy water.
    [Export(PropertyHint.Range, "0,6,0.1")] public float starRippleBlurLod = 4.0f;

    // Atmospheric attenuation of the starfield. Stars are much dimmer than
    // the moon, so they're washed out far more aggressively by fog/dust —
    // defaults bias toward near-total loss at full weather.
    [Export(PropertyHint.Range, "0,1,0.01")] public float fogStarDim = 0.95f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustStarDim = 0.8f;

    [ExportSubgroup("Fill Lights")]
    // Two off-axis fill directions, computed each frame from the primary
    // light's yaw + a configurable yaw offset + pitch below horizon.
    // Orthogonal fills (yaw offsets ~90° apart) give the cleanest slope
    // reading.
    [Export] public float fillAPitchDegrees = 35f;
    [Export] public float fillAYawOffsetDegrees = 90f;
    [Export] public float fillBPitchDegrees = 50f;
    [Export] public float fillBYawOffsetDegrees = -90f;

    [ExportGroup("Water")]
    [ExportSubgroup("Depth")]
    // Base world-unit distance over which water's alpha ramps from the
    // authored surface value up toward 1.0. Larger = more transparent at
    // depth (tropical lagoon feel); smaller = opaque quickly (puddle feel).
    // In Apply() this is modulated by muddiness (murky water loses depth
    // visibility fast) and direct light level (dim light lets less through
    // regardless of clarity), so the effective depth scale floats roughly
    // in [0.3, base × 1.5] meters.
    [Export(PropertyHint.Range, "0.5,30,0.1")] public float waterDepthScale = 6.0f;
    // Minimum alpha at the water's edge (thickness → 0). Clamps the
    // authored WaterColor.a from below so clean water still reads as
    // visible color along the shoreline. Set to 0 for fully-glassy water
    // that disappears at the edge.
    [Export(PropertyHint.Range, "0,1,0.01")] public float waterEdgeOpacity = 0.3f;
    // Maximum surface alpha at full depth, separately for clean and muddy
    // water. Capping clean water below 1.0 lets deep tropical water stay
    // partly see-through even straight down through several meters —
    // without this, depth_factor saturates and the surface goes fully
    // opaque regardless of clarity. Lerped by muddiness in Apply() so
    // clean water stays glassy and silty water occludes.
    [Export(PropertyHint.Range, "0,1,0.01")] public float waterAlphaMaxClean = 0.55f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float waterAlphaMaxMuddy = 1.0f;

    [ExportSubgroup("Shoreline Rim")]
    // Contiguous foam-colored band at the water/land boundary — drawn on
    // top of the noisy shoreline foam as a solid rim so the shoreline has
    // a clear outline rather than scattered noise.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rimWidth = 0.2f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float rimStrength = 0.6f;

    [ExportSubgroup("Ripples")]
    // Two procedural noise layers sampled in world XZ sum into the water
    // surface's height field; its finite-difference gradient perturbs the
    // shading normal. Two scales break up spatial tiling; both layers drift
    // along the wind vector (from weather) — layer B is rotated by a small
    // angle so the two layers don't lock into one apparent direction.
    [Export] public float rippleScaleA = 0.2f;
    [Export] public float rippleScaleB = 0.1f;
    // Scroll speed per m/s of wind, for each layer, BEFORE saturation.
    // Layer A speed = min(windSpeed, rippleSpeedSaturation) × rippleSpeedA.
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedA = 0.01f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedB = 0.006f;
    [Export(PropertyHint.Range, "-180,180,1")] public float rippleAngleOffsetB = 30f;
    // Caps how fast ripples scroll regardless of wind. Above this wind
    // speed the scroll rate stops growing — prevents the ripple pattern
    // from turning into a blur at high wind while still letting normal
    // perturbation keep increasing. Tune independently of ripple strength
    // (which saturates via SimData.RippleWindRef).
    [Export(PropertyHint.Range, "0.5,20,0.1")] public float rippleSpeedSaturation = 4.0f;
    // Wind-driven cell size shift. 0 = cell size fixed. 1 = cells grow
    // 2× at the ripple strength saturation wind (waves get longer as
    // wind picks up). Negative = cells shrink with wind. Lets you keep
    // a calm base scale while winds produce larger patterns.
    [Export(PropertyHint.Range, "-1,2,0.01")] public float rippleScaleWindResponse = 0f;

    [ExportSubgroup("Dynamic Ripples")]
    // Radial ripple impacts emitted by entities moving through water (Player,
    // Mob). Each impact expands as a ring at `dynamicRippleSpeed` m/s, fading
    // over `dynamicRippleLifetime` seconds. The water shader composites them
    // onto the existing ripple normal AFTER the wind_factor flatten, so
    // footstep ripples remain visible on cave pools and indoor still water.
    [Export(PropertyHint.Range, "0.5,10,0.1")] public float dynamicRippleSpeed = 1.5f;
    [Export(PropertyHint.Range, "0.2,5,0.1")] public float dynamicRippleLifetime = 1.0f;
    // Fade-in window (seconds) — ramps amplitude from 0 to full so the
    // age=0 "central pulse" doesn't pop into existence. Should be large
    // enough that the ring radius (= speed × fade_in) clears the impact
    // origin before the ring becomes visible. With speed=1.5 and
    // fade_in=0.2, the ring is at ~0.3m by full visibility.
    [Export(PropertyHint.Range, "0.05,1,0.01")] public float dynamicRippleFadeIn = 0.2f;
    // Gaussian envelope tightness around the moving ring crest. Higher =
    // thinner ring; lower = wider, softer ring. The ring's visible
    // half-width in meters is roughly 1 / sqrt(falloff).
    [Export(PropertyHint.Range, "0.5,200,0.5")] public float dynamicRippleFalloff = 30.0f;
    // Overall normal-perturbation amplitude. 0 disables dynamic ripples even
    // when impacts are queued.
    [Export(PropertyHint.Range, "0,2,0.01")] public float dynamicRippleTilt = 0.6f;

    [ExportSubgroup("Reflections")]
    // Fake reflection FOV — under an orthographic iso camera, true mirror-
    // reflection geometry makes the sun visible only in a tiny band of
    // time/position because all water fragments share a near-parallel
    // refl direction. These knobs let the water shader remap the sun's
    // (and moon's) world direction into a wide virtual FOV so the body
    // sweeps a readable arc across the water surface. 160° horizontal
    // spreads sunrise-to-sunset across the full visible water; 90°
    // vertical puts zenith at the top edge with horizon at the bottom.
    // vertical_center shifts the vertical zero — raise to push the arc
    // up toward the top of screen. Only the sun/moon sprites are remapped
    // this way; clouds and sky gradient still sample via true reflection
    // direction, so the sun is a standalone feature rather than a
    // coherent window into the sky.
    // FOV defaults at 90° × 90° — wide enough to spread the sun's daily arc
    // across the visible water, narrow enough that tan-based projection
    // stays close to linear (per-pixel angular rate roughly uniform), so
    // the sun stays circular and not squashed at screen edges. Wider values
    // like 160° give a dramatic fisheye-style sweep but compress the sun
    // to a few pixels at edges and stretch it near center.
    [Export(PropertyHint.Range, "30,220,1")] public float reflectionFovHorizontalDeg = 90f;
    [Export(PropertyHint.Range, "30,220,1")] public float reflectionFovVerticalDeg = 90f;
    // Pushes the vertical center down — values below 0.5 anchor the horizon
    // line higher on screen, so the sun (which appears above horizon) sits
    // lower. Raising this toward 1.0 pushes sun up toward top of screen.
    [Export(PropertyHint.Range, "0,1,0.01")] public float reflectionFovVerticalCenter = 0.3f;

    // Near-1 fresnel means reflection stays strong at most view angles
    // rather than only at grazing. Weather modulation in Apply() lifts this
    // toward ~2 in heavy overcast where sky light is diffuse and a sharper
    // fresnel reads better; clear-sky scenes keep the low default.
    [Export(PropertyHint.Range, "0.5,8,0.1")] public float fresnelPower = 1.5f;
    // Base reflection strength at non-grazing angles. Muddiness damps this
    // toward diffuse (scum surfaces don't mirror); dim lighting damps it
    // further so night water doesn't glow from sky reflection. Bright pixel
    // highlights come from the sun/moon disks in sample_sky_from — no
    // separate glint term, because under an orthographic camera all water
    // fragments share a reflection direction and any angle-based glint
    // would paint the entire surface uniformly.
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float reflectionStrength = 1.0f;
    // Artistic minimum reflection mix independent of fresnel. Physical
    // fresnel at typical iso camera angles gives only ~3–8% reflection
    // blend — nearly invisible. Raising this floor guarantees a visible
    // baseline reflection at any view angle. 0 = pure fresnel (physically
    // correct, often invisible under ortho top-down); 0.15–0.3 gives a
    // subtle always-there reflection that grows at grazing angles.
    [Export(PropertyHint.Range, "0,1,0.01")] public float reflectionMin = 0.2f;
    // Base brightness of sprite reflections (LitSprite.UpdateReflection
    // children rendered through sprite_reflection.gdshader). Muddiness,
    // fog, and light level damp this further in Apply() so murky / dim
    // scenes show subtle reflections rather than the full mirror image.
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float spriteReflectionTint = 0.7f;
    // Maximum source-pixel jitter applied to sprite reflections at full
    // ripple_strength. 0 = perfectly rigid mirror; 2–4 px = visible
    // wobble that breaks up the mirror look in choppy weather.
    [Export(PropertyHint.Range, "0,8,0.1")] public float spriteReflectionPixelJitter = 2.0f;


    [ExportSubgroup("Waves")]
    // Vertex-displacement wave on top water faces. Amplitude scales with
    // GustedWindSpeed and is damped by WaterColor.a (muddiness); a swamp
    // hardly moves even in wind, a stormy sea with low muddiness rolls.
    [Export(PropertyHint.Range, "0,0.5,0.001")] public float waveAmpPerMps = 0.01f;
    // World-units per wave cycle. Lower = choppier, higher = ocean swell.
    [Export(PropertyHint.Range, "1,32,0.1")] public float waveLength = 1.0f;
    // Spatial frequency of the intermittent wave envelope. Smaller = larger
    // patches of active vs calm water; larger = busier surface.
    [Export(PropertyHint.Range, "0.005,0.5,0.001")] public float waveGateScale = 0.005f;
    // Wave-phase angular rate per m/s of wind. The shader's wave temporal
    // phase is integrated in C# as `wavePhase += windSpeed * waveSpeedPerMps
    // * dt`, so calm water freezes the surface and stormy water pumps it
    // fast. 0.24 at 5 m/s wind reproduces the prior fixed `time * 1.2` rate.
    [Export(PropertyHint.Range, "0,2,0.01")] public float waveSpeedPerMps = 0.24f;

    [ExportSubgroup("Wave Streaks")]
    // Wave streaks are conceptually "ripple layer 2" — a coarser scrolling-
    // noise field added to the base ripple normal with its own weight,
    // scale, and drift speed. Strength rides on _palette.RippleStrength so
    // streaks fade smoothly with wind/rain. Streak weight is independent
    // of the foam path — whitecap foam (below) fires from the COMPOSITE
    // tilt regardless of which layer caused it (base ripples alone can
    // foam in choppy zones even with streak strength at 0).
    [Export(PropertyHint.Range, "0,2,0.01")] public float waveStreakStrength = 0.6f;
    // World-XZ multipliers for the two noise octaves. Smaller value =
    // larger spatial features (longer waves); larger value = finer wavelets.
    [Export(PropertyHint.Range, "0.005,0.5,0.001")] public float waveStreakScaleA = 0.05f;
    [Export(PropertyHint.Range, "0.005,0.5,0.001")] public float waveStreakScaleB = 0.09f;
    // Drift speeds (world-units / second) along the wind vector for each
    // octave. Different speeds keep the layers from beating against each
    // other into a static pattern.
    [Export(PropertyHint.Range, "0,4,0.01")] public float waveStreakSpeedA = 0.4f;
    [Export(PropertyHint.Range, "0,4,0.01")] public float waveStreakSpeedB = 0.7f;

    [ExportSubgroup("Whitecaps")]
    // Whitecap foam fires wherever the FINAL composite normal (ripple +
    // streak combined) is sufficiently sideways. The threshold compares
    // sin²(tilt_angle): 0.005 ≈ 4°, 0.02 ≈ 8°, 0.05 ≈ 13°, 0.15 ≈ 23°.
    // Lower = foam on gentler ripple crests; higher = only on the
    // steepest waves. Strength multiplies the speckle-masked foam mask.
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float whitecapFoamStrength = 0.7f;
    [Export(PropertyHint.Range, "0.0,0.1,0.00001")] public float whitecapFoamThreshold = 0.02f;

    [ExportSubgroup("Ripples (Pixelation)")]
    // Pixels per world-unit used to quantize ripple-noise UVs. Higher values
    // yield finer noise; lower values give chunky, hand-drawn-looking ripples.
    // Muddy water drops this further so ripples read slower and blockier.
    [Export(PropertyHint.Range, "1,32,0.5")] public float ripplePixelSize = 6.0f;

    [ExportSubgroup("Refraction")]
    // Screen-space refraction strength. 0 disables (free — shader skips the
    // extra screen sample); >0 enables at the cost of one extra tap per
    // water fragment plus the implicit back-buffer copy Godot does for
    // hint_screen_texture. Muddiness damps this toward zero automatically.
    [Export(PropertyHint.Range, "0,1,0.001")] public float refractionStrength = 0.05f;

    [ExportSubgroup("Caustics")]
    // Underwater caustic bands projected onto the seabed reconstructed from
    // the depth buffer. Damped by muddiness and gated by direct sun
    // visibility in Apply(), so only lit shallows in clear water light up.
    [Export(PropertyHint.Range, "0,4,0.01")] public float causticStrength = 0.75f;
    [Export(PropertyHint.Range, "0.01,2,0.005")] public float causticScale = 0.35f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float causticSpeed = 0.4f;
    // Exponent applied to the ribbon pattern — higher values make caustics
    // into thin crisp lines; lower values spread them into a softer glow.
    // Default ~3 keeps bands chunky and visible; raise toward 8+ for
    // physically-thin focused beams (which can be hard to see at ortho iso).
    [Export(PropertyHint.Range, "1,32,0.5")] public float causticSharpness = 3.0f;
    // World-unit depth at which caustic intensity has attenuated by 1/e.
    // Keep small (1–3) so caustics are confined to true shallows.
    [Export(PropertyHint.Range, "0.1,10,0.05")] public float causticDepthFade = 2.0f;
    [Export] public Color causticColor = new Color(0.9f, 0.95f, 1.0f);
    // Per-(m/s of wind) drift rate for the caustic noise sample. Each
    // frame the shader's `caustic_offset` accumulates `wind_dir *
    // effectiveSpeed * causticDriftPerMps * dt`, where effectiveSpeed
    // floors at causticBaselineCurrent so the field still evolves slowly
    // in calm water (real water always has slight currents that keep
    // caustics moving). Same world-locked-scroll convention as foam_offset
    // and ripple_offset_a/b.
    [Export(PropertyHint.Range, "0,2,0.01")] public float causticDriftPerMps = 0.15f;
    // Floor on the wind speed used to integrate caustic_offset, in m/s.
    // Even in dead-calm weather caustics keep evolving at this baseline
    // rate so the pattern doesn't completely freeze. Real water has slight
    // currents and thermal motion; this is the visual stand-in.
    [Export(PropertyHint.Range, "0,4,0.05")] public float causticBaselineCurrent = 0.5f;

    [ExportSubgroup("Shoreline Foam")]
    // Foam COLOR is derived entirely from zone (DustColor + WaterColor +
    // muddiness) and current lighting (SunTint × light level) in Apply().
    // Only the SHAPE / SCALE / STRENGTH knobs remain here since those are
    // visual-sculpt choices, not zone-driven.
    [Export(PropertyHint.Range, "0.1,8,0.05")] public float foamDepth = 4.0f;
    [Export(PropertyHint.Range, "0.1,16,0.05")] public float foamScale = 7.0f;
    // Per-(m/s of wind) drift vector for shoreline foam noise UVs. Each
    // frame the shader's `foam_offset` accumulates `foamScroll * windSpeed
    // * dt`, so calm water freezes the foam pattern and stormy water pushes
    // it across the shore quickly. Direction stays fixed (not wind-aligned)
    // so the artistic choice of scroll direction survives wind shifts.
    [Export] public Vector2 foamScroll = new Vector2(0.5f, -0.15f);
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float foamStrength = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamThreshold = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamSharpness = 0.0f;

    // Screenspace reflection removed — see voxel_water.gdshader for the
    // rationale. Reflectable Sprite3D nodes spawn flipped child sprites via
    // LitSprite.UpdateReflection; the water shader's only reflection path is
    // sky/clouds/sun/moon/stars, which all sample correctly under any view.

    [ExportGroup("Wind")]
    // Sprite / grass sway amplitude in world meters per m/s of wind speed.
    // The shader's wind_amplitude global is computed each frame as
    //     GustedWindSpeed * windToSwayMeters
    // so changing wind weather scales sway naturally without touching the
    // shader or per-weather amplitude knobs.
    [Export(PropertyHint.Range, "0,0.05,0.0001")] public float windToSwayMeters = 0.013f;

    // Extra Hz of rustle frequency per m/s of GustedWindSpeed, added on top of
    // the per-weather palette.WindFrequency base when integrating windPhase.
    // The palette controls the "weather mood" frequency (calm vs overcast);
    // this knob makes live gusts also visibly speed up the rustle so a sudden
    // wind ramp reads as stormier, not just bigger. Applied to grass + tree
    // shaders alike via the shared wind_phase global. Default 0.1 gives
    // roughly +1 Hz at a 10 m/s gust — noticeable without becoming jittery.
    [Export(PropertyHint.Range, "0,1,0.01")] public float windFrequencyPerMps = 0.1f;

    [ExportGroup("Clouds")]
    // Cloud spatial tiling (authored). Separate from the weather-driven
    // cloudThreshold / cloudSharpness (which come from the palette) —
    // pattern SCALE is a scene-wide visual choice, not weather.
    [Export] public float cloudScale = 0.15f;
    // World Y of the flat cloud plane used for projective sun-shadow casting.
    [Export] public float cloudAltitude = 50f;
    // Cloud noise scroll rate per m/s of wind.
    [Export(PropertyHint.Range, "0,0.01,0.0001")] public float cloudScrollPerMps = 0.0015f;
    // Opacity of projected cloud shadows on the ground. 1.0 = cloud
    // fully blocks direct sun (shadow area = ambient only); 0.0 =
    // clouds cast no shadow. Values around 0.66 read as "clouds dim
    // the sun where they pass overhead but don't crush to black" —
    // preserves directional shape cues + warm sun tint + specular
    // in cloud-shadowed areas while still giving clouds visible
    // presence on the ground. Scene-structural, not weather-derived:
    // it's a contrast-sculpt choice, and ambient (on SimData) is the
    // separate knob for whole-scene shadow floor.
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShadowStrength = 0.66f;

    // Peak DirectionalLight3D energy added on top of the body's normal
    // LightEnergy during a lightning flash (flash intensity 1.0 adds
    // exactly this much; intensity 0.3 adds 30% of it). 3.0 reads as a
    // sharp pop above midday sun without saturating the scene to pure
    // white — adjustable per project. Applied to both sun and moon
    // lights so flashes light the world correctly at night too; only
    // the active body is Visible, so the energy on the inactive body
    // is harmless.
    [Export(PropertyHint.Range, "0,16,0.1")] public float lightningFlashEnergyBoost = 3.0f;

    [ExportGroup("Fog")]
    // Wire this to res://resources/materials/fog_volumetric.tres — the
    // shader's per-material uniforms are pushed here from the palette.
    [Export] public ShaderMaterial fogMaterial;
    [Export] public float fogMaxDistance = 100.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int fogSteps = 48;
    // Target world-space spacing between shaft raymarch samples (V1). The
    // march adds samples on deep dust bands so step size never exceeds this,
    // keeping the "platter" step banding sub-pixel. Lower = smoother beams,
    // higher fill cost. 0.35 m is a good smooth/perf balance.
    [Export(PropertyHint.Range, "0.05,2,0.01")] public float shaftStepSize = 0.35f;

    // External "see-farther" multiplier. The bird's-eye driver lerps this up
    // during the fly-up so the overview isn't choked by ground-level fog —
    // fog_max_distance scales linearly with it. 1.0 = unchanged (default
    // in-game state). Set by GameClient.UpdateBirdsEyeCamera; restored to 1.0
    // on FlyDown completion. Anything else (palette swaps, weather) is
    // unaffected.
    //
    // NOTE: this used to ALSO thin both fog densities by 1/multiplier, but that
    // dimmed the authored fog_map volumes (low-lying painted fog) the overview
    // is meant to show off. Authored fog now stays at full weather-scaled
    // density; only the GENERAL haze is suppressed, via AmbientFogScale below.
    public float FogVisibilityScale { get; set; } = 1f;

    // Multiplier on the uniform whole-scene haze (ambient_fog_density) only —
    // NOT the authored fog_map. The bird's-eye driver eases this toward ~0 so
    // the wide overview isn't washed out by general atmosphere while painted
    // low-lying fog volumes remain visible. 1.0 = unchanged (default in-game
    // state); restored to 1.0 on FlyDown completion.
    public float AmbientFogScale { get; set; } = 1f;

    [ExportGroup("Sunbeams")]
    [ExportSubgroup("Dust Band")]
    [Export(PropertyHint.Range, "1,64,0.1")] public float dustBandHeight = 16.0f;
    [Export(PropertyHint.Range, "0,2,0.001")] public float dustNoiseScale = 0.062f;
    [Export] public Vector2 dustNoiseScroll = new Vector2(0.05f, 0.03f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseStrength = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseThreshold = 0.2f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseSharpness = 0.5f;

    [ExportSubgroup("Inscatter")]
    [Export(PropertyHint.Range, "0,32,0.01")] public float blockHaloIntensity = 6.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpness = 0.95f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpnessLowSunFloor = 0.35f;

    // --- Sun-wash (carve-by-darkening) controls ---
    // How dark fully-shadowed air gets at full global wash — the main beam
    // CONTRAST knob. Darkens shadowed columns; expands value range, can't
    // blow out, doesn't desaturate.
    [Export(PropertyHint.Range, "0,1,0.01")] public float washShadowDarkness = 0.5f;
    // Bounded warm tint added in the lit gaps (sun/dust color in the beams).
    // The only additive term — keep small.
    [Export(PropertyHint.Range, "0,1,0.01")] public float washTintStrength = 0.15f;

    [ExportSubgroup("Shaping")]
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float shaftGroundFade = 0.2f;

    // Wetness is a client-only visual signal driven by rain / fog /
    // humidity. Lives here, not on SimData, because it never affects sim
    // (no AI / damage / etc. reads it) — only voxel_clip and detail_sprite
    // shaders sample the resulting `wetness_level` global. Tunables are
    // expressed in GAME minutes (DayLengthSeconds + time_scale aware) so
    // pacing tracks the in-world clock, not real time.
    //
    // Model: each input has a *ceiling fraction* — at full strength it
    // can wet things up to that fraction. The current target is
    //   target = max(rain*Krain, fog*Kfog, humidity*Khumid)
    // and the displayed wetness exponentially approaches that target with
    // the half-life below (same tau in both directions). Using max() not
    // sum() keeps wetness from creeping up just because every weather
    // axis contributes a little — a bone-dry desert with humidity=0.05
    // can never look wetter than 0.05*Khumid no matter how long it sits.
    [ExportGroup("Wetness")]
    // Wetness ceiling at full rain (rainAmount=1). 1.0 = full rain
    // eventually saturates the surface; smaller values cap how wet it can
    // ever get even in a downpour.
    [Export(PropertyHint.Range, "0,1,0.01")] public float wetnessFromRain = 1.0f;
    // Wetness ceiling at full derived fog (Fog=1). 0.6 = heavy fog gets
    // surfaces visibly damp but never as wet as actual rain.
    [Export(PropertyHint.Range, "0,1,0.01")] public float wetnessFromFog = 0.6f;
    // Wetness ceiling at full humidity (humidity=1). Small — a perfectly
    // muggy day leaves a faint dew, not actual rain-soaked surfaces.
    [Export(PropertyHint.Range, "0,1,0.005")] public float wetnessFromHumidity = 0.15f;
    // Half-life of the gap between displayed wetness and the current
    // target, in GAME minutes. Same tau in both directions: rain wets
    // surfaces over the first several half-lives; sun + low-humidity
    // weather dries them on the same curve.
    [Export(PropertyHint.Range, "0.5,60,0.5")] public float wetnessHalfLifeGameMinutes = 10f;
    // Specular highlight amplitude pushed to voxel_clip + detail_sprite via
    // the wet_spec_strength shader global. >~4 starts blooming through the
    // HDR bloom pass; tune alongside wetAlbedoFloor for the look you want.
    [Export(PropertyHint.Range, "0,32,0.1")] public float wetSpecStrength = 8.0f;
    // Albedo multiplier when fully wet. 1.0 = no darkening, 0.0 = solid
    // black. Lower values give the highlight more contrast against the
    // wet material at the cost of a more saturated dark base.
    [Export(PropertyHint.Range, "0,1,0.01")] public float wetAlbedoFloor = 0.15f;
    // Strength of the Fresnel sky reflection that makes wet ground look like
    // a partial mirror — the primary wet cue. 0 = old look (darken + sun
    // glint only); ~1 = strong grazing-angle sky sheen on flat wet ground.
    [Export(PropertyHint.Range, "0,4,0.05")] public float wetReflectStrength = 1.0f;
    // Schlick base reflectance for the wet Fresnel — head-on reflectivity
    // floor. Small; the grazing-angle term supplies most of the sheen.
    [Export(PropertyHint.Range, "0,0.3,0.005")] public float wetFresnelBase = 0.04f;

    // Accumulated cloud / ripple scroll offsets — integrated per frame from
    // `wind direction * speed`. These are the shader inputs (replacing the
    // old "speed * TIME" shader-side math) so mid-lerp speed changes don't
    // rescale the entire elapsed-time * speed product and visibly teleport
    // the texture. Exposed publicly so a future save/load layer can persist
    // and restore them — they're sim state, not authored data.
    public Vector2 cloudOffset;
    public Vector2 rippleOffsetA;
    public Vector2 rippleOffsetB;
    public Vector2 waveStreakOffsetA;
    public Vector2 waveStreakOffsetB;
    public Vector2 dustNoiseOffsetA;
    public Vector2 dustNoiseOffsetB;
    // Shoreline-foam UV scroll offset, integrated as
    // `foamScroll * windSpeed * dt` so foam moves only when there's wind
    // (driving wave energy at shore) and speeds up in stormy weather.
    public Vector2 foamOffset;
    // World-space caustic noise scroll offset, integrated as
    // `wind_dir * windSpeed * causticDriftPerMps * dt`. The shader sub-
    // tracts this from the seabed world position before sampling, so the
    // caustic field translates along wind direction in world units (same
    // pattern as ripple_offset / foam_offset).
    public Vector3 causticOffset;
    // Wave-displacement temporal phase, integrated as
    // `windSpeed * waveSpeedPerMps * dt`. Replaces a fixed `time * 1.2`
    // rate so wave undulation freezes in calm air and whips in gales.
    public float wavePhase;

    // --- Dynamic-ripple ring buffer ---------------------------------------
    // Active radial ripples emitted by entities moving through water.
    // Layout: parallel arrays so we can push the whole buffer to the shader
    // each frame as a single Rgbaf texture (xy=world XZ, z=age, w=strength).
    // Capped at MaxDynamicRipples — when full, the oldest entry is replaced
    // (visual loss is imperceptible because the eldest ripple is also the
    // dimmest from age fade).
    private const int MaxDynamicRipples = 32;
    private struct ActiveRipple
    {
        public float X, Z;
        public float Age;
        public float Strength;
    }
    private readonly ActiveRipple[] _dynamicRipples = new ActiveRipple[MaxDynamicRipples];
    private int _dynamicRippleCount;
    private Image _rippleImage;
    private ImageTexture _rippleTexture;
    // Grass / tree rustle sin phase. Integrates
    //     (palette.WindFrequency + GustedWindSpeed * windFrequencyPerMps) * dt
    // per frame so the rustle rate tracks both weather mood (palette base)
    // AND live gust speed. Pre-integration avoids the phase-jump artifact
    // you'd get from multiplying TIME by a varying frequency in the shader.
    public float windPhase;
    // Gust-wave phase in radians (integrates palette.GustFrequency * 2π
    // per frame). Drives the amplitude-multiplier wave in Apply().
    public float gustPhase;

    // --- Time-of-day / sun state -----------------------------------------
    // Primary light direction for the current frame (direction light travels).
    // Sun during the day half (t ∈ [0.25, 0.75]), moon during the night half.
    // Remapped / clamped so its elevation never drops below SunsetAngleDegrees
    // — the fade to invisible happens via LightEnergy, not by swinging the
    // direction horizontal.
    private Vector3 _primaryLightDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's ACTUAL direction on the (non-physical) full-orbit great circle.
    // NOT the remapped light direction — used only by the sky shader's sun
    // disk (sky_sun_dir) so the disk still slides visibly through the whole
    // sky on the authored arc.
    private Vector3 _sunActualDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's signed elevation in degrees from the sky-disk arc. Drives the
    // day/sunset/night phase blend in WeatherDerivation and anything else
    // that wants the body's celestial disk position (disk glow fade, water
    // clarity). NOT used for DirectionalLight3D energy or shaft fades —
    // those key off the remapped light-direction elevations below.
    private float _sunElevationDegrees = 45f;

    // Elevations (degrees) of the time-remapped sun- and moon-LIGHT
    // directions. Each is pinned to at least SunsetAngleDegrees during the
    // body's active half of the cycle and held at SunsetAngleDegrees while
    // the body is inactive, so SmoothStep fades keyed off them terminate
    // cleanly at t=0.25/0.75 instead of mid-afternoon.
    private float _sunLightElevationDegrees = 45f;
    private float _moonLightElevationDegrees = 25f;
    // Which body owns the primary light slot this frame. Set by
    // UpdateSunAndMoon (orbital-phase test, not elevation), consumed by
    // Apply() to hard-disable the inactive body's Visible + ShadowEnabled
    // so it contributes zero light AND skips the shadow atlas pass.
    // Without this, the inactive DirectionalLight3D still consumes a
    // shadow slot at LightEnergy=0, doubling shadow-render cost and
    // (in PSSM mode) splitting atlas resolution between two bodies.
    private bool _sunIsPrimary = true;

    // Normalized time-of-day used by UpdateSunAndMoon this frame. Cached
    // here so Apply() can compute time-based disk fades without repeating
    // the same World/editor fallback lookup.
    private double _timeOfDay01 = 0.5;

    // Current blended zone + weather (runtime). In editor mode these
    // stay null; the preview path reads previewZone / previewZone.weather
    // directly. ZoneBlend.Sample rewrites these in place each frame.
    private ZoneData _blendedZone;
    private WeatherData _blendedWeather;
    // Blended runtime fields for the current sample. Mirrors what would
    // live on a working ZoneState; kept as scalars so SkyController's
    // accessors don't have to reconstruct a struct on every read.
    private Vector3 _blendedWindDirection = new Vector3(1f, 0f, 0f);
    private float _blendedElevation;

    // Most recently derived palette. Updated in _Process before Apply.
    private DerivedPalette _palette;

    // --- Public accessors ------------------------------------------------
    // The current (blended) weather. RainEffect reads windSpeed from
    // here; gameplay might read rainAmount for gameplay gating in the
    // future. Wind DIRECTION lives on Zone (zone-intrinsic, not
    // weather-state) — see Zone accessor below.
    public WeatherData Weather
    {
        get
        {
            if (!Engine.IsEditorHint()) { return _blendedWeather; }
            return previewZone?.weather;
        }
    }

    // The current (blended) zone. Theme colors / dust / water come
    // from this; runtime fields (windDirection, elevation) come via
    // ZoneState below since they no longer live on the authored
    // ZoneData.
    public ZoneData Zone
    {
        get
        {
            if (!Engine.IsEditorHint()) { return _blendedZone; }
            return previewZone;
        }
    }

    // The current (blended) ZoneState — bundles the working ZoneData
    // with the runtime windDirection / elevation. RainEffect reads
    // WindDirection from here; WeatherSimulation reads Elevation.
    public ZoneState ZoneState
    {
        get
        {
            if (!Engine.IsEditorHint())
            {
                return new ZoneState
                {
                    Data = _blendedZone,
                    WindDirection = _blendedWindDirection,
                    Elevation = _blendedElevation,
                };
            }
            return new ZoneState
            {
                Data = previewZone,
                WindDirection = previewWindDirection,
                Elevation = previewElevation,
            };
        }
    }

    // The current derived palette. RainEffect reads RainIntensity /
    // RainWeight via ApplyPrecipitation; other consumers can read it
    // directly for ambient / shafts / etc. Returned by value (the
    // struct is small and callers read a single field at a time).
    public DerivedPalette Palette => _palette;

    // Sun elevation factor in [0, 1]: sin of the sun's elevation above the
    // horizon, clamped at 0 below it. Used as the multiplier on
    // WeatherData.sunTemperature when sampling environmental temperature —
    // 0 at night so the sun adds nothing while it's down, full at noon.
    public float SunFactor => Mathf.Max(0f, -_sunActualDir.Y);

    // Push a radial water-ripple impact at world XZ. Called by Player and
    // Mob each physics tick while moving through water (see
    // WaterRippleEmitter). The ripple expands as a ring at
    // `dynamicRippleSpeed` m/s and fades out over `dynamicRippleLifetime`
    // seconds. When the buffer is full, the oldest active ripple is
    // overwritten — by definition the most-faded one, so the visual loss
    // is imperceptible.
    public void EmitWaterRipple(Vector2 worldXZ, float strength)
    {
        if (_dynamicRippleCount < MaxDynamicRipples)
        {
            _dynamicRipples[_dynamicRippleCount++] = new ActiveRipple
            {
                X = worldXZ.X,
                Z = worldXZ.Y,
                Age = 0f,
                Strength = strength,
            };
            return;
        }
        int oldestIdx = 0;
        float oldestAge = -1f;
        for (int i = 0; i < MaxDynamicRipples; i++)
        {
            if (_dynamicRipples[i].Age > oldestAge)
            {
                oldestAge = _dynamicRipples[i].Age;
                oldestIdx = i;
            }
        }
        _dynamicRipples[oldestIdx] = new ActiveRipple
        {
            X = worldXZ.X,
            Z = worldXZ.Y,
            Age = 0f,
            Strength = strength,
        };
    }

    // windSpeed + gusted wave added on top. Exposed so RainEffect's tilt
    // math and SkyController's own sway amplitude agree on "how gusty is
    // right now" without both recomputing the wave. Updated in Apply().
    public float GustedWindSpeed { get; private set; }

    // Current blended ambient (day/sunset/night). Exposed so gameplay code
    // (WorldState.GetPerceivedLightWorld) reads the SAME ambient the shaders
    // see — stealth logic stays in sync with the visual darkness of night.
    public float CurrentAmbient { get; private set; } = 0.4f;

    // Current absolute primary intensity — palette day-side and night-side
    // values lerped by NightT. Pushed to the `sun_intensity` shader global;
    // exposing it here lets gameplay perception dim with the visuals at
    // dusk/night.
    public float CurrentPrimaryIntensity { get; private set; } = 2f;

    public override void _Ready()
    {
        Current = this;
        if (!Engine.IsEditorHint())
        {
            ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("fill_a_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
            ShaderGlobals.Register("fill_b_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
            // Sky-only globals for the sun/moon disks.
            ShaderGlobals.Register("sky_sun_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("moon_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.55f, 0.6f, 0.75f));
            ShaderGlobals.Register("sun_disk_glow", RenderingServer.GlobalShaderParameterType.Float, 1f);
            ShaderGlobals.Register("moon_disk_glow", RenderingServer.GlobalShaderParameterType.Float, 0f);
            ShaderGlobals.Register("sun_angular_size_deg", RenderingServer.GlobalShaderParameterType.Float, 2f);
            ShaderGlobals.Register("moon_angular_size_deg", RenderingServer.GlobalShaderParameterType.Float, 2.5f);
            ShaderGlobals.Register("sun_disk_intensity", RenderingServer.GlobalShaderParameterType.Float, 4.0f);
            ShaderGlobals.Register("moon_disk_intensity", RenderingServer.GlobalShaderParameterType.Float, 2.0f);
            ShaderGlobals.Register("sky_debug_sun_disk", RenderingServer.GlobalShaderParameterType.Bool, false);
            // Load default textures if the exports are unwired, so the sun/
            // moon always have a visible shape out of the box. Inspector-set
            // overrides take precedence via the Apply() push.
            if (sunTexture == null) { sunTexture = GD.Load<Texture2D>("res://assets/textures/skybox/sun_disc.tres"); }
            if (moonTexture == null) { moonTexture = GD.Load<Texture2D>("res://assets/textures/skybox/moon_disc.tres"); }
            if (starTexture == null) { starTexture = GD.Load<Texture2D>("res://assets/textures/skybox/starfield_placeholder.tres"); }
            if (sunTexture != null)
            {
                ShaderGlobals.Register("sun_texture", RenderingServer.GlobalShaderParameterType.Sampler2D, sunTexture);
            }
            if (moonTexture != null)
            {
                ShaderGlobals.Register("moon_texture", RenderingServer.GlobalShaderParameterType.Sampler2D, moonTexture);
            }
            if (starTexture != null)
            {
                ShaderGlobals.Register("star_texture", RenderingServer.GlobalShaderParameterType.Sampler2D, starTexture);
            }
            ShaderGlobals.Register("star_intensity", RenderingServer.GlobalShaderParameterType.Float, 1.0f);
            ShaderGlobals.Register("star_ripple_blur_lod", RenderingServer.GlobalShaderParameterType.Float, 4f);
            ShaderGlobals.Register("sky_night_factor", RenderingServer.GlobalShaderParameterType.Float, 0f);
            // Water globals pushed by Apply() — seed with sensible defaults so
            // shaders compile before the first Apply() runs without dropping
            // into "global was removed" warnings.
            ShaderGlobals.Register("water_shallow_tint", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.35f, 0.7f, 0.7f));
            ShaderGlobals.Register("water_deep_tint", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.05f, 0.15f, 0.4f));
            ShaderGlobals.Register("water_alpha_min", RenderingServer.GlobalShaderParameterType.Float, 0.3f);
            ShaderGlobals.Register("water_turbidity_exp", RenderingServer.GlobalShaderParameterType.Float, 1f);
            ShaderGlobals.Register("water_muddiness", RenderingServer.GlobalShaderParameterType.Float, 0.5f);
            ShaderGlobals.Register("water_refraction_strength", RenderingServer.GlobalShaderParameterType.Float, 0.05f);
            ShaderGlobals.Register("caustic_strength", RenderingServer.GlobalShaderParameterType.Float, 0.3f);
            ShaderGlobals.Register("caustic_scale", RenderingServer.GlobalShaderParameterType.Float, 0.35f);
            ShaderGlobals.Register("caustic_speed", RenderingServer.GlobalShaderParameterType.Float, 0.4f);
            ShaderGlobals.Register("caustic_sharpness", RenderingServer.GlobalShaderParameterType.Float, 8f);
            ShaderGlobals.Register("caustic_depth_fade", RenderingServer.GlobalShaderParameterType.Float, 2f);
            ShaderGlobals.Register("caustic_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.9f, 0.95f, 1.0f));
            ShaderGlobals.Register("caustic_offset", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Zero);
            ShaderGlobals.Register("reflection_tint", RenderingServer.GlobalShaderParameterType.Float, 0.7f);
            ShaderGlobals.Register("reflection_pixel_jitter_max", RenderingServer.GlobalShaderParameterType.Float, 2.0f);
            ShaderGlobals.Register("water_wave_amp", RenderingServer.GlobalShaderParameterType.Float, 0f);
            ShaderGlobals.Register("water_wave_length", RenderingServer.GlobalShaderParameterType.Float, 6f);
            ShaderGlobals.Register("water_wave_gate_scale", RenderingServer.GlobalShaderParameterType.Float, 0.05f);
            ShaderGlobals.Register("wave_streak_strength", RenderingServer.GlobalShaderParameterType.Float, 0f);
            ShaderGlobals.Register("wave_streak_scale_a", RenderingServer.GlobalShaderParameterType.Float, 0.05f);
            ShaderGlobals.Register("wave_streak_scale_b", RenderingServer.GlobalShaderParameterType.Float, 0.09f);
            ShaderGlobals.Register("wave_streak_offset_a", RenderingServer.GlobalShaderParameterType.Vec2, Vector2.Zero);
            ShaderGlobals.Register("wave_streak_offset_b", RenderingServer.GlobalShaderParameterType.Vec2, Vector2.Zero);
            ShaderGlobals.Register("whitecap_foam_strength", RenderingServer.GlobalShaderParameterType.Float, 0.7f);
            ShaderGlobals.Register("whitecap_foam_threshold", RenderingServer.GlobalShaderParameterType.Float, 0.02f);
            ShaderGlobals.Register("foam_min_light", RenderingServer.GlobalShaderParameterType.Float, 0.4f);
            ShaderGlobals.Register("water_depth_scale", RenderingServer.GlobalShaderParameterType.Float, 6f);
            ShaderGlobals.Register("water_edge_opacity", RenderingServer.GlobalShaderParameterType.Float, 0.3f);
            ShaderGlobals.Register("water_rim_width", RenderingServer.GlobalShaderParameterType.Float, 0.2f);
            ShaderGlobals.Register("water_rim_strength", RenderingServer.GlobalShaderParameterType.Float, 0.6f);
            ShaderGlobals.Register("ripple_pixel_size", RenderingServer.GlobalShaderParameterType.Float, 6f);
            ShaderGlobals.Register("water_debug_mode", RenderingServer.GlobalShaderParameterType.Int, 0);
            ShaderGlobals.Register("reflection_debug_mode", RenderingServer.GlobalShaderParameterType.Int, 0);
            ShaderGlobals.Register("water_disable_ripples", RenderingServer.GlobalShaderParameterType.Bool, false);
            ShaderGlobals.Register("reflection_min", RenderingServer.GlobalShaderParameterType.Float, 0.2f);
            ShaderGlobals.Register("reflection_fov_h_deg", RenderingServer.GlobalShaderParameterType.Float, 90f);
            ShaderGlobals.Register("reflection_fov_v_deg", RenderingServer.GlobalShaderParameterType.Float, 90f);
            ShaderGlobals.Register("reflection_fov_v_center", RenderingServer.GlobalShaderParameterType.Float, 0.3f);

            // Lingering surface wetness in [0, 1]. Driven by WorldState.WetnessLevel,
            // pushed from Apply() each frame. Declared as `global uniform` in
            // voxel_clip.gdshader, so seed via Register (not RegisterRuntime) —
            // RegisterRuntime would call GlobalShaderParameterAdd on a name the
            // shader compiler already created, tripping the duplicate-add error.
            ShaderGlobals.Register("wetness_level", RenderingServer.GlobalShaderParameterType.Float, 0f);
            ShaderGlobals.Register("wet_spec_strength", RenderingServer.GlobalShaderParameterType.Float, wetSpecStrength);
            ShaderGlobals.Register("wet_albedo_floor", RenderingServer.GlobalShaderParameterType.Float, wetAlbedoFloor);
            ShaderGlobals.Register("wet_reflect_strength", RenderingServer.GlobalShaderParameterType.Float, wetReflectStrength);
            ShaderGlobals.Register("wet_fresnel_base", RenderingServer.GlobalShaderParameterType.Float, wetFresnelBase);

            // Working copies for the zone blend output. Re-populated in
            // _Process each frame — these exist so ZoneBlend can write
            // into stable instances without allocating per frame.
            _blendedZone = new ZoneData();
            _blendedWeather = new WeatherData();
        }

        // Dynamic ripple buffer + control globals — created in BOTH editor
        // and runtime modes because SkyController carries [Tool] and Apply()
        // (the per-frame Set pusher) runs in editor too. Without registering
        // here the editor spams material_storage.cpp:1677 every frame trying
        // to Set globals that don't exist (CLAUDE.md cause #3).
        // Width=MaxDynamicRipples × height=1 Rgbaf image holding (x, z, age,
        // strength) per active ripple. Sampled via texelFetch in
        // voxel_water.gdshader; allocated once and updated in-place each
        // frame.
        _rippleImage = Image.CreateEmpty(MaxDynamicRipples, 1, false, Image.Format.Rgbaf);
        _rippleTexture = ImageTexture.CreateFromImage(_rippleImage);
        ShaderGlobals.RegisterRuntime("water_ripple_tex", RenderingServer.GlobalShaderParameterType.Sampler2D, _rippleTexture);
        ShaderGlobals.RegisterRuntime("water_ripple_count", RenderingServer.GlobalShaderParameterType.Int, 0);
        ShaderGlobals.RegisterRuntime("water_ripple_speed", RenderingServer.GlobalShaderParameterType.Float, dynamicRippleSpeed);
        ShaderGlobals.RegisterRuntime("water_ripple_lifetime", RenderingServer.GlobalShaderParameterType.Float, dynamicRippleLifetime);
        ShaderGlobals.RegisterRuntime("water_ripple_fade_in", RenderingServer.GlobalShaderParameterType.Float, dynamicRippleFadeIn);
        ShaderGlobals.RegisterRuntime("water_ripple_falloff", RenderingServer.GlobalShaderParameterType.Float, dynamicRippleFalloff);
        ShaderGlobals.RegisterRuntime("water_ripple_tilt", RenderingServer.GlobalShaderParameterType.Float, dynamicRippleTilt);

        UpdateSunAndMoon();
        // Seed the palette with null-safe fallbacks so the very first
        // Apply() (before _Process has ever run) doesn't push a zeroed
        // DerivedPalette to the shaders — which would briefly blacken
        // the sky during scene load.
        _palette = WeatherDerivation.Derive(null, null, _sunElevationDegrees, 0.5f, null);
        Apply();
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        using var _prof = Profiler.Sample("SkyController.Process");
        // Blend zones → (_blendedZone, _blendedWeather). In editor or
        // before the World is up, fall back to previewZone.
        ZoneData currentZone = _blendedZone;
        WeatherData currentWeather = _blendedWeather;
        SimData sim = World.Current?.WorldState?.SimData;

        if (!Engine.IsEditorHint() && sim != null && _blendedZone != null && _blendedWeather != null)
        {
            Vector3 playerPos = World.Current.player?.GlobalPosition ?? Vector3.Zero;
            WorldState ws = World.Current.WorldState;
            ZoneBlend.Sample(playerPos, ws, _blendedZone, _blendedWeather,
                out _blendedWindDirection, out _blendedElevation);

            // Diurnal + 12-hour-variance perturbation on top of the
            // zone-blended max envelope. Re-rolls the variance state
            // when game time crosses a 12-hour boundary, then rewrites
            // _blendedWeather in place with the values currently in
            // effect (so WeatherDerivation and every downstream consumer
            // sees the simulated weather, not the zone max).
            if (ws != null)
            {
                WeatherSimulation.UpdateVariance(ws, sim);
                WeatherSimulation.Apply(_blendedWeather, _blendedZone, _blendedElevation, ws, sim);
                // Publish the blended wind direction to WorldState so
                // gameplay consumers (RainEffect, physics) see a single
                // authoritative current wind. Other weather variables
                // currently have no gameplay readers, but this is where
                // they'd flow through.
                ws.WindDirection = _blendedWindDirection;
            }
        }
        else
        {
            currentZone = previewZone;
            currentWeather = previewZone?.weather;
        }

        // Orbit first — derivation needs _sunElevationDegrees.
        UpdateSunAndMoon();

        // Derive. A null zone/weather still produces a palette with
        // fallback values so editor preview works without wiring.
        _palette = WeatherDerivation.Derive(currentZone, currentWeather, _sunElevationDegrees, (float)_timeOfDay01, sim);

        // Advance lingering surface wetness from the post-Derive inputs
        // (palette.Fog is computed inside Derive). Runs only when a real
        // WorldState exists — preview zone wetness has nothing to drive.
        WorldState wetnessWs = World.Current?.WorldState;
        if (wetnessWs != null && sim != null)
        {
            UpdateWetness(wetnessWs, currentWeather, _palette.Fog, sim, (float)delta);
        }

        // Integrate scroll offsets using the CURRENT (blended) weather
        // speed / palette frequencies. Parametric `speed * TIME` in the
        // shader can't do this — changing speed would rescale accumulated
        // time and snap the texture.
        float dt = (float)delta;
        if (currentWeather != null)
        {
            Vector3 windDir = !Engine.IsEditorHint() ? _blendedWindDirection : previewWindDirection;
            Vector2 windXZ = new Vector2(windDir.X, windDir.Z);
            if (windXZ.LengthSquared() > 0.0001f) { windXZ = windXZ.Normalized(); }
            else { windXZ = new Vector2(1f, 0f); }
            float angleB = Mathf.DegToRad(rippleAngleOffsetB);
            Vector2 windXZ_B = new Vector2(
                windXZ.X * Mathf.Cos(angleB) - windXZ.Y * Mathf.Sin(angleB),
                windXZ.X * Mathf.Sin(angleB) + windXZ.Y * Mathf.Cos(angleB));

            // Steady wind only for cloud + ripple drift; gusts drive
            // sprite sway + rain tilt via GustedWindSpeed.
            //
            // Sign is NEGATIVE so the visible cloud/ripple motion matches
            // the wind direction. See the original rationale: shaders
            // sample `cuv = world_xz * tiling + cloud_offset`, so adding
            // to the sample coord makes the visible pattern scroll in
            // the -offset direction.
            float steadySpeed = currentWeather.windSpeed;
            // Ripple scroll saturates at rippleSpeedSaturation — at higher
            // wind the pattern stops scrolling faster, which prevents the
            // ripple surface from smearing into visual noise. Strength
            // (normal perturbation) keeps scaling beyond this via the
            // RippleWindRef curve in WeatherDerivation.
            float rippleScrollSpeed = Mathf.Min(steadySpeed, Mathf.Max(rippleSpeedSaturation, 0.01f));
            cloudOffset -= windXZ * steadySpeed * cloudScrollPerMps * dt;
            rippleOffsetA -= windXZ * rippleScrollSpeed * rippleSpeedA * dt;
            rippleOffsetB -= windXZ_B * rippleScrollSpeed * rippleSpeedB * dt;
            // Wave-streak offsets integrate the same way — keeps the noise
            // pattern locked to world XZ, with offset advancing along wind.
            waveStreakOffsetA -= windXZ * waveStreakSpeedA * dt;
            waveStreakOffsetB -= windXZ * waveStreakSpeedB * dt;
            // Caustic noise scroll. Integrated in 3D world space; layer 2
            // of the shader's caustic_pattern is offset by this vector,
            // so it slides past the stationary layer 1 → ribbons evolve
            // at a wind-driven rate. Effective speed is floored at
            // causticBaselineCurrent so calm water still has slight
            // evolution (background currents).
            float causticEffSpeed = Mathf.Max(steadySpeed, causticBaselineCurrent);
            causticOffset -= new Vector3(windXZ.X, 0f, windXZ.Y) * causticEffSpeed * causticDriftPerMps * dt;
            // Shoreline foam scroll. Direction is the authored foamScroll
            // vector (NOT wind-aligned, by artistic choice); only magnitude
            // scales with wind speed.
            foamOffset += foamScroll * steadySpeed * dt;
            // Wave displacement phase. Steady wind only (gusts shouldn't
            // visibly stutter the wave temporal frequency).
            wavePhase += steadySpeed * waveSpeedPerMps * dt;
            windPhase += (_palette.WindFrequency + GustedWindSpeed * windFrequencyPerMps) * dt;
            gustPhase += _palette.GustFrequency * Mathf.Tau * dt;

            dustNoiseOffsetA += dustNoiseScroll * dt;
            Vector2 dustScrollB = new Vector2(-dustNoiseScroll.Y, dustNoiseScroll.X) * 0.7f;
            dustNoiseOffsetB += dustScrollB * dt;
        }

        TickDynamicRipples(dt);

        Apply();

        Vector3 fillADir = ComputeFillDirection(_primaryLightDir, fillAPitchDegrees, fillAYawOffsetDegrees);
        Vector3 fillBDir = ComputeFillDirection(_primaryLightDir, fillBPitchDegrees, fillBYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", _primaryLightDir);
        RenderingServer.GlobalShaderParameterSet("fill_a_world_dir", fillADir);
        RenderingServer.GlobalShaderParameterSet("fill_b_world_dir", fillBDir);

    }

    // Advance every active dynamic ripple's age by dt, drop expired entries
    // by compacting in place, then upload the buffer to the GPU as a
    // width=MaxDynamicRipples Rgbaf row. Pixel layout per active slot is
    // (X, Z, Age, Strength); inactive slots are zeroed so the shader's
    // bounds-checked loop skips them. SetPixel on Rgbaf format stores the
    // four Color floats verbatim — outside [0,1] is fine.
    private void TickDynamicRipples(float dt)
    {
        if (_rippleImage == null || _rippleTexture == null)
        {
            return;
        }

        float lifetime = Mathf.Max(dynamicRippleLifetime, 0.01f);
        int writeIdx = 0;
        for (int i = 0; i < _dynamicRippleCount; i++)
        {
            _dynamicRipples[i].Age += dt;
            if (_dynamicRipples[i].Age >= lifetime)
            {
                continue;
            }
            if (writeIdx != i)
            {
                _dynamicRipples[writeIdx] = _dynamicRipples[i];
            }
            writeIdx++;
        }
        _dynamicRippleCount = writeIdx;

        for (int i = 0; i < MaxDynamicRipples; i++)
        {
            if (i < _dynamicRippleCount)
            {
                ActiveRipple r = _dynamicRipples[i];
                _rippleImage.SetPixel(i, 0, new Color(r.X, r.Z, r.Age, r.Strength));
            }
            else
            {
                _rippleImage.SetPixel(i, 0, new Color(0f, 0f, 0f, 0f));
            }
        }
        _rippleTexture.Update(_rippleImage);
    }

    // Compute sun / moon positions from the current time and push results
    // to _sunActualDir (sky disk), _primaryLightDir / _sunLightElevationDegrees
    // / _moonLightElevationDegrees (DirectionalLight3Ds + shader fades), and
    // _sunElevationDegrees (WeatherDerivation's phase blend).
    //
    // Two arcs, not one:
    //   - The SKY DISK uses the full great-circle orbit so the sun / moon
    //     sprite still slides through the sky and below the geometric
    //     horizon naturally. This is purely a visual sprite position,
    //     not a simulated body.
    //   - The LIGHT DIRECTION is remapped from time-of-day so sunrise /
    //     sunset land exactly at SunsetAngleDegrees (the effective
    //     horizon), and the body is "held" at that endpoint during its
    //     inactive half of the cycle. Outside its active half, LightEnergy
    //     has already faded to 0, so updating the direction is wasted
    //     work — holding it keeps shadow geometry sane without implying
    //     a sub-horizon "orbit" that isn't a real light.
    private void UpdateSunAndMoon()
    {
        double t;
        if (!Engine.IsEditorHint() && World.Current?.WorldState != null)
        {
            t = World.Current.WorldState.TimeOfDay01;
        }
        else
        {
            t = previewTimeOfDay;
        }
        _timeOfDay01 = t;

        SimData sim = World.Current?.WorldState?.SimData;
        float sunMaxElev = sim?.sunMaxElevationDegrees ?? 60f;
        float noonAzimuth = sim?.noonAzimuthDegrees ?? 45f;
        float sunsetAngle = sim?.sunsetAngleDegrees ?? 15f;

        // Shared orbit basis. noonDir is the sun-at-noon direction on the
        // celestial sphere (azimuth + max elevation); eastDir is horizontal
        // at noon's azimuth + 90° (where the sun's arc emerges). noonDir
        // and eastDir are orthonormal, so `noonDir·sin(θ) + eastDir·cos(θ)`
        // parameterizes a great circle peaking at θ=π/2.
        //
        // This models southern-hemisphere-style orbits naturally: for a
        // world where +X+Z is north and the player is south of the equator
        // (sun passes through north at noon), set NoonAzimuthDegrees = 45.
        float azimuthRad = Mathf.DegToRad(noonAzimuth);
        float maxElevRad = Mathf.DegToRad(sunMaxElev);
        float cosMaxElev = Mathf.Cos(maxElevRad);

        Vector3 noonDir = new Vector3(
            Mathf.Sin(azimuthRad) * cosMaxElev,
            Mathf.Sin(maxElevRad),
            Mathf.Cos(azimuthRad) * cosMaxElev);
        Vector3 eastDir = new Vector3(
            Mathf.Sin(azimuthRad + Mathf.Pi * 0.5f),
            0f,
            Mathf.Cos(azimuthRad + Mathf.Pi * 0.5f));

        // --- Sky disk: unchanged full-orbit great circle ---------------
        // θ = 0 at t=0.25 (east horizon), π/2 at noon, π at t=0.75 (west
        // horizon), 3π/2 at midnight. Visual sprite only — this is the
        // "non-physical" disk position kept separate from the light arc.
        float diskPhase = Mathf.Tau * ((float)t - 0.25f);
        Vector3 sunDiskPos = (noonDir * Mathf.Sin(diskPhase) + eastDir * Mathf.Cos(diskPhase)).Normalized();
        _sunActualDir = (-sunDiskPos).Normalized();
        _sunElevationDegrees = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sunDiskPos.Y, -1f, 1f)));

        // --- Light directions: remapped so rise/set land at
        //     SunsetAngleDegrees exactly at t=0.25 / 0.75 -----------------
        // Pick θ₀ so sin(θ₀) · sin(SunMaxElev) == sin(SunsetAngle), then
        // lerp θ across [θ₀, π − θ₀] over the active half of the cycle.
        // Outside the active half, dayT clamps to 0 or 1 and the light is
        // held at the rise or set endpoint. Sun's active half is [0.25,
        // 0.75]; moon's is [0.75, 1.25] wrapped through midnight. The
        // wrap produces one invisible direction jump at t=0.5 in the
        // moon's held direction — harmless because the moon light has
        // been at LightEnergy=0 for the entire sun day.
        float sinSunset = Mathf.Sin(Mathf.DegToRad(sunsetAngle));
        float sinMaxSafe = Mathf.Max(Mathf.Sin(maxElevRad), 1e-4f);
        float phaseAtSunsetAngle = Mathf.Asin(Mathf.Clamp(sinSunset / sinMaxSafe, -1f, 1f));

        Vector3 ComputeLightPos(double bodyT)
        {
            float dayT = Mathf.Clamp((float)((bodyT - 0.25) * 2.0), 0f, 1f);
            float phase = Mathf.Lerp(phaseAtSunsetAngle, Mathf.Pi - phaseAtSunsetAngle, dayT);
            return (noonDir * Mathf.Sin(phase) + eastDir * Mathf.Cos(phase)).Normalized();
        }

        Vector3 sunLightPos = ComputeLightPos(t);
        double moonT = t + 0.5;
        if (moonT >= 1.0) { moonT -= 1.0; }
        Vector3 moonLightPos = ComputeLightPos(moonT);

        Vector3 sunLightDir = (-sunLightPos).Normalized();
        Vector3 moonLightDir = (-moonLightPos).Normalized();

        // Primary switches by time-of-day half, not elevation — both
        // directions are clamped to sunsetAngle in their off-half, so an
        // elevation test can't tell them apart.
        bool isDay = t >= 0.25 && t < 0.75;
        _primaryLightDir = isDay ? sunLightDir : moonLightDir;
        _sunIsPrimary = isDay;

        _sunLightElevationDegrees = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sunLightPos.Y, -1f, 1f)));
        _moonLightElevationDegrees = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(moonLightPos.Y, -1f, 1f)));

        OrientLight(sunLight, sunLightDir);
        OrientLight(moonLight, moonLightDir);

        if (!Engine.IsEditorHint() && World.Current?.WorldState != null)
        {
            World.Current.WorldState.ShadowLightDirection = _primaryLightDir;
        }
    }

    // Push the current palette + time-of-day state to the GPU.
    public void Apply()
    {
        CurrentAmbient = _palette.Ambient;

        // Day and night sides each have a single SimData knob baked into
        // the palette (DayIntensityBase / NightIntensityBase). SkyController
        // just lerps the two by NightT — no extra global multipliers, so
        // tuning the sun and moon are fully independent edits in SimData.
        float effDayIntensity = _palette.PrimaryIntensity;
        float effNightIntensity = _palette.NightPrimaryIntensity;
        CurrentPrimaryIntensity = Mathf.Lerp(effDayIntensity, effNightIntensity, _palette.NightT);

        SimData sim = World.Current?.WorldState?.SimData;
        float sunsetAngle = sim?.sunsetAngleDegrees ?? 10f;

        // DirectionalLight3D energy crossfade — keyed off each body's
        // REMAPPED light-direction elevation (clamped at sunsetAngle
        // during its inactive half), so the fade terminates exactly at
        // t=0.25/0.75 rather than at the body's actual celestial horizon
        // crossing. Moon energy is additionally scaled by
        // NightPrimaryIntensity so moonlight is physically dimmer than
        // daylight regardless of whether Godot's shadow pass sees it as
        // "the one active light".
        float lightFadeEnd = sunsetAngle + lightEnergyFadeAngleDegrees;
        float lightFadeStart = lightFadeEnd + Mathf.Max(lightEnergyFadeRangeDegrees, 0.01f);
        float sunEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, _sunLightElevationDegrees);
        float moonEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, _moonLightElevationDegrees);

        // Lightning flash boost. Added on top of each body's baseline
        // LightEnergy so a flash brightens the scene at any time of
        // day. Only the active body is Visible, so the boost on the
        // inactive one is silently discarded. The companion
        // cloud_shadow_strength blank-out below ensures the boost
        // actually reaches the ground even where a cloud was darkening
        // it (the user-requested "should brighten even under cloud
        // shadows" behavior).
        float flashIntensity = LightningFlasher.Current?.Intensity ?? 0f;
        float flashEnergy = flashIntensity * lightningFlashEnergyBoost;

        // Hard-disable the inactive body. Even at LightEnergy=0 a
        // DirectionalLight3D with ShadowEnabled=true still renders a
        // full shadow atlas pass and (in PSSM mode) splits the atlas
        // budget with the active body — so the moon's "off" shadow
        // is silently halving the sun's resolution at noon. Toggling
        // Visible off the inactive body removes both the energy and
        // shadow contributions cleanly.
        if (sunLight != null)
        {
            sunLight.LightEnergy = sunEnergyFactor + flashEnergy;
            sunLight.Visible = _sunIsPrimary;
        }
        if (moonLight != null)
        {
            moonLight.LightEnergy = moonEnergyFactor * _palette.NightPrimaryIntensity + flashEnergy;
            moonLight.Visible = !_sunIsPrimary;
        }

        // Shadow softness via light_angular_distance (degrees). Penumbra
        // width scales with caster distance and is consistent across PSSM
        // cascades — unlike shadow_blur (texel-counted), which produces
        // camera-angle-dependent quality. Soften based on primary-light
        // PITCH plus a small lift from hazy air. Sun + moon share a shadow
        // atlas; drive off the HIGHER of the two remapped elevations so
        // the active body dictates the softness.
        //
        // cos(elev) is the "how sideways the light is" term — 0 at
        // zenith, 1 at horizon. With SunMaxElevationDegrees < 90 the
        // factor never reaches 0 at noon; that's correct — a sun that
        // never quite reaches overhead has slightly softened shadows
        // even at peak.
        float primaryLightElev = Mathf.Max(_sunLightElevationDegrees, _moonLightElevationDegrees);
        float elevBlurFactor = Mathf.Clamp(Mathf.Cos(Mathf.DegToRad(Mathf.Max(primaryLightElev, 0f))), 0f, 1f);
        float humidityForBlur = Weather?.humidity ?? 0.5f;
        float dustForBlur = Weather?.dustAmount ?? 0.1f;
        float atmBlurFactor = Mathf.Clamp(humidityForBlur + dustForBlur, 0f, 1f);
        float effShadowAngular = shadowAngularBase
            + shadowAngularLowSunBoost * elevBlurFactor
            + shadowAngularAtmosphericBoost * atmBlurFactor;
        if (sunLight != null) { sunLight.LightAngularDistance = effShadowAngular; }
        if (moonLight != null) { moonLight.LightAngularDistance = effShadowAngular; }

        // _nightT for disk glow fade. Same formula as WeatherDerivation.PhaseWeights.
        float colorRange = Mathf.Max(sim?.sunsetColorRangeDegrees ?? 10f, 0.01f);
        float dayNightThreshold = sunsetAngle + colorRange;
        float nightT = 1f - Mathf.SmoothStep(-dayNightThreshold, dayNightThreshold, _sunElevationDegrees);

        float effSunDiskGlow = sunDiskGlowStrength * (1f - nightT);
        float effMoonDiskGlow = moonDiskGlowStrength * nightT;
        // Sun disk intensity lerps between sunsetDiskIntensity (horizon) and
        // sunDiskIntensity (noon) with sin(orbital phase) as the parameter —
        // sin(phase) is 1 at noon, 0 at sunrise/sunset. Derived from the
        // sun's current elevation vs. its max: sin(phase) = sin(elev)/sin(maxElev).
        // Clamped to [0,1] so below-horizon time lands at sunsetDiskIntensity
        // (the disk is invisible there anyway via the shader's sun_up gate).
        // Humidity and dust then attenuate multiplicatively — thicker air
        // scatters the disk softer regardless of time-of-day.
        float sunMaxElevRad = Mathf.DegToRad(sim?.sunMaxElevationDegrees ?? 60f);
        float sinMaxElev = Mathf.Max(Mathf.Sin(sunMaxElevRad), 1e-4f);
        float sunPhaseT = Mathf.Clamp(Mathf.Sin(Mathf.DegToRad(_sunElevationDegrees)) / sinMaxElev, 0f, 1f);
        float humidityForDisk = Weather?.humidity ?? 0f;
        float dustForDisk = Weather?.dustAmount ?? 0f;
        float fogForDisk = _palette.Fog;
        float humidityAtten = Mathf.Lerp(1f, 1f - humidityDiskDim, humidityForDisk);
        float dustAtten = Mathf.Lerp(1f, 1f - dustDiskDim, dustForDisk);

        // Time-of-day fade for the disks — ramps in at rise over
        // sunDiskFadeTime seconds and out before set over the same,
        // so the disk doesn't pop on/off at the horizon. Active windows
        // are [0.25, 0.75] for the sun and [0.75, 1.25] (wrapped) for
        // the moon. fadeTod is clamped to half the active window (0.25
        // of a day) so very long fade times produce a triangular peak
        // rather than overlapping past 1.
        float dayLengthSec = Mathf.Max(sim?.dayLengthSeconds ?? 600f, 0.01f);
        float fadeTod = Mathf.Clamp(sunDiskFadeTime / dayLengthSec, 0f, 0.25f);
        float sunDiskFade = ComputeDiskFade(_timeOfDay01, 0.25, 0.75, fadeTod);
        float moonDiskFade = ComputeDiskFade(_timeOfDay01, 0.75, 1.25, fadeTod);

        float effSunDiskIntensity = Mathf.Lerp(sunsetDiskIntensity, sunDiskIntensity, sunPhaseT) * humidityAtten * dustAtten * sunDiskFade;

        // Same atmospheric attenuation idea for nighttime celestial: fog
        // and dust both obscure the moon and stars. Stars get a steeper
        // default falloff since they're far dimmer to begin with.
        float moonFogAtten = Mathf.Lerp(1f, 1f - fogMoonDim, fogForDisk);
        float moonDustAtten = Mathf.Lerp(1f, 1f - dustMoonDim, dustForDisk);
        float effMoonDiskIntensity = moonDiskIntensity * moonFogAtten * moonDustAtten * moonDiskFade;
        float starFogAtten = Mathf.Lerp(1f, 1f - fogStarDim, fogForDisk);
        float starDustAtten = Mathf.Lerp(1f, 1f - dustStarDim, dustForDisk);
        float effStarIntensity = starIntensity * starFogAtten * starDustAtten;

        // --- Global uniforms ---------------------------------------------
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(_palette.SunTint));
        RenderingServer.GlobalShaderParameterSet("sun_ambient", _palette.Ambient);
        RenderingServer.GlobalShaderParameterSet("sun_intensity", CurrentPrimaryIntensity);
        RenderingServer.GlobalShaderParameterSet("fill_a_color", ColorToVec3(_palette.FillA));
        RenderingServer.GlobalShaderParameterSet("fill_b_color", ColorToVec3(_palette.FillB));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(_palette.HorizonTint));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(_palette.ZenithTint));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(_palette.CloudTint));
        RenderingServer.GlobalShaderParameterSet("sky_sun_dir", _sunActualDir);
        RenderingServer.GlobalShaderParameterSet("moon_color", ColorToVec3(_palette.MoonDiskColor));
        RenderingServer.GlobalShaderParameterSet("sun_disk_glow", effSunDiskGlow);
        RenderingServer.GlobalShaderParameterSet("moon_disk_glow", effMoonDiskGlow);
        RenderingServer.GlobalShaderParameterSet("sun_angular_size_deg", sunAngularSizeDeg);
        RenderingServer.GlobalShaderParameterSet("moon_angular_size_deg", moonAngularSizeDeg);
        RenderingServer.GlobalShaderParameterSet("sun_disk_intensity", effSunDiskIntensity);
        RenderingServer.GlobalShaderParameterSet("moon_disk_intensity", effMoonDiskIntensity);
        if (sunTexture != null) { RenderingServer.GlobalShaderParameterSet("sun_texture", sunTexture); }
        if (moonTexture != null) { RenderingServer.GlobalShaderParameterSet("moon_texture", moonTexture); }
        if (starTexture != null) { RenderingServer.GlobalShaderParameterSet("star_texture", starTexture); }
        RenderingServer.GlobalShaderParameterSet("star_intensity", effStarIntensity);
        RenderingServer.GlobalShaderParameterSet("star_ripple_blur_lod", starRippleBlurLod);
        RenderingServer.GlobalShaderParameterSet("sky_night_factor", nightT);
        RenderingServer.GlobalShaderParameterSet("cloud_offset", cloudOffset);
        RenderingServer.GlobalShaderParameterSet("cloud_threshold", _palette.CloudThreshold);
        RenderingServer.GlobalShaderParameterSet("cloud_sharpness", _palette.CloudSharpness);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_altitude", cloudAltitude);
        // Cloud-shadow attenuation. Multiplicatively blanked by an
        // active lightning flash so the flash energy boost above reaches
        // the ground even where a cloud was darkening it — the
        // "brighten even under cloud shadows" requirement. At flash
        // intensity 1.0 cloud shadows fully disappear; at 0 the
        // authored cloudShadowStrength is unchanged.
        float flashShadowMask = 1f - Mathf.Clamp(flashIntensity, 0f, 1f);
        RenderingServer.GlobalShaderParameterSet("cloud_shadow_strength", cloudShadowStrength * flashShadowMask);
        RenderingServer.GlobalShaderParameterSet("wetness_level", World.Current?.WorldState?.WetnessLevel ?? 0f);
        RenderingServer.GlobalShaderParameterSet("wet_spec_strength", wetSpecStrength);
        RenderingServer.GlobalShaderParameterSet("wet_albedo_floor", wetAlbedoFloor);
        RenderingServer.GlobalShaderParameterSet("wet_reflect_strength", wetReflectStrength);
        RenderingServer.GlobalShaderParameterSet("wet_fresnel_base", wetFresnelBase);

        // --- Water -------------------------------------------------------
        // Muddiness comes from ZoneData.WaterColor.a (via palette). It drives:
        //   - reflection boost (denser surface = better mirror)
        //   - refraction damp (particles scatter before bending)
        //   - whitecap threshold lift (viscous water resists foam)
        //   - wave amplitude damp (heavier water moves less)
        //   - ripple cell size nudge (chunkier, slower-feeling ripples)
        //   - foam tint toward water color (done in derivation)
        float muddy = _palette.WaterMuddiness;

        // Sun-vs-ambient clarity modulates surface alpha: direct sun penetrates
        // and lights the bottom (reads more translucent); overcast/ambient-only
        // bounces off the surface (reads more opaque). Gated by sun elevation
        // so the moon doesn't count — moonlight doesn't penetrate water
        // meaningfully, so night water should read opaque at its authored alpha.
        float sunAbove = Mathf.SmoothStep(0f, 12f, _sunElevationDegrees);
        float primaryLit = Mathf.Max(CurrentPrimaryIntensity, 0f);
        float ambientLit = Mathf.Max(CurrentAmbient, 0f);
        float sunClarity = sunAbove * primaryLit / (primaryLit + ambientLit * 2f + 1e-4f);
        float effAlphaMin = Mathf.Clamp(_palette.WaterAlphaMin * Mathf.Lerp(1.0f, 0.6f, sunClarity), 0f, 1f);

        // Light-level factor: direct-light proxy used to dim reflection and
        // glint when the scene is dim. Without this, night water reads as a
        // glowing mirror of the (faint) night sky and moon-lit glint halos
        // paint the entire surface.
        float lightLevel = Mathf.Clamp(CurrentPrimaryIntensity / 2.0f, 0.2f, 1.0f);

        float cloudCover01 = Weather?.cloudCover ?? 0f;
        float humidity01 = Weather?.humidity ?? 0.5f;
        float fog01 = _palette.Fog;
        float effFresnel = fresnelPower + cloudCover01 * 0.8f;
        // Reflection clarity is about AIR + WATER quality, not ambient brightness.
        // A clear night with a moon should reflect the moon cleanly; scaling
        // reflection by direct light intensity would kill that. Instead:
        //   muddy  → scatters light within the water (diffuse surface)
        //   humid  → hazy air softens the mirror
        //   fog    → more severe scattering
        // No lightLevel term here so moon/stars reflect at full strength on a
        // clear calm night.
        float reflectionClarity = Mathf.Lerp(1.0f, 0.1f, muddy)
            * (1f - humidity01 * 0.4f)
            * (1f - fog01 * 0.6f)
            * (1f - cloudCover01 * 0.2f);
        float effReflection = reflectionStrength * reflectionClarity;

        // "Glassy" conditions — bright direct sun on clear, low-humidity,
        // unfogged water. Under these the surface should read as a window,
        // not a mirror: reflections only on grazing angles and ripple
        // crests (fresnel peaks), floor pushed near zero so top-down view
        // sees through. muddy, fog, humidity, cloud cover each pull it back
        // toward a glossy mirror so overcast/mucky water still reflects.
        float glassyT = sunClarity
            * (1f - muddy)
            * (1f - fog01)
            * (1f - humidity01 * 0.6f)
            * (1f - cloudCover01 * 0.5f);
        // Reduce the fresnel-driven reflection a bit in glassy conditions
        // (grazing highlights still exist, just slightly dimmer) so the
        // whole surface doesn't flash white when the sun is low.
        effReflection *= Mathf.Lerp(1f, 0.4f, glassyT);
        // Refraction collapses to zero as muddiness saturates — particles
        // scatter before light can bend cleanly. Dropping all the way to 0
        // (rather than 10%) lets the shader's "skip SCREEN_TEXTURE sample"
        // path fire on fully-opaque muddy water, which is the whole point
        // of that early-exit in voxel_water.gdshader.
        float effRefraction = refractionStrength * Mathf.Lerp(1.0f, 0.0f, muddy);
        // Depth scale: clean water in bright light lets sight reach many
        // meters down; murky water or dim light crushes visible depth
        // toward a voxel or two. Floor keeps the ramp non-zero so pixels
        // right at the shoreline don't pop to full opacity at any light.
        float effDepthScale = Mathf.Max(waterDepthScale * Mathf.Lerp(1.3f, 0.35f, muddy) * lightLevel, 0.3f);
        float effWaveAmp = GustedWindSpeed * waveAmpPerMps * Mathf.Lerp(1.0f, 0.35f, muddy);
        float effRipplePx = ripplePixelSize * Mathf.Lerp(1.0f, 0.6f, muddy);

        // Wind-driven cell size: positive rippleScaleWindResponse enlarges
        // ripple cells at higher wind (longer-wavelength waves); negative
        // shrinks them. Formula divides the UV multiplier so the cell size
        // in world units grows with the response factor.
        SimData sim2 = World.Current?.WorldState?.SimData;
        float rippleWindRef = sim2?.rippleWindRef ?? 10f;
        float windFrac = Mathf.Clamp((Weather?.windSpeed ?? 0f) / Mathf.Max(rippleWindRef, 0.1f), 0f, 1f);
        float scaleShift = 1f / Mathf.Max(1f + rippleScaleWindResponse * windFrac, 0.1f);
        float effRippleScaleA = rippleScaleA * scaleShift;
        float effRippleScaleB = rippleScaleB * scaleShift;

        RenderingServer.GlobalShaderParameterSet("ripple_scale_a", effRippleScaleA);
        RenderingServer.GlobalShaderParameterSet("ripple_scale_b", effRippleScaleB);
        RenderingServer.GlobalShaderParameterSet("ripple_offset_a", rippleOffsetA);
        RenderingServer.GlobalShaderParameterSet("ripple_offset_b", rippleOffsetB);
        RenderingServer.GlobalShaderParameterSet("ripple_strength", _palette.RippleStrength);
        RenderingServer.GlobalShaderParameterSet("ripple_pixel_size", effRipplePx);
        RenderingServer.GlobalShaderParameterSet("water_ripple_count", _dynamicRippleCount);
        RenderingServer.GlobalShaderParameterSet("water_ripple_speed", dynamicRippleSpeed);
        RenderingServer.GlobalShaderParameterSet("water_ripple_lifetime", Mathf.Max(dynamicRippleLifetime, 0.01f));
        RenderingServer.GlobalShaderParameterSet("water_ripple_fade_in", Mathf.Max(dynamicRippleFadeIn, 0.001f));
        RenderingServer.GlobalShaderParameterSet("water_ripple_falloff", dynamicRippleFalloff);
        RenderingServer.GlobalShaderParameterSet("water_ripple_tilt", dynamicRippleTilt);
        RenderingServer.GlobalShaderParameterSet("water_shallow_tint", ColorToVec3(_palette.WaterShallowTint));
        RenderingServer.GlobalShaderParameterSet("water_deep_tint", ColorToVec3(_palette.WaterDeepTint));
        RenderingServer.GlobalShaderParameterSet("water_alpha_min", effAlphaMin);
        // Lerp the depth-saturated alpha cap by muddiness so clean water
        // never reaches full opacity even through deep columns.
        float effAlphaMax = Mathf.Lerp(waterAlphaMaxClean, waterAlphaMaxMuddy, muddy);
        RenderingServer.GlobalShaderParameterSet("water_alpha_max", effAlphaMax);
        RenderingServer.GlobalShaderParameterSet("water_turbidity_exp", _palette.WaterTurbidityExp);
        RenderingServer.GlobalShaderParameterSet("water_depth_scale", effDepthScale);
        RenderingServer.GlobalShaderParameterSet("water_edge_opacity", waterEdgeOpacity);
        RenderingServer.GlobalShaderParameterSet("water_rim_width", rimWidth);
        RenderingServer.GlobalShaderParameterSet("water_rim_strength", rimStrength);
        RenderingServer.GlobalShaderParameterSet("water_muddiness", muddy);
        RenderingServer.GlobalShaderParameterSet("water_refraction_strength", effRefraction);
        // Caustics scale with three factors: water clarity, inverse wind
        // speed (calmer surface focuses light into bands; choppy water
        // scatters them), and per-fragment total received light — the
        // last one lives in the shader (`light` = sun_lit + block_lit).
        // That keeps a torch in a cave producing caustics on the seabed
        // below it, even though no sky is reachable, while shadowed water
        // with no nearby light fades to nothing. RippleStrength is the
        // wind-driven proxy used here: it's already wind+rain damped by
        // WeatherDerivation, so calm weather gives full caustics and a
        // storm collapses them. Per-fragment cloud shadows + sun
        // intensity decay at night come for free via the shader's
        // `sun_lit` term, so no explicit cloud-cover or time-of-day
        // factor is needed at this level.
        // Wind damp is squared: a chopped-up surface scatters focus
        // dramatically — linear damping left caustics readable on cloudy/
        // windy days, which broke the artistic intent (caustics should be
        // a calm-water-only effect). Squaring kills them above moderate
        // wind while preserving the full pop on glassy water.
        float windDamp = 1f - _palette.RippleStrength;
        float effCaustic = causticStrength
            * (1f - muddy)
            * windDamp * windDamp;
        RenderingServer.GlobalShaderParameterSet("caustic_strength", effCaustic);
        RenderingServer.GlobalShaderParameterSet("caustic_scale", causticScale);
        RenderingServer.GlobalShaderParameterSet("caustic_speed", causticSpeed);
        RenderingServer.GlobalShaderParameterSet("caustic_sharpness", causticSharpness);
        RenderingServer.GlobalShaderParameterSet("caustic_depth_fade", causticDepthFade);
        RenderingServer.GlobalShaderParameterSet("caustic_color", ColorToVec3(causticColor));
        RenderingServer.GlobalShaderParameterSet("caustic_offset", causticOffset);
        // Sprite reflection brightness — same air-clarity factors as the
        // sky reflection (muddiness, humidity, fog, cloud cover) plus a
        // light-level term so dim scenes don't paint bright mirror copies
        // of upright objects on dark water. CVar `sprite_reflections_disabled`
        // forces the tint to zero, killing the entire effect without
        // touching any LitSprite reflection nodes.
        float effSpriteReflTint = CVars.spriteReflectionsDisabled.Value
            ? 0f
            : spriteReflectionTint * reflectionClarity * lightLevel;
        RenderingServer.GlobalShaderParameterSet("reflection_tint", effSpriteReflTint);
        // Reflection pixel jitter scales with weather-driven ripple strength
        // (already wind+rain damped in WeatherDerivation), so calm water
        // produces a near-rigid mirror and choppy water visibly distorts.
        RenderingServer.GlobalShaderParameterSet("reflection_pixel_jitter_max", spriteReflectionPixelJitter);
        RenderingServer.GlobalShaderParameterSet("water_wave_amp", effWaveAmp);
        RenderingServer.GlobalShaderParameterSet("water_wave_length", Mathf.Max(waveLength, 0.1f));
        RenderingServer.GlobalShaderParameterSet("water_wave_gate_scale", waveGateScale);
        RenderingServer.GlobalShaderParameterSet("water_wave_phase", wavePhase);
        // Wave streaks ride the same wind/rain/muddiness curve as the base
        // ripples (baked into _palette.RippleStrength by WeatherDerivation),
        // so calm water gets no streaks and breezy/rainy water shows visible
        // crest noise + whitecaps on top of the ripple normal.
        float effStreakStrength = waveStreakStrength * _palette.RippleStrength;
        RenderingServer.GlobalShaderParameterSet("wave_streak_strength", effStreakStrength);
        RenderingServer.GlobalShaderParameterSet("wave_streak_scale_a", waveStreakScaleA);
        RenderingServer.GlobalShaderParameterSet("wave_streak_scale_b", waveStreakScaleB);
        RenderingServer.GlobalShaderParameterSet("wave_streak_offset_a", waveStreakOffsetA);
        RenderingServer.GlobalShaderParameterSet("wave_streak_offset_b", waveStreakOffsetB);
        RenderingServer.GlobalShaderParameterSet("whitecap_foam_strength", whitecapFoamStrength);
        RenderingServer.GlobalShaderParameterSet("whitecap_foam_threshold", whitecapFoamThreshold);
        RenderingServer.GlobalShaderParameterSet("fresnel_power", effFresnel);
        RenderingServer.GlobalShaderParameterSet("reflection_strength", effReflection);
        // Reflection floor: in glassy conditions we want fresnel to be the
        // ONLY driver of reflection (grazing highlights, ripple glints).
        // Drop the floor toward zero when glassyT → 1, so top-down views
        // through the surface are dominated by the through-water color +
        // caustics rather than a minimum reflection layer.
        float effReflectionMin = reflectionMin * reflectionClarity * Mathf.Lerp(1f, 0.05f, glassyT);
        RenderingServer.GlobalShaderParameterSet("reflection_min", effReflectionMin);
        RenderingServer.GlobalShaderParameterSet("reflection_fov_h_deg", reflectionFovHorizontalDeg);
        RenderingServer.GlobalShaderParameterSet("reflection_fov_v_deg", reflectionFovVerticalDeg);
        RenderingServer.GlobalShaderParameterSet("reflection_fov_v_center", reflectionFovVerticalCenter);
        // Foam color derived entirely from regional palette + direct light:
        //   - Start from a soft tint of the zone's DustColor (shoreline
        //     froth physically carries suspended sediment — the regional
        //     "particulate color" is the closest we have to that).
        //   - Pull toward WaterShallowTint by muddiness, so murky water's
        //     "foam" reads as scum/film in the water's own color rather
        //     than bright white.
        //   - Multiply by SunTint (CurrentPrimaryIntensity gated by
        //     lightLevel) so foam warms at sunset / cools at moonlight /
        //     dims at night rather than staying a single hard value.
        // No foamColor export — white foam under every condition fights
        // too many regional palettes. Lightness baseline (0.9) keeps clean
        // shoreline surf readably bright against deep water.
        Color foamParticulate = _palette.FogTint.Lerp(new Color(1f, 1f, 1f), 0.4f);
        Color foamBase = foamParticulate.Lerp(_palette.WaterShallowTint, muddy * 0.7f);
        Color sunTintLit = new Color(
            _palette.SunTint.R * lightLevel,
            _palette.SunTint.G * lightLevel,
            _palette.SunTint.B * lightLevel,
            1f);
        Color effFoam = new Color(
            Mathf.Clamp(foamBase.R * (0.55f + 0.5f * sunTintLit.R), 0f, 1f),
            Mathf.Clamp(foamBase.G * (0.55f + 0.5f * sunTintLit.G), 0f, 1f),
            Mathf.Clamp(foamBase.B * (0.55f + 0.5f * sunTintLit.B), 0f, 1f),
            1f);
        RenderingServer.GlobalShaderParameterSet("foam_color", ColorToVec3(effFoam));
        // Foam lighting floor — a small base so foam never collapses to
        // black plus a clarity-and-light-level bonus so clear bright
        // conditions show crisp white foam while overcast/foggy/dim
        // conditions let it dim with the scene. Without this scale, foam
        // looks emissive on dark stormy nights (a constant 0.7 floor was
        // higher than ambient sky light, making foam glow against
        // surrounding water that was lit by dim moonlight only).
        float clarity = (1f - cloudCover01 * 0.7f) * (1f - fog01 * 0.5f);
        float effFoamMinLight = 0.18f + 0.45f * lightLevel * clarity;
        RenderingServer.GlobalShaderParameterSet("foam_min_light", effFoamMinLight);
        RenderingServer.GlobalShaderParameterSet("foam_depth", foamDepth);
        RenderingServer.GlobalShaderParameterSet("foam_scale", foamScale);
        RenderingServer.GlobalShaderParameterSet("foam_offset", foamOffset);
        RenderingServer.GlobalShaderParameterSet("foam_strength", foamStrength);
        RenderingServer.GlobalShaderParameterSet("foam_threshold", foamThreshold);
        RenderingServer.GlobalShaderParameterSet("foam_sharpness", foamSharpness);

        // --- Wind --------------------------------------------------------
        // Two-octave low-frequency sin sum for naturally uneven gusts.
        // Output is [0, 1]; added to windSpeed via GustStrength so
        // effective speed stays in [windSpeed, windSpeed + GustStrength].
        float gustWave = Mathf.Sin(gustPhase) * 0.7f
                       + Mathf.Sin(gustPhase * 1.7f + 1.3f) * 0.3f;
        float gust01 = (gustWave + 1f) * 0.5f;
        WeatherData weather = Weather;
        float steadyWindSpeed = weather?.windSpeed ?? 0f;
        GustedWindSpeed = steadyWindSpeed + gust01 * _palette.GustStrength;
        float amplitude = GustedWindSpeed * windToSwayMeters;

        Vector3 windDirForShader = !Engine.IsEditorHint() ? _blendedWindDirection : previewWindDirection;
        if (windDirForShader.LengthSquared() < 1e-6f) { windDirForShader = new Vector3(1f, 0f, 0f); }
        RenderingServer.GlobalShaderParameterSet("wind_dir", windDirForShader.Normalized());
        RenderingServer.GlobalShaderParameterSet("wind_amplitude", amplitude);
        RenderingServer.GlobalShaderParameterSet("wind_phase", windPhase);

        // --- Shaft blend (sun → moon) ------------------------------------
        // Fade each source's shaft contribution as ITS body approaches the
        // horizon. Shaft COLORS already have sunset warm bias baked in by
        // derivation; this step combines sun + moon channels into one
        // effective intensity + color via the horizon smoothstep.
        float shaftFadeEnd = sunsetAngle + shaftFadeAngleDegrees;
        float shaftFadeStart = shaftFadeEnd + Mathf.Max(shaftFadeRangeDegrees, 0.1f);
        float sunShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, _sunLightElevationDegrees);
        float moonShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, _moonLightElevationDegrees);

        // Global sun-wash intensity (client-side tuning). Product of the scalar
        // weather: humidity-folded dust × (baseline + cloudGain × cloudCover²),
        // capped. cloudCover² so heavier overhead cover pushes beams brighter
        // (and sparser). The shaft COLOUR (zone-derived) stays in the palette.
        float washCloudCover = weather?.cloudCover ?? 0f;
        float washDust = weather?.dustAmount ?? 0.1f;
        float washHumidity = weather?.humidity ?? 0.5f;
        float washDustFromHumidity = sim?.dustFromHumidity ?? 0.5f;
        float washEffDust = Mathf.Clamp(washDust + washHumidity * washDustFromHumidity, 0f, 1f);
        float washCover = shaftWashBaseline + shaftWashCloudGain * washCloudCover * washCloudCover;
        float washIntensity = Mathf.Min(shaftWashMax, washEffDust * washCover);

        // Combined day/night shaft fade (sun + moon-scaled), reaching 0 at the
        // day/night boundary. Folded into effShaftIntensity for the weather
        // wash; passed separately so the shader can fade the per-voxel
        // authored-fog shaft boost on the same curve.
        float shaftDayFactor = sunShaftFactor + moonBeamScale * moonShaftFactor;
        float effShaftIntensity = washIntensity * shaftDayFactor;
        if (!CVars.sunShafts.Value)
        {
            effShaftIntensity = 0f;
            shaftDayFactor = 0f;
        }

        float shaftColorT = moonShaftFactor / (sunShaftFactor + moonShaftFactor + 1e-6f);
        Color effShaftColor = _palette.SunShaftColor.Lerp(_palette.MoonShaftColor, shaftColorT);

        // Dynamic fog step count. When the primary light is low in the sky,
        // each raymarch step crosses more sun/shadow boundaries per unit of
        // march distance — so we boost step count there to kill banding
        // without spending the cycles at noon.
        float primaryY = Mathf.Abs(_primaryLightDir.Y);
        float stepScale = Mathf.Min(1f / Mathf.Max(primaryY, 0.3f), 2f);
        int effFogSteps = Mathf.Clamp(Mathf.RoundToInt(fogSteps * stepScale), fogSteps, 128);

        // --- Fog material uniforms ---------------------------------------
        if (fogMaterial != null)
        {
            // FogVisibilityScale > 1 stretches the raymarch range so the overview
            // sees far; AmbientFogScale < 1 suppresses the general whole-scene
            // haze without touching the authored fog_map. Both default to 1.0
            // (palette untouched) outside the bird's-eye overlook. Authored fog
            // (fog_density → fog_map) stays at full weather-scaled density so
            // painted low-lying fog volumes remain visible in the overview.
            float visScale = Mathf.Max(0.01f, FogVisibilityScale);
            fogMaterial.SetShaderParameter("fog_color", ColorToVec3(_palette.FogTint));
            fogMaterial.SetShaderParameter("fog_density", _palette.FogDensity);
            fogMaterial.SetShaderParameter("ambient_fog_density", _palette.AmbientFogDensity * AmbientFogScale);
            fogMaterial.SetShaderParameter("fog_max_distance", fogMaxDistance * visScale);
            fogMaterial.SetShaderParameter("fog_steps", effFogSteps);
            fogMaterial.SetShaderParameter("shaft_step_size", shaftStepSize);
            fogMaterial.SetShaderParameter("dust_density", _palette.DustDensity);
            fogMaterial.SetShaderParameter("dust_band_height", dustBandHeight);

            float playerY = World.Current?.player?.GlobalPosition.Y ?? float.NaN;
            float ceiling = float.IsNaN(playerY) ? -1e20f : playerY + dustBandHeight;
            fogMaterial.SetShaderParameter("dust_reference_y", ceiling);
            fogMaterial.SetShaderParameter("dust_noise_strength", dustNoiseStrength);
            fogMaterial.SetShaderParameter("dust_noise_scale", dustNoiseScale);
            fogMaterial.SetShaderParameter("dust_noise_threshold", dustNoiseThreshold);
            fogMaterial.SetShaderParameter("dust_noise_sharpness", dustNoiseSharpness);
            fogMaterial.SetShaderParameter("dust_noise_scroll", dustNoiseScroll);
            fogMaterial.SetShaderParameter("dust_noise_offset_a", dustNoiseOffsetA);
            fogMaterial.SetShaderParameter("dust_noise_offset_b", dustNoiseOffsetB);
            fogMaterial.SetShaderParameter("sun_shaft_intensity", effShaftIntensity);
            fogMaterial.SetShaderParameter("shaft_color", ColorToVec3(effShaftColor));
            fogMaterial.SetShaderParameter("block_halo_intensity", blockHaloIntensity);
            fogMaterial.SetShaderParameter("fog_shaft_gain", fogShaftGain);
            fogMaterial.SetShaderParameter("shaft_day_factor", shaftDayFactor);
            fogMaterial.SetShaderParameter("wash_shadow_darkness", washShadowDarkness);
            fogMaterial.SetShaderParameter("wash_tint_strength", washTintStrength);
            fogMaterial.SetShaderParameter("shaft_light_floor", CVars.shaftLightFloor.Value);

            float shaftSharpnessBlend = Mathf.Max(sunShaftFactor, moonShaftFactor);
            float effCloudShaftSharpness = Mathf.Lerp(cloudShaftSharpnessLowSunFloor, cloudShaftSharpness, shaftSharpnessBlend);
            fogMaterial.SetShaderParameter("cloud_shaft_sharpness", effCloudShaftSharpness);
            fogMaterial.SetShaderParameter("shaft_ground_fade", shaftGroundFade);
        }

        ApplyMotes(washDust, washCloudCover, sunShaftFactor, moonShaftFactor);
        ApplyPrecipitation();
    }

    // Drives the floating dust-mote GPU particle system (MoteEffect). Density
    // (AmountRatio) is gated on RAW dust amount (`dust`, weather.dustAmount) —
    // NOT the humidity-folded value the shafts use — so dust motes appear only
    // in genuinely DUSTY air. Humid / foggy but clean air still shows god-rays
    // (those run on humidity+dust) but no motes. Also faded by shaft presence
    // so motes vanish at night with no moon. The per-particle beam/occlusion/
    // noise gates in mote.gdshader do the spatial selection; the specular glint
    // colour is the global sun_color, so only the dust-hued base is pushed here.
    private void ApplyMotes(float dust, float cloudCover, float sunShaftFactor, float moonShaftFactor)
    {
        MoteEffect motes = MoteEffect.Current;
        if (motes == null) { return; }

        float shaftPresence = Mathf.Clamp(sunShaftFactor + moonShaftFactor, 0f, 1f);
        // Clean "not dusty → none" to "dusty → full" ramp on the raw dust level.
        float wash = Mathf.SmoothStep(0.05f, 0.4f, dust) * shaftPresence;
        if (!CVars.sunShafts.Value) { wash = 0f; }
        motes.SetIntensity(wash);

        if (motes.MoteMatRuntime != null)
        {
            // Base albedo = the true regional dust colour (tan/ochre), NOT
            // FogTint — FogTint is atmospheric haze and reads near-white, which
            // washed the motes out.
            motes.MoteMatRuntime.SetShaderParameter("dust_color", ColorToVec3(_palette.DustColor));
            // Cloud cover feeds the mote shaft-occlusion gate (same signal the
            // god-rays use) so overcast days light motes in the sunlit gaps and
            // clear days don't blanket open ground.
            motes.MoteMatRuntime.SetShaderParameter("cloud_cover", cloudCover);
            // Mirror the fog's animated dust-noise (identical values + scroll
            // offsets) so motes sit inside the SAME structured beam shape and
            // their edges track the shafts instead of just roughly agreeing.
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_scale", dustNoiseScale);
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_threshold", dustNoiseThreshold);
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_sharpness", dustNoiseSharpness);
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_strength", dustNoiseStrength);
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_offset_a", dustNoiseOffsetA);
            motes.MoteMatRuntime.SetShaderParameter("dust_noise_offset_b", dustNoiseOffsetB);
        }
    }

    // Dynamic precipitation manager. Consumes palette.RainIntensity +
    // palette.RainWeight and scales the RainEffect node's runtime
    // materials accordingly. rainWeight scales fall velocity, drop
    // albedo alpha, and streak length linearly, and inversely scales
    // wind tilt via RainEffect.WindTiltScale.
    private void ApplyPrecipitation()
    {
        RainEffect rain = RainEffect.Current;
        if (rain == null) { return; }

        rain.SetIntensity(_palette.RainIntensity);

        float weight = Mathf.Max(_palette.RainWeight, 0.01f);

        if (rain.FallProcRuntime != null)
        {
            rain.FallProcRuntime.InitialVelocityMin = rain.BaseInitialVelocityMin * weight;
            rain.FallProcRuntime.InitialVelocityMax = rain.BaseInitialVelocityMax * weight;
        }

        if (rain.DropMatRuntime != null)
        {
            Color albedo = rain.BaseDropAlbedo;
            albedo.A = rain.BaseDropAlbedo.A * weight;
            rain.DropMatRuntime.SetShaderParameter("albedo", albedo);
            rain.DropMatRuntime.SetShaderParameter("streak_length_px", rain.BaseStreakLengthPx * weight);
        }

        if (rain.SplashMatRuntime != null)
        {
            Color splash = rain.BaseSplashAlbedo;
            splash.A = rain.BaseSplashAlbedo.A * weight;
            rain.SplashMatRuntime.SetShaderParameter("albedo", splash);
        }

        rain.WindTiltScale = 1.0f / weight;
    }

    // Advance displayed wetness in [0, 1] toward a weather-derived target.
    // Target = max(rain*Krain, fog*Kfog, humidity*Khumid) — using max()
    // not sum() so wetness can't creep up just because every axis is
    // slightly nonzero; a desert with humidity=0.05 caps at 0.05*Khumid
    // forever. Approach is first-order with the configured half-life
    // (same tau both directions), and times are in GAME minutes
    // (DayLengthSeconds + time_scale aware) so pacing tracks the in-world
    // clock. Uses the post-Derive `fog` value (palette.Fog) so the visible
    // fog and the wetness it implies stay coupled.
    private void UpdateWetness(WorldState ws, WeatherData weather, float fog, SimData sim, float dt)
    {
        if (ws == null || sim == null || dt <= 0f) { return; }
        float rain = Mathf.Clamp(weather?.rainAmount ?? 0f, 0f, 1f);
        float humidity = Mathf.Clamp(weather?.humidity ?? 0f, 0f, 1f);
        float fogClamped = Mathf.Clamp(fog, 0f, 1f);
        float target = Mathf.Max(
            rain * wetnessFromRain,
            Mathf.Max(fogClamped * wetnessFromFog, humidity * wetnessFromHumidity));
        target = Mathf.Clamp(target, 0f, 1f);
        // 24 game-hours * 60 game-min = 1440 game-min/day.
        float dayLength = Mathf.Max(sim.dayLengthSeconds, 1f);
        float gameMinPerRealSec = (1440f / dayLength) * CVars.timeScale.Value;
        float dtGameMin = dt * gameMinPerRealSec;
        float halfLifeGameMin = Mathf.Max(wetnessHalfLifeGameMinutes, 1e-3f);
        // alpha = 1 - 0.5^(dt/halfLife) — fraction of the gap to close
        // this frame. Frame-rate independent and continuous.
        float alpha = 1f - Mathf.Pow(0.5f, dtGameMin / halfLifeGameMin);
        ws.WetnessLevel = Mathf.Clamp(ws.WetnessLevel + (target - ws.WetnessLevel) * alpha, 0f, 1f);
    }

    private static Vector3 ComputeFillDirection(Vector3 sunDir, float pitchDeg, float yawOffsetDeg)
    {
        float sunYaw = Mathf.Atan2(sunDir.X, sunDir.Z);
        float fillYaw = sunYaw + Mathf.DegToRad(yawOffsetDeg);
        float pitch = Mathf.DegToRad(pitchDeg);
        float horiz = Mathf.Cos(pitch);
        Vector3 dir = new Vector3(horiz * Mathf.Sin(fillYaw), -Mathf.Sin(pitch), horiz * Mathf.Cos(fillYaw));
        return dir.Normalized();
    }

    private static Vector3 ColorToVec3(Color c)
    {
        return new Vector3(c.R, c.G, c.B);
    }

    private static void OrientLight(DirectionalLight3D light, Vector3 lightDir)
    {
        if (light == null) { return; }
        Vector3 pos = light.GlobalPosition;
        Vector3 up = Mathf.Abs(lightDir.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        light.LookAtFromPosition(pos, pos + lightDir, up);
    }

    // 0 outside [riseT, setT], smoothstep up over fadeTod at rise,
    // plateau at 1, smoothstep down over fadeTod before set.
    // setT may exceed 1 to express a window that wraps past midnight
    // (moon: rise 0.75, set 1.25). t wraps into [riseT, riseT+1).
    private static float ComputeDiskFade(double t, double riseT, double setT, float fadeTod)
    {
        double window = setT - riseT;
        if (window <= 0.0) { return 0f; }
        double rel = t - riseT;
        if (rel < 0.0) { rel += 1.0; }
        if (rel >= window) { return 0f; }
        float plateau = Mathf.Min(fadeTod, (float)(window * 0.5));
        if (plateau <= 0f) { return 1f; }
        float riseFade = Mathf.SmoothStep(0f, plateau, (float)rel);
        float setFade = Mathf.SmoothStep(0f, plateau, (float)(window - rel));
        return Mathf.Min(riseFade, setFade);
    }
}
