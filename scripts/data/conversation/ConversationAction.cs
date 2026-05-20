using Godot;

// Side effect fired when the conversation traverses an edge — on the entry
// that opens the conversation, or on the response the player picks. Subclass
// this and override Execute to author new action types without editing a
// central enum or switch. Each subclass should be a [GlobalClass] so it
// surfaces in the editor's resource picker for ConversationEntry.actions and
// ConversationResponse.actions.
//
// Actions fire in array order, BEFORE the destination branch's text is
// shown — so an OpenShop action that closes the conversation will suppress
// the unread branch entirely.
//
// Subclasses live in scripts/data/conversation/actions/ (e.g. OpenShopAction,
// GiveItemsAction, SetFlagAction). The runtime calls Execute once per
// traversal; idempotency / repeat-suppression is the subclass's
// responsibility.
[GlobalClass]
public partial class ConversationAction : Resource
{
    public virtual void Execute(ConversationContext ctx)
    {
        GD.PushError($"ConversationAction subclass '{GetType().Name}' did not override Execute");
    }
}
