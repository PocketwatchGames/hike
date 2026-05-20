using Godot;
using Godot.Collections;

// One (condition, branch-name) pair used in ConversationData.entryBranches.
// A null condition is treated as "always valid" — use this for the fallback
// entry at the end of the list.
[GlobalClass]
public partial class ConversationEntry : Resource
{
    [Export] public ConversationCondition condition;
    // Name of a ConversationBranch in the same ConversationData.
    [Export] public StringName branch;
    // Side effects fired in order when this entry opens the conversation.
    // Run before the destination branch's text is shown.
    [Export] public Array<ConversationAction> actions;
}
