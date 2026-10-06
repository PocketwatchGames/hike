using Godot;

// Toggle-summons a persistent, tamed pet (the dog). The summoned mob is owned
// by the casting Player (Player.SummonedPet), one pet at a time: a cast of the
// same pet dismisses a live one, and anything else — no pet, a dead one, or a
// pet of another kind — is cleared and replaced by a fresh summon. Player-only;
// needs a loaded chunk to spawn into. Mirrors DoSummonMinion's spawn, but taming
// routes the pet through the companion follow/persist path rather than the
// self-draining minion path.
[GlobalClass]
public partial class SummonPetEffect : ItemEffect
{
	[Export] public SpeciesData pet;

	public override void Apply(IActionActor actor, in ActionContext context)
	{
		if (pet == null || actor is not Player player)
		{
			return;
		}
		Sim sim = Sim.Current;
		if (sim == null)
		{
			return;
		}

		Mob existing = player.SummonedPet;
		bool dismissingSamePet = existing != null && GodotObject.IsInstanceValid(existing) && existing.alive
			&& existing.SimState?.Species == pet;

		player.DismissPet();

		// A live pet of this kind toggles off — this Use only dismisses.
		if (dismissingSamePet)
		{
			return;
		}

		Mob summoned = sim.SpawnMob(pet, ItemEventHandlers.ResolveAimPoint(actor, context));
		if (summoned == null)
		{
			return;
		}
		summoned.Tame();
		player.AdoptPet(summoned);
		// Only summoning spends a charge; the dismiss branch above returns before
		// here, so putting the pet away is free. The timeline carries no
		// DecrementStack, so this is the only consume path.
		ItemEventHandlers.ConsumeOneFromStack(actor, context.primaryItem);
	}
}
