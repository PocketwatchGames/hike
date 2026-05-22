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

	// Sense modifiers. Camouflage is additive (summed across equipped armor
	// + active status effects) — a positive value makes the player harder to
	// spot. Vision / hearing / noise / scent are multiplicative scalars on
	// the PlayerData base value; 1.0 is neutral, <1 reduces, >1 increases.
	// Noise and scent reductions make the player quieter / less detectable;
	// vision / hearing reductions narrow the player's own senses. Stacks
	// multiplicatively with the matching StatusEffectData fields via
	// Player.GetSenseStats.
	[Export] public float camouflage = 0f;
	[Export] public float visionMultiplier = 1f;
	[Export] public float hearingMultiplier = 1f;
	[Export] public float noiseMultiplier = 1f;
	[Export] public float scentMultiplier = 1f;

	[Export] public override int maxLevel { get; set; } = 5;

	public override ItemState CreateState()
	{
		return new ArmorState(this);
	}
}
