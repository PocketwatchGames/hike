public class ConsumableState : ItemState
{
	public override ConsumableData data => _data;
	private readonly ConsumableData _data;

	public bool isActive;

	// Runtime-only ref to the pet this item most recently summoned
	// (SummonPetEffect), so a later Use can desummon a live pet or replace a
	// dead one. Not persisted — the item lives on Inventory, which isn't
	// world-serialized; the summoned pet persists via the companion store.
	public Mob SummonedPet;

	public ConsumableState(ConsumableData d) : base(d)
	{
		_data = d;
	}

	public virtual void OnEquipped(Player player) { }
	public virtual void OnUnequipped(Player player) { }
}
