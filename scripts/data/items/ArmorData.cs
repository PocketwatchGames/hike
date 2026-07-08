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

	// Names of the MeshInstance3D parts this piece shows on the player's 3D
	// model while equipped, replacing that slot's bare-body default — a body
	// piece names its torso+legs outfit meshes, a head piece its helmet/hood.
	// Player.UpdateArmorVisual composites these per slot; empty = the slot falls
	// back to the bare body (no visual change on equip).
	//
	// Split by gender because the two rigs prefix their parts differently (Female
	// F_, Male M_) and the outfits don't map by a simple prefix swap (the Male
	// Mage has no cape, the Male Knight no skirt), so each set names its own rig's
	// meshes. GetWornMeshNames resolves the live gender; an empty set for a gender
	// leaves that rig on its bare body (author both to dress both bodies).
	[Export] public string[] wornMeshNamesFemale = System.Array.Empty<string>();
	[Export] public string[] wornMeshNamesMale = System.Array.Empty<string>();

	// The worn-mesh set for the spawned body type. Empty when this piece isn't
	// authored for that gender — the compositor then keeps the bare body.
	public string[] GetWornMeshNames(EGender gender)
	{
		return gender == EGender.Male ? wornMeshNamesMale : wornMeshNamesFemale;
	}

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
