// Coarse slot classification — what kind of item this is and therefore which
// equip slot (or store) it belongs to. Exactly one per item, resolved via
// ItemData.Category. Distinct from EItemType, which is a [Flags] taste-tag set
// read by mob preferences; Category drives inventory placement.
public enum EItemCategory
{
	// Sentinel for ItemData.categoryOverride ("derive from the subclass"). Never
	// returned by ItemData.Category itself.
	None = 0,
	WeaponMelee,
	WeaponRanged,
	Armor,
	Helmet,
	// The 3-slot active hotbar: potions, food, scrolls.
	Equipment,
	// Carried in the material-only backpack; cooking ingredients, loot, meat.
	Material,
	// Ammo (arrows): never enters the inventory — a pickup reclaims it straight
	// into the firing weapon. Neither backpack material nor an equip slot.
	Ammo,
	// The player's lantern — its own dedicated equip slot, kept out of the
	// Equipment hotbar. TorchData items resolve to this.
	Lantern,
}
