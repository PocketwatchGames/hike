using Godot;

// Fires when the player has released the stay command. Drives the
// Stay -> Follow transition.
[GlobalClass]
public partial class CommandedFollowCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return !me.StayCommanded;
    }
}
