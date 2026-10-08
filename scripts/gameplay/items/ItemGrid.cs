using System;
using System.Collections.Generic;
using System.IO;
using Godot;

// A row of item slots with gaps allowed — the storage behind a member's belt and
// backpack and the party stash. Owns placement only (stacking, swapping,
// splitting); an owner that cares how items enter or leave (Inventory's equip,
// hotbar and spoil bookkeeping) wraps one and implements IItemGrid itself.
public class ItemGrid : IItemGrid
{
	private ItemState[] _slots;

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

	// Change the slot count. Growing adds empty slots at the end; shrinking cuts
	// the trailing ones, and their items are appended to `evicted` for the owner
	// to rehome — a grid has nowhere to put them itself.
	public void Resize(int capacity, List<ItemState> evicted)
	{
		capacity = Math.Max(0, capacity);
		if (capacity == _slots.Length)
		{
			return;
		}
		for (int i = capacity; i < _slots.Length; i++)
		{
			if (_slots[i] != null)
			{
				evicted.Add(_slots[i]);
			}
		}
		Array.Resize(ref _slots, capacity);
	}

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

	// Merge into matching stacks, then take the first empty slot.
	public ItemState Add(ItemState incoming)
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
		int index = FirstEmpty();
		if (index < 0)
		{
			return incoming;
		}
		_slots[index] = incoming;
		return null;
	}

	public bool CanFullyAdd(ItemData data, int count)
	{
		return data != null && count > 0 && RoomFor(data) >= count;
	}

	// How many units of `data` Add could take: the space left in matching stacks
	// plus a full stack per empty slot.
	public int RoomFor(ItemData data)
	{
		if (data == null)
		{
			return 0;
		}
		int perSlot = data.IsStackable ? Math.Max(1, data.maxStack) : 1;
		int room = 0;
		for (int i = 0; i < _slots.Length; i++)
		{
			ItemState existing = _slots[i];
			if (existing == null)
			{
				room += perSlot;
			}
			else if (data.IsStackable && existing.data == data)
			{
				room += existing.RemainingStackSpace();
			}
		}
		return room;
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

	public bool MoveWithin(int from, int to, int count) => Move(this, from, this, to, count);

	// Move `count` units from a slot of `src` to a slot of `dst` (the same grid or
	// another): onto an empty slot or a matching stack, or — for a whole stack —
	// swapping with what is there. Placement only, for an owner moving between
	// grids it holds both of. False when nothing moved.
	public static bool Move(ItemGrid src, int from, ItemGrid dst, int to, int count)
	{
		ItemState source = src?.At(from);
		if (source == null || dst == null || count <= 0 || (src == dst && from == to) || !dst.InRange(to))
		{
			return false;
		}
		bool whole = count >= source.stackCount;
		ItemState target = dst._slots[to];
		if (target == null)
		{
			if (whole)
			{
				src._slots[from] = null;
			}
			dst._slots[to] = whole ? source : source.SplitOff(count);
			return true;
		}
		if (Merges(target, source))
		{
			int moved = source.TransferTo(target, Math.Min(count, target.RemainingStackSpace()));
			if (source.stackCount <= 0)
			{
				src._slots[from] = null;
			}
			return moved > 0;
		}
		if (!whole)
		{
			return false;
		}
		src._slots[from] = target;
		dst._slots[to] = source;
		return true;
	}

	// Spend up to `count` units of a reagent from the materials here, matching by
	// the item's parent chain (Cooking.Satisfies). Emptied stacks free their slot.
	// Returns how many units were spent; `spentItems`, when given, collects them.
	public int SpendMaterial(ItemData reagentItem, int count, List<SpentItem> spentItems = null)
	{
		int spent = 0;
		for (int i = 0; i < _slots.Length && spent < count; i++)
		{
			ItemState s = _slots[i];
			if (s?.data == null || !s.data.IsMaterial || s.stackCount <= 0 || !Cooking.Satisfies(s.data, reagentItem))
			{
				continue;
			}
			int take = s.Consume(count - spent);
			spent += take;
			SpentItem.Add(spentItems, s.data, take);
			if (s.stackCount <= 0)
			{
				_slots[i] = null;
			}
		}
		return spent;
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
