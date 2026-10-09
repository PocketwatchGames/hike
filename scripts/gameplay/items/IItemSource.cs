// A container a member can use items out of — their own Inventory, or the party
// stash. A use records its source on the ActionContext, and the unit it spends
// comes off through that source, so the container keeps its own bookkeeping
// (Inventory's equip and hotbar state) and no spend has to look for the item.
public interface IItemSource
{
	bool Holds(ItemState item);

	// Take `count` units off `item`, clearing its slot when it empties. No-op for
	// an item this source doesn't hold.
	void Spend(ItemState item, int count);
}
