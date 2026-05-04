using Godot;

// ItemEffect that adds a StatusEffectState to the target actor. Slots into the
// existing ApplyEffect event's `effects` array — same authoring path as
// HealEffect, but the target accumulates ticks over the data's duration
// instead of getting an immediate one-shot HP delta.
[GlobalClass]
public partial class ApplyStatusEffect : ItemEffect
{
	[Export] public StatusEffectData statusEffect;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (statusEffect == null)
		{
			return;
		}
		if (actor is Player player)
		{
			player.AddStatusEffect(statusEffect);
		}
		else if (actor is Mob mob)
		{
			mob.AddStatusEffect(statusEffect);
		}
	}
}
