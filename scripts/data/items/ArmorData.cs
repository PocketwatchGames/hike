using Godot;

[GlobalClass]
public partial class ArmorData : ItemData
{
	[Export] public float maxArmor = 0f;
	[Export] public EInventorySlot armorSlot = EInventorySlot.ArmorBody;

	// Stat modifications granted while this piece is equipped. Composed with
	// the wearer's inherent modifiers and active status effects when the
	// actor queries any stat. Authoring examples:
	//   { ColdResist,   +10  } — leather lining (additive threshold shift)
	//   { Camouflage,   +5   } — wolf cloak (additive sense bonus)
	//   { Noise,         0.8 } — padded boots (multiplicative)
	//   { Pierce,        0.5 } — chainmail (halves pierce-bypass chance)
	//   { Fire,          0.5 } — fire-warded plate (halves fire damage)
	[Export] public Godot.Collections.Array<StatModifier> modifiers;

	// Names of the MeshInstance3D parts this piece shows on the player's 3D
	// model while equipped (the worn-mesh path referenced by ItemData.heldModel).
	// They live on the shared polysplit rig and replace that slot's bare-body
	// default — a body piece names its torso+legs outfit meshes, a head piece
	// its helmet/hood. Player.UpdateArmorVisual composites these per slot.
	// Empty = the slot falls back to the bare body (no visual change on equip).
	[Export] public string[] wornMeshNames = System.Array.Empty<string>();

	[Export] public override int maxLevel { get; set; } = 5;

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
