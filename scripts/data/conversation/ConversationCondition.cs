using Godot;

// Base class for conversation predicates. Subclass this and override Evaluate
// to add new authoring rules without editing a central enum or switch. Each
// subclass should be a [GlobalClass] so it surfaces in the editor's resource
// picker for ConversationEntry.condition and ConversationResponse.condition.
//
// A null condition slot is treated as "always true" by the runtime — only
// instantiate a subclass when you actually want to gate something.
[GlobalClass]
public partial class ConversationCondition : Resource
{
    public virtual bool Evaluate(ConversationContext ctx)
    {
        GD.PushError($"ConversationCondition subclass '{GetType().Name}' did not override Evaluate");
        return false;
    }
}
