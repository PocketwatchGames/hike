using Godot;

[GlobalClass]
public partial class LookAtBehaviorData : BehaviorData
{
    // Seconds the mob holds its gaze on the investigation point before
    // returning to the default behavior. Tunable per-brain — 3s reads as
    // "heard something, looked, lost interest" for a non-combatant like a
    // kun-kun without feeling glued in place.
    [Export] public float lookDurationSeconds = 3f;

    public override BehaviorBase CreateRuntime() => new BehaviorLookAt(this);
}
