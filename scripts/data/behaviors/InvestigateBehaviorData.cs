using Godot;

[GlobalClass]
public partial class InvestigateBehaviorData : BehaviorData
{
    public InvestigateBehaviorData() { behaviorFlags = EBehaviorFlags.Engaging; }

    public override BehaviorBase CreateRuntime() => new BehaviorInvestigate(this);
}
