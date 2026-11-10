using Godot;

// Fires when the player has commanded the companion to stay put. Drives the
// Follow -> Stay transition.
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CommandedStayCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        return me.StayCommanded;
    }
}
