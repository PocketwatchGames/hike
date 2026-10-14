using Godot;
using Godot.Collections;

// What a body bleeds when struck: the substance (colour, stain shapes) and
// how much of it comes out per severity. Shared by every creature that bleeds
// the same way — referenced from MobData.blood and PlayerData.blood.
//
// Severity is decided by the receiver: lethal beats heavy (a crit or a
// backstab) beats an ordinary hit. An unset `heavy` falls back to `hit`, an
// unset `lethal` to `heavy`.
//
// [Tool]: reachable from MobData (a [Tool] class) — see the [Tool]-parent rule
// in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class BloodData : Resource
{
	// Tints the burst scenes' particles (particle_lit's `tint` instance uniform)
	// and the ground stains. Alpha is the stains' starting opacity.
	[Export] public Color color = new Color(0.55f, 0.03f, 0.03f, 0.85f);
	// Picked at random per stain. Shapes only: drawn white, tinted by `color`.
	[Export] public Array<Texture2D> stainTextures = new();

	[Export] public BloodBurstData hit;
	[Export] public BloodBurstData heavy;
	[Export] public BloodBurstData lethal;

	public BloodBurstData PickBurst(bool lethalBlow, EDamageTriggerFlags triggers)
	{
		BloodBurstData heavyOrHit = heavy ?? hit;
		if (lethalBlow)
		{
			return lethal ?? heavyOrHit;
		}
		if ((triggers & (EDamageTriggerFlags.Crit | EDamageTriggerFlags.Backstab)) != 0)
		{
			return heavyOrHit;
		}
		return hit;
	}
}
