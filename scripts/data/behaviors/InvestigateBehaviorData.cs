using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class InvestigateBehaviorData : BehaviorData
{
    public InvestigateBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    public override BehaviorBase CreateRuntime() => new BehaviorInvestigate(this);
}
