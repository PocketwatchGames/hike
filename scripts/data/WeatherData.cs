using Godot;

// Mutable weather simulation state. Authored fields on RegionData.weather
// represent the region's MAX for each channel (the daytime peak / clear-
// air ceiling); WeatherSimulation perturbs a per-frame working copy in
// place to produce the values actually IN EFFECT right now (diurnal
// curve + 12-hour weather variance). All visual consequences (sky/fog/
// cloud/shaft colors, fill tints, light intensities, cloud shape, gust
// rhythm, ripples, dust density, etc.) are DERIVED by
// WeatherDerivation.Derive() from this + a RegionData palette + time-
// of-day. See also scripts/client/WeatherSimulation.cs and
// scripts/client/WeatherDerivation.cs.
//
// Region palette (SunColor/MoonColor/SkyColor/DustColor) lives on
// RegionData, not here, so a single weather forecast plays out across
// differently-themed regions by recoloring only the palette while the
// weather variables stay the same.
[GlobalClass]
public partial class WeatherData : Resource
{
    // Overcast-ness. 0 = cloudless, 1 = fully covered. Drives cloud
    // QUANTITY (cloudThreshold) on the sky dome, the ambient lift that
    // comes with low cloud, gust rhythm (stormier skies gust harder),
    // and rain drop "heft" (rainWeight = f(cloudCover)).
    [Export(PropertyHint.Range, "0,1,0.01")] public float cloudCover = 0.0f;

    // Steady horizontal wind in m/s. Drives sprite/grass sway, cloud
    // scroll speed, water ripple drift, and rain tilt. Gusts ride on top
    // via a derivation-based amplitude that rises with cloudCover.
    [Export(PropertyHint.Range, "0,40,0.1")] public float windSpeed = 2.0f;

    // Compass heading of the wind in world XZ. Blended between regions
    // via shortest-arc interpolation so a 350° region and a 10° region
    // meet at 0°, not 180°. Y component unused; magnitude ignored
    // (consumers normalize).
    [Export] public Vector3 windDirection = new Vector3(0.7f, 0f, 0.7f);

    // Air temperature in degrees C. Visualization not wired yet —
    // reserved for snow thresholds, sky hue shift, and heat haze.
    [Export] public float temperature = 18.0f;

    // 0 = desert-dry, 1 = saturated / tropical. Raises ambient light
    // and cloud edge softness (translucent clouds), desaturates fills,
    // thickens ambient distance haze, and pulls atmospheric colors
    // toward white.
    [Export(PropertyHint.Range, "0,1,0.01")] public float humidity = 0.5f;

    // Rain drop COUNT. 0 = no rain, 1 = full downpour. Drives both the
    // falling-streak emission rate and the ground-splash rate on
    // RainEffect. Rain drop HEFT (velocity, alpha, streak length,
    // wind susceptibility) is derived from cloudCover.
    [Export(PropertyHint.Range, "0,1,0.01")] public float rainAmount = 0.0f;

    // Atmospheric dust amount — the scattering medium that makes
    // shafts visible. NOT authored: WeatherSimulation writes a
    // simulated value here each frame, derived from the region's
    // authored RegionData.DustAmount (the region max), perturbed by
    // simulated wind / elevation / humidity / rain. Downstream
    // WeatherDerivation reads this current value.
    public float dustAmount = 0.1f;
}
