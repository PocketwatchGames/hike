using System;
using Godot;

// Moves items between any two IItemGrids — the one implementation of "put this
// over there" that every two-sided item screen shares.
public static class ItemTransfer
{
	// Move `count` units onto a chosen slot. Within one member's grids (one grid,
	// or belt <-> backpack) this is Inventory.Move — nothing leaves the inventory.
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

	// Equip the stack at `from[fromIndex]` — the whole of it, gear never stacks.
	// From one of the member's own grids this is Inventory.Equip; from another grid
	// the gear it displaces takes the emptied slot.
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
		if (source?.data == null || !source.data.IsEquippable)
		{
			return false;
		}
		ItemState moving = from.Take(fromIndex, source.stackCount);
		Return(from, fromIndex, inventory.EquipIncoming(moving));
		return true;
	}

	// Move the stack at `from[fromIndex]` onto the member's belt — as much as the
	// belt alone has room for; the rest stays where it was. Returns the units moved.
	public static int SendToBelt(IItemGrid from, int fromIndex, Inventory inventory)
	{
		ItemState source = from?.At(fromIndex);
		if (source?.data == null || inventory == null)
		{
			return 0;
		}
		int units = Math.Min(source.stackCount, inventory.Belt.RoomFor(source.data));
		if (units <= 0)
		{
			return 0;
		}
		ItemState moving = from.Take(fromIndex, units);
		int taken = moving.stackCount;
		ItemState leftover = inventory.AddOnly(inventory.Belt, moving);
		Return(from, fromIndex, leftover);
		return taken - (leftover?.stackCount ?? 0);
	}

	// Unequip onto a chosen slot of `to`: an empty slot takes it, and gear for
	// the same equip slot swaps in. False for anything else.
	public static bool Unequip(Inventory inventory, EInventorySlot slot, IItemGrid to, int toIndex)
	{
		if (inventory == null || to == null)
		{
			return false;
		}
		if (OwnedBy(to, inventory))
		{
			return inventory.UnequipTo(slot, (Inventory.CarriedGrid)to, toIndex);
		}
		ItemState item = inventory.GetEquipped(slot);
		if (item == null || toIndex < 0 || toIndex >= to.Capacity)
		{
			return false;
		}
		ItemState target = to.At(toIndex);
		if (target != null)
		{
			return target.data?.EquipSlotKind == slot && Equip(to, toIndex, inventory);
		}
		inventory.Remove(item);
		Return(to, toIndex, item);
		return true;
	}

	// Send equipped gear to wherever it fits in `to`. False when it doesn't.
	public static bool SendEquipped(Inventory inventory, EInventorySlot slot, IItemGrid to)
	{
		if (inventory == null || to == null)
		{
			return false;
		}
		if (OwnedBy(to, inventory))
		{
			return inventory.Unequip(slot);
		}
		ItemState item = inventory.GetEquipped(slot);
		if (item?.data == null || !to.CanFullyAdd(item.data, item.stackCount))
		{
			return false;
		}
		inventory.Remove(item);
		Return(to, -1, to.Add(item));
		return true;
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
