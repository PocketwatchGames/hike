// A row of item slots that items can be moved in and out of, whoever owns it —
// a member's belt or backpack (Inventory.CarriedGrid) or the party stash (ItemGrid). ItemTransfer
// moves between any two, so a screen pairing a backpack with a stash, a chest or
// a merchant needs no move logic of its own.
//
// A stack handed IN is caller-owned and detached; whatever a method can't place
// comes back as the leftover (the same object, shrunk), null when everything went
// in. A stored stack is the grid's from then on.
public interface IItemGrid
{
	int Capacity { get; }

	// The stack at `index`, null for an empty or out-of-range slot.
	ItemState At(int index);

	// Detach `count` units from the slot — the stack itself when `count` covers it.
	// Null for an empty slot.
	ItemState Take(int index, int count);

	// Put a detached stack at a slot: an empty slot takes it, a matching stack
	// merges up to its capacity, and anything else is swapped out through
	// `displaced` when `allowSwap`, else refused.
	ItemState PlaceAt(int index, ItemState incoming, bool allowSwap, out ItemState displaced);

	// Merge into matching stacks, then take the first empty slot.
	ItemState Add(ItemState incoming);

	// True when at least `count` units of `data` would fit through Add.
	bool CanFullyAdd(ItemData data, int count);

	// Move `count` units from one slot to another of this grid: onto an empty slot
	// or a matching stack, or — for a whole stack — swapping with what is there.
	// False when nothing moved.
	bool MoveWithin(int from, int to, int count);
}
