using Godot;
using Godot.Collections;

[GlobalClass]
public partial class BehaviorNode : Resource
{
    // Per-brain instance name. Transitions reference sibling nodes by this name.
    [Export] public StringName name;
    [Export] public BehaviorData data;
    [Export] public Array<BehaviorNodeTransition> transitions;
}
