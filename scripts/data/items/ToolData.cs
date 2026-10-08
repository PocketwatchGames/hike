using Godot;

// A carried tool used from the hotbar — the shovel. Unlike a consumable it is
// never spent: its timeline does the work and the item stays in the backpack.
[GlobalClass]
public partial class ToolData : ItemData, IUsableItem
{
	[Export] public ItemActionProfile actionProfile;
	public ItemActionProfile ActionProfile => actionProfile;

	protected override EItemCategory ComputeCategory() => EItemCategory.Usable;
}
