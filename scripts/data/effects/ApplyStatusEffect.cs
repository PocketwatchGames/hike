using System.Collections.Generic;
using Godot;

// ItemEffect that adds a StatusEffectState to the target actor. Slots into the
// existing ApplyEffect event's `effects` array — same authoring path as
// HealEffect, but the target accumulates ticks over the data's duration
// instead of getting an immediate one-shot HP delta.
[GlobalClass]
public partial class ApplyStatusEffect : ItemEffect
{
	// Fixed effect this event always applies (health potion → Heal). Leave null
	// to instead draw one boon from the using item's possibleBoons menu (fairy
	// corpse → one of its boons), so the applied boon is per-instance state
	// rather than baked into the action data.
	[Export] public StatusEffectData statusEffect;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		// Fixed effect — apply it straight away, no choice involved. Consumption
		// is the owning event's separate DecrementStack bit (a health potion is
		// spent the moment its release tick fires).
		if (statusEffect != null)
		{
			ApplyEffectToActor(actor, statusEffect);
			return;
		}

		ItemState item = context.primaryItem;
		if (item == null || item.possibleBoons.Count == 0)
		{
			return;
		}

		// The player gets to choose: hand the item's whole boon menu to the
		// UpgradeScreen via GameClient and apply whichever the player picks.
		// Falls through to the random pick when no selection UI is available
		// (and always for mobs — the fairy's gift to a creature is capricious).
		//
		// Consumption is committed to the player's PICK, not the press: the corpse
		// is spent only when a boon is actually chosen (inside the selection
		// callback), so backing out of the menu leaves it — still unidentified — in
		// the pack. The owning event therefore carries NO DecrementStack bit; we
		// consume here instead.
		if (actor is Player && GameClient.Current?.startUpgradeSelection != null)
		{
			var choices = new List<BoonData>(item.possibleBoons);
			GameClient.Current.startUpgradeSelection.Invoke(choices, chosen =>
			{
				ApplyBoon(actor, chosen);
				ItemEventHandlers.ConsumeOneFromStack(actor, item);
			});
			return;
		}

		// No selection UI (and always for mobs): a random boon is applied and the
		// item consumed immediately — there's no choice to wait on.
		ApplyBoon(actor, PickRandom(item));
		ItemEventHandlers.ConsumeOneFromStack(actor, item);
	}

	// Apply a boon: its status effect (if any) to the actor, and its granted
	// item (if any) into the player's pack. Either half may be absent — gold is
	// item-only, the dash / restore boons are effect-only.
	static void ApplyBoon(IActionActor actor, BoonData boon)
	{
		if (boon == null)
		{
			return;
		}
		ApplyEffectToActor(actor, boon.statusEffect);
		if (boon.grantedItem != null && actor is Player player)
		{
			player.GrantItem(boon.grantedItem);
		}
	}

	static void ApplyEffectToActor(IActionActor actor, StatusEffectData effect)
	{
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

	// Select one entry from the item's per-instance boon menu at random — the
	// fairy's gift is capricious. Used for mobs and as the fallback when no
	// selection UI is available.
	private static BoonData PickRandom(ItemState item)
	{
		if (item == null || item.possibleBoons.Count == 0)
		{
			return null;
		}
		int index = (int)(GD.Randi() % (uint)item.possibleBoons.Count);
		return item.possibleBoons[index];
	}
}
