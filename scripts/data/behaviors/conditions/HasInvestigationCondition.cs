using Godot;

[GlobalClass]
public partial class HasInvestigationCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.investigation.HasValue;
    }
}
