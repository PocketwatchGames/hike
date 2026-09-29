using Godot;

// Runtime state of an attuned alchemy spell — the persistent cast instance held
// in the single spell slot (Inventory._castInstance / the runner's primaryItem).
// Its "ammo" is however many casts the party reagent pool currently affords, so
// this holds no stack; it only owns the pet a summon spell keeps alive.
public class SpellState : ItemState
{
	public override SpellData data => _data;
	private readonly SpellData _data;

	// The pet this spell summoned (SummonPetEffect), owned for as long as the
	// spell stays attuned: unattuning desummons it. Not persisted, and needs no
	// persistence — attunement clears every sunrise and a save is only written
	// at a sunrise wake, so no summoned pet is ever alive in a save.
	public Mob SummonedPet { get; private set; }

	public SpellState(SpellData d) : base(d)
	{
		_data = d;
	}

	// Take ownership of `pet`. Drops the reference if the pet leaves the tree by
	// any other path (death cleanup, eviction), so a later cast summons fresh
	// rather than dismissing a stale ref.
	public void AdoptPet(Mob pet)
	{
		SummonedPet = pet;
		if (pet == null)
		{
			return;
		}
		pet.TreeExiting += () =>
		{
			if (SummonedPet == pet)
			{
				SummonedPet = null;
			}
		};
	}

	// Hand the owned pet to `other` (the same spell re-attuned on another party
	// member), leaving this instance with nothing to desummon.
	public void TransferPetTo(SpellState other)
	{
		Mob pet = SummonedPet;
		SummonedPet = null;
		other?.AdoptPet(pet);
	}

	// Remove the owned pet from the world, live or dead.
	public void DesummonPet()
	{
		Mob pet = SummonedPet;
		SummonedPet = null;
		if (pet != null && GodotObject.IsInstanceValid(pet))
		{
			pet.Despawn();
		}
	}

	// Attune hooks — fired when this spell is set into / cleared from the slot.
	public virtual void OnEquipped(Player player) { }

	public virtual void OnUnequipped(Player player)
	{
		DesummonPet();
	}
}
