using Godot;
using Godot.Collections;

[GlobalClass]
public partial class BrainData : Resource
{
    [Export] public StringName idleBehavior;
    [Export] public Array<BehaviorNode> behaviors;
}
