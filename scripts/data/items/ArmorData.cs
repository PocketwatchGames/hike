using Godot;

[GlobalClass]
public partial class ArmorData : ItemData
{
	[Export] public float maxArmor = 0f;
	[Export] public EInventorySlot armorSlot = EInventorySlot.ArmorBody;

	// Same sign convention as StatusEffectData: positive coldResistance lowers
	// the cold threshold (harder to chill); positive heatResistance raises the
	// hot threshold (harder to overheat). Stacks with status-effect resistances.
	[Export] public float coldResistance = 0f;
	[Export] public float heatResistance = 0f;

	[Export] public override int maxLevel { get; set; } = 5;

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
