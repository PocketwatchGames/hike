using Godot;

// Tuning for BehaviorStay: a commanded companion holds position until told to
// follow again.
[GlobalClass]
public partial class StayBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorStay(this);
}
