// Coarse slot classification — what kind of item this is and therefore which
// equip slot it fills (if any) and how it is picked up. Exactly one per item,
// resolved via ItemData.Category. Distinct from EItemType, which is a [Flags]
// taste-tag set read by mob preferences. Stored as an int in .tres
// (ItemData.categoryOverride), so rename in place but never reorder.
public enum EItemCategory
{
	// Sentinel for ItemData.categoryOverride ("derive from the subclass"). Never
	// returned by ItemData.Category itself.
	None = 0,
	WeaponLeft,
	WeaponRight,
	Armor,
	Helmet,
	// Used rather than equipped: potions and spells from the hotbar, scrolls on
	// pickup.
	Usable,
	// Cooking ingredients, loot, meat — the reagent pool for cooking and spells,
	// and the only category picked up on contact. Lands past the hotbar.
	Material,
	// Rounds for a ranged weapon (AmmoData): carried in the backpack, never
	// equipped — loaded per type instead (Inventory.LoadedAmmo).
	Ammo,
	Lantern,
	Shield,
	// Carried vessels (flasks): kept in the backpack, never a cooking reagent and
	// never picked up on contact.
	Container,
	// A sack (SackData): carried in the belt or backpack, holding the categories it
	// lists. Never a reagent, and never inside another sack.
	Sack,
	// A carried tool (ToolData — the shovel): kept in the backpack and never used
	// from it. Carrying it is what enables its interactions (ItemData.toolUses).
	Tool,
}
