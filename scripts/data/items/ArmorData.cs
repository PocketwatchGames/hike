using Godot;

[GlobalClass]
public partial class ArmorData : ItemData
{
	[Export] public float maxArmor = 0f;
	[Export] public EInventorySlot armorSlot = EInventorySlot.Armor;

	protected override EItemCategory ComputeCategory() => armorSlot == EInventorySlot.Helmet ? EItemCategory.Helmet : EItemCategory.Armor;

	// Stat modifications granted while this piece is equipped. Composed with
	// the wearer's inherent modifiers and active status effects when the
	// actor queries any stat. Authoring examples:
	//   { ColdResist,   +10  } — leather lining (additive threshold shift)
	//   { Camouflage,   +5   } — wolf cloak (additive sense bonus)
	//   { Noise,         0.8 } — padded boots (multiplicative)
	//   { ArmorPenetration, 0.5 } — chainmail (halves armor-penetration-bypass chance)
	//   { Fire,          0.5 } — fire-warded plate (halves fire damage)
	[Export] public Godot.Collections.Array<StatModifier> modifiers;
	// Managed read-mirror of `modifiers` — see MobData.ModifiersFlat. Folded
	// per equipped slot on every Player.ComposeStat call.
	private StatModifier[] _modifiersFlat;
	public StatModifier[] ModifiersFlat => _modifiersFlat ??= StatModifierUtil.Flatten(modifiers);

	// The outfit shown on the player's 3D model while this piece is equipped —
	// a key into PlayerData.outfits, the central mesh-name registry. A body
	// piece draws the outfit's body meshes, a head piece its head meshes; empty
	// = no visual change on equip (the slot falls back to the class outfit /
	// bare body).
	[Export] public StringName outfit;

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
