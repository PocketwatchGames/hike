using Godot;

// Switches the speaker's brain into its behavior node named `behavior` — the
// sheet's `behavior:<node>` cell. What happens next is the brain's own authoring
// (an NPC fairy's Escape node vanishes her), so a scripted exit, a walk to a
// spot or a turn to fight is a node in the speaker's brain, not a new action.
//
// Requires ctx.speaker to be a Mob whose brain has that node; errors otherwise.
[GlobalClass]
public partial class SetSpeakerBehaviorAction : ConversationAction
{
    [Export] public StringName behavior;

    public override void Execute(ConversationContext ctx)
    {
        if (ctx.speaker is not Mob mob)
        {
            GD.PushError($"SetSpeakerBehaviorAction: the speaker is not a mob, so it has no '{behavior}' behavior.");
            return;
        }
        mob.ForceBehavior(behavior);
    }
}
