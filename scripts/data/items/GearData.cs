using Godot;

// Equipment that wears out with use: weapons, armor, shields, and hotbar tools
// (ToolData). GearState holds the live durability; what each kind of use costs
// and what breaking takes away is described on GearState.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class GearData : ItemData
{
	// Weight while equipped. The total over SimData.minWeightPenalty costs max
	// stamina, run/swim speed and dash distance (Player.PenalizedWeight). Mobs
	// ignore it.
	[Export] public float weight = 0f;

	// How much wear this piece survives before it breaks, at a wear multiplier of
	// 1. Weapons count uses (a landed melee hit, a round spent); armor and shields
	// count DAMAGE absorbed (what the pool soaks, what a block or parry stops).
	// 0 = never wears out (mob weapons, the bare-hands fallbacks).
	[Export(PropertyHint.Range, "0,1000,1,or_greater")] public int maxDurability = 0;

	// What restoring this piece from broken to full durability costs at a crafting
	// bench. A partly worn piece pays a share (GearState.ComposeRepairCost). Empty =
	// can't be repaired.
	[Export] public Godot.Collections.Array<MaterialCount> repair = new();
	// In-world hours a bench spends restoring this piece from broken to full; a
	// partly worn piece takes a share (GearState.ComposeRepairHours).
	[Export(PropertyHint.Range, "0,48,0.25,or_greater")] public float repairHours = 1f;
}
