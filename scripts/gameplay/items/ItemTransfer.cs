using System;
using Godot;

// Moves items between any two IItemGrids — the one implementation of "put this
// over there" that every two-sided item screen shares.
public static class ItemTransfer
{
	// Move `count` units onto a chosen slot. Within one grid this is MoveWithin.
	// Across grids a whole stack may swap with what is there (the displaced item
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
		ItemState source = from.At(fromIndex);
		if (source?.data == null || count <= 0 || toIndex < 0 || toIndex >= to.Capacity)
		{
			return false;
		}
		bool whole = count >= source.stackCount;
		ItemState target = to.At(toIndex);
		bool merges = ItemGrid.Merges(target, source);
		if (merges && target.RemainingStackSpace() <= 0)
		{
			return false;
		}
		if (target != null && !merges && !whole)
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
