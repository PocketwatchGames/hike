using Godot;

// Toggle-summons a persistent, tamed pet (the dog) from a reusable consumable.
// The summoned mob is tracked on the triggering ConsumableState so a later Use
// can find it: a live pet is desummoned, a dead one is cleared and re-summoned,
// and no pet at all is summoned fresh. Player-only; needs a loaded chunk to
// spawn into. Mirrors DoSummonMinion's spawn but owns lifetime on the item, not
// a WeaponState, and taming routes the pet through the companion follow/persist
// path rather than the self-draining minion path.
[GlobalClass]
public partial class SummonPetEffect : ItemEffect
{
	[Export] public MobDescriptor pet;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (pet == null || actor is not Player)
		{
			return;
		}
		// Lifetime is tracked on the item, so this effect only makes sense on a
		// consumable — a mob or a weapon triggering it is a no-op.
		if (context.primaryItem is not ConsumableState consumable)
		{
			return;
		}
		Sim sim = Sim.Current;
		if (sim == null)
		{
			return;
		}

		Mob existing = consumable.SummonedPet;
		bool haveExisting = existing != null && GodotObject.IsInstanceValid(existing);
		bool haveLivePet = haveExisting && existing.alive;

		// Clear whatever's tracked: a live pet is being dismissed, a corpse is
		// being cleaned up before we call a fresh one.
		if (haveExisting)
		{
			existing.Despawn();
		}
		consumable.SummonedPet = null;

		// A live pet toggles off — this Use only desummons.
		if (haveLivePet)
		{
			return;
		}

		// No pet (or the tracked one was dead): summon a fresh tamed pet.
		Mob summoned = sim.SpawnMob(pet, ItemEventHandlers.ResolveAimPoint(actor));
		if (summoned == null)
		{
			return;
		}
		summoned.Tame();
		consumable.SummonedPet = summoned;
		// Drop our reference if the pet dies/despawns/evicts by any other path,
		// so the next Use summons fresh rather than desummoning a stale ref.
		summoned.TreeExiting += () =>
		{
			if (consumable.SummonedPet == summoned)
			{
				consumable.SummonedPet = null;
			}
		};
		// Only summoning/resurrecting spends a treat; the desummon branch above
		// returns before here, so putting the dog away is free. The stack is
		// this item's "ammo" — the timeline carries no DecrementStack, so this
		// is the only consume path.
		ItemEventHandlers.ConsumeOneFromStack(actor, consumable);
	}
}
