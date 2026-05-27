using Godot;

// Fires when the mob has a triggered (aware-of) target AND that target is
// within `distance` (3D). The proximity inverse of CanBurrowAndOutOfRange:
// used to gate startle/flee transitions on closeness, so a resting animal only
// bolts when the threat actually closes in and stays put once it's a safe
// distance away. A perched bird uses this for Perch -> FlyFlee so it doesn't
// keep fleeing from a player it can see but that's already far off.
[GlobalClass]
public partial class ThreatWithinDistanceCondition : BehaviorTransitionData
{
    [Export] public float distance = 10f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        if (targetPerception.pawnTarget == null)
        {
            return false;
        }
        return (targetPerception.pawnTarget.GlobalPosition - me.GlobalPosition).LengthSquared() < distance * distance;
    }
}
