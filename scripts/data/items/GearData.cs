using Godot;

// Equipment that is carried into a fight and wears out doing it: weapons, armor
// and shields. GearState holds the live durability; what each kind of use costs
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
}
