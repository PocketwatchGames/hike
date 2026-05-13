using Godot;

[GlobalClass]
public partial class PlayerSpawnData : Resource
{
	[Export] public WeaponData meleeWeaponData;
	[Export] public WeaponData rangedWeaponData;

	// Items added to the player's inventory and pushed into consumable hotbar
	// slots in order. Each is created at maxStack.
	[Export] public ConsumableData[] startingConsumables;

	// Items added to the player's backpack at spawn. Each entry's count is
	// split into maxStack-sized stacks. No equip / hotbar placement.
	[Export] public ItemCount[] startingInventory;
}
