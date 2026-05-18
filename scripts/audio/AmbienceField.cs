// Selects which AmbienceState scalar an AmbienceLayerData remaps into a
// volume / pitch curve. Authored on the resource so the same layer asset
// can be reused (e.g. one wind layer in forest zones and a desert
// zone overrides only the stream + a couple of curves).
//
// Wire values are stable — appended only, never reused, since these
// values land in .tres files via the editor's enum picker. Add new
// fields at the end and never reorder.
public enum AmbienceField : byte
{
    Wetness = 0,
    WindSpeed = 1,
    FoliageDensity = 2,
    WaterDensity = 3,
    ShorelineFactor = 4,
    Openness = 5,
    Caveness = 6,
    FogDensity = 7,
    // 0..1 mirror of the simulated WeatherData.lightningAmount channel,
    // smoothly ramping during a stormy-phase handover (the cloud + rain
    // gates inside WeatherSimulation rise through the crossfade window,
    // so this field naturally fades up rather than popping in). Drives
    // the distant rolling-thunder bed; the future near-strike audio will
    // be event-driven by the lightning hazard, not by this field.
    LightningIntensity = 8,
    // 0..1 mirror of palette.RainIntensity — the SLEWED, FLOORED display
    // value SkyController writes for the rain particle effect, not the
    // raw simulated rainAmount. Routing rain audio through this field
    // keeps the audio in lock-step with the visible rain: same lead
    // time on the way in (visual + audio both ramp up before simRain
    // crosses any threshold), same minimum-visible floor (a barely-
    // rainy phase still produces an obviously-audible drizzle), same
    // ramp-down. Wetness is still the right source for residual /
    // ambient audio that should outlast the rain (rain-on-leaves,
    // dripping); RainIntensity is the right source for the rain itself.
    RainIntensity = 9,

    // Constant 1.0 — for layers whose volume should only depend on the
    // time-of-day curve, not on a state field. Insect bed / distant
    // ocean fall in this bucket.
    Constant = 255,
}
