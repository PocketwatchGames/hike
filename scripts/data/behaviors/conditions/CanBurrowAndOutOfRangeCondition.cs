using Godot;

// Replaces the legacy ToBurrow rule:
//   mobData.canBurrow && pawnTarget != null && distance(pawnTarget, weaponPosition) > hideRange
// TODO: split into composable AndCondition + CanBurrowCondition + OutOfRangeCondition
// once a second condition needs the same primitives.
[GlobalClass]
public partial class CanBurrowAndOutOfRangeCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        if (!me.mobData.canBurrow || !me.CanBurrowNow || targetPerception.pawnTarget == null)
        {
            return false;
        }
        return (targetPerception.pawnTarget.GlobalPosition - me.weaponPosition).Length() > me.mobData.hideRange;
    }
}
