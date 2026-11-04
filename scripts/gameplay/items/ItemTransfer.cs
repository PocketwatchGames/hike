using System;
using Godot;

// Moves items between any two IItemGrids — the one implementation of "put this
// over there" that every two-sided item screen shares.
public static class ItemTransfer
{
	// Move `count` units onto a chosen slot. Within one member's grids (one grid,
	// or backpack <-> sack) this is Inventory.Move — nothing leaves the inventory.
	// Across owners a whole stack may swap with what is there (the displaced item
	// takes the emptied source slot); a partial one only merges or fills an empty
	// slot. Checked before anything is detached, so a refused move changes nothing.
	public static bool MoveTo(IItemGrid from, int fromIndex, int count, IItemGrid to, int toIndex)
	{
		if (from == null || to == null)
		{
			return false;
		}
		if (from == to)
		{
			return from.MoveWithin(fromIndex, toIndex, count);
		}
		if (from is Inventory.CarriedGrid carriedFrom && to is Inventory.CarriedGrid carriedTo && carriedFrom.owner == carriedTo.owner)
		{
			return carriedFrom.owner.Move(carriedFrom, fromIndex, carriedTo, toIndex, count);
		}
		ItemState source = from.At(fromIndex);
		if (source?.data == null || count <= 0 || toIndex < 0 || toIndex >= to.Capacity)
		{
			return false;
		}
		bool whole = count >= source.stackCount;
		ItemState target = to.At(toIndex);
		bool merges = ItemGrid.Merges(target, source);
		if (!to.Accepts(source.data))
		{
			return false;
		}
		if (merges && target.RemainingStackSpace() <= 0)
		{
			return false;
		}
		if (target != null && !merges && (!whole || !from.Accepts(target.data)))
		{
			return false;
		}
		ItemState moving = from.Take(fromIndex, count);
		ItemState leftover = to.PlaceAt(toIndex, moving, allowSwap: whole, out ItemState displaced);
		Return(from, fromIndex, displaced);
		Return(from, fromIndex, leftover);
		return true;
	}

	// Send `count` units to wherever they fit in `to` — matching stacks first,
	// then the first empty slot. What doesn't fit stays where it was. Returns the
	// units sent.
	public static int Send(IItemGrid from, int fromIndex, int count, IItemGrid to)
	{
		if (from == null || to == null || from == to)
		{
			return 0;
		}
		ItemState source = from.At(fromIndex);
		if (source?.data == null || count <= 0 || !to.CanFullyAdd(source.data, 1))
		{
			return 0;
		}
		ItemState moving = from.Take(fromIndex, Math.Min(count, source.stackCount));
		int taken = moving.stackCount;
		ItemState leftover = to.Add(moving);
		Return(from, fromIndex, leftover);
		return taken - (leftover?.stackCount ?? 0);
	}

	// Equip the stack at `from[fromIndex]`. From one of the member's own grids
	// this is Inventory.Equip (the backpack's top level only); from another grid
	// the stack moves onto the backpack and is equipped there, and what doesn't fit
	// stays where it was.
	public static bool Equip(IItemGrid from, int fromIndex, Inventory inventory)
	{
		if (from == null || inventory == null)
		{
			return false;
		}
		if (OwnedBy(from, inventory))
		{
			return inventory.Equip(from.At(fromIndex));
		}
		ItemState source = from.At(fromIndex);
		if (source?.data == null || !inventory.EquipFits(source.data))
		{
			return false;
		}
		ItemState moving = from.Take(fromIndex, source.stackCount);
		int taken = moving.stackCount;
		ItemState leftover = inventory.EquipIncoming(moving);
		Return(from, fromIndex, leftover);
		return (leftover?.stackCount ?? 0) < taken;
	}

	// Bring the stack at `from[fromIndex]` into the member's backpack and put it on
	// the belt. False, moving nothing, when it doesn't belong on the belt, the belt
	// is full, or the backpack has no slot of its own for it.
	public static bool TakeToBelt(IItemGrid from, int fromIndex, Inventory inventory)
	{
		ItemState source = from?.At(fromIndex);
		if (source?.data == null || inventory == null || !Inventory.Beltable(source.data))
		{
			return false;
		}
		int free = inventory.FirstFreeBeltSlot();
		if (free < 0 || inventory.Backpack.grid.FirstEmpty() < 0)
		{
			return false;
		}
		ItemState moving = from.Take(fromIndex, source.stackCount);
		int index = inventory.Backpack.grid.FirstEmpty();
		ItemState leftover = inventory.Backpack.PlaceAt(index, moving, allowSwap: false, out _);
		Return(from, fromIndex, leftover);
		return inventory.SetBelt(free, inventory.Backpack.At(index));
	}

	// Salvage up to `count` units of the stack at `from[fromIndex]`, with every
	// yield going into `into` (a crafting bench fills the stash). All or nothing:
	// false, changing nothing, when `into` hasn't room for every yield.
	public static bool SalvageInto(IItemGrid from, int fromIndex, int count, Player player, ItemGrid into)
	{
		ItemState source = from?.At(fromIndex);
		if (into == null || count <= 0 || !CanSalvage(source, player))
		{
			return false;
		}
		ItemData data = source.data;
		count = Math.Min(count, source.stackCount);
		var yields = new System.Collections.Generic.List<ItemState>();
		for (int i = 0; i < count; i++)
		{
			data.SalvageYields(yields);
		}
		if (!into.CanFullyAddAll(yields))
		{
			return false;
		}
		yields.Clear();
		for (int i = 0; i < count && CanSalvage(source, player); i++)
		{
			from.Take(fromIndex, 1);
			data.Salvage(player, yields);
		}
		foreach (ItemState state in yields)
		{
			into.Add(state);
		}
		return true;
	}

	// What can be salvaged is the item's say (ItemData.CanSalvage). A filled sack
	// is refused: its contents leave with it, and salvage would destroy them.
	public static bool CanSalvage(ItemState item, Player player)
	{
		return player != null && item?.data != null && item.data.CanSalvage(player) && !SackState.IsFilled(item);
	}

	static bool OwnedBy(IItemGrid grid, Inventory inventory)
	{
		return grid is Inventory.CarriedGrid carried && carried.owner == inventory;
	}

	// Put something that came out of `grid` back: its own slot first (empty after a
	// whole take, or still holding the rest of a partial one), else anywhere.
	static void Return(IItemGrid grid, int index, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		ItemState rest = grid.PlaceAt(index, item, allowSwap: false, out _);
		rest = rest != null ? grid.Add(rest) : null;
		if (rest != null)
		{
			GD.PushError($"ItemTransfer: no room to return {rest.stackCount}x '{rest.data?.ResourcePath}' — it is lost.");
		}
	}
}
