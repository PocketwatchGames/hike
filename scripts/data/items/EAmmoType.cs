// The kind of round a ranged weapon fires (WeaponData.ammoType) and an AmmoData
// is. Stored as an int in .tres, so append only — never reorder.
public enum EAmmoType
{
	// Needs no ammo: a sling, a summoner, every mob weapon.
	None = 0,
	Arrow,
	Ball,
	Shot,
	Dart,
	Count,
}
