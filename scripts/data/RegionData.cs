using Godot;

// Per-region theming. A large world can have many regions (deserts,
// forests, mountains) — each tints its atmosphere through these four
// colors, but the *weather* (cloud cover, wind, rain, etc.) blowing
// across them stays the same. See WeatherDerivation for the full map
// of (region × weather × time-of-day) → visual output.
//
// For now this also owns a nested WeatherData so the 4-quadrant
// bootstrap treats each quadrant as its own weather "biome". When
// dynamic weather is added later, the nested weather becomes the
// climate BASELINE and a global weather system layers on top
// (e.g. a passing storm modulates cloudCover across all regions).
[GlobalClass]
public partial class RegionData : Resource
{
    // Color of direct sunlight at full noon in this region. Derivation
    // pushes it toward amber/red at sunset via dustAmount, and mixes
    // it into the day sky horizon + fog tint.
    [Export] public Color SunColor = new Color(1.0f, 0.96f, 0.88f);

    // Color of direct moonlight. Used at night in the same slot as
    // SunColor. A cool pale blue reads as moonlight without going
    // full grayscale.
    [Export] public Color MoonColor = new Color(0.55f, 0.6f, 0.75f);

    // Sky color at the zenith during full day. Horizon is derived from
    // this plus SunColor + atmospheric haze; night/sunset variants are
    // also derived off this.
    [Export] public Color SkyColor = new Color(0.25f, 0.48f, 0.82f);

    // Atmospheric dust tint. Dust is how shafts take color, how
    // sunsets go warm, and what pulls fog/fill colors toward a regional
    // character (ochre in deserts, cool violet near glaciers).
    // dustAmount (on WeatherData) controls intensity; this controls hue.
    [Export] public Color DustColor = new Color(0.85f, 0.78f, 0.6f);

    // Baseline weather for this region. Becomes the climate baseline
    // once dynamic weather exists.
    [Export] public WeatherData weather;
}
