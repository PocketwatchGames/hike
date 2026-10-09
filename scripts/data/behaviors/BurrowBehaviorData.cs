using Godot;

// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BurrowBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorBurrow(this);
}
