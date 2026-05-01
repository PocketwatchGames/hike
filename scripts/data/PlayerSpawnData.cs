using Godot;

[GlobalClass]
public partial class PlayerSpawnData : Resource
{
	[Export] public WeaponData meleeWeaponData;
	[Export] public WeaponData rangedWeaponData;

	// Items added to the player's inventory and pushed into consumable hotbar
	// slots in order. Each is created at maxStack.
	[Export] public ConsumableData[] startingConsumables;

	// Items added to the player's backpack at spawn. Each is created at
	// maxStack. No equip / hotbar placement.
	[Export] public ItemData[] startingInventory;
}
