// Runtime state of an attuned alchemy spell — the persistent cast instance held
// in the single spell slot (Inventory._castInstance / the runner's primaryItem).
// Its "ammo" is however many casts the party reagent pool currently affords, so
// this holds no stack; it only tracks the pet a summon spell keeps alive.
public class SpellState : ItemState
{
	public override SpellData data => _data;
	private readonly SpellData _data;

	// Runtime-only ref to the pet this spell most recently summoned
	// (SummonPetEffect), so a later cast can desummon a live pet or replace a
	// dead one. Not persisted — the attuned spell lives on Inventory, which
	// isn't world-serialized; the summoned pet persists via the companion store.
	public Mob SummonedPet;

	public SpellState(SpellData d) : base(d)
	{
		_data = d;
	}

	// Attune hooks — fired when this spell is set into / cleared from the slot.
	public virtual void OnEquipped(Player player) { }
	public virtual void OnUnequipped(Player player) { }
}
