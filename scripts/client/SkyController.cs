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

    [ExportGroup("Sun")]
    // Wire to the scene's DirectionalLight3D. When set, its transform is
    // the single source of truth for sun direction — both Godot's built-in
    // terrain shadow pass (via the node itself) and the fog shader's beam
    // direction (via sun_world_dir global) track the same rotation.
    // Without this wired, falls back to WorldState.ShadowLightDirection
    // (the day/night sim), which only drives the fog shader and would
    // disagree with terrain shadows if the node transform is edited.
    [Export] public DirectionalLight3D sunLight;
    // Fill light pitch below horizon (degrees) and yaw offset from the sun.
    [Export] public float fillPitchDegrees = 35f;
    [Export] public float fillYawOffsetDegrees = 135f;

    [ExportGroup("Water — Ripples")]
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

    [ExportGroup("Water — Reflections")]
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

    [ExportGroup("Water — Shoreline Foam")]
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

    [ExportGroup("Water — Screenspace Reflection")]
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

    [ExportGroup("Fog — Raymarch Config")]
    // Wire this to res://resources/materials/fog_volumetric.tres — the
    // shader's per-material uniforms are pushed here from the fields below.
    [Export] public ShaderMaterial fogMaterial;
    [Export] public float fogMaxDistance = 100.0f;
    [Export(PropertyHint.Range, "1,64,1")] public int fogSteps = 48;

    [ExportGroup("Fog — Dust Band Geometry")]
    // How many meters above the reference Y the dust layer extends.
    // Above this height, dust fades to 0 → no beam contribution. 8-12m
    // is a natural range for "mist near the ground" in an outdoor scene.
    [Export(PropertyHint.Range, "1,64,0.1")] public float dustBandHeight = 10.0f;
    [Export(PropertyHint.Range, "0,2,0.001")] public float dustNoiseScale = 0.12f;
    [Export] public Vector2 dustNoiseScroll = new Vector2(0.05f, 0.03f);

    [ExportGroup("Fog — Inscatter Tuning")]
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

    [ExportGroup("Fog — Sun Shadow Raymarch (geometry-shaped beams)")]
    // Screen-space raymarch toward the sun at each fog march sample.
    // Samples depth_tex to check if tree/terrain/sprite geometry is
    // blocking the sun ray at that point. Produces the sharp canopy-
    // shaped beams the cloud-noise path cannot. Expensive — K depth
    // samples per fog step; disable for performance if needed.
    [Export] public bool sunShadowEnabled = true;
    [Export(PropertyHint.Range, "1,16,1")] public int sunShadowSteps = 6;
    [Export(PropertyHint.Range, "1,64,0.1")] public float sunShadowDistance = 16.0f;
    [Export(PropertyHint.Range, "0,0.01,0.0001")] public float sunShadowBias = 0.0005f;

    [ExportGroup("Fog — Shaft Shaping")]
    // Fraction of the view-ray march over which inscatter fades to 0 as
    // it approaches the terminating surface. Dust peaks at the ground
    // (dust_band_height gate is full when right above surface), so this
    // fade prevents shafts from painting color directly onto the ground
    // pixel — beams visually "float" above the terrain rather than
    // blanketing it. 0 = shafts paint onto ground; 0.5 = fade over the
    // last half of the march.
    [Export(PropertyHint.Range, "0,0.5,0.01")] public float shaftGroundFade = 0.2f;

    [ExportGroup("Fog — Mote Shimmer (animated)")]
    // Animated visual noise that makes beams shimmer. Carries no scattering
    // density of its own (that comes from dust density on WeatherData) — pure
    // cosmetic motion. moteStrength is weather-driven (on WeatherData); scale
    // and scroll are authored scene constants.
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

    public override void _Ready()
    {
        Current = this;
        // Seed ALL static globals here ONCE. The old code pushed them every
        // frame from _Process, which broke any runtime override. _Process
        // now only pushes genuinely dynamic values (sun direction).
        if (!Engine.IsEditorHint())
        {
            ShaderGlobals.Register("sun_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, new Vector3(-0.215f, -0.819f, -0.532f));
            ShaderGlobals.Register("fill_world_dir", RenderingServer.GlobalShaderParameterType.Vec3, Vector3.Down);

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
            Vector2 windXZ = new Vector2(weather.windDirection.X, weather.windDirection.Z);
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
        }

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

        // Sun direction: prefer the DirectionalLight3D node's transform
        // when one is wired so terrain shadows (Godot-native, driven by
        // the same node) and the fog shader's beams track the same
        // rotation. Fall back to WorldState.ShadowLightDirection (the
        // day/night sim) only when no node is assigned — and only at
        // runtime, since WorldState doesn't exist in the editor.
        Vector3 sunDir;
        if (sunLight != null)
        {
            // DirectionalLight3D emits light along its -Z (forward) axis.
            sunDir = -sunLight.GlobalTransform.Basis.Z.Normalized();
        }
        else if (!Engine.IsEditorHint())
        {
            sunDir = World.Current?.WorldState?.ShadowLightDirection ?? new Vector3(-0.215f, -0.819f, -0.532f);
        }
        else
        {
            // Editor with no light node wired — keep a stable default so
            // the preview doesn't flicker.
            sunDir = new Vector3(-0.215f, -0.819f, -0.532f);
        }
        Vector3 fillDir = ComputeFillDirection(sunDir, fillPitchDegrees, fillYawOffsetDegrees);
        RenderingServer.GlobalShaderParameterSet("sun_world_dir", sunDir);
        RenderingServer.GlobalShaderParameterSet("fill_world_dir", fillDir);
    }

    // Push every authored atmospheric value to the GPU. Called every frame so
    // live lerping + inspector edits (via [Tool]) take effect immediately.
    public void Apply()
    {
        // In editor mode with no weather resource assigned, nothing to push.
        // (Runtime _Ready creates a default; only the editor allows a null
        // weather reference.)
        if (weather == null) { return; }

        // --- Global uniforms ---------------------------------------------
        RenderingServer.GlobalShaderParameterSet("sun_color", ColorToVec3(weather.sunColor));
        RenderingServer.GlobalShaderParameterSet("sun_ambient", weather.sunAmbient);
        RenderingServer.GlobalShaderParameterSet("sun_tint_color", ColorToVec3(weather.sunTintColor));
        RenderingServer.GlobalShaderParameterSet("fill_tint_color", ColorToVec3(weather.fillTintColor));
        RenderingServer.GlobalShaderParameterSet("horizon_color", ColorToVec3(weather.horizonColor));
        RenderingServer.GlobalShaderParameterSet("zenith_color", ColorToVec3(weather.zenithColor));
        RenderingServer.GlobalShaderParameterSet("cloud_color", ColorToVec3(weather.cloudColor));
        // Pre-integrated offsets (see _Process). Passing position directly
        // instead of speed avoids the lerp discontinuity the shader's old
        // `scroll * TIME` form exhibited when weather changed cloudScrollSpeed.
        RenderingServer.GlobalShaderParameterSet("cloud_offset", cloudOffset);
        RenderingServer.GlobalShaderParameterSet("cloud_threshold", weather.cloudThreshold);
        RenderingServer.GlobalShaderParameterSet("cloud_sharpness", weather.cloudSharpness);
        RenderingServer.GlobalShaderParameterSet("cloud_scale", weather.cloudScale);
        RenderingServer.GlobalShaderParameterSet("cloud_altitude", cloudAltitude);
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
        RenderingServer.GlobalShaderParameterSet("wind_dir", weather.windDirection.Normalized());
        RenderingServer.GlobalShaderParameterSet("wind_amplitude", amplitude);
        RenderingServer.GlobalShaderParameterSet("wind_phase", windPhase);

        // --- Fog material uniforms ---------------------------------------
        if (fogMaterial != null)
        {
            fogMaterial.SetShaderParameter("fog_color", ColorToVec3(weather.fogColor));
            fogMaterial.SetShaderParameter("fog_density", weather.fogDensity);
            fogMaterial.SetShaderParameter("fog_max_distance", fogMaxDistance);
            fogMaterial.SetShaderParameter("fog_steps", fogSteps);
            fogMaterial.SetShaderParameter("dust_density", weather.dustDensity);
            fogMaterial.SetShaderParameter("dust_band_height", dustBandHeight);
            fogMaterial.SetShaderParameter("dust_noise_strength", weather.dustNoiseStrength);
            fogMaterial.SetShaderParameter("dust_noise_scale", dustNoiseScale);
            fogMaterial.SetShaderParameter("dust_noise_threshold", weather.dustNoiseThreshold);
            fogMaterial.SetShaderParameter("dust_noise_sharpness", weather.dustNoiseSharpness);
            fogMaterial.SetShaderParameter("dust_noise_scroll", dustNoiseScroll);
            fogMaterial.SetShaderParameter("sun_shaft_intensity", weather.sunShaftIntensity);
            fogMaterial.SetShaderParameter("block_halo_intensity", weather.blockHaloIntensity);
            fogMaterial.SetShaderParameter("scatter_anisotropy", scatterAnisotropy);
            fogMaterial.SetShaderParameter("shaft_sun_threshold", shaftSunThreshold);
            fogMaterial.SetShaderParameter("cloud_shaft_weight", weather.cloudShaftWeight);
            fogMaterial.SetShaderParameter("cloud_shaft_sharpness", weather.cloudShaftSharpness);
            fogMaterial.SetShaderParameter("shaft_camera_fade_degrees", shaftCameraFadeDegrees);
            fogMaterial.SetShaderParameter("sun_shadow_enabled", sunShadowEnabled);
            fogMaterial.SetShaderParameter("sun_shadow_steps", sunShadowSteps);
            fogMaterial.SetShaderParameter("sun_shadow_distance", sunShadowDistance);
            fogMaterial.SetShaderParameter("sun_shadow_bias", sunShadowBias);
            fogMaterial.SetShaderParameter("shaft_ground_fade", shaftGroundFade);
            fogMaterial.SetShaderParameter("mote_strength", weather.moteStrength);
            fogMaterial.SetShaderParameter("mote_scale", moteScale);
            fogMaterial.SetShaderParameter("mote_scroll", moteScroll);
        }
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
}
