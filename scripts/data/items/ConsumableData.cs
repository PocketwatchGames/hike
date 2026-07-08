using Godot;

[GlobalClass]
public partial class ConsumableData : ItemData
{
	// Action timeline driving Use of this consumable. Same data shape as
	// WeaponData.actionProfile — the runner doesn't distinguish.
	[Export] public ItemActionProfile actionProfile;

	// Optional — shown in place of inventorySprite while ConsumableState.isActive
	// is true (e.g. lit torch). Null falls back to inventorySprite.
	[Export] public Texture2D activeSprite;

	protected override EItemCategory ComputeCategory() => EItemCategory.Equipment;

	public override ItemState CreateState()
	{
		return new ConsumableState(this);
	}
}
