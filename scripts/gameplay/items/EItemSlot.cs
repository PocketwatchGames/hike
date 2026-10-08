// The equip slots an item can occupy. Member ORDER is wire-stable — the values
// are stored as ints in .tres (ArmorData.armorSlot, WeaponModData.onAttackSlot),
// so rename in place but never reorder.
public enum EInventorySlot
{
	None,
	Helmet,
	Armor,
	WeaponLeft,
	WeaponRight,
	Shield,
	Lantern,
	Count
}
