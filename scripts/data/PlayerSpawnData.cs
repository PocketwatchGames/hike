using Godot;
using Godot.Collections;

[GlobalClass]
public partial class PlayerSpawnData : Resource
{
	// The player character's display name, shown on the stats panel. A proper
	// name, so it's authored as-is rather than localized (names aren't
	// translated); lives here so a new-game / character-creation flow sets it
	// per run. Blank falls back to the Player's default name.
	[Export] public string playerName = "Wyatt Anderson";

	// Body type the player spawns as. Selects which base character model
	// renders — Player.Initialize looks this up in the player scene's
	// per-gender model map and activates the matching model subtree (each
	// gender shares the skeleton + animation library, so only the visible
	// model swaps). Defaults to Female (0).
	[Export] public EGender gender = EGender.Female;

	// Modular-appearance picks: each is an INDEX into the matching palette on
	// PlayerData (skinTones / hairColors / hairStyles). Player.Initialize
	// resolves them to a flat recolor on the skin / hair meshes and the hair
	// mesh to show. Out-of-range indices clamp to a sane default (see
	// PlayerData.Get*), so a partially-authored spawn never breaks. Defaults
	// (0) pick the first palette entry.
	[Export] public int skinTone;
	[Export] public int hairColor;
	[Export] public int hairStyle;

	// Items the player tries to spawn already equipped. Each entry is added to
	// the inventory and then we attempt to move it into the matching slot:
	// armor → its armorSlot, weapons → their CanonicalSlot (melee → WeaponLeft,
	// ranged → WeaponRight). If the target slot is already taken (or the item
	// isn't equippable), the item stays in the backpack.
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
