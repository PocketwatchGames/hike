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
    // Sun-side absolute intensity (DayIntensityBase × weather-scaled
    // factors, day blended with sunset). SkyController consumes this
    // alongside NightPrimaryIntensity, lerping by NightT to produce
    // CurrentPrimaryIntensity.
    public float PrimaryIntensity;
    // Moon-side intensity fraction (night). Multiplied by CVars.moonIntensity
    // in SkyController for both the scene primary blend AND the
    // MoonLight.LightEnergy on Godot's shadow-casting directional node.
    public float NightPrimaryIntensity;
    // Day↔night phase weight. 0 = full day, 1 = full night, smoothstepped
    // through the dayNightThreshold band around the horizon. SkyController
    // uses this to blend the (sun*Primary, moon*Night) pair.
    public float NightT;

    // Off-axis sculpt fills. Blended across all three phases.
    public Color FillA;
    public Color FillB;

    // Cloud and sky dome tints. Blended across all three phases.
    public Color CloudTint;
    public Color HorizonTint;
    public Color ZenithTint;

    // Fog body tint (blended) + densities. `Fog` is the simulated fog
    // level in [0, 1], derived in WeatherDerivation from simulated
    // humidity + cool-half-of-day diurnal — there is no authored fog
    // input, so this is the single canonical fog signal that
    // SkyController's disk / water reads consume.
    public Color FogTint;
    public float Fog;
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

    // Water. All derived from RegionData.WaterColor + DustColor + weather +
    // time-of-day. One authored RGBA drives all of these via the muddiness
    // (alpha) channel and region atmosphere colors. See WeatherDerivation.
    public float RippleStrength;
    // Per-region shallow & deep tints. Shallow is the authored WaterColor.rgb;
    // deep is derived (red-absorbed physics for clear water, dust-tinted
    // sediment for murky water) from WaterColor + DustColor + muddiness.
    public Color WaterShallowTint;
    public Color WaterDeepTint;
    // Surface alpha floor (WaterColor.a). The effective value pushed to
    // the shader is further modulated by sun-vs-ambient clarity in
    // SkyController.Apply().
    public float WaterAlphaMin;
    // Exponent applied to the depth_factor in the shader before it drives
    // the alpha ramp. Clear water uses > 1 (stays translucent longer);
    // muddy water uses < 1 (hits opaque quickly within ~1 voxel of depth).
    public float WaterTurbidityExp;
    // Muddiness (= WaterColor.a) — passed through so SkyController can
    // apply its own muddy-modulation to exports (reflection boost,
    // refraction damp, whitecap threshold lift, wave amplitude damp).
    public float WaterMuddiness;

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
