using Godot;

[GlobalClass]
public partial class ConsumableData : ItemData
{
	// Action timeline driving Use of this consumable. Same data shape as
	// WeaponData.actionProfile — the runner doesn't distinguish.
	[Export] public ItemActionProfile actionProfile;
}
