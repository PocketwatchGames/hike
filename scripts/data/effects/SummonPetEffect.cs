using Godot;

// Toggle-summons a persistent, tamed pet (the dog) from an attuned spell.
// The summoned mob is owned by the triggering SpellState, which desummons it
// when the spell is unattuned: a later cast dismisses a live pet, clears a dead
// one and re-summons, and summons fresh when there is none. Player-only; needs
// a loaded chunk to spawn into. Mirrors DoSummonMinion's spawn but owns
// lifetime on the spell, not a WeaponState, and taming routes the pet through
// the companion follow/persist path rather than the self-draining minion path.
[GlobalClass]
public partial class SummonPetEffect : ItemEffect
{
	[Export] public SpeciesData pet;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (pet == null || actor is not Player)
		{
			return;
		}
		// Lifetime is tracked on the item, so this effect only makes sense on a
		// consumable — a mob or a weapon triggering it is a no-op.
		if (context.primaryItem is not SpellState consumable)
		{
			return;
		}
		Sim sim = Sim.Current;
		if (sim == null)
		{
			return;
		}

		Mob existing = consumable.SummonedPet;
		bool haveLivePet = existing != null && GodotObject.IsInstanceValid(existing) && existing.alive;

		// Clear whatever's owned: a live pet is being dismissed, a corpse is
		// being cleaned up before we call a fresh one.
		consumable.DesummonPet();

		// A live pet toggles off — this Use only desummons.
		if (haveLivePet)
		{
			return;
		}

		// No pet (or the owned one was dead): summon a fresh tamed pet.
		Mob summoned = sim.SpawnMob(pet, ItemEventHandlers.ResolveAimPoint(actor, context));
		if (summoned == null)
		{
			return;
		}
		summoned.Tame();
		consumable.AdoptPet(summoned);
		// Only summoning/resurrecting spends a treat; the desummon branch above
		// returns before here, so putting the dog away is free. The stack is
		// this item's "ammo" — the timeline carries no DecrementStack, so this
		// is the only consume path.
		ItemEventHandlers.ConsumeOneFromStack(actor, consumable);
	}
}
