using System.Collections.Generic;

// Helpers for the party stash lists (WorldSimState.PartyMaterialStash /
// PartyEquipmentStash) — uncapped List<ItemState> stores shared across the party.
public static class ItemStash
{
	// Add an item to a stash list, merging into an existing same-kind stack (for
	// stackables) before appending the remainder. The passed reference is consumed
	// — after this call `item` may be an emptied stack that was fully merged.
	public static void Add(List<ItemState> stash, ItemState item)
	{
		if (stash == null || item == null || item.data == null || item.stackCount <= 0)
		{
			return;
		}
		if (item.data.IsStackable)
		{
			foreach (ItemState existing in stash)
			{
				if (existing == null || !existing.IsSameKind(item))
				{
					continue;
				}
				int space = existing.RemainingStackSpace();
				if (space <= 0)
				{
					continue;
				}
				int moved = System.Math.Min(space, item.stackCount);
				existing.stackCount += moved;
				item.stackCount -= moved;
				if (item.stackCount <= 0)
				{
					return;
				}
			}
		}
		stash.Add(item);
	}
}
