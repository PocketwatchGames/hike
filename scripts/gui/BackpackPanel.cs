using Godot;
using Godot.Collections;
using System.Collections.Generic;

// Reusable slot-grid view over a flat list of items — a member's backpack and
// the party stash both render through this.
// The panel owns NO verb behaviour: it wires each ItemSlotPanel's raw
// focus/press events and forwards them with the slot's grid index, so the
// controlling screen (InventoryScreen / StashScreen / CookingScreen) drives
// select / drop / use / equip against whatever list it bound. Call Refresh with
// the backing list to repaint. Mirrors how StashScreen previously hand-wired its
// stash slots, lifted into one component.
[GlobalClass]
public partial class BackpackPanel : Control
{
	[Export] private Array<ItemSlotPanel> _slots = new();

	// Raw slot events, index = the slot's position in the grid (== the index into
	// the last Refresh list). The screen decides what a tap / hold / press means.
	public System.Action<int, ItemSlotPanel> onSlotFocused;
	public System.Action<int, ItemSlotPanel> onSlotButtonDown;
	public System.Action<int, ItemSlotPanel> onSlotButtonUp;

	public IReadOnlyList<ItemSlotPanel> Slots => _slots;
	public int SlotCount => _slots?.Count ?? 0;

	// Optional: the slot scene and the container more slots are added to when a
	// list is longer than the authored grid (the party stash's capacity is data).
	// Without them a longer list is reported, never silently clipped.
	[Export] private PackedScene _slotScene;
	[Export] private Container _slotContainer;

	public override void _Ready()
	{
		if (_slots == null)
		{
			_slots = new();
		}
		int count = _slots.Count;
		for (int i = 0; i < count; i++)
		{
			WireSlot(i, _slots[i]);
		}
	}

	void WireSlot(int index, ItemSlotPanel panel)
	{
		if (panel == null)
		{
			return;
		}
		panel.onFocusEntered += p => onSlotFocused?.Invoke(index, p);
		panel.onButtonDown += p => onSlotButtonDown?.Invoke(index, p);
		panel.onButtonUp += p => onSlotButtonUp?.Invoke(index, p);
	}

	// Grow the grid to at least `count` slots.
	void EnsureSlots(int count)
	{
		if (_slots.Count >= count)
		{
			return;
		}
		if (_slotScene == null || _slotContainer == null)
		{
			GD.PushError($"BackpackPanel '{Name}': {count} items but only {_slots.Count} slots, and no slot scene to add more — the rest are hidden.");
			return;
		}
		for (int i = _slots.Count; i < count; i++)
		{
			ItemSlotPanel panel = _slotScene.Instantiate<ItemSlotPanel>();
			_slotContainer.AddChild(panel);
			_slots.Add(panel);
			WireSlot(i, panel);
		}
	}

	// Repaint every slot from `items` (slot i shows items[i], or empty past the
	// list's end); the grid grows to fit a longer list. The list may be sparse
	// (a backpack or the stash, with null holes) or dense — the panel just indexes
	// it positionally. `stackCounts`, when
	// given, supplies the badge count per slot for views whose rows don't map 1:1
	// onto a single stack (the almanac's merged carried + stash materials).
	public void Refresh(IReadOnlyList<ItemState> items, IReadOnlyList<int> stackCounts = null)
	{
		EnsureSlots(items?.Count ?? 0);
		int slotCount = _slots.Count;
		for (int i = 0; i < slotCount; i++)
		{
			ItemState item = (items != null && i < items.Count) ? items[i] : null;
			int count = (stackCounts != null && i < stackCounts.Count) ? stackCounts[i] : -1;
			_slots[i]?.SetItem(item, count);
		}
	}

	public ItemSlotPanel GetSlot(int index)
	{
		return (_slots != null && index >= 0 && index < _slots.Count) ? _slots[index] : null;
	}

	public int IndexOf(ItemSlotPanel panel)
	{
		return _slots?.IndexOf(panel) ?? -1;
	}

	// First empty slot, or the first slot if all are occupied, or null if the grid
	// has no slots — the select-mode auto-target destination.
	public ItemSlotPanel FirstEmptyOrFirst()
	{
		if (_slots == null || _slots.Count == 0)
		{
			return null;
		}
		foreach (ItemSlotPanel p in _slots)
		{
			if (p != null && p.Item == null)
			{
				return p;
			}
		}
		return _slots[0];
	}

	// First slot holding an item, or null if the grid is empty — the cooking
	// screen's auto-highlight target when no recipe can be pre-selected.
	public ItemSlotPanel FirstOccupied()
	{
		if (_slots == null)
		{
			return null;
		}
		foreach (ItemSlotPanel p in _slots)
		{
			if (p != null && p.Item != null)
			{
				return p;
			}
		}
		return null;
	}

	public void SetFocusable(bool focusable)
	{
		if (_slots == null)
		{
			return;
		}
		foreach (ItemSlotPanel p in _slots)
		{
			p?.SetFocusable(focusable);
		}
	}

	public void ClearVisuals()
	{
		if (_slots == null)
		{
			return;
		}
		foreach (ItemSlotPanel p in _slots)
		{
			p?.SetGhost(null);
			p?.SetDimmed(false);
		}
	}

	public IEnumerable<ItemSlotPanel> EnumerateSlots()
	{
		if (_slots == null)
		{
			yield break;
		}
		foreach (ItemSlotPanel p in _slots)
		{
			if (p != null)
			{
				yield return p;
			}
		}
	}
}
