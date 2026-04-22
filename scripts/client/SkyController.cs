using Godot;

// Owns the authored atmospheric state: sky dome colors, clouds, sun tint,
// fog, haze, inscatter. Everything here is simulation-driven visual state
// that weather / time-of-day systems write to; none of it belongs in CVars.
//
// Two output channels, handled uniformly:
//   - Global shader uniforms (sky dome + voxel + sprite + water consume
//     these via project.godot [shader_globals]). Registered once in _Ready
//     so CVar overrides would stick — but these values aren't CVars any
//     more, so it's just a seeding pass.
//   - Per-material uniforms on fog_volumetric.tres (the fog raymarch is the
//     only consumer). Pushed to the material in _Ready via Apply().
//
// For runtime updates (weather systems, day/night transitions): mutate the
// [Export] fields, then call Apply() to re-push to GPU. _Process handles
// the one genuinely dynamic value — sun direction from WorldState.
//
// [Tool] makes this script execute in the Godot editor too. _Process calls
// Apply() every editor frame, so any value tweaked in the inspector shows
// its effect immediately in the viewport without needing to play the scene.
[Tool]
[GlobalClass]
public partial class SkyController : Node3D
{
    // Static reference for CVars / weather-preset code that needs to mutate
    // atmospheric exports at runtime. Set in _Ready, cleared in _ExitTree.
    // There's only one SkyController per game scene.
    public static SkyController Current { get; private set; }

    [ExportGroup("Weather")]
    // Live, mutable weather state. In the editor this points at a static .tres
    // (e.g. default.tres); at runtime _Ready duplicates it so LerpToWeather can
    // mutate fields without touching the authored asset on disk. All weather-
    // driven exports (sky colors, wind, clouds, fog, inscatter) live on this
    // resource so a weather transition is a single Lerp over every field.
    [Export] public WeatherData weather;

    [ExportGroup("TimeOfDay")]
    [ExportSubgroup("Lights")]
    // Wire to the scene's SunLight DirectionalLight3D. SkyController writes
    // this light's transform each frame to face the sun's actual position
    // (never flips to the moon). Its LightEnergy is scaled by the sun's
    // above-horizon factor so the sun's shadow fades out as it sets.
    [Export] public DirectionalLight3D sunLight;
    // Wire to the scene's MoonLight DirectionalLight3D. Mirror of sunLight
    // for the moon's side of the sky — oriented at the moon's position each
    // frame, LightEnergy scaled by the moon's above-horizon factor. Enables
    // genuine simultaneous sun+moon directional shadows during dawn/dusk
    // crossover; moon shadows cast at night without sun having to be
    // re-used as a stand-in. Lit shaders multiply their DIFFUSE_LIGHT
    // contribution by LIGHT_ENERGY so the sum across the two lights stays
    // correct (no double-counting during either's dormant phase).
    [Export] public DirectionalLight3D moonLight;

    [ExportSubgroup("Preview")]
    // Editor preview only — no WorldState exists in the editor, so the
    // orbit needs a manual parameter to preview nighttime / sunset looks.
    // At runtime this is ignored and WorldState.TimeOfDay01 drives the orbit.
    //
    // The orbit shape ITSELF (SunMaxElevationDegrees, SunSideSwayDegrees,
    // SunsetAngleDegrees, SunsetColorRangeDegrees) lives on SimData because
    // those same values feed the simulation-side ShadowLightDirection and
    // CurrentAmbient that gameplay perception reads. Keeping them on
    // WorldState.SimData means gameplay and visuals key off one source.
    [Export(PropertyHint.Range, "0,1,0.001")] public float previewTimeOfDay = 0.5f;

    [ExportSubgroup("Fades")]
    // Each phenomenon's horizon fade is a PAIR:
    //   - FadeAngle   : degrees ABOVE SimData.SunsetAngleDegrees at which
    //                   the fade reaches its 0 value (source fully gone).
    //   - FadeRange   : width (degrees) of the fade band. fadeStart = end + range.
    // Above fadeStart the phenomenon is at full intensity; between
    // fadeStart and fadeEnd it smoothsteps down to 0; below fadeEnd it's 0.
    // Both fades pivot on SunsetAngleDegrees, so moving sunset up or down
    // carries the whole horizon transition with it. Cloud shadows no
    // longer have a fade here — cloud_shadow_ground drops the sun-
    // direction projection so shadows drift at a constant wind-rate
    // regardless of time of day, no pop to hide.

    // Sun and moon DirectionalLight3D LightEnergy fade. fadeAngle=0 means
    // the light reaches 0 energy exactly at sunset; fadeRange is how many
    // degrees above that it spends ramping up to full. Lit shaders multiply
    // DIFFUSE_LIGHT by LIGHT_ENERGY, so this fade is how the sun and moon
    // shadows genuinely crossfade rather than snap at the horizon flip.
    [Export(PropertyHint.Range, "0,30,0.5")] public float lightEnergyFadeAngleDegrees = 0f;
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float lightEnergyFadeRangeDegrees = 5f;

    // Shaft (god-ray) fade. Needs a positive fadeAngle so shafts are fully
    // gone by the time the primary direction sign-flips and the dust
    // projection `(cloud_altitude - p.y) / max(to_sun.y, 0.15)` saturates
    // at its clamp — otherwise shafts scroll very fast across the screen
    // before snapping to the mirror direction.
    [Export(PropertyHint.Range, "0,30,0.5")] public float shaftFadeAngleDegrees = 0f;
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float shaftFadeRangeDegrees = 12f;

    [ExportSubgroup("Disks")]
    // Glow strength of the "primary" disk (sun at day, moon at night) in
    // the sky shader. Scaled by (1 - _nightT) so the moon renders as a
    // crisp disk without the warm halo the sun has. 0 = no halo, 1 = full
    // sun halo. Authored on SkyController (scene) since it's a visual-
    // sculpt parameter, not weather-driven.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunDiskGlowStrength = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float moonDiskGlowStrength = 0.15f;

    [ExportSubgroup("Fill Lights")]
    // Two off-axis fill directions, computed each frame from the primary
    // light's yaw + a configurable yaw offset + pitch below horizon. Neither
    // should be aligned with the primary — its directional contribution
    // comes from the BFS sun_mask + shadow atlas, not from a tint. Orthogonal
    // fills (yaw offsets ~90° apart) give the cleanest slope-reading.
    [Export] public float fillAPitchDegrees = 55f;
    [Export] public float fillAYawOffsetDegrees = 90f;
    [Export] public float fillBPitchDegrees = 65f;
    [Export] public float fillBYawOffsetDegrees = -90f;

