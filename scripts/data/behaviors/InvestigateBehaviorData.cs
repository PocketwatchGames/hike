using Godot;

// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class InvestigateBehaviorData : BehaviorData
{
    public InvestigateBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    public override BehaviorBase CreateRuntime() => new BehaviorInvestigate(this);
}
