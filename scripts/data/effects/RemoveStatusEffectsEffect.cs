using Godot;

// ItemEffect that removes every active status effect on the target actor whose
// data.tags overlaps `tagMask`. Authored on cure potions and similar — the
// cure-poison potion sets tagMask = Poisoned so every effect of that family
// (status_poison, status_food_poisoning) is cleared in a single sip. Matching buildup meters are also zeroed so a
// partially-charged effect doesn't immediately re-apply after the cure.
[GlobalClass]
public partial class RemoveStatusEffectsEffect : ItemEffect
{
	[Export, CompactFlags] public EHitTag tagMask;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (tagMask == EHitTag.None)
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
