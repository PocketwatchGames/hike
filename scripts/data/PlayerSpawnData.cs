using Godot;
using Godot.Collections;

[GlobalClass]
public partial class PlayerSpawnData : Resource
{
	// Items the player tries to spawn already equipped. Each entry is added to
	// the inventory and then we attempt to move it into the matching slot:
	// armor → its armorSlot, weapons → WeaponLeft else WeaponRight. If the
	// target slot is already taken (or the item isn't equippable), the item
	// stays in the backpack.
	[Export] public ItemCount[] equippedInventory;

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