    [ExportGroup("Water")]
    [ExportSubgroup("Ripples")]
    // Two procedural noise layers sampled in world XZ sum into the water
    // surface's height field; its finite-difference gradient perturbs the
    // shading normal. Two scales break up spatial tiling; both layers drift
    // along the wind vector (from [Wind] above) — layer B is rotated by a
    // small angle so the two layers don't lock into one apparent direction.
    [Export] public float rippleScaleA = 0.4f;
    [Export] public float rippleScaleB = 1.1f;
    // Scroll speed (world units/sec) along the wind direction for each layer.
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedA = 0.17f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedB = 0.16f;
    // Angle offset applied to wind direction for layer B (degrees). ~20-40°
    // reads as natural wind-driven chop; 0 locks B to A's direction and can
    // show visible tiling; 90° reads as unphysical cross-currents.
    [Export(PropertyHint.Range, "-180,180,1")] public float rippleAngleOffsetB = 30f;

    [ExportSubgroup("Reflections")]
    // Fresnel shape: at glancing view angles the surface reflects more.
    // Lower = more pervasive reflection; higher = mirror only at the
    // grazing edge. The iso camera mostly views water at ~30-45° so
    // lower powers suit this game.
    [Export(PropertyHint.Range, "1,8,0.1")] public float fresnelPower = 3.0f;
    // Maximum reflection contribution. Fresnel rolls this off toward the
    // camera direction; this caps the grazing-angle peak.
    [Export(PropertyHint.Range, "0,1,0.01")] public float reflectionStrength = 0.6f;
    // Specular sun glint — pinprick where the reflection ray aligns with
    // the direction to the sun. Sharpness controls pinprick size.
    [Export(PropertyHint.Range, "4,512,1")] public float glintSharpness = 64.0f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float glintStrength = 2.0f;

    [ExportSubgroup("Shoreline Foam")]
    // Lighter band where water meets solid terrain (thickness from the
    // depth buffer falls below foamDepth). Animated via scrolling noise
    // in world XZ so the foam line reads as surf, not a static halo.
    [Export] public Color foamColor = new Color(0.95f, 0.98f, 1.0f);
    // Water column depth below which foam starts fading in. Measured in
    // view-space world units — ~0.5-1.5 reads as a real shoreline width.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float foamDepth = 0.8f;
    // Noise tiling (higher = smaller foam patches).
    [Export(PropertyHint.Range, "0.1,8,0.05")] public float foamScale = 2.5f;
    [Export] public Vector2 foamScroll = new Vector2(0.12f, -0.07f);
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float foamStrength = 0.9f;
    // Noise threshold + sharpness: values ABOVE threshold become foam;
    // sharpness controls the transition (0 = soft gradient, 1 = hard step).
    // Matches the cloud_shadow.gdshaderinc remap shape.
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamThreshold = 0.45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamSharpness = 0.6f;

    [ExportSubgroup("Screenspace Reflection")]
    // Screen-space raymarch that captures terrain silhouettes (cliffs,
    // tree lines) and sprites standing in/behind water. Marches along
    // the reflection ray in world space; each step projects to NDC and
    // tests depth_tex. Off-screen misses fade via the edge mask.
    [Export(PropertyHint.Range, "1,60,0.1")] public float ssrMaxDistance = 30.0f;
    // Step count. Shader caps the compile-time loop at SSR_MAX_STEPS
    // (24); raising beyond that has no effect.
    [Export(PropertyHint.Range, "1,24,1")] public float ssrSteps = 12.0f;
    // View-space thickness tolerance for accepting a depth hit. Small =
    // misses thin geometry; large = ray can tunnel past the correct hit
    // and latch onto something behind it.
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float ssrThickness = 1.0f;

    [ExportGroup("Clouds")]
    // Cloud spatial tiling (authored). Separate from weather-driven colors /
    // density remap — cloud patterns don't change shape with weather.
    [Export] public float cloudScale = 0.15f;
    // World Y of the flat cloud plane used for projective sun-shadow casting
    // (see shaders/cloud_shadow.gdshaderinc). The sky dome renders clouds at
    // "infinity" via sky_common, so this altitude isn't directly visible —
    // it controls how far along the sun direction ground points project to
    // sample the cloud pattern. 40–80 usually reads well.
    [Export] public float cloudAltitude = 60f;

    [ExportGroup("Fog")]
    // Wire this to res://resources/materials/fog_volumetric.tres — the
    // shader's per-material uniforms are pushed here from the fields below.
    [Export] public ShaderMaterial fogMaterial;
    [Export] public float fogMaxDistance = 100.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int fogSteps = 48;

