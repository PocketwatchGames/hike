using Godot;

// Base class for behavior tree transition conditions. Subclass this and override
// Evaluate to add new authoring rules without editing a central enum or switch.
// Each subclass should be a [GlobalClass] so it surfaces in the editor's resource
// picker for BehaviorNodeTransition.condition.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BehaviorTransitionData : Resource
{
    public virtual bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        GD.PushError($"BehaviorTransitionData subclass '{GetType().Name}' did not override Evaluate");
        return false;
    }
}
