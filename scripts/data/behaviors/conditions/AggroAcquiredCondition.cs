using Godot;

[GlobalClass]
public partial class AggroAcquiredCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return targetPerception.pawnTarget != null;
    }
}
