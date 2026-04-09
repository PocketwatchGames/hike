using Godot;

[GlobalClass]
public partial class PerceptionZeroCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.perception == 0;
    }
}
