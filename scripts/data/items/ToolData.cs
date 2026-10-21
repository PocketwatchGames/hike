using Godot;

// A carried tool — the shovel. It has no use of its own: carrying it anywhere
// enables the interactions its ItemData.toolUses name (a Dig prompt), each of
// which wears it, and a broken one enables nothing until repaired.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ToolData : GearData
{
	protected override EItemCategory ComputeCategory() => EItemCategory.Tool;

	public override ItemState CreateState()
	{
		return new GearState(this);
	}
}
