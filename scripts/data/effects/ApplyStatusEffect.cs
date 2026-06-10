using Godot;

// ItemEffect that adds a StatusEffectState to the target actor. Slots into the
// existing ApplyEffect event's `effects` array — same authoring path as
// HealEffect, but the target accumulates ticks over the data's duration
// instead of getting an immediate one-shot HP delta.
[GlobalClass]
public partial class ApplyStatusEffect : ItemEffect
{
	// Fixed effect this event always applies (health potion → Heal). Leave null
	// to instead draw one effect from the using item's possibleStatusEffects
	// menu (fairy corpse → one of its boons), so the applied effect is
	// per-instance state rather than baked into the action data.
	[Export] public StatusEffectData statusEffect;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		StatusEffectData effect = statusEffect ?? PickFromItem(context.primaryItem);
		if (effect == null)
		{
			return;
		}
		if (actor is Player player)
		{
			player.AddStatusEffect(effect);
		}
		else if (actor is Mob mob)
		{
			mob.AddStatusEffect(effect);
		}
	}

	// Select one entry from the item's per-instance possibility menu. Random for
	// now — the fairy's gift is capricious — but this is the seam where the
	// player's eventual choice plugs in (the UI narrows / orders the list and
	// this picks the chosen entry).
	private static StatusEffectData PickFromItem(ItemState item)
	{
		if (item == null || item.possibleStatusEffects.Count == 0)
		{
			return null;
		}
		int index = (int)(GD.Randi() % (uint)item.possibleStatusEffects.Count);
		return item.possibleStatusEffects[index];
	}
}
