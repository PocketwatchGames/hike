using Godot;
using Godot.Collections;

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

	// Things the player already knows about when the run begins. Each
	// entry is a TeachableConcept subclass — ItemTeachable identifies an
	// item by name, RecipeTeachable seeds a recipe into the cookbook,
	// LanguageTeachable grants language components, RegionTeachable
	// reveals a map region, MobTeachable seeds a bestiary entry. Applied
	// via the same Teach() path that scrolls / NPC rewards use, so a
	// "starter pack" of knowledge composes the same way mid-run rewards
	// do. Announcements are suppressed during initial application (see
	// GameClient.SuppressAnnouncements) — the player shouldn't see a
	// stack of banners on the first frame.
	[Export] public Array<TeachableConcept> initialKnowledge = new();
}
