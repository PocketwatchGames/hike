using Godot;

[GlobalClass]
public partial class BehaviorNodeTransition : Resource
{
    [Export] public BehaviorTransitionData condition;
    // Name of the target BehaviorNode within the same BrainData.
    [Export] public StringName destination;
}
