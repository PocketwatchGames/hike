using Godot;

// Tuning for BehaviorStay: a commanded companion holds position until told to
// follow again. No knobs yet — kept as its own data type so the brain author
// picks it from the inspector and so future stay tuning (e.g. a sit anim) has
// a home.
[GlobalClass]
public partial class StayBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorStay(this);
}
