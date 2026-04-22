using Godot;

// Authored atmospheric state for one weather "type" (clear, overcast, stormy, etc.).
// SkyController owns a live working copy of this resource and can LerpToWeather(target, seconds)
// to blend every field from its current value toward a target preset over time.
//
// Only values that should change with weather / time-of-day live here. Scene-structural
// tuning (SSR config, cloud altitude, fog material reference, ripple surface geometry,
// shadow raymarch quality) stays on SkyController as direct exports — those aren't
// weather-driven and would be confusing to lerp.
//
// Fields are grouped by time-of-day phase: Day holds the high-sun values, Sunset
// holds the golden-hour band (used at both sunrise and sunset), Night holds the
// moon-primary values. SkyController blends between them each frame based on the
// sun's current elevation, so authoring a new weather preset means filling in
// each color three times. Weather-independent environmental state (wind
// direction) lives on SimData, not here.
[GlobalClass]
public partial class WeatherData : Resource
{
    [ExportGroup("Day")]
    [Export] public Color horizonColor = new Color(0.72f, 0.82f, 0.92f);
    [Export] public Color zenithColor = new Color(0.25f, 0.48f, 0.82f);
    [Export] public Color sunColor = new Color(1.0f, 0.96f, 0.88f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunAmbient = 0.4f;
    // Multiplier on CVars.sun_intensity at noon. Lets each weather preset
    // tune overall scene brightness at high sun — dim stormy / overcast
    // days, boost crisp clear days — without touching the player-facing
    // sun_intensity CVar. 1.0 = CVar value unchanged; 0.6 = 60% of noon
    // base; 1.3 = slight boost. Interacts with sunsetLightIntensity and
    // moonLightIntensity via the same day/sunset/night blend that drives
    // the color choices — all three read as "this weather's direct-light
    // level at this time of day".
    [Export(PropertyHint.Range, "0,2,0.01")] public float dayLightIntensity = 1.0f;
    // Two off-axis fill tints that darken surfaces facing away from their
    // respective world directions (set on SkyController). Neither is aligned
    // with the sun — the sun's directional contribution already comes from
    // the BFS sun_mask + shadow atlas. These sculpt slope across the whole
    // scene (sunlit and shadowed) since they don't fade with sun_mask.
    [Export] public Color fillAColor = new Color(0.78f, 0.78f, 0.92f);
    [Export] public Color fillBColor = new Color(0.92f, 0.86f, 0.72f);
    // Fog tint during full day. Sunset and night have their own variants
    // (sunsetFogColor, nightFogColor) that SkyController crossfades into.
    [Export] public Color fogColor = new Color(0.85f, 0.88f, 0.95f);
    // Day-time shaft (god-ray) color + intensity. Day beams feed through
    // `sun_shaft_intensity` and `shaft_color` uniforms on the fog material.
    [Export(PropertyHint.Range, "0,32,0.01")] public float sunShaftIntensity = 8.0f;
    [Export] public Color sunShaftColor = new Color(1.0f, 0.96f, 0.88f);

    [ExportGroup("Sunset")]
    // Used at BOTH sunrise and sunset — the blend is symmetric around
    // sunsetAngleDegrees, so authoring one "golden hour" palette covers
    // both horizon crossings.
    [Export] public Color sunsetHorizonColor = new Color(1.0f, 0.55f, 0.3f);
    [Export] public Color sunsetZenithColor = new Color(0.3f, 0.2f, 0.4f);
    // Primary light color at the sunset peak — replaces sunColor through
    // the sunset blend band centered on sunsetAngleDegrees.
    [Export] public Color sunsetColor = new Color(1.0f, 0.65f, 0.35f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunsetAmbient = 0.4f;
    // Fraction of the day-base sun_intensity applied at sunset peak.
    // Lets sunset be authored as mellower than noon (typical ~0.7) without
    // touching CVars.sun_intensity. Paired with sunsetAmbient so each
    // weather preset can tune the direct↔ambient split of golden hour —
    // e.g. a stormy sunset might want low direct but high ambient.
    [Export(PropertyHint.Range, "0,2,0.01")] public float sunsetLightIntensity = 0.7f;
    [Export] public Color sunsetFillAColor = new Color(0.75f, 0.55f, 0.7f);
    [Export] public Color sunsetFillBColor = new Color(1.0f, 0.7f, 0.5f);
    [Export] public Color sunsetCloudColor = new Color(1.0f, 0.78f, 0.6f);
    [Export] public Color sunsetFogColor = new Color(0.9f, 0.65f, 0.55f);
    // Shaft (god-ray) color at sunset peak. SkyController blends between
    // sunShaftColor, this, and moonShaftColor using the same 3-way weights
    // as the other color fields, so golden-hour shafts can lean harder
    // into warm amber than either the day or night shafts would.
    [Export] public Color sunsetShaftColor = new Color(1.0f, 0.6f, 0.3f);

    [ExportGroup("Night")]
    // When the sun is below sunsetAngleDegrees the moon takes over as the
    // directional source. Pale blue-white reads as moonlight without going
    // full grayscale.
    [Export] public Color nightHorizonColor = new Color(0.12f, 0.14f, 0.22f);
    [Export] public Color nightZenithColor = new Color(0.03f, 0.04f, 0.09f);
    [Export] public Color moonColor = new Color(0.55f, 0.6f, 0.75f);
    // Default BELOW sunAmbient: moonlight scatters through the atmosphere
    // far less than sunlight, so moonlit scenes have very dark shadows
    // and low fill. Setting this high turns night into "dim day" — the
    // direct-vs-ambient split collapses and Godot's DirectionalLight3D
    // shadow (which only shadows the direct portion via ATTENUATION on
    // ALBEDO in voxel_clip.gdshader) becomes imperceptible. Keep it well
    // below sunAmbient for the "crisp moon shadows" look.
    [Export(PropertyHint.Range, "0,1,0.01")] public float moonAmbient = 0.2f;
    // Fraction of day-base sun_intensity used at full night; also scales
    // MoonLight.LightEnergy directly. Paired with moonAmbient so each
    // weather can choose its night character — a foggy overcast night
    // might want low direct + moderate ambient (diffuse gloom), a clear
    // full moon wants crisp direct + low ambient. Typical range 0.2–0.5.
    [Export(PropertyHint.Range, "0,2,0.01")] public float moonLightIntensity = 0.3f;
    [Export] public Color nightFillAColor = new Color(0.3f, 0.33f, 0.45f);
    [Export] public Color nightFillBColor = new Color(0.35f, 0.38f, 0.5f);
    [Export] public Color nightCloudColor = new Color(0.4f, 0.43f, 0.55f);
    [Export] public Color nightFogColor = new Color(0.22f, 0.27f, 0.38f);
    // Night-time shaft (moonlight god-ray) color + intensity. Independent
    // of sunShaft* so nighttime can lean into a spookier, cooler-toned
    // vibe than day shafts. SkyController crossfades between the two
    // around the shaft fade band so neither produces a visible shaft
    // while the primary direction is flipping across the horizon.
    [Export(PropertyHint.Range, "0,32,0.01")] public float moonShaftIntensity = 4.0f;
    [Export] public Color moonShaftColor = new Color(0.45f, 0.55f, 0.75f);

    [ExportGroup("Wind")]
    // Direction lives on SimData as a world-level property. These tune the
    // SPEED and RHYTHM of wind per weather — stormy presets can crank up
    // wind / gust speed without changing the compass bearing.
    //
    // windSpeed is the steady horizontal wind in meters per second. Drives
    // sprite/grass sway amplitude (via SkyController.windToSwayMeters), rain
    // tilt (via RainEffect.tiltDegPerMps), cloud scroll rate (via
    // SkyController.cloudScrollPerMps) and water ripple drift (via
    // SkyController.rippleSpeedA/B reinterpreted as per-m/s fractions). All
    // those consumers convert m/s into their own visual unit through scene-
    // level constants, so retuning a weather preset's wind never requires
    // touching the scene.
    [Export(PropertyHint.Range, "0,40,0.1")] public float windSpeed = 2.0f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float windFrequency = 1.5f;
    // Additional horizontal wind speed (m/s) added at the peak of each gust
    // wave. Effective speed varies between windSpeed (gust trough) and
    // windSpeed + gustStrength (gust peak). Affects only consumers that
    // should pulse with gusts — sprite sway and rain tilt — not clouds or
    // ripples (those drift with steady wind only).
    [Export(PropertyHint.Range, "0,30,0.1")] public float gustStrength = 1.0f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float gustFrequency = 0.15f;

    [ExportGroup("Water")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float rippleStrength = 0.15f;

    [ExportGroup("Clouds")]
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpness = 0.7f;
    [Export] public float cloudScale = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShadowStrength = 1.0f;

    [ExportGroup("Atmospherics")]
    // Authored voxel fog density multiplier (scales per-voxel fog_map values).
    // Only fills volumes where WorldGen / the editor explicitly placed fog —
    // rolling banks in valleys, etc. Color is split across Day/Sunset/Night
    // (fogColor, sunsetFogColor, nightFogColor) and blended by time of day;
    // density is a single weather-wide value since haze thickness is a
    // mood choice, not a palette choice.
    [Export(PropertyHint.Range, "0,1,0.001")] public float fogDensity = 0.05f;
    // Uniform whole-scene haze that sits on top of the authored voxel fog.
    // Same fogColor tint (blended day/sunset/night) but not gated by the
    // fog_map — applies everywhere inside the raymarch. Use to make
    // overcast / foggy / stormy presets feel enclosed in atmosphere even
    // where no authored fog exists. Typical range 0.005–0.05: small
    // values add a subtle distance haze, larger values feel pea-soup.
    [Export(PropertyHint.Range, "0,0.2,0.0001")] public float ambientFogDensity = 0f;
    // Uniform atmospheric dust — the scattering medium that god rays need
    // in order to be visible. Bound by SkyController.dustBandHeight to
    // keep dust near the ground.
    [Export(PropertyHint.Range, "0,1,0.0001")] public float dustDensity = 0.003f;

    [ExportGroup("Weather Particles")]
    // Rain emission strength. 0 = no rain, 1 = full downpour. Drives both
    // falling-streak density and ground-splash rate on the RainEffect node.
    // Lerped like every other field, so transitions in/out via LerpToWeather
    // fade smoothly. Future particle variants (hail, snow, dust-storm motes)
    // add their own *Intensity fields alongside this one.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainIntensity = 0.0f;
    // Visual "heft" of each drop. SkyController.ApplyPrecipitation scales
    // fall velocity, drop-material albedo alpha, and streak length by this
    // value, and INVERSELY scales how strongly wind/gusts tilt the rain:
    // a rainWeight=0.3 drizzle blows sideways at the first gust, while a
    // rainWeight=2.0 downpour barrels through the same wind near-vertical.
    // Orthogonal to rainIntensity — weight is the character of a single
    // drop, intensity is how many are falling. Default 1.0 = "normal storm".
    [Export(PropertyHint.Range, "0.1,3,0.01")] public float rainWeight = 1.0f;

    // Copy every field from `other` into this. Used by SkyController to apply an
    // instantaneous weather snapshot without allocating a new Resource.
    public void CopyFrom(WeatherData other)
    {
        horizonColor = other.horizonColor;
        zenithColor = other.zenithColor;
        sunColor = other.sunColor;
        sunAmbient = other.sunAmbient;
        dayLightIntensity = other.dayLightIntensity;
        fillAColor = other.fillAColor;
        fillBColor = other.fillBColor;
        fogColor = other.fogColor;
        sunShaftIntensity = other.sunShaftIntensity;
        sunShaftColor = other.sunShaftColor;
        sunsetHorizonColor = other.sunsetHorizonColor;
        sunsetZenithColor = other.sunsetZenithColor;
        sunsetColor = other.sunsetColor;
        sunsetAmbient = other.sunsetAmbient;
        sunsetLightIntensity = other.sunsetLightIntensity;
        sunsetFillAColor = other.sunsetFillAColor;
        sunsetFillBColor = other.sunsetFillBColor;
        sunsetCloudColor = other.sunsetCloudColor;
        sunsetFogColor = other.sunsetFogColor;
        sunsetShaftColor = other.sunsetShaftColor;
        nightHorizonColor = other.nightHorizonColor;
        nightZenithColor = other.nightZenithColor;
        moonColor = other.moonColor;
        moonAmbient = other.moonAmbient;
        moonLightIntensity = other.moonLightIntensity;
        nightFillAColor = other.nightFillAColor;
        nightFillBColor = other.nightFillBColor;
        nightCloudColor = other.nightCloudColor;
        nightFogColor = other.nightFogColor;
        moonShaftIntensity = other.moonShaftIntensity;
        moonShaftColor = other.moonShaftColor;
        windSpeed = other.windSpeed;
        windFrequency = other.windFrequency;
        gustStrength = other.gustStrength;
        gustFrequency = other.gustFrequency;
        rippleStrength = other.rippleStrength;
        cloudColor = other.cloudColor;
        cloudThreshold = other.cloudThreshold;
        cloudSharpness = other.cloudSharpness;
        cloudScale = other.cloudScale;
        cloudShadowStrength = other.cloudShadowStrength;
        fogDensity = other.fogDensity;
        ambientFogDensity = other.ambientFogDensity;
        dustDensity = other.dustDensity;
        rainIntensity = other.rainIntensity;
        rainWeight = other.rainWeight;
    }

    // Interpolate every field into this from (a -> b) at t in [0, 1]. t is expected
    // already eased/clamped by the caller.
    public void LerpFields(WeatherData a, WeatherData b, float t)
    {
        horizonColor = a.horizonColor.Lerp(b.horizonColor, t);
        zenithColor = a.zenithColor.Lerp(b.zenithColor, t);
        sunColor = a.sunColor.Lerp(b.sunColor, t);
        sunAmbient = Mathf.Lerp(a.sunAmbient, b.sunAmbient, t);
        dayLightIntensity = Mathf.Lerp(a.dayLightIntensity, b.dayLightIntensity, t);
        fillAColor = a.fillAColor.Lerp(b.fillAColor, t);
        fillBColor = a.fillBColor.Lerp(b.fillBColor, t);
        fogColor = a.fogColor.Lerp(b.fogColor, t);
        sunShaftIntensity = Mathf.Lerp(a.sunShaftIntensity, b.sunShaftIntensity, t);
        sunShaftColor = a.sunShaftColor.Lerp(b.sunShaftColor, t);
        sunsetHorizonColor = a.sunsetHorizonColor.Lerp(b.sunsetHorizonColor, t);
        sunsetZenithColor = a.sunsetZenithColor.Lerp(b.sunsetZenithColor, t);
        sunsetColor = a.sunsetColor.Lerp(b.sunsetColor, t);
        sunsetAmbient = Mathf.Lerp(a.sunsetAmbient, b.sunsetAmbient, t);
        sunsetLightIntensity = Mathf.Lerp(a.sunsetLightIntensity, b.sunsetLightIntensity, t);
        sunsetFillAColor = a.sunsetFillAColor.Lerp(b.sunsetFillAColor, t);
        sunsetFillBColor = a.sunsetFillBColor.Lerp(b.sunsetFillBColor, t);
        sunsetCloudColor = a.sunsetCloudColor.Lerp(b.sunsetCloudColor, t);
        sunsetFogColor = a.sunsetFogColor.Lerp(b.sunsetFogColor, t);
        sunsetShaftColor = a.sunsetShaftColor.Lerp(b.sunsetShaftColor, t);
        nightHorizonColor = a.nightHorizonColor.Lerp(b.nightHorizonColor, t);
        nightZenithColor = a.nightZenithColor.Lerp(b.nightZenithColor, t);
        moonColor = a.moonColor.Lerp(b.moonColor, t);
        moonAmbient = Mathf.Lerp(a.moonAmbient, b.moonAmbient, t);
        moonLightIntensity = Mathf.Lerp(a.moonLightIntensity, b.moonLightIntensity, t);
        nightFillAColor = a.nightFillAColor.Lerp(b.nightFillAColor, t);
        nightFillBColor = a.nightFillBColor.Lerp(b.nightFillBColor, t);
        nightCloudColor = a.nightCloudColor.Lerp(b.nightCloudColor, t);
        nightFogColor = a.nightFogColor.Lerp(b.nightFogColor, t);
        moonShaftIntensity = Mathf.Lerp(a.moonShaftIntensity, b.moonShaftIntensity, t);
        moonShaftColor = a.moonShaftColor.Lerp(b.moonShaftColor, t);
        windSpeed = Mathf.Lerp(a.windSpeed, b.windSpeed, t);
        windFrequency = Mathf.Lerp(a.windFrequency, b.windFrequency, t);
        gustStrength = Mathf.Lerp(a.gustStrength, b.gustStrength, t);
        gustFrequency = Mathf.Lerp(a.gustFrequency, b.gustFrequency, t);
        rippleStrength = Mathf.Lerp(a.rippleStrength, b.rippleStrength, t);
        cloudColor = a.cloudColor.Lerp(b.cloudColor, t);
        cloudThreshold = Mathf.Lerp(a.cloudThreshold, b.cloudThreshold, t);
        cloudSharpness = Mathf.Lerp(a.cloudSharpness, b.cloudSharpness, t);
        cloudScale = Mathf.Lerp(a.cloudScale, b.cloudScale, t);
        cloudShadowStrength = Mathf.Lerp(a.cloudShadowStrength, b.cloudShadowStrength, t);
        fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t);
        ambientFogDensity = Mathf.Lerp(a.ambientFogDensity, b.ambientFogDensity, t);
        dustDensity = Mathf.Lerp(a.dustDensity, b.dustDensity, t);
        rainIntensity = Mathf.Lerp(a.rainIntensity, b.rainIntensity, t);
        rainWeight = Mathf.Lerp(a.rainWeight, b.rainWeight, t);
    }
}
