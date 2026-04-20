using Godot;

// Authored atmospheric state for one weather "type" (clear, overcast, stormy, etc.).
// SkyController owns a live working copy of this resource and can LerpToWeather(target, seconds)
// to blend every field from its current value toward a target preset over time.
//
// Only values that should change with weather / time-of-day live here. Scene-structural
// tuning (SSR config, cloud altitude, fog material reference, ripple surface geometry,
// shadow raymarch quality) stays on SkyController as direct exports — those aren't
// weather-driven and would be confusing to lerp.
[GlobalClass]
public partial class WeatherData : Resource
{
    [ExportGroup("Sky Dome")]
    [Export] public Color horizonColor = new Color(0.72f, 0.82f, 0.92f);
    [Export] public Color zenithColor = new Color(0.25f, 0.48f, 0.82f);

    [ExportGroup("Sun")]
    [Export] public Color sunColor = new Color(1.0f, 0.96f, 0.88f);
    [Export(PropertyHint.Range, "0,1,0.01")] public float sunAmbient = 0.4f;
    // Two off-axis fill tints that darken surfaces facing away from their
    // respective world directions (set on SkyController). Neither is aligned
    // with the sun — the sun's directional contribution already comes from
    // the BFS sun_mask + shadow atlas. These sculpt slope across the whole
    // scene (sunlit and shadowed) since they don't fade with sun_mask.
    [Export] public Color fillAColor = new Color(0.78f, 0.78f, 0.92f);
    [Export] public Color fillBColor = new Color(0.92f, 0.86f, 0.72f);

    [ExportGroup("Wind")]
    [Export] public Vector3 windDirection = new Vector3(0.7f, 0f, 0.7f);
    [Export(PropertyHint.Range, "0,1,0.001")] public float windAmplitude = 0.05f;
    [Export(PropertyHint.Range, "0,5,0.01")] public float windFrequency = 1.5f;
    [Export(PropertyHint.Range, "0,3,0.01")] public float gustStrength = 0.6f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float gustFrequency = 0.15f;

    [ExportGroup("Water")]
    [Export(PropertyHint.Range, "0,1,0.01")] public float rippleStrength = 0.15f;

    [ExportGroup("Clouds")]
    [Export] public Color cloudColor = new Color(1.0f, 0.98f, 0.95f);
    [Export(PropertyHint.Range, "0,0.1,0.0001")] public float cloudScrollSpeed = 0.006f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudThreshold = 0.5f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudSharpness = 0.7f;
    [Export] public float cloudScale = 0.15f;
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudShadowStrength = 1.0f;

    [ExportGroup("Fog — Authored")]
    [Export] public Color fogColor = new Color(0.85f, 0.88f, 0.95f);
    [Export(PropertyHint.Range, "0,1,0.001")] public float fogDensity = 0.05f;

    [ExportGroup("Fog — Atmospheric Dust")]
    [Export(PropertyHint.Range, "0,1,0.0001")] public float dustDensity = 0.003f;

    [ExportGroup("Inscatter — Shafts + Halos")]
    [Export(PropertyHint.Range, "0,32,0.01")] public float sunShaftIntensity = 8.0f;

    // Copy every field from `other` into this. Used by SkyController to apply an
    // instantaneous weather snapshot without allocating a new Resource.
    public void CopyFrom(WeatherData other)
    {
        horizonColor = other.horizonColor;
        zenithColor = other.zenithColor;
        sunColor = other.sunColor;
        sunAmbient = other.sunAmbient;
        fillAColor = other.fillAColor;
        fillBColor = other.fillBColor;
        windDirection = other.windDirection;
        windAmplitude = other.windAmplitude;
        windFrequency = other.windFrequency;
        gustStrength = other.gustStrength;
        gustFrequency = other.gustFrequency;
        rippleStrength = other.rippleStrength;
        cloudColor = other.cloudColor;
        cloudScrollSpeed = other.cloudScrollSpeed;
        cloudThreshold = other.cloudThreshold;
        cloudSharpness = other.cloudSharpness;
        cloudScale = other.cloudScale;
        cloudShadowStrength = other.cloudShadowStrength;
        fogColor = other.fogColor;
        fogDensity = other.fogDensity;
        dustDensity = other.dustDensity;
        sunShaftIntensity = other.sunShaftIntensity;
    }

    // Interpolate every field into this from (a -> b) at t in [0, 1]. t is expected
    // already eased/clamped by the caller.
    public void LerpFields(WeatherData a, WeatherData b, float t)
    {
        horizonColor = a.horizonColor.Lerp(b.horizonColor, t);
        zenithColor = a.zenithColor.Lerp(b.zenithColor, t);
        sunColor = a.sunColor.Lerp(b.sunColor, t);
        sunAmbient = Mathf.Lerp(a.sunAmbient, b.sunAmbient, t);
        fillAColor = a.fillAColor.Lerp(b.fillAColor, t);
        fillBColor = a.fillBColor.Lerp(b.fillBColor, t);
        windDirection = a.windDirection.Lerp(b.windDirection, t);
        windAmplitude = Mathf.Lerp(a.windAmplitude, b.windAmplitude, t);
        windFrequency = Mathf.Lerp(a.windFrequency, b.windFrequency, t);
        gustStrength = Mathf.Lerp(a.gustStrength, b.gustStrength, t);
        gustFrequency = Mathf.Lerp(a.gustFrequency, b.gustFrequency, t);
        rippleStrength = Mathf.Lerp(a.rippleStrength, b.rippleStrength, t);
        cloudColor = a.cloudColor.Lerp(b.cloudColor, t);
        cloudScrollSpeed = Mathf.Lerp(a.cloudScrollSpeed, b.cloudScrollSpeed, t);
        cloudThreshold = Mathf.Lerp(a.cloudThreshold, b.cloudThreshold, t);
        cloudSharpness = Mathf.Lerp(a.cloudSharpness, b.cloudSharpness, t);
        cloudScale = Mathf.Lerp(a.cloudScale, b.cloudScale, t);
        cloudShadowStrength = Mathf.Lerp(a.cloudShadowStrength, b.cloudShadowStrength, t);
        fogColor = a.fogColor.Lerp(b.fogColor, t);
        fogDensity = Mathf.Lerp(a.fogDensity, b.fogDensity, t);
        dustDensity = Mathf.Lerp(a.dustDensity, b.dustDensity, t);
        sunShaftIntensity = Mathf.Lerp(a.sunShaftIntensity, b.sunShaftIntensity, t);
    }
}
