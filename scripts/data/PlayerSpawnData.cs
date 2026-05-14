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

	// Items whose name the player already knows when the run begins. Each
	// entry is seeded into WorldSimState.IdentifiedItems during
	// Player.Initialize so its unidentifiedDisplayName never shows.
	// Items whose ItemData has no placeholder are silently skipped by
	// IdentifyItem; listing them here is harmless.
	[Export] public ItemData[] initiallyIdentifiedItems;
}
