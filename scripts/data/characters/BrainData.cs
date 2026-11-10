using Godot;
using Godot.Collections;

// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BrainData : Resource
{
    [Export] public StringName idleBehavior;
    [Export] public Array<BehaviorNode> behaviors;
}
