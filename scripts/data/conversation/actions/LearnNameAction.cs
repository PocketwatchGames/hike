using Godot;

// The speaker introduces themselves: the party learns the name of the character
// whose conversation this is, and the panel's name box switches from their
// description to their name. Knowledge like any other - provisional until the
// party next camps.
[GlobalClass]
public partial class LearnNameAction : ConversationAction
{
    public override void Execute(ConversationContext ctx)
    {
        ctx.sim?.WorldState?.SimState?.LearnName(ctx.conversation);
    }
}
