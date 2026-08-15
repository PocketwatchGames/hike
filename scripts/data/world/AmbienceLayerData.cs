using Godot;

// One global ambience layer — a looping stream whose volume / pitch is
// driven by a single AmbienceState field through a Curve, optionally
// scaled by a time-of-day curve. One AudioStreamPlayer per layer at
// runtime; AmbienceController instantiates and ticks them.
//
// Authored once per layer-shape (rain-on-leaves, base-wind, gust, insect
// bed, distant ocean, …) and held in ZoneData.ambience. A zone with
// no entry for a given shape simply doesn't play it.
//
// Wind layers are an exception: NO time-of-day curve, entirely sim-
// driven. Set timeOfDayVolume to a flat 1.0 curve in that case (or
// leave null, which is treated the same).
// [Tool] to match ZoneAmbienceData, which holds these.
[Tool]
[GlobalClass]
public partial class AmbienceLayerData : Resource
{
    // The looping stream that plays as long as the volume curve permits.
    // Should be a streaming-format asset (Ogg or MP3) so it doesn't sit
    // fully decoded in memory; loop on the asset itself, not in code.
    [Export] public AudioStream stream;

    // Which AmbienceState field this layer's main curve reads. Wetness
    // for rain layers, WindSpeed for wind layers, etc.
    [Export] public AmbienceField sourceField = AmbienceField.WindSpeed;

    // Volume curve mapping the source field's value (X axis, 0..1) to a
    // linear amplitude (Y axis, 0..1). 0 mutes the layer entirely (the
    // player stops streaming when amplitude hits 0). The curve is the
    // SHAPE of the layer's response — e.g. a wind-rustle layer's curve
    // typically threshold-shapes so it stays silent below a wind floor.
    [Export] public Curve volumeCurve;

    // Pitch curve, same X axis. Y multiplies the stream's playback rate.
    // Defaults to a flat 1.0 if null. Useful for wind layers that should
    // pitch up under heavy gusts.
    [Export] public Curve pitchCurve;

    // Time-of-day envelope. X = TimeOfDay01 (0=sunrise, 0.25=noon, 0.5=sunset,
    // 0.75=midnight, 1=the next sunrise), Y = additional volume multiplier.
    // Insect bed layers gate themselves entirely on this (high near dusk, low
    // at noon). Wind layers should leave this null (treated as flat 1.0) so
    // wind responds to sim, not clock.
    [Export] public Curve timeOfDayVolume;

    // Optional secondary gate field. If set (anything other than the
    // sentinel `Constant` and a non-null gateCurve), the layer's output
    // is multiplied by gateCurve.Sample(state[gateField]). Used to
    // express dependencies the plan calls out — foliage rustle =
    // WindSpeed (primary) × FoliageDensity (gate); rain-on-water =
    // Wetness (primary) × WaterDensity (gate). Default `Constant` +
    // null gateCurve means "no gate, multiplier is 1".
    [Export] public AmbienceField gateField = AmbienceField.Constant;

    [Export] public Curve gateCurve;

    // Master volume multiplier applied after both curves. Per-layer
    // mix knob so designers can balance levels without re-authoring
    // curves; defaults to 1.0.
    [Export(PropertyHint.Range, "0,2,0.01")] public float volumeScale = 1.0f;

    // Bus name to play through. Should match an entry in the bus layout.
    // Global ambience layers default to "Ambience2D" (no reverb send);
    // overrideable per layer if a layer wants to flow through World3D's
    // reverb (e.g. a leaky-into-cave wind tail).
    [Export] public string bus = "Ambience2D";
}
