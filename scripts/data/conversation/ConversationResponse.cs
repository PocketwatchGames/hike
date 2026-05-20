using Godot;
using Godot.Collections;

// One player choice attached to a ConversationBranch. Picking it advances the
// conversation to `destination`; an empty destination ends the conversation.
[GlobalClass]
public partial class ConversationResponse : Resource
{
    // Localization key for the player's spoken line / button label. Empty
    // StringName = silent transition (rendered as a generic "continue"
    // prompt rather than echoing a player line).
    [Export] public StringName textLocKey;

    // Optional gate on whether the response is shown at all. Null = always
    // available.
    [Export] public ConversationCondition condition;

    // Name of the next ConversationBranch. Empty StringName ends the
    // conversation.
    [Export] public StringName destination;

    // Side effects fired in order when the player picks this response. Run
    // before the destination branch's text is shown — an action that ends
    // the conversation (e.g. OpenShop) will suppress the unread destination.
    [Export] public Array<ConversationAction> actions;
}
