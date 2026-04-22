using Godot;

[GlobalClass]
public partial class SimData : Resource
{
    [Export] public float Gravity = 9.8f;
    [Export] public float VisibleTime = 0.25f;

    [ExportGroup("Time of Day")]
    // Seconds of wall-clock time for a full day/night cycle at time_scale = 1.
    // The time_scale CVar multiplies this advancement for fast-forward testing.
    // Sun/moon orbit shape (max elevation, side sway, sunset band width) is
    // on SkyController since it's visual-authoring state, not simulation.
    [Export] public float DayLengthSeconds = 600f;

    // Normalized time the world starts at: 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Applied when a fresh game is started.
    [Export(PropertyHint.Range, "0,1,0.001")] public float InitialTimeOfDay = 0.3f;
}