    [ExportGroup("Sunbeams")]
    [ExportSubgroup("Dust Band")]
    // How many meters above the reference Y the dust layer extends.
    // Above this height, dust fades to 0 → no beam contribution. 8-12m
    // is a natural range for "mist near the ground" in an outdoor scene.
    [Export(PropertyHint.Range, "1,64,0.1")] public float dustBandHeight = 10.0f;
    [Export(PropertyHint.Range, "0,2,0.001")] public float dustNoiseScale = 0.12f;
    [Export] public Vector2 dustNoiseScroll = new Vector2(0.05f, 0.03f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseStrength = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseThreshold = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseSharpness = 0.5f;

    [ExportSubgroup("Inscatter")]
    // Henyey-Greenstein phase. 0 = isotropic (shafts visible from any view
    // direction); positive = forward-peaked (dramatic only when camera faces
    // sun). The isometric camera's locked pitch rarely faces the sun, so 0
    // is the safe default.
    [Export(PropertyHint.Range, "-0.95,0.95,0.01")] public float scatterAnisotropy = 0.0f;
    // Sun-visibility threshold for shaft contribution. The voxel LightMap
    // leaks partial sun values into shallow cave air via lateral BFS
    // propagation — those values (~0.2-0.5) are correct for terrain
    // lighting but show up as unwanted underground shafts when scaled
    // by shaft intensity. Raising this forces shaft contribution to only
    // fully-sunlit voxels. 0 = no filtering (raw lightmap), 1 = only
    // perfectly lit voxels. 0.6-0.8 usually eliminates underground
    // shaft bleed without losing shafts at cave entrances.
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftSunThreshold = 0.7f;
    // Half-angle (degrees) at which shafts begin fading toward zero as
    // the view ray aligns with the sun axis. Beams viewed along their
    // length foreshorten into radial dots — physically correct but
    // distracting; fading hides the transition. 90 disables the fade.
    [Export(PropertyHint.Range, "0,90,0.1")] public float shaftCameraFadeDegrees = 60.0f;
    [Export(PropertyHint.Range, "0,32,0.01")] public float blockHaloIntensity = 6.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpness = 0.95f;
    // Softer floor for cloud_shaft_sharpness at low sun. A very sharp
    // shaft mask (authored at 0.95+) looks great at noon but makes the
    // cloud-noise texture tiling read as a hard, repeating grid once the
    // sun is low and the `cloud_altitude / to_sun.y` projection stretches
    // each noise feature across the screen. Lerping toward this softer
    // floor as primary elevation drops through the shaft-fade band blurs
    // the seam into a gradient and hides the pattern, without needing a
    // multi-sample blur. Stays interpolated even if shafts are fully
    // off (at zero intensity the sharpness value doesn't matter).
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpnessLowSunFloor = 0.35f;

    [ExportSubgroup("Sun Shadow Raymarch")]
    // Screen-space raymarch toward the sun at each fog march sample.
    // Samples depth_tex to check if tree/terrain/sprite geometry is
    // blocking the sun ray at that point. Produces the sharp canopy-
    // shaped beams the cloud-noise path cannot. Expensive — K depth
    // samples per fog step; disable for performance if needed.
    [Export] public bool sunShadowEnabled = true;
    [Export(PropertyHint.Range, "1,16,1")] public int sunShadowSteps = 6;
    [Export(PropertyHint.Range, "1,64,0.1")] public float sunShadowDistance = 16.0f;
    [Export(PropertyHint.Range, "0,0.01,0.0001")] public float sunShadowBias = 0.0005f;

    [ExportSubgroup("Shaping")]
    // Fraction of the view-ray march over which inscatter fades to 0 as
    // it approaches the terminating surface. Dust peaks at the ground
    // (dust_band_height gate is full when right above surface), so this
    // fade prevents shafts from painting color directly onto the ground
    // pixel — beams visually "float" above the terrain rather than
    // blanketing it. 0 = shafts paint onto ground; 0.5 = fade over the
    // last half of the march.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float shaftGroundFade = 0.2f;

    [ExportSubgroup("Mote Shimmer")]
    // Animated visual noise that makes beams shimmer. Carries no scattering
    // density of its own (that comes from dust density on WeatherData) — pure
    // cosmetic motion.
    [Export(PropertyHint.Range, "0,1,0.01")] public float moteStrength = 0.5f;
    [Export] public float moteScale = 0.18f;
    [Export] public Vector3 moteScroll = new Vector3(0.35f, 0.12f, -0.25f);

    // Accumulated cloud / ripple scroll offsets — integrated per frame from
    // `wind direction * speed`. These are the shader inputs (replacing the
    // old "speed * TIME" shader-side math) so mid-lerp speed changes don't
    // rescale the entire elapsed-time * speed product and visibly teleport
    // the texture. Exposed publicly so a future save/load layer can persist
    // and restore them — they're sim state, not authored data.
    public Vector2 cloudOffset;
    public Vector2 rippleOffsetA;
    public Vector2 rippleOffsetB;
    // Pre-integrated dust-noise + mote-shimmer offsets. Same rationale as
    // cloudOffset above: `TIME * scroll` in the shader loses low-bit
    // precision as TIME grows and also teleports whenever scroll speed
    // is retuned. Integrating here keeps the motion locally linear and
    // decouples past motion from current speed. `offsetB` is the rotated-
    // perpendicular companion of offsetA at 0.7x speed — matches the
    // original shader form but with the rotation applied at integration
    // time so the shader just reads position.
    public Vector2 dustNoiseOffsetA;
    public Vector2 dustNoiseOffsetB;
    public Vector3 moteOffset;
    // Grass-sway sin phase (integrates wind_frequency per frame). Passed to
    // detail_sprite.gdshader as wind_phase; same integration rationale as the
    // scroll offsets — a frequency change only affects future sway speed,
    // never the accumulated past.
    public float windPhase;
    // Gust-wave phase in radians (integrates gustFrequency * 2π per frame).
    // Drives the amplitude-multiplier wave in Apply() instead of recomputing
    // from TIME * gustFrequency.
    public float gustPhase;

    // --- Weather lerp state -----------------------------------------------
    // When non-null, _Process interpolates `weather` from _lerpFrom -> _lerpTo
    // over _lerpDuration seconds. See LerpToWeather(). `weather` is the single
    // mutable working copy — _lerpFrom is a snapshot of its values at the moment
    // the lerp started; _lerpTo is the target (usually a .tres preset). Both
    // are duplicates so neither the previous state nor the authored asset is
    // touched by the per-frame writes.
    private WeatherData _lerpFrom;
    private WeatherData _lerpTo;
    private float _lerpDuration;
    private float _lerpElapsed;

    // --- Time-of-day blend state -----------------------------------------
    // Recomputed in _Process from the current time-of-day; read by Apply()
    // to push effective (day/sunset/night-blended) color values to shaders.
    // _nightT: 0 = full day (sun high), 1 = full night (sun far below horizon).
    // _sunsetT: 1 at the horizon, fades to 0 past ±sunsetElevationDegrees.
    // Blend rule: base = lerp(day, night, _nightT); final = lerp(base, sunset, _sunsetT).
    private float _nightT;
    private float _sunsetT;
    // Primary light direction for the current frame (direction light travels).
    // Sun during the day, moon at night, flipped at the horizon crossing.
    private Vector3 _primaryLightDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's ACTUAL direction (always — not the primary which flips to the
    // moon at night). Used only by the sky shader so the sun disk sits at
    // the sun's position (below horizon, invisible, during the night) and
    // the moon disk sits at -this.
    private Vector3 _sunActualDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's signed elevation in degrees (positive = above horizon). Stored
    // so Apply() can derive the shaft fade factor without recomputing the
    // asin from the direction vector.
    private float _sunElevationDegrees = 45f;

    // Current blended ambient (day/sunset/night). Exposed so gameplay code
    // (WorldState.GetPerceivedLightWorld) reads the SAME ambient the shaders
    // see — stealth logic stays in sync with the visual darkness of night.
    public float CurrentAmbient { get; private set; } = 0.4f;

    // Current time-of-day-scaled primary intensity — CVars.sunIntensity
    // multiplied by the day/sunset/night intensity blend. Same value that
    // gets pushed to the `sun_intensity` shader global; exposing it here
    // lets gameplay perception (WorldState.GetPerceivedLightWorld) dim
    // with the visuals at dusk/night instead of seeing full-noon brightness
    // round the clock.
    public float CurrentPrimaryIntensity { get; private set; } = 2f;

    public override void _Ready()
    {
        Current = this;
        // Seed ALL static globals here ONCE. The old code pushed them every
        // frame from _Process, which broke any runtime override. _Process
        // now only pushes genuinely dynamic values (sun direction).
        if (!Engine.IsEditorHint())
        {
            ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("fill_a_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
            ShaderGlobals.Register("fill_b_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);
            // Sky-only globals for the sun/moon disks. sky_sun_dir is the sun's
            // ACTUAL direction (never flips to moon), so the sky shader can
            // place the sun disk independently of the lighting-side primary.
            ShaderGlobals.Register("sky_sun_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("moon_color", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(0.55f, 0.6f, 0.75f));
            ShaderGlobals.Register("sun_disk_glow", RenderingServer.GlobalShaderParameterType.Float, 1f);
            ShaderGlobals.Register("moon_disk_glow", RenderingServer.GlobalShaderParameterType.Float, 0f);

            // Duplicate the authored WeatherData into a private working copy so
            // runtime lerps don't mutate the .tres asset on disk. In editor mode
            // we skip this — the user expects inspector edits to save back to
            // the asset as usual. If the scene didn't assign one, load the
            // default preset via ResourceLoader (guarantees the C# type is
            // resolved, which .tscn ExtResource cannot guarantee on cold load).
            if (weather == null && ResourceLoader.Exists("res://resources/weather/default.tres"))
            {
                weather = ResourceLoader.Load<WeatherData>("res://resources/weather/default.tres");
            }
            if (weather != null)
            {
                weather = (WeatherData)weather.Duplicate();
            }
            else
            {
                weather = new WeatherData();
            }
        }

        // Wind globals (wind_dir / wind_amplitude / wind_frequency) live in
        // project.godot's [shader_globals], so they exist from editor startup
        // — Apply() can write to them every frame in [Tool] mode without
        // hitting the "global_shader_uniforms.variables.has(p_name)" assert.
        // Don't try to register them here: in [Tool] mode a script reload
        // resets ShaderGlobals._registered while leaving the underlying
        // RenderingServer state untouched, and re-Add fails noisily.
        //
        // Seed the sun/moon orbit before the first Apply() so the initial
        // frame already reflects the current time-of-day (e.g. a world that
        // starts at midnight doesn't flash a bright-day frame first).
        UpdateSunAndMoon();
        Apply();
    }

    // Smoothly transition the live `weather` fields from their current values
    // to `target` over `durationSeconds`. Calling again mid-lerp restarts from
    // the current (already-blended) state toward the new target, so rapid
    // weather changes chain naturally. durationSeconds <= 0 snaps instantly.
    public void LerpToWeather(WeatherData target, float durationSeconds)
    {
        if (target == null || weather == null) { return; }
        if (durationSeconds <= 0f)
        {
            weather.CopyFrom(target);
            _lerpFrom = null;
            _lerpTo = null;
            return;
        }
        _lerpFrom = (WeatherData)weather.Duplicate();
        _lerpTo = target;
        _lerpDuration = durationSeconds;
        _lerpElapsed = 0f;
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        // Advance any in-flight weather lerp. Each frame, t walks from 0->1
        // over _lerpDuration; at t==1 we snap to the target and clear state
        // so further frames don't keep Lerping (which would be a no-op but
        // still allocates a Duplicate on every LerpToWeather call).
        if (_lerpTo != null && weather != null && _lerpFrom != null)
        {
            _lerpElapsed += (float)delta;
            float t = Mathf.Clamp(_lerpElapsed / _lerpDuration, 0f, 1f);
            // Smoothstep — no overshoot, softer ends than linear. Good default
            // for "feel" on weather transitions.
            float eased = t * t * (3f - 2f * t);
            weather.LerpFields(_lerpFrom, _lerpTo, eased);
            if (t >= 1f)
            {
                weather.CopyFrom(_lerpTo);
                _lerpFrom = null;
                _lerpTo = null;
            }
        }

        // Integrate cloud / ripple scroll offsets from the CURRENT (already-
        // lerped) weather speed. Must happen after the lerp advancement above
        // so mid-transition frames see the blended speed, not yesterday's.
        // Parametric `speed * TIME` in the shader can't do this — changing
        // speed rescales all of time's accumulated position and snaps the
        // texture; integrating here decouples the two.
        if (weather != null)
        {
            float dt = (float)delta;
            Vector3 windDir = GetWindDirection();
            Vector2 windXZ = new Vector2(windDir.X, windDir.Z);
            if (windXZ.LengthSquared() > 0.0001f) { windXZ = windXZ.Normalized(); }
            else { windXZ = new Vector2(1f, 0f); }
            float angleB = Mathf.DegToRad(rippleAngleOffsetB);
            Vector2 windXZ_B = new Vector2(
                windXZ.X * Mathf.Cos(angleB) - windXZ.Y * Mathf.Sin(angleB),
                windXZ.X * Mathf.Sin(angleB) + windXZ.Y * Mathf.Cos(angleB));
            cloudOffset += windXZ * weather.cloudScrollSpeed * dt;
            rippleOffsetA += windXZ * rippleSpeedA * dt;
            rippleOffsetB += windXZ_B * rippleSpeedB * dt;
            windPhase += weather.windFrequency * dt;
            gustPhase += weather.gustFrequency * Mathf.Tau * dt;

            // Dust-noise scrolls along two paths: primary in the authored
            // direction, secondary along the 90°-rotated axis at 0.7x the
            // speed. Integrate both so the shader consumes positions,
            // never `TIME * speed`.
            dustNoiseOffsetA += dustNoiseScroll * dt;
            Vector2 dustScrollB = new Vector2(-dustNoiseScroll.Y, dustNoiseScroll.X) * 0.7f;
            dustNoiseOffsetB += dustScrollB * dt;

            // Mote shimmer drifts in 3D world space; same integration story.
            moteOffset += moteScroll * dt;
        }

        // Drive sun/moon orbit from the current time-of-day BEFORE Apply()
        // so Apply() sees the correct _nightT / _sunsetT blend weights when
        // pushing the effective colors to the GPU.
        UpdateSunAndMoon();

        // Re-apply authored values every frame so inspector tweaks take
        // effect live — whether from the scene tab at edit time (via [Tool])
        // or from the Remote scene tree at runtime while debugging. Apply()
        // is ~15 shader-parameter sets, cheap enough to run unconditionally.
        //
        // This used to be conditional because CVar callbacks wrote the same
        // globals, and a per-frame Apply() would clobber CVar overrides.
        // All those CVars are gone now (values live on this node's exports),
        // so the conflict is gone too.
        Apply();

        Vector3 fillADir = ComputeFillDirection(_primaryLightDir, fillAPitchDegrees, fillAYawOffsetDegrees);
        Vector3 fillBDir = ComputeFillDirection(_primaryLightDir, fillBPitchDegrees, fillBYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", _primaryLightDir);
        RenderingServer.GlobalShaderParameterSet("fill_a_world_dir", fillADir);
        RenderingServer.GlobalShaderParameterSet("fill_b_world_dir", fillBDir);
    }

    // Compute sun position on the celestial sphere from the current time,
    // pick sun-vs-moon as the primary directional light, orient the SunLight
    // node so Godot's shadow pass tracks it, and stash the blend weights
    // that Apply() will use for the day/sunset/night color blend.
    private void UpdateSunAndMoon()
    {
        // Current normalized time. Runtime reads from WorldState;
        // editor preview reads from the inspector slider.
        double t;
        if (!Engine.IsEditorHint() && World.Current?.WorldState != null)
        {
            t = World.Current.WorldState.TimeOfDay01;
        }
        else
        {
            t = previewTimeOfDay;
        }

        // Orbit + horizon tuning comes from SimData so one set of values
        // drives both the visuals and the gameplay-visible
        // ShadowLightDirection / CurrentAmbient. Editor / pre-WorldState
        // frames fall back to the defaults we'd want at mid-latitude.
        SimData sim = World.Current?.WorldState?.SimData;
        float sunMaxElev = sim?.SunMaxElevationDegrees ?? 60f;
        float sunSideSway = sim?.SunSideSwayDegrees ?? 30f;

        // Phase offset so t=0.25 is sunrise (elevation=0, rising),
        // t=0.5 is noon (peak), t=0.75 is sunset (elevation=0, falling),
        // t=0 / t=1 is midnight (lowest point).
        float phase = Mathf.Tau * ((float)t - 0.25f);

        // Elevation follows sin(phase) scaled to the authored max.
        // At noon (phase = π/2), sin = 1 → elevation = sunMaxElev.
        // At midnight (phase = -π/2), sin = -1 → elevation = -sunMaxElev.
        float elevRad = Mathf.Sin(phase) * Mathf.DegToRad(sunMaxElev);

        // Yaw sweeps from -sideSway at sunrise through 0 at noon to
        // +sideSway at sunset. -cos(phase) gives -1 at sunrise (phase=0),
        // 0 at noon (phase=π/2), +1 at sunset (phase=π).
        float yawRad = -Mathf.Cos(phase) * Mathf.DegToRad(sunSideSway);

        // Sun position as a unit vector on the celestial sphere, from
        // observer to sun. +Y is up; +Z is the noon-side horizontal axis.
        float cosElev = Mathf.Cos(elevRad);
        Vector3 sunPos = new Vector3(
            Mathf.Sin(yawRad) * cosElev,
            Mathf.Sin(elevRad),
            Mathf.Cos(yawRad) * cosElev).Normalized();

        // Moon is the anti-sun: opposite point on the celestial sphere.
        // When sunPos.Y > 0 (sun up), moonPos.Y < 0 (moon down) and vice
        // versa — the two light sources swap roles at the horizon.
        Vector3 moonPos = -sunPos;

        // Actual sun direction — the direction sunlight travels, always,
        // regardless of whether the sun is currently the primary light.
        // Only the sky shader's sun disk uses this; lighting and shadows
        // key off _primaryLightDir below.
        _sunActualDir = (-sunPos).Normalized();

        // Primary directional light direction — exposed via the sun_world_dir
        // shader global and WorldState.ShadowLightDirection. Still flips at
        // the horizon because a single vector can only represent one source;
        // the fog shader's shaft projection and cloud_shadow_ground both read
        // this. The Godot-side shadow atlases, by contrast, now come from
        // two DEDICATED DirectionalLight3Ds (sunLight + moonLight) which we
        // orient independently, so their shadows genuinely crossfade at
        // horizon via LightEnergy instead of relying on the primary flip.
        Vector3 primaryPos = sunPos.Y >= 0f ? sunPos : moonPos;
        _primaryLightDir = (-primaryPos).Normalized();

        // Point each DirectionalLight3D at its own source. sunLight always
        // faces the sun's actual direction; moonLight always faces the
        // moon's (= -sunActualDir). Whichever source is below the horizon
        // will have its LightEnergy driven to 0 in Apply(), so even though
        // its transform points "up into the ground", it contributes nothing
        // to DIFFUSE_LIGHT and its shadow atlas is effectively inert.
        Vector3 moonActualDir = -_sunActualDir;
        OrientLight(sunLight, _sunActualDir);
        OrientLight(moonLight, moonActualDir);

        // Mirror into WorldState so IsPointInDirectionalSun raycasts and
        // AI perception use the same direction the shaders see.
        if (!Engine.IsEditorHint() && World.Current?.WorldState != null)
        {
            World.Current.WorldState.ShadowLightDirection = _primaryLightDir;
        }

        // Blend weights from sun elevation (in degrees, for intuitive units).
        float sunElevDeg = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sunPos.Y, -1f, 1f)));
        _sunElevationDegrees = sunElevDeg;

        // Pivot the day/sunset/night color blend around SimData.SunsetAngleDegrees
        // rather than the geometric horizon. sunsetT peaks at 1 whenever
        // the sun or moon is near the effective horizon (|elev| ≈ sunsetAngle),
        // fading over SimData.SunsetColorRangeDegrees. Double-peaked as a
        // natural consequence: one peak at sunset (setting), another at
        // moonrise (moon crossing up through sunsetAngle on the other side).
        float sunsetAngle = sim?.SunsetAngleDegrees ?? 10f;
        float colorRange = Mathf.Max(sim?.SunsetColorRangeDegrees ?? 10f, 0.01f);

        // nightT: 0 at full day (sun well above sunsetAngle), 1 at full
        // night (moon well above sunsetAngle, i.e. sun well below
        // -sunsetAngle). Symmetric transition band of (sunsetAngle + range)
        // on each side so day and night anchor at the same elevation.
        float dayNightThreshold = sunsetAngle + colorRange;
        _nightT = 1f - Mathf.SmoothStep(-dayNightThreshold, dayNightThreshold, sunElevDeg);

        // sunsetT: peak (=1) when |sunElev| == sunsetAngle, fading to 0
        // over colorRange as the source moves away from the effective
        // horizon in either direction. This gives warm sunset colors at
        // sunset AND moonrise, not just at the geometric horizon crossing.
        float distFromSunsetPeak = Mathf.Abs(Mathf.Abs(sunElevDeg) - sunsetAngle);
        _sunsetT = 1f - Mathf.SmoothStep(0f, colorRange, distFromSunsetPeak);
    }

    // Push every authored atmospheric value to the GPU. Called every frame so
    // live lerping + inspector edits (via [Tool]) take effect immediately.
    public void Apply()
    {
        // In editor mode with no weather resource assigned, nothing to push.
        // (Runtime _Ready creates a default; only the editor allows a null
        // weather reference.)
        if (weather == null) { return; }

        // Time-of-day color blend: base = lerp(day, night, _nightT);
        // final = lerp(base, sunset, _sunsetT). Two-step so sunset can
        // sit on top of the day↔night fade instead of competing with it.
        Color effSunColor = weather.sunColor.Lerp(weather.moonColor, _nightT).Lerp(weather.sunsetColor, _sunsetT);
        float effAmbient = Mathf.Lerp(Mathf.Lerp(weather.sunAmbient, weather.moonAmbient, _nightT), weather.sunsetAmbient, _sunsetT);
        Color effFillA = weather.fillAColor.Lerp(weather.nightFillAColor, _nightT).Lerp(weather.sunsetFillAColor, _sunsetT);
        Color effFillB = weather.fillBColor.Lerp(weather.nightFillBColor, _nightT).Lerp(weather.sunsetFillBColor, _sunsetT);
        Color effCloud = weather.cloudColor.Lerp(weather.nightCloudColor, _nightT).Lerp(weather.sunsetCloudColor, _sunsetT);
        Color effFog = weather.fogColor.Lerp(weather.nightFogColor, _nightT).Lerp(weather.sunsetFogColor, _sunsetT);
        Color effHorizon = weather.horizonColor.Lerp(weather.nightHorizonColor, _nightT).Lerp(weather.sunsetHorizonColor, _sunsetT);
        Color effZenith = weather.zenithColor.Lerp(weather.nightZenithColor, _nightT).Lerp(weather.sunsetZenithColor, _sunsetT);
        CurrentAmbient = effAmbient;

        // Scale the primary directional light's intensity with time of day:
        // night dims to weather.moonLightIntensity, sunset to
        // weather.sunsetLightIntensity, noon is full (CVar value unchanged).
        // Overwrites the CVar's direct write from the previous frame — the
        // CVar acts as the "noon base" and SkyController applies the time-
        // of-day modulation on top. Moving the scales onto WeatherData
        // keeps them in the same resource as the paired *Ambient values,
        // so each preset can tune the direct↔ambient split coherently.
        float intensityScale = Mathf.Lerp(Mathf.Lerp(weather.dayLightIntensity, weather.moonLightIntensity, _nightT), weather.sunsetLightIntensity, _sunsetT);
        float effSunIntensity = CVars.sunIntensity.Value * intensityScale;
        CurrentPrimaryIntensity = effSunIntensity;

        // SimData owns the orbit/horizon pivot — every horizon-relative
        // fade below references sunsetAngle from there so changing it
        // slides the whole horizon transition in one place.
        SimData sim = World.Current?.WorldState?.SimData;
        float sunsetAngle = sim?.SunsetAngleDegrees ?? 10f;

        // Drive sunLight / moonLight LightEnergy from each source's own
        // above-horizon factor so the two shadow atlases genuinely crossfade
        // at dawn/dusk. lit shaders multiply DIFFUSE_LIGHT by LIGHT_ENERGY,
        // so the sum of contributions stays sensibly bounded: sun dominant
        // during day (moonEnergy ~0), moon dominant at night (sunEnergy ~0),
        // both partial during the crossover window — shadows from both
        // directions visible simultaneously, each at proportional strength.
        // Moon energy is additionally scaled by weather.moonLightIntensity
        // since moonlight is physically dimmer than daylight.
        //
        // Fade is expressed relative to sunsetAngleDegrees: energy reaches
        // 0 at (sunsetAngle + fadeAngle) elevation and full at
        // (sunsetAngle + fadeAngle + fadeRange).
        float lightFadeEnd = sunsetAngle + lightEnergyFadeAngleDegrees;
        float lightFadeStart = lightFadeEnd + Mathf.Max(lightEnergyFadeRangeDegrees, 0.01f);
        float sunEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, _sunElevationDegrees);
        float moonEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, -_sunElevationDegrees);
        if (sunLight != null) { sunLight.LightEnergy = sunEnergyFactor; }
        if (moonLight != null) { moonLight.LightEnergy = moonEnergyFactor * weather.moonLightIntensity; }

        // Disk glow: the "sun disk" in the sky shader is drawn at the
        // actual sun position; at night the sun is below the horizon so
        // the disk itself is invisible, but fading glow prevents any
        // residual halo bleed. The "moon disk" has its own glow strength.
        float effSunDiskGlow = sunDiskGlowStrength * (1f - _nightT);
        float effMoonDiskGlow = moonDiskGlowStrength * _nightT;

        // --- Global uniforms ---------------------------------------------
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(effSunColor));
        RenderingServer.GlobalShaderParameterSet("sun_ambient", effAmbient);
        RenderingServer.GlobalShaderParameterSet("sun_intensity", effSunIntensity);
        RenderingServer.GlobalShaderParameterSet("fill_a_color", ColorToVec3(effFillA));
        RenderingServer.GlobalShaderParameterSet("fill_b_color", ColorToVec3(effFillB));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(effHorizon));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(effZenith));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(effCloud));
        // Sky-only: sun's ACTUAL direction + moon's color (weather.moonColor
        // unblended — the disk is literally the moon, not the blended
        // primary). The sky shader draws both disks keyed off sky_sun_dir.
        RenderingServer.GlobalShaderParameterSet("sky_sun_dir", _sunActualDir);
        RenderingServer.GlobalShaderParameterSet("moon_color", ColorToVec3(weather.moonColor));
        RenderingServer.GlobalShaderParameterSet("sun_disk_glow", effSunDiskGlow);
        RenderingServer.GlobalShaderParameterSet("moon_disk_glow", effMoonDiskGlow);
        // Pre-integrated offsets (see _Process). Passing position directly
        // instead of speed avoids the lerp discontinuity the shader's old
        // `scroll * TIME` form exhibited when weather changed cloudScrollSpeed.
        RenderingServer.GlobalShaderParameterSet("cloud_offset", cloudOffset);
        RenderingServer.GlobalShaderParameterSet("cloud_threshold", weather.cloudThreshold);
        RenderingServer.GlobalShaderParameterSet("cloud_sharpness", weather.cloudSharpness);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", weather.cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_altitude", cloudAltitude);
        // Cloud shadow strength is pushed unscaled — cloud_shadow_ground
        // no longer projects along the sun direction, so there's no
        // fast-scroll / direction-flip pop at horizon that would need
        // fading. Shadows stay visible through dusk and night, drifting
        // only with wind via cloud_offset.
        RenderingServer.GlobalShaderParameterSet("cloud_shadow_strength", weather.cloudShadowStrength);

        // --- Water -------------------------------------------------------
        RenderingServer.GlobalShaderParameterSet("ripple_scale_a", rippleScaleA);
        RenderingServer.GlobalShaderParameterSet("ripple_scale_b", rippleScaleB);
        // Pre-integrated offsets. Layer B's direction rotation is applied at
        // integration time in _Process, not here.
        RenderingServer.GlobalShaderParameterSet("ripple_offset_a", rippleOffsetA);
        RenderingServer.GlobalShaderParameterSet("ripple_offset_b", rippleOffsetB);
        RenderingServer.GlobalShaderParameterSet("ripple_strength", weather.rippleStrength);
        RenderingServer.GlobalShaderParameterSet("fresnel_power", fresnelPower);
        RenderingServer.GlobalShaderParameterSet("reflection_strength", reflectionStrength);
        RenderingServer.GlobalShaderParameterSet("glint_sharpness", glintSharpness);
        RenderingServer.GlobalShaderParameterSet("glint_strength", glintStrength);
        RenderingServer.GlobalShaderParameterSet("ssr_max_distance", ssrMaxDistance);
        RenderingServer.GlobalShaderParameterSet("ssr_steps", ssrSteps);
        RenderingServer.GlobalShaderParameterSet("ssr_thickness", ssrThickness);
        RenderingServer.GlobalShaderParameterSet("foam_color", ColorToVec3(foamColor));
        RenderingServer.GlobalShaderParameterSet("foam_depth", foamDepth);
        RenderingServer.GlobalShaderParameterSet("foam_scale", foamScale);
        RenderingServer.GlobalShaderParameterSet("foam_scroll", foamScroll);
        RenderingServer.GlobalShaderParameterSet("foam_strength", foamStrength);
        RenderingServer.GlobalShaderParameterSet("foam_threshold", foamThreshold);
        RenderingServer.GlobalShaderParameterSet("foam_sharpness", foamSharpness);

        // --- Wind --------------------------------------------------------
        // Two-octave low-frequency sin sum for naturally uneven gusts —
        // single-sin gives a metronome rhythm that reads as fake. Output
        // is normalized to [0, 1] then scaled by gustStrength so the final
        // amplitude multiplier stays in [1, 1 + gustStrength]. Driven by
        // gustPhase (integrated in _Process) rather than TIME*frequency so
        // lerping gustFrequency doesn't jump the wave.
        float gustWave = Mathf.Sin(gustPhase) * 0.7f
                       + Mathf.Sin(gustPhase * 1.7f + 1.3f) * 0.3f;
        float gust01 = (gustWave + 1f) * 0.5f;
        float amplitude = weather.windAmplitude * (1f + gust01 * weather.gustStrength);
        RenderingServer.GlobalShaderParameterSet("wind_dir", GetWindDirection().Normalized());
        RenderingServer.GlobalShaderParameterSet("wind_amplitude", amplitude);
        RenderingServer.GlobalShaderParameterSet("wind_phase", windPhase);

        // --- Shaft blend (sun → moon) ------------------------------------
        // Fade each source's shaft contribution as ITS body approaches the
        // horizon. Same relative-to-sunset parameterization as the light
        // energy fade, with independent fadeAngle/fadeRange. Deep day:
        // only sun shafts. Deep night: only moon shafts. Around the
        // horizon (either side) both fade through 0 in sync so the
        // primary-direction flip in the fog shader has no visible effect.
        float shaftFadeEnd = sunsetAngle + shaftFadeAngleDegrees;
        float shaftFadeStart = shaftFadeEnd + Mathf.Max(shaftFadeRangeDegrees, 0.1f);
        float sunShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, _sunElevationDegrees);
        float moonShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, -_sunElevationDegrees);

        float effShaftIntensity = weather.sunShaftIntensity * sunShaftFactor
                                 + weather.moonShaftIntensity * moonShaftFactor;

        // Three-way shaft color blend: sun → moon along the sun/moon above-
        // horizon crossfade, sunset layered on top via _sunsetT so golden-
        // hour beams can lean harder amber than either day or night shafts.
        // Matches the day/sunset/night pattern used for the scene primary
        // color and fills — keeps all color blends in lockstep.
        float shaftColorT = moonShaftFactor / (sunShaftFactor + moonShaftFactor + 1e-6f);
        Color effShaftColor = weather.sunShaftColor.Lerp(weather.moonShaftColor, shaftColorT).Lerp(weather.sunsetShaftColor, _sunsetT);

        // Dynamic fog step count. When the primary light is low in the sky,
        // each raymarch step crosses more sun/shadow boundaries per unit of
        // march distance — both because shafts arrive at a grazing angle
        // (view rays cut across them faster) and because cloud/dust
        // projections scale with 1/to_sun.y (each step samples cloud noise
        // further from the last). 48 steps is plenty at noon; ~96 catches
        // the low-sun cases where banding shows up.
        //
        // Formula: scale = 1 / max(|primaryDir.y|, 0.3). |primaryDir.y| =
        // |sin(primary elevation)|, so at zenith (scale=1) no boost; at
        // elev=17.5° (y=0.3) the scale saturates at 3.33x, clamped to 2x
        // so we never go past ~2x the baseline cost. Below fadeEnd (8°)
        // the shafts are already fading to 0, so extra steps there are
        // wasted — the primary `fogSteps` value stays in effect and the
        // boost only matters while shafts are contributing.
        float primaryY = Mathf.Abs(_primaryLightDir.Y);
        float stepScale = Mathf.Min(1f / Mathf.Max(primaryY, 0.3f), 2f);
        int effFogSteps = Mathf.Clamp(Mathf.RoundToInt(fogSteps * stepScale), fogSteps, 128);

        // --- Fog material uniforms ---------------------------------------
        if (fogMaterial != null)
        {
            fogMaterial.SetShaderParameter("fog_color", ColorToVec3(effFog));
            fogMaterial.SetShaderParameter("fog_density", weather.fogDensity);
            fogMaterial.SetShaderParameter("ambient_fog_density", weather.ambientFogDensity);
            fogMaterial.SetShaderParameter("fog_max_distance", fogMaxDistance);
            fogMaterial.SetShaderParameter("fog_steps", effFogSteps);
            fogMaterial.SetShaderParameter("dust_density", weather.dustDensity);
            fogMaterial.SetShaderParameter("dust_band_height", dustBandHeight);
            // Dust ceiling tracks the player's altitude so the band stays
            // local as the player climbs hills or descends into valleys.
            // ceiling = player.y + dustBandHeight puts the player right at
            // the bottom of the fade, so dust_gate == 1 at the player's
            // feet and fades to 0 one band_height above. Falls back to
            // disabled (-1e20 = per-pixel legacy) when no world/player is
            // present (editor preview before the game runs).
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
            fogMaterial.SetShaderParameter("mote_offset", moteOffset);
            fogMaterial.SetShaderParameter("sun_shaft_intensity", effShaftIntensity);
            fogMaterial.SetShaderParameter("shaft_color", ColorToVec3(effShaftColor));
            fogMaterial.SetShaderParameter("block_halo_intensity", blockHaloIntensity);
            fogMaterial.SetShaderParameter("scatter_anisotropy", scatterAnisotropy);
            fogMaterial.SetShaderParameter("shaft_sun_threshold", shaftSunThreshold);
            // Cloud shaft sharpness tapers toward `cloudShaftSharpnessLowSunFloor`
            // as whichever source is above the horizon approaches its fade band.
            // sunShaftFactor/moonShaftFactor already encode "how present this
            // source's shafts are"; reuse them so the transition aligns with
            // the shaft intensity fade. Max of the two since whichever source
            // is active should dominate the sharpness.
            float shaftSharpnessBlend = Mathf.Max(sunShaftFactor, moonShaftFactor);
            float effCloudShaftSharpness = Mathf.Lerp(cloudShaftSharpnessLowSunFloor, cloudShaftSharpness, shaftSharpnessBlend);
            fogMaterial.SetShaderParameter("cloud_shaft_sharpness", effCloudShaftSharpness);
            fogMaterial.SetShaderParameter("shaft_camera_fade_degrees", shaftCameraFadeDegrees);
            fogMaterial.SetShaderParameter("sun_shadow_enabled", sunShadowEnabled);
            fogMaterial.SetShaderParameter("sun_shadow_steps", sunShadowSteps);
            fogMaterial.SetShaderParameter("sun_shadow_distance", sunShadowDistance);
            fogMaterial.SetShaderParameter("sun_shadow_bias", sunShadowBias);
            fogMaterial.SetShaderParameter("shaft_ground_fade", shaftGroundFade);
            fogMaterial.SetShaderParameter("mote_strength", moteStrength);
            fogMaterial.SetShaderParameter("mote_scale", moteScale);
            fogMaterial.SetShaderParameter("mote_scroll", moteScroll);
        }

        // --- Weather particles -------------------------------------------
        // Fetched via static singleton rather than [Export] because the C#
        // cross-scene-instance cast is broken in Godot 4 — see RainEffect.Current.
        // The node consumes an already-lerped value; it doesn't know about
        // weather presets or blending. Same pattern will apply to future
        // particle variants (hail, snow, dust-storm motes).
        if (RainEffect.Current != null) { RainEffect.Current.SetIntensity(weather.rainIntensity); }
    }

    private static Vector3 ComputeFillDirection(Vector3 sunDir, float pitchDeg, float yawOffsetDeg)
    {
        // Sun yaw from its horizontal travel direction.
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

    // World's prevailing wind direction. Lives on WorldState as mutable
    // sim state (weather systems may rotate it during play). Weather
    // presets tune magnitude via windAmplitude / gustStrength, not the
    // compass bearing. Falls back to a reasonable NE default in editor
    // preview where no WorldState exists yet.
    private static readonly Vector3 DefaultWindDirection = new Vector3(0.7f, 0f, 0.7f);
    private static Vector3 GetWindDirection()
    {
        WorldState ws = World.Current?.WorldState;
        return ws != null ? ws.WindDirection : DefaultWindDirection;
    }

    // Orient a DirectionalLight3D so its -Z axis (Godot's light emission
    // direction) matches `lightDir` (the direction the light TRAVELS).
    // Guards against the degenerate case where lightDir is parallel to Y
    // by swapping the reference up vector.
    private static void OrientLight(DirectionalLight3D light, Vector3 lightDir)
    {
        if (light == null) { return; }
        Vector3 pos = light.GlobalPosition;
        Vector3 up = Mathf.Abs(lightDir.Y) > 0.99f ? Vector3.Forward : Vector3.Up;
        light.LookAtFromPosition(pos, pos + lightDir, up);
    }
}
