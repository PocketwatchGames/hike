using Godot;

// Optional loot behavior: the world pickup is hidden and non-interactive
// outside an authored time-of-day window, and during it emerges from the ground
// by scaling up from zero and rising to its rest pose — becoming interactive
// only once that rise completes — then retracts back into the ground when the
// window ends. Authored as a sub-resource on LootData (null = ordinary
// always-present loot) so it sits with the other world-on-ground loot dynamics.
// See Loot's emergence section for the runtime.
[GlobalClass]
public partial class TimedEmergenceData : Resource
{
    // Time-of-day window (normalized [0,1): 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset) during which the loot is emerged. The window
    // wraps midnight when Begin > End, so the defaults below mean "night"
    // (sunset 0.75 -> sunrise 0.25); set 0.25/0.75 for daytime, 0.25/0.4 for
    // morning, 0.95/0.05 for around midnight, etc. Begin == End = never emerges.
    [Export(PropertyHint.Range, "0,1,0.001")] public float beginTimeOfDay = 0.75f;
    [Export(PropertyHint.Range, "0,1,0.001")] public float endTimeOfDay = 0.25f;

    // Seconds the scale-up + rise (and the reverse retract) takes. Runs on the
    // sim clock (GameTimeMs), so it slows under slow-mo and gates interactivity
    // deterministically — the pickup only becomes grabbable after this elapses.
    [Export] public float emergeSeconds = 2f;

    // How far below the rest pose (metres) the visual starts when buried, so it
    // rises this distance out of the ground as it scales in.
    [Export] public float riseDistance = 0.5f;

    // Per-instance random delay window (game seconds) applied after the window
    // opens/closes before THIS pickup begins emerging/retracting, so a patch
    // doesn't pop in unison. Each instance rolls a delay in [0, StaggerSeconds].
    [Export] public float staggerSeconds = 20f;

    // True when the given normalized time-of-day falls inside the emerge window,
    // accounting for windows that wrap past midnight (Begin > End).
    public bool Contains(double timeOfDay01)
    {
        if (Mathf.IsEqualApprox(beginTimeOfDay, endTimeOfDay))
        {
            return false;
        }
        if (beginTimeOfDay < endTimeOfDay)
        {
            return timeOfDay01 >= beginTimeOfDay && timeOfDay01 < endTimeOfDay;
        }
        return timeOfDay01 >= beginTimeOfDay || timeOfDay01 < endTimeOfDay;
    }
}
