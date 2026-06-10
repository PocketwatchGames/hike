using Godot;

// Fires when the player has commanded the companion to stay put. Drives the
// Follow -> Stay transition.
[GlobalClass]
public partial class CommandedStayCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.StayCommanded;
    }
}
