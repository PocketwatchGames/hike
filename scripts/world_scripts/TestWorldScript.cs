using Godot;

// test_world's scripted events.
[GlobalClass]
public partial class TestWorldScript : WorldScriptData
{
    // The town gate stays shut and unusable until the party has slept a night.
    // The lock is also the once-only guard: a later sunrise finds it cleared.
    private const string TownGateLocked = "town_gate_locked";
    private const string TownGate = "town_gate";

    public override void OnNewDay(WorldScriptApi api, int day)
    {
        if (api.GetBool(TownGateLocked))
        {
            api.SetBool(TownGateLocked, false);
            api.OpenDoor(TownGate);
        }
    }
}
