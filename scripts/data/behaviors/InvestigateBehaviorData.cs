using Godot;

[GlobalClass]
public partial class InvestigateBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorInvestigate(this);
}
