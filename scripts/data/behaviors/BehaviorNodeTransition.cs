using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BehaviorNodeTransition : Resource
{
    [Export] public BehaviorTransitionData condition;
    // Name of the target BehaviorNode within the same BrainData.
    [Export] public StringName destination;
}
