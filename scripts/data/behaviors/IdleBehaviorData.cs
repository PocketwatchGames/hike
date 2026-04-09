using Godot;

[GlobalClass]
public partial class IdleBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorIdle(this);
}
