using Godot;

// Side effect fired at a transition point in a conversation:
//   ConversationEntry.actions               — when the entry opens the conversation
//   ConversationLine.startActions           — as a paragraph starts typing
//   ConversationLine.completeActions        — when the player advances past it
//   ConversationResponse.startActions       — when the response is offered
//   ConversationResponse.completeActions    — when the player picks it
//
// Subclass this and override Execute to author new action types without
// editing a central enum or switch. Each subclass should be a [GlobalClass]
// so it surfaces in the editor's resource picker for every slot.
//
// Actions fire in array order, BEFORE the destination / chooser is built —
// so an OpenShop action that closes the conversation will suppress the
// unread branch or chooser entirely.
//
// The runtime calls Execute once per traversal; idempotency /
// repeat-suppression is the subclass's responsibility.
[GlobalClass]
public partial class ConversationAction : Resource
{
    public virtual void Execute(ConversationContext ctx)
    {
        GD.PushError($"ConversationAction subclass '{GetType().Name}' did not override Execute");
    }
}
