using Godot;

// Per-zone theming. A large world can have many zones (deserts,
// forests, mountains) — each tints its atmosphere through these four
// colors, but the *weather* (cloud cover, wind, rain, etc.) blowing
// across them stays the same. See WeatherDerivation for the full map
// of (zone × weather × time-of-day) → visual output.
// [Tool] because SkyController is [Tool] and holds one of these as previewZone
// — see the [Tool]-parent rule in the root CLAUDE.md. Everything ZoneData
// references is [Tool] for the same reason.
[Tool]
[GlobalClass]
public partial class ZoneData : Resource
{
    // Color of direct sunlight at full noon in this zone. Derivation
    // pushes it toward amber/red at sunset via dustAmount, and mixes
    // it into the day sky horizon + fog tint.
    [Export] public Color sunColor = new Color(1.0f, 0.96f, 0.88f);

    // Color of direct moonlight. Used at night in the same slot as
    // SunColor. A cool pale blue reads as moonlight without going
    // full grayscale.
    [Export] public Color moonColor = new Color(0.55f, 0.6f, 0.75f);

    // Sky color at the zenith during full day. Horizon is derived from
    // this plus SunColor + atmospheric haze; night/sunset variants are
    // also derived off this.
    [Export] public Color skyColor = new Color(0.25f, 0.48f, 0.82f);

    // Atmospheric dust tint. Dust is how shafts take color, how
    // sunsets go warm, and what pulls fog/fill colors toward a regional
    // character (ochre in deserts, cool violet near glaciers).
    // dustAmount (on WeatherData) controls intensity; this controls hue.
    [Export] public Color dustColor = new Color(0.85f, 0.78f, 0.6f);

    // Water surface color (RGB). Alpha is unused — water "muddiness" /
    // opacity lives on its own WaterOpacity field below so designers
    // can tune color and opacity independently.
    // Deep-water color is derived from this + DustColor via physics
    // (red absorbed first in clear water) and sediment pull (murky
    // water takes on the regional dust color). See
    // WeatherDerivation.Derive().
    [Export] public Color waterColor = new Color(0.3f, 0.45f, 0.5f, 1.0f);

    // Water "muddiness" / weight. Drives surface opacity, how quickly
    // depth goes opaque, ripple damping, wave amplitude damping,
    // whitecap threshold, foam tinting, reflection boost, and
    // refraction fade. Example authored values:
    //   murky swamp      — 0.85
    //   stormy sea       — 0.55
    //   glassy tropical  — 0.25
    //   slow river       — 0.45
    [Export(PropertyHint.Range, "0,1,0.01")] public float waterOpacity = 0.5f;

    // Atmospheric dust amount — the scattering medium that makes
    // shafts visible. Zone-intrinsic (deserts dusty, jungles not),
    // not weather-state, so it lives here rather than on WeatherData.
    // WeatherSimulation reads this as the MAX and outputs a perturbed
    // current value (wind / elevation / humidity / rain modulated).
    // DustColor (above) controls hue; this controls intensity.
    [Export(PropertyHint.Range, "0,1,0.01")] public float dustAmount = 0.1f;

    // Baseline weather for this zone. Becomes the climate baseline
    // once dynamic weather exists.
    [Export] public WeatherData weather;

    // Authored ambience set for this zone — looping global layers
    // (wind / rain / insect bed / distant ocean) plus the positional
    // emitter palette used by ChunkAmbienceSpawner. AmbienceController
    // instantiates one player per global layer per zone and crossfades
    // weights as the player crosses zone borders, the same way the
    // visual palette already blends.
    [Export] public ZoneAmbienceData ambience;

    // Whether the ambient FairySpawner may spawn a fairy while the player stands in
    // this zone. Off by default — turn it on for the zones (forests, glades) where
    // fairies belong. When on, FairySpawnChance modulates how likely each of the
    // day's spawn windows actually produces a fairy here. Read live off the chunk
    // under the player, so it lives on the runtime-loaded ZoneData rather than the
    // worldgen-only ZoneGenData. See FairySpawner.
    [Export] public bool canSpawnFairy = false;
    [Export(PropertyHint.Range, "0,1,0.01")] public float fairySpawnChance = 1f;
}
