// The equip slots an item can occupy. Member ORDER is wire-stable — the values
// are stored as ints in .tres (ArmorData.armorSlot, WeaponModData.onAttackSlot),
// so rename in place but never reorder. Equipment is the 3-slot active hotbar
// (potions / food / torches); the backpack (materials) has no slot here.
public enum EInventorySlot
{
	None,
	Helmet,
	Armor,
	WeaponMelee,
	WeaponRanged,
	Equipment,
	Count
}
