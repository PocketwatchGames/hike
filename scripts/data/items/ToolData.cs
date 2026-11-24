using Godot;

// A tool used from the hotbar — the shovel. Belted by hand and run through its
// actionProfile timeline (the Dig event); never spent, it wears by its
// ItemData.toolUses row instead, and a broken one can't be used until repaired.
// [Tool]: in the item closure (ItemEvent → ItemData) — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ToolData : GearData, IUsableItem
{
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	protected override EItemCategory ComputeCategory() => EItemCategory.Tool;

	public override ItemState CreateState()
	{
		return new GearState(this);
	}
}
