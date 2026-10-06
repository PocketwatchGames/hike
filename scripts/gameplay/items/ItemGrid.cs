using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// A fixed row of item slots with gaps allowed — the storage behind a member's
// backpack and the party stash. Owns placement only (stacking, swapping,
// splitting); an owner that cares how items enter or leave (Inventory's equip,
// hotbar and spoil bookkeeping) wraps one and implements IItemGrid itself.
public class ItemGrid : IItemGrid
{
	private readonly ItemState[] _slots;

	public ItemGrid(int capacity)
	{
		_slots = new ItemState[Math.Max(0, capacity)];
	}

	public int Capacity => _slots.Length;

	// Slots[i] is the stack at slot i, or null. Count is the capacity.
	public IReadOnlyList<ItemState> Slots => _slots;

	public ItemState this[int index]
	{
		get => _slots[index];
		set => _slots[index] = value;
	}

	public bool InRange(int index) => index >= 0 && index < _slots.Length;

	public ItemState At(int index) => InRange(index) ? _slots[index] : null;

	public int IndexOf(ItemState item)
	{
		if (item == null)
		{
			return -1;
		}
		for (int i = 0; i < _slots.Length; i++)
		{
			if (_slots[i] == item)
			{
				return i;
			}
		}
		return -1;
	}

	public int OccupiedCount()
	{
		int count = 0;
		for (int i = 0; i < _slots.Length; i++)
		{
			if (_slots[i] != null)
			{
				count++;
			}
		}
		return count;
	}

	// The first empty slot at or after `start`, wrapping round to the front.
	public int FirstEmpty(int start = 0)
	{
		for (int k = 0; k < _slots.Length; k++)
		{
			int i = (start + k) % _slots.Length;
			if (_slots[i] == null)
			{
				return i;
			}
		}
		return -1;
	}

	public void Clear()
	{
		Array.Clear(_slots);
	}

	// Same kind is not enough: two swords are the same kind but never one stack.
	public static bool Merges(ItemState into, ItemState from)
	{
		return into?.data != null && into.data.IsStackable && into.CanStackWith(from);
	}

	// Fold as much of `item` as fits into the grid's matching stacks.
	public void MergeIntoStacks(ItemState item)
	{
		if (item?.data == null || !item.data.IsStackable)
		{
			return;
		}
		for (int i = 0; i < _slots.Length && item.stackCount > 0; i++)
		{
			ItemState existing = _slots[i];
			if (existing != item && Merges(existing, item))
			{
				item.TransferTo(existing, existing.RemainingStackSpace());
			}
		}
	}

	// Add, with the empty-slot search starting at `firstSlot` (wrapping) — the
	// backpack starts materials past the hotbar.
	public ItemState Add(ItemState incoming, int firstSlot)
	{
		if (incoming?.data == null || incoming.stackCount <= 0)
		{
			return null;
		}
		MergeIntoStacks(incoming);
		if (incoming.stackCount <= 0)
		{
			return null;
		}
		int index = FirstEmpty(firstSlot);
		if (index < 0)
		{
			return incoming;
		}
		_slots[index] = incoming;
		return null;
	}

	public ItemState Add(ItemState incoming) => Add(incoming, 0);

	public bool CanFullyAdd(ItemData data, int count)
	{
		if (data == null || count <= 0)
		{
			return false;
		}
		int remaining = count;
		int emptySlots = 0;
		for (int i = 0; i < _slots.Length; i++)
		{
			ItemState existing = _slots[i];
			if (existing == null)
			{
				emptySlots++;
			}
			else if (data.IsStackable && existing.data == data)
			{
				remaining -= existing.RemainingStackSpace();
			}
		}
		if (remaining <= 0)
		{
			return true;
		}
		int perSlot = data.IsStackable ? Math.Max(1, data.maxStack) : 1;
		return remaining <= emptySlots * perSlot;
	}

	public ItemState Take(int index, int count)
	{
		ItemState source = At(index);
		if (source == null || count <= 0)
		{
			return null;
		}
		if (count >= source.stackCount)
		{
			_slots[index] = null;
			return source;
		}
		return source.SplitOff(count);
	}

	public ItemState PlaceAt(int index, ItemState incoming, bool allowSwap, out ItemState displaced)
	{
		displaced = null;
		if (incoming?.data == null || incoming.stackCount <= 0)
		{
			return null;
		}
		if (!InRange(index))
		{
			return incoming;
		}
		ItemState target = _slots[index];
		if (target == null)
		{
			_slots[index] = incoming;
			return null;
		}
		if (Merges(target, incoming))
		{
			incoming.TransferTo(target, target.RemainingStackSpace());
			return incoming.stackCount > 0 ? incoming : null;
		}
		if (allowSwap)
		{
			displaced = target;
			_slots[index] = incoming;
			return null;
		}
		return incoming;
	}

	public bool MoveWithin(int from, int to, int count)
	{
		ItemState source = At(from);
		if (source == null || count <= 0 || from == to || !InRange(to))
		{
			return false;
		}
		bool whole = count >= source.stackCount;
		ItemState target = _slots[to];
		if (target == null)
		{
			_slots[to] = whole ? source : source.SplitOff(count);
			if (whole)
			{
				_slots[from] = null;
			}
			return true;
		}
		if (Merges(target, source))
		{
			int moved = source.TransferTo(target, Math.Min(count, target.RemainingStackSpace()));
			if (source.stackCount <= 0)
			{
				_slots[from] = null;
			}
			return moved > 0;
		}
		if (!whole)
		{
			return false;
		}
		(_slots[from], _slots[to]) = (target, source);
		return true;
	}

	// Drop every spoiled cohort; a stack that empties leaves its slot.
	public void PruneExpired(double nowClock)
	{
		for (int i = 0; i < _slots.Length; i++)
		{
			ItemState item = _slots[i];
			if (item == null)
			{
				continue;
			}
			item.PruneExpired(nowClock);
			if (item.stackCount <= 0)
			{
				_slots[i] = null;
			}
		}
	}

	// Inside a shared EntitySerializer table. Slot by slot, gaps included, so the
	// layout survives a save.
	public void Serialize(BinaryWriter w)
	{
		w.Write(_slots.Length);
		for (int i = 0; i < _slots.Length; i++)
		{
			EntitySerializer.WriteItem(w, _slots[i]);
		}
	}

	// Replaces the contents with what the save holds, slot for slot. Returns the
	// items that no longer have a slot (the grid shrank) for the caller to place;
	// an item whose ItemData no longer exists reads back null and is gone.
	public List<ItemState> Restore(BinaryReader r)
	{
		Clear();
		var overflow = new List<ItemState>();
		int count = r.ReadInt32();
		for (int i = 0; i < count; i++)
		{
			ItemState item = EntitySerializer.ReadItem(r);
			if (item == null)
			{
				continue;
			}
			if (i < _slots.Length)
			{
				_slots[i] = item;
			}
			else
			{
				overflow.Add(item);
			}
		}
		return overflow;
	}
}
