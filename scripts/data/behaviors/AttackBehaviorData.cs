using Godot;

[GlobalClass]
public partial class AttackBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorAttack(this);
}
