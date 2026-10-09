using Godot;

// A carried tool used from the hotbar — the shovel. Unlike a consumable it is
// never spent: its timeline does the work and the item stays in the backpack.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class ToolData : ItemData, IUsableItem
{
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;
}
