using Godot;

// Output of WeatherDerivation.Derive() — every value SkyController pushes
// to shader globals / fog material / DirectionalLight3D nodes. The
// time-of-day phase blend (day/sunset/night) happens INSIDE derivation,
// so the color/ambient/intensity fields here are already a single value.
// The sun-vs-moon horizon crossfade for shafts and light energy stays
// in SkyController — its fade-band shape is a visual-sculpt choice tied
// to shadow quality, not weather.
public struct DerivedPalette
{
    // Primary directional light tint (day→sunset→night blended).
    public Color SunTint;
    // Scene ambient (day→sunset→night blended).
    public float Ambient;
    // Scale on CVars.sunIntensity for the day primary. Phase-blended.
    public float PrimaryIntensity;
    // Night-phase intensity (unblended). Used to scale MoonLight.LightEnergy
    // independently of the phase blend — Godot's directional shadow energy
    // is a node property, not a global uniform, so we can't rely on the
    // phase-blended PrimaryIntensity for it.
    public float NightPrimaryIntensity;

    // Off-axis sculpt fills. Blended across all three phases.
    public Color FillA;
    public Color FillB;

    // Cloud and sky dome tints. Blended across all three phases.
    public Color CloudTint;
    public Color HorizonTint;
    public Color ZenithTint;

    // Fog body tint (blended) + densities.
    public Color FogTint;
    public float FogDensity;
    public float AmbientFogDensity;
    public float DustDensity;

    // Cloud shape — weather-driven only, no phase blend.
    public float CloudThreshold;
    public float CloudSharpness;

    // Shaft channels. Day and night shaft intensities come in separately
    // so SkyController can crossfade them by each source's above-horizon
    // factor. Each shaft COLOR already has sunset bias baked in.
    public float SunShaftIntensity;
    public float MoonShaftIntensity;
    public Color SunShaftColor;
    public Color MoonShaftColor;

    // Water.
    public float RippleStrength;

    // Wind rhythm. windSpeed itself comes straight from WeatherData;
    // these are derived. Sprite sway uses gustedSpeed = windSpeed +
    // gustWave × GustStrength; SkyController computes that and drives
    // the shader from there.
    public float WindFrequency;
    public float GustStrength;
    public float GustFrequency;

    // Rain pass-through + derived weight.
    public float RainIntensity;
    public float RainWeight;

    // Region MoonColor, unblended — the sky shader's moon disk should
    // literally be the moon, not the phase-blended primary.
    public Color MoonDiskColor;
}
