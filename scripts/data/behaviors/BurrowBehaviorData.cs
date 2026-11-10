using Godot;

// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BurrowBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorBurrow(this);
}
