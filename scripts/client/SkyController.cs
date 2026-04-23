using Godot;

// Owns the runtime visual pipeline for the sky dome, sun / moon, fog,
// shafts, water ripples, cloud shadows, and precipitation. Every frame
// it:
//   1. Samples a blended RegionData + WeatherData at the player's XZ
//      via RegionBlend (4-quadrant scaffolding on SimData for now).
//   2. Recomputes sun / moon orbit from WorldState.TimeOfDay01.
//   3. Derives a full DerivedPalette from (region, weather, sunElev,
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
// fade bands, etc.) — weather / region-driven visuals come from the
// palette.
//
// [Tool] makes this run in the editor. When no World/Player exists, it
// falls back to `previewRegion` so inspector edits produce live sky
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
    // Editor / pre-World fallback region. Used for live sky preview
    // when no SimData / player exists (pure inspector tweaking). At
    // runtime the four SimData regions take over via RegionBlend.
    [Export] public RegionData previewRegion;

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
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float lightEnergyFadeRangeDegrees = 5f;

    // Shaft (god-ray) fade. Needs a positive fadeAngle so shafts are fully
    // gone by the time the primary direction sign-flips.
    [Export(PropertyHint.Range, "0,30,0.5")] public float shaftFadeAngleDegrees = 0f;
    [Export(PropertyHint.Range, "0.1,30,0.5")] public float shaftFadeRangeDegrees = 12f;

    [ExportSubgroup("Disks")]
    // Glow strength of the "primary" disk (sun at day, moon at night) in
    // the sky shader. Authored on SkyController since it's a visual-sculpt
    // parameter, not weather-driven.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunDiskGlowStrength = 1f;
    [Export(PropertyHint.Range, "0,2,0.01")] public float moonDiskGlowStrength = 0.15f;

    [ExportSubgroup("Fill Lights")]
    // Two off-axis fill directions, computed each frame from the primary
    // light's yaw + a configurable yaw offset + pitch below horizon.
    // Orthogonal fills (yaw offsets ~90° apart) give the cleanest slope
    // reading.
    [Export] public float fillAPitchDegrees = 55f;
    [Export] public float fillAYawOffsetDegrees = 90f;
    [Export] public float fillBPitchDegrees = 65f;
    [Export] public float fillBYawOffsetDegrees = -90f;

    [ExportGroup("Water")]
    [ExportSubgroup("Ripples")]
    // Two procedural noise layers sampled in world XZ sum into the water
    // surface's height field; its finite-difference gradient perturbs the
    // shading normal. Two scales break up spatial tiling; both layers drift
    // along the wind vector (from weather) — layer B is rotated by a small
    // angle so the two layers don't lock into one apparent direction.
    [Export] public float rippleScaleA = 0.4f;
    [Export] public float rippleScaleB = 1.1f;
    // Scroll speed per m/s of wind, for each layer. Layer A speed =
    // blendedWeather.windSpeed * rippleSpeedA in world units/sec.
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedA = 0.04f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float rippleSpeedB = 0.04f;
    [Export(PropertyHint.Range, "-180,180,1")] public float rippleAngleOffsetB = 30f;

    [ExportSubgroup("Reflections")]
    [Export(PropertyHint.Range, "1,8,0.1")] public float fresnelPower = 3.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float reflectionStrength = 0.6f;
    [Export(PropertyHint.Range, "4,512,1")] public float glintSharpness = 64.0f;
    [Export(PropertyHint.Range, "0,8,0.1")] public float glintStrength = 2.0f;

    [ExportSubgroup("Shoreline Foam")]
    [Export] public Color foamColor = new Color(0.95f, 0.98f, 1.0f);
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float foamDepth = 0.8f;
    [Export(PropertyHint.Range, "0.1,8,0.05")] public float foamScale = 2.5f;
    [Export] public Vector2 foamScroll = new Vector2(0.12f, -0.07f);
    [Export(PropertyHint.Range, "0,1.5,0.01")] public float foamStrength = 0.9f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamThreshold = 0.45f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float foamSharpness = 0.6f;

    [ExportSubgroup("Screenspace Reflection")]
    [Export(PropertyHint.Range, "1,60,0.1")] public float ssrMaxDistance = 30.0f;
    [Export(PropertyHint.Range, "1,24,1")] public float ssrSteps = 12.0f;
    [Export(PropertyHint.Range, "0.1,4,0.05")] public float ssrThickness = 1.0f;

    [ExportGroup("Wind")]
    // Sprite / grass sway amplitude in world meters per m/s of wind speed.
    // The shader's wind_amplitude global is computed each frame as
    //     GustedWindSpeed * windToSwayMeters
    // so changing wind weather scales sway naturally without touching the
    // shader or per-weather amplitude knobs.
    [Export(PropertyHint.Range, "0,0.05,0.0001")] public float windToSwayMeters = 0.013f;

    [ExportGroup("Clouds")]
    // Cloud spatial tiling (authored). Separate from the weather-driven
    // cloudThreshold / cloudSharpness (which come from the palette) —
    // pattern SCALE is a scene-wide visual choice, not weather.
    [Export] public float cloudScale = 0.15f;
    // World Y of the flat cloud plane used for projective sun-shadow casting.
    [Export] public float cloudAltitude = 60f;
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

    [ExportGroup("Fog")]
    // Wire this to res://resources/materials/fog_volumetric.tres — the
    // shader's per-material uniforms are pushed here from the palette.
    [Export] public ShaderMaterial fogMaterial;
    [Export] public float fogMaxDistance = 100.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int fogSteps = 48;

    [ExportGroup("Sunbeams")]
    [ExportSubgroup("Dust Band")]
    [Export(PropertyHint.Range, "1,64,0.1")] public float dustBandHeight = 10.0f;
    [Export(PropertyHint.Range, "0,2,0.001")] public float dustNoiseScale = 0.12f;
    [Export] public Vector2 dustNoiseScroll = new Vector2(0.05f, 0.03f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseStrength = 0.7f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseThreshold = 0.4f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustNoiseSharpness = 0.5f;

    [ExportSubgroup("Inscatter")]
    [Export(PropertyHint.Range, "-0.95,0.95,0.01")] public float scatterAnisotropy = 0.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float shaftSunThreshold = 0.7f;
    [Export(PropertyHint.Range, "0,90,0.1")] public float shaftCameraFadeDegrees = 60.0f;
    [Export(PropertyHint.Range, "0,32,0.01")] public float blockHaloIntensity = 6.0f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpness = 0.95f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShaftSharpnessLowSunFloor = 0.35f;

    [ExportSubgroup("Sun Shadow Raymarch")]
    [Export] public bool sunShadowEnabled = true;
    [Export(PropertyHint.Range, "1,16,1")] public int sunShadowSteps = 6;
    [Export(PropertyHint.Range, "1,64,0.1")] public float sunShadowDistance = 16.0f;
    [Export(PropertyHint.Range, "0,0.01,0.0001")] public float sunShadowBias = 0.0005f;

    [ExportSubgroup("Shaping")]
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float shaftGroundFade = 0.2f;

    [ExportSubgroup("Mote Shimmer")]
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
    public Vector2 dustNoiseOffsetA;
    public Vector2 dustNoiseOffsetB;
    public Vector3 moteOffset;
    // Grass-sway sin phase (integrates palette.WindFrequency per frame).
    public float windPhase;
    // Gust-wave phase in radians (integrates palette.GustFrequency * 2π
    // per frame). Drives the amplitude-multiplier wave in Apply().
    public float gustPhase;

    // --- Time-of-day / sun state -----------------------------------------
    // Primary light direction for the current frame (direction light travels).
    // Sun during the day, moon at night, flipped at the horizon crossing.
    private Vector3 _primaryLightDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's ACTUAL direction (always — not the primary which flips to the
    // moon at night). Used only by the sky shader's sun disk.
    private Vector3 _sunActualDir = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Sun's signed elevation in degrees (positive = above horizon). Stored
    // so Apply() / derivation can use it without recomputing the asin.
    private float _sunElevationDegrees = 45f;

    // Current blended region + weather (runtime). In editor mode these
    // stay null; the preview path reads previewRegion / previewRegion.weather
    // directly. RegionBlend.Sample rewrites these in place each frame.
    private RegionData _blendedRegion;
    private WeatherData _blendedWeather;

    // Most recently derived palette. Updated in _Process before Apply.
    private DerivedPalette _palette;

    // --- Public accessors ------------------------------------------------
    // The current (blended) weather. RainEffect reads windDirection /
    // windSpeed from here; gameplay might read rainAmount for gameplay
    // gating in the future.
    public WeatherData Weather
    {
        get
        {
            if (!Engine.IsEditorHint()) { return _blendedWeather; }
            return previewRegion?.weather;
        }
    }

    // The current derived palette. RainEffect reads RainIntensity /
    // RainWeight via ApplyPrecipitation; other consumers can read it
    // directly for ambient / shafts / etc. Returned by value (the
    // struct is small and callers read a single field at a time).
    public DerivedPalette Palette => _palette;

    // windSpeed + gusted wave added on top. Exposed so RainEffect's tilt
    // math and SkyController's own sway amplitude agree on "how gusty is
    // right now" without both recomputing the wave. Updated in Apply().
    public float GustedWindSpeed { get; private set; }

    // Current blended ambient (day/sunset/night). Exposed so gameplay code
    // (WorldState.GetPerceivedLightWorld) reads the SAME ambient the shaders
    // see — stealth logic stays in sync with the visual darkness of night.
    public float CurrentAmbient { get; private set; } = 0.4f;

    // Current time-of-day-scaled primary intensity — CVars.sunIntensity
    // multiplied by the day/sunset/night intensity blend. Same value
    // pushed to the `sun_intensity` shader global; exposing it here lets
    // gameplay perception dim with the visuals at dusk/night.
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

            // Working copies for the region blend output. Re-populated in
            // _Process each frame — these exist so RegionBlend can write
            // into stable instances without allocating per frame.
            _blendedRegion = new RegionData();
            _blendedWeather = new WeatherData();
        }

        UpdateSunAndMoon();
        // Seed the palette with null-safe fallbacks so the very first
        // Apply() (before _Process has ever run) doesn't push a zeroed
        // DerivedPalette to the shaders — which would briefly blacken
        // the sky during scene load.
        _palette = WeatherDerivation.Derive(null, null, _sunElevationDegrees, null);
        Apply();
    }

    public override void _ExitTree()
    {
        if (Current == this) { Current = null; }
    }

    public override void _Process(double delta)
    {
        // Blend regions → (_blendedRegion, _blendedWeather). In editor or
        // before the World is up, fall back to previewRegion.
        RegionData currentRegion = _blendedRegion;
        WeatherData currentWeather = _blendedWeather;
        SimData sim = World.Current?.WorldState?.SimData;

        if (!Engine.IsEditorHint() && sim != null && _blendedRegion != null && _blendedWeather != null)
        {
            Vector3 playerPos = World.Current.player?.GlobalPosition ?? Vector3.Zero;
            RegionBlend.Sample(playerPos, sim, _blendedRegion, _blendedWeather);

            // Publish the blended wind direction to WorldState so gameplay
            // consumers (RainEffect, physics) see a single authoritative
            // current wind. Other weather variables currently have no
            // gameplay readers, but this is where they'd flow through.
            WorldState ws = World.Current.WorldState;
            if (ws != null) { ws.WindDirection = _blendedWeather.windDirection; }
        }
        else
        {
            currentRegion = previewRegion;
            currentWeather = previewRegion?.weather;
        }

        // Orbit first — derivation needs _sunElevationDegrees.
        UpdateSunAndMoon();

        // Derive. A null region/weather still produces a palette with
        // fallback values so editor preview works without wiring.
        _palette = WeatherDerivation.Derive(currentRegion, currentWeather, _sunElevationDegrees, sim);

        // Integrate scroll offsets using the CURRENT (blended) weather
        // speed / palette frequencies. Parametric `speed * TIME` in the
        // shader can't do this — changing speed would rescale accumulated
        // time and snap the texture.
        float dt = (float)delta;
        if (currentWeather != null)
        {
            Vector3 windDir = currentWeather.windDirection;
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
            cloudOffset -= windXZ * steadySpeed * cloudScrollPerMps * dt;
            rippleOffsetA -= windXZ * steadySpeed * rippleSpeedA * dt;
            rippleOffsetB -= windXZ_B * steadySpeed * rippleSpeedB * dt;
            windPhase += _palette.WindFrequency * dt;
            gustPhase += _palette.GustFrequency * Mathf.Tau * dt;

            dustNoiseOffsetA += dustNoiseScroll * dt;
            Vector2 dustScrollB = new Vector2(-dustNoiseScroll.Y, dustNoiseScroll.X) * 0.7f;
            dustNoiseOffsetB += dustScrollB * dt;

            moteOffset += moteScroll * dt;
        }

        Apply();

        Vector3 fillADir = ComputeFillDirection(_primaryLightDir, fillAPitchDegrees, fillAYawOffsetDegrees);
        Vector3 fillBDir = ComputeFillDirection(_primaryLightDir, fillBPitchDegrees, fillBYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", _primaryLightDir);
        RenderingServer.GlobalShaderParameterSet("fill_a_world_dir", fillADir);
        RenderingServer.GlobalShaderParameterSet("fill_b_world_dir", fillBDir);
    }

    // Compute sun position on the celestial sphere from the current time,
    // pick sun-vs-moon as the primary directional light, orient the light
    // nodes, and stash sun elevation for derivation.
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

        SimData sim = World.Current?.WorldState?.SimData;
        float sunMaxElev = sim?.SunMaxElevationDegrees ?? 60f;
        float sunSideSway = sim?.SunSideSwayDegrees ?? 30f;

        // Phase: t=0.25 is sunrise, 0.5 is noon, 0.75 is sunset, 0/1 is midnight.
        float phase = Mathf.Tau * ((float)t - 0.25f);
        float elevRad = Mathf.Sin(phase) * Mathf.DegToRad(sunMaxElev);
        float yawRad = -Mathf.Cos(phase) * Mathf.DegToRad(sunSideSway);

        float cosElev = Mathf.Cos(elevRad);
        Vector3 sunPos = new Vector3(
            Mathf.Sin(yawRad) * cosElev,
            Mathf.Sin(elevRad),
            Mathf.Cos(yawRad) * cosElev).Normalized();
        Vector3 moonPos = -sunPos;

        _sunActualDir = (-sunPos).Normalized();
        Vector3 primaryPos = sunPos.Y >= 0f ? sunPos : moonPos;
        _primaryLightDir = (-primaryPos).Normalized();

        Vector3 moonActualDir = -_sunActualDir;
        OrientLight(sunLight, _sunActualDir);
        OrientLight(moonLight, moonActualDir);

        if (!Engine.IsEditorHint() && World.Current?.WorldState != null)
        {
            World.Current.WorldState.ShadowLightDirection = _primaryLightDir;
        }

        _sunElevationDegrees = Mathf.RadToDeg(Mathf.Asin(Mathf.Clamp(sunPos.Y, -1f, 1f)));
    }

    // Push the current palette + time-of-day state to the GPU.
    public void Apply()
    {
        CurrentAmbient = _palette.Ambient;

        float effSunIntensity = CVars.sunIntensity.Value * _palette.PrimaryIntensity;
        CurrentPrimaryIntensity = effSunIntensity;

        SimData sim = World.Current?.WorldState?.SimData;
        float sunsetAngle = sim?.SunsetAngleDegrees ?? 10f;

        // DirectionalLight3D energy crossfade — sun and moon each fade
        // through their own above-horizon smoothstep. The sum stays
        // sensibly bounded: sun dominant during day, moon at night,
        // both partial during the crossover window.
        //
        // Moon energy is additionally scaled by NightPrimaryIntensity so
        // moonlight is physically dimmer than daylight regardless of
        // whether Godot's shadow pass sees it as "the one active light".
        float lightFadeEnd = sunsetAngle + lightEnergyFadeAngleDegrees;
        float lightFadeStart = lightFadeEnd + Mathf.Max(lightEnergyFadeRangeDegrees, 0.01f);
        float sunEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, _sunElevationDegrees);
        float moonEnergyFactor = Mathf.SmoothStep(lightFadeEnd, lightFadeStart, -_sunElevationDegrees);
        if (sunLight != null) { sunLight.LightEnergy = sunEnergyFactor; }
        if (moonLight != null) { moonLight.LightEnergy = moonEnergyFactor * _palette.NightPrimaryIntensity; }

        // _nightT for disk glow fade. Recomputed from sun elevation +
        // sunset band — same formula as WeatherDerivation.PhaseWeights
        // but we only need nightT here.
        float colorRange = Mathf.Max(sim?.SunsetColorRangeDegrees ?? 10f, 0.01f);
        float dayNightThreshold = sunsetAngle + colorRange;
        float nightT = 1f - Mathf.SmoothStep(-dayNightThreshold, dayNightThreshold, _sunElevationDegrees);

        float effSunDiskGlow = sunDiskGlowStrength * (1f - nightT);
        float effMoonDiskGlow = moonDiskGlowStrength * nightT;

        // --- Global uniforms ---------------------------------------------
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(_palette.SunTint));
        RenderingServer.GlobalShaderParameterSet("sun_ambient", _palette.Ambient);
        RenderingServer.GlobalShaderParameterSet("sun_intensity", effSunIntensity);
        RenderingServer.GlobalShaderParameterSet("fill_a_color", ColorToVec3(_palette.FillA));
        RenderingServer.GlobalShaderParameterSet("fill_b_color", ColorToVec3(_palette.FillB));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(_palette.HorizonTint));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(_palette.ZenithTint));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(_palette.CloudTint));
        RenderingServer.GlobalShaderParameterSet("sky_sun_dir", _sunActualDir);
        RenderingServer.GlobalShaderParameterSet("moon_color", ColorToVec3(_palette.MoonDiskColor));
        RenderingServer.GlobalShaderParameterSet("sun_disk_glow", effSunDiskGlow);
        RenderingServer.GlobalShaderParameterSet("moon_disk_glow", effMoonDiskGlow);
        RenderingServer.GlobalShaderParameterSet("cloud_offset", cloudOffset);
        RenderingServer.GlobalShaderParameterSet("cloud_threshold", _palette.CloudThreshold);
        RenderingServer.GlobalShaderParameterSet("cloud_sharpness", _palette.CloudSharpness);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_altitude", cloudAltitude);
        RenderingServer.GlobalShaderParameterSet("cloud_shadow_strength", cloudShadowStrength);

        // --- Water -------------------------------------------------------
        RenderingServer.GlobalShaderParameterSet("ripple_scale_a", rippleScaleA);
        RenderingServer.GlobalShaderParameterSet("ripple_scale_b", rippleScaleB);
        RenderingServer.GlobalShaderParameterSet("ripple_offset_a", rippleOffsetA);
        RenderingServer.GlobalShaderParameterSet("ripple_offset_b", rippleOffsetB);
        RenderingServer.GlobalShaderParameterSet("ripple_strength", _palette.RippleStrength);
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

        Vector3 windDirForShader = weather?.windDirection ?? new Vector3(1f, 0f, 0f);
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
        float sunShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, _sunElevationDegrees);
        float moonShaftFactor = Mathf.SmoothStep(shaftFadeEnd, shaftFadeStart, -_sunElevationDegrees);

        float effShaftIntensity = _palette.SunShaftIntensity * sunShaftFactor
                                 + _palette.MoonShaftIntensity * moonShaftFactor;

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
            fogMaterial.SetShaderParameter("fog_color", ColorToVec3(_palette.FogTint));
            fogMaterial.SetShaderParameter("fog_density", _palette.FogDensity);
            fogMaterial.SetShaderParameter("ambient_fog_density", _palette.AmbientFogDensity);
            fogMaterial.SetShaderParameter("fog_max_distance", fogMaxDistance);
            fogMaterial.SetShaderParameter("fog_steps", effFogSteps);
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
            fogMaterial.SetShaderParameter("mote_offset", moteOffset);
            fogMaterial.SetShaderParameter("sun_shaft_intensity", effShaftIntensity);
            fogMaterial.SetShaderParameter("shaft_color", ColorToVec3(effShaftColor));
            fogMaterial.SetShaderParameter("block_halo_intensity", blockHaloIntensity);
            fogMaterial.SetShaderParameter("scatter_anisotropy", scatterAnisotropy);
            fogMaterial.SetShaderParameter("shaft_sun_threshold", shaftSunThreshold);

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

        ApplyPrecipitation();
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
}
