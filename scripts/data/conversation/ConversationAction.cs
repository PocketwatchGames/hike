using Godot;

// Side effect fired at a transition point in a conversation — three slots:
//   ConversationEntry.actions   — when the entry opens the conversation
//   ConversationBranch.endActions — when the typewriter finishes a branch,
//                                   before the response chooser shows
//   ConversationResponse.actions  — when the player picks the response
//
// Subclass this and override Execute to author new action types without
// editing a central enum or switch. Each subclass should be a [GlobalClass]
// so it surfaces in the editor's resource picker for all three slots.
//
// Actions fire in array order, BEFORE the destination / chooser is built —
// so an OpenShop action that closes the conversation will suppress the
// unread branch or chooser entirely.
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
