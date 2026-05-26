using Godot;

// ItemEffect that removes every active status effect on the target actor whose
// data.tags overlaps `tagMask`. Authored on cure potions and similar — the
// cure-poison potion sets tagMask = Poison so any effect tagged Poison
// (status_poison, status_food_poisoning, future poison-flavored DoTs) is
// cleared in a single sip. Matching buildup meters are also zeroed so a
// partially-charged effect doesn't immediately re-apply after the cure.
[GlobalClass]
public partial class RemoveStatusEffectsEffect : ItemEffect
{
	[Export, CompactFlags] public EStat tagMask;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (tagMask == EStat.None)
		{
			return;
		}
		if (actor is Player player)
		{
			player.RemoveStatusEffectsByTagMask(tagMask);
		}
		else if (actor is Mob mob)
		{
			mob.RemoveStatusEffectsByTagMask(tagMask);
		}
	}
}
