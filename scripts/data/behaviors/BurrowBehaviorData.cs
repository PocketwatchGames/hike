using Godot;

[GlobalClass]
public partial class BurrowBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorBurrow(this);
}
