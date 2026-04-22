using Godot;

// Static, authored world-level simulation constants. Mutable runtime state
// (TimeOfDay01, WindDirection, ShadowLightDirection, etc.) lives on WorldState
// — `Data` is never used for mutable values (see CLAUDE.md conventions).
[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;
    [Export] public float VisibleTime = 0.25f;

    [ExportGroup("Time of Day")]
    // Seconds of wall-clock time for a full day/night cycle at time_scale = 1.
    // The time_scale CVar multiplies this advancement for fast-forward testing.
    [Export] public float DayLengthSeconds = 600f;

    // Normalized time the world starts at: 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Applied when a fresh game is started.
    [Export(PropertyHint.Range, "0,1,0.001")] public float InitialTimeOfDay = 0.3f;

    // Sun's maximum elevation above the horizon at noon. 90 = sun passes
    // through zenith; lower values tilt the orbit so the sun peaks at a
    // shallower angle (higher-latitude look). Drives both visual sky
    // placement AND the simulation-side ShadowLightDirection that
    // gameplay raycasts (stealth, AI visibility) query.
    [Export(PropertyHint.Range, "10,90,1")] public float SunMaxElevationDegrees = 60f;

    // Horizontal sway of the sun's orbit. The sun sits at -SunSideSwayDegrees
    // yaw at sunrise, 0 at noon, +SunSideSwayDegrees at sunset. 0 locks the
    // sun to a single azimuth (unnatural); 30 reads as a mid-latitude day.
    // Same dual role: visual placement + simulation ShadowLightDirection.
    [Export(PropertyHint.Range, "0,89,1")] public float SunSideSwayDegrees = 30f;

    // The effective horizon — the elevation above geometric 0° at which
    // sources are considered "at sunset/moonrise". Models an occluding
    // horizon line (mountains, tree ring, distant cliffs) so the sun can
    // visually set before it drops below the actual geometric horizon,
    // and the moon can visibly rise into view some minutes before it
    // would astronomically appear. Every horizon fade in SkyController
    // (light energy, shafts, cloud shadows, color blend) is an OFFSET
    // from this angle, and the gameplay `CurrentAmbient` blend pivots
    // on it too.
    [Export(PropertyHint.Range, "0,45,0.5")] public float SunsetAngleDegrees = 15f;

    // Half-width (degrees) of the sunrise/sunset color blend band, measured
    // from SunsetAngleDegrees. The sunset color variants peak when the sun
    // (or moon) is exactly at SunsetAngleDegrees elevation, fade to day
    // colors at SunsetAngleDegrees + this, fade to night colors at
    // SunsetAngleDegrees - this. Also parameterizes the ambient blend
    // that gameplay stealth/perception consumes.
    [Export(PropertyHint.Range, "1,45,0.5")] public float SunsetColorRangeDegrees = 10f;
}
