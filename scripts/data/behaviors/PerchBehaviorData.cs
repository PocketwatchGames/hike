using Godot;

// Tuning for BehaviorPerch — the grounded/resting state of a flying mob. No
// knobs yet; the bird simply holds its perch (or stands) until a transition
// (e.g. aggro acquired) pulls it into flight.
[GlobalClass]
public partial class PerchBehaviorData : BehaviorData
{
    // While perched and aware of a threat within this HORIZONTAL (2D) distance,
    // the bird periodically sounds an alarm (yell) aimed at the threat. Larger
    // than the flee distance — it calls out before it bolts.
    [Export] public float yellDistance = 20f;
    // Seconds between alarm calls while a threat stays within yellDistance.
    [Export] public float alarmIntervalSeconds = 3f;

    public override BehaviorBase CreateRuntime() => new BehaviorPerch(this);
}
