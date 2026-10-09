using Godot;

// Tuning for BehaviorStay: a commanded companion holds position until told to
// follow again.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class StayBehaviorData : BehaviorData
{
    public override BehaviorBase CreateRuntime() => new BehaviorStay(this);
}
