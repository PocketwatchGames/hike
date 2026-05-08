using Godot;

// Mutable weather simulation state. Authored fields on ZoneData.weather
// represent the zone's MAX for each channel (the daytime peak / clear-
// air ceiling); WeatherSimulation perturbs a per-frame working copy in
// place to produce the values actually IN EFFECT right now (diurnal
// curve + 12-hour weather variance). All visual consequences (sky/fog/
// cloud/shaft colors, fill tints, light intensities, cloud shape, gust
// rhythm, ripples, dust density, etc.) are DERIVED by
// WeatherDerivation.Derive() from this + a ZoneData palette + time-
// of-day. See also scripts/client/WeatherSimulation.cs and
// scripts/client/WeatherDerivation.cs.
//
// Zone palette (SunColor/MoonColor/SkyColor/DustColor) lives on
// ZoneData, not here, so a single weather forecast plays out across
// differently-themed zones by recoloring only the palette while the
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

    // Ambient air temperature in degrees F (the value WeatherSimulation
    // perturbs each frame; same role the pre-split `temperature` field had).
    // GameClient.SampleAirTemperature returns this plus the sun's radiant
    // contribution at the player's position, gated by sun angle and shade.
    [Export] public float airTemperature = 64.4f;

    // Additional degrees F that direct sunlight contributes on top of
    // airTemperature when the sun is above the horizon and the sample
    // point is unshaded. Scaled by the sun's elevation factor (sin of
    // elevation angle), so it tapers smoothly into / out of night.
    [Export] public float sunTemperature = 15f;

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
    // simulated value here each frame, derived from the zone's
    // authored ZoneData.DustAmount (the zone max), perturbed by
    // simulated wind / elevation / humidity / rain. Downstream
    // WeatherDerivation reads this current value.
    public float dustAmount = 0.1f;
}
