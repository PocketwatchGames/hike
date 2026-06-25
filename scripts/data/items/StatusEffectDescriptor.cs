using Godot;

// Scope of a status effect acting as a WEAPON MODIFIER: does it change every
// attack the weapon makes, or only one specific charge tier? Lives on the
// per-composition StatusEffectDescriptor (not on the shared StatusEffectData),
// so the same effect can be all-attacks on one weapon and charge-scoped on
// another. See StatusEffectDescriptor.scope and StatusEffectController.
public enum EWeaponModScope
{
	AllAttacks = 0,
	SpecificCharge = 1,
}

// Pairs a weapon-modifier StatusEffectData with the scope that decides which of
// the weapon's attacks it modifies. Authored inside an ItemDescriptor's
// `statusEffects` list so a spawn source (WorldGenData loadout, loot table) can bolt
// a mod onto an item without baking it into the shared WeaponData — e.g. a
// "Fragile" bomb (all attacks) or a "Piercing" bow whose charged shot punches
// through several foes. Composed onto the item's `statusEffects` controller at
// creation; the scope + chargeIndex travel onto the live StatusEffectState so
// the firing path (ItemEventHandlers.DoProjectile) can filter by charge tier.
[GlobalClass]
public partial class StatusEffectDescriptor : Resource
{
	[Export] public StatusEffectData effect;

	// Does this mod affect every attack the weapon makes, or only one charge
	// tier? Default AllAttacks matches a weapon-global mod (Fragile).
	[Export] public EWeaponModScope scope = EWeaponModScope.AllAttacks;

	// Which charge tier — an index into the weapon's
	// ItemActionProfile.chargedActions — this mod applies to when
	// scope == SpecificCharge. Ignored for AllAttacks. The bow's charged
	// (heavy) shot is index 1.
	[Export] public int chargeIndex = 0;
}
