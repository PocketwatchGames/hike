using Godot;

// Fires when the player has released the stay command. Drives the
// Stay -> Follow transition.
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CommandedFollowCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return !me.StayCommanded;
    }
}
