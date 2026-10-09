using Godot;

// Fires when the mob can burrow now and its target is beyond hideRange.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CanBurrowAndOutOfRangeCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        if (!me.CanBurrowNow || targetPerception.pawnTarget == null)
        {
            return false;
        }
        return (targetPerception.pawnTarget.GlobalPosition - me.weaponPosition).Length() > me.mobData.hideRange;
    }
}
