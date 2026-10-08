using Godot;
using System.Collections.Generic;

// Reusable slot-grid view over a flat list of items — a member's belt and
// backpack, the party stash, the cooking screen's materials. The grid shows
// exactly one slot per list entry, so the list's length (a grid's capacity) is
// the only thing that sizes it: slots are instanced from `_slotScene` into
// `_slotContainer` on demand, and ones past the end are hidden, kept for reuse.
// The panel owns NO verb behaviour: it wires each ItemSlotPanel's raw
// focus/press events and forwards them with the slot's grid index, so the
// controlling screen (InventoryScreen / StashScreen / CookingScreen) drives
// select / drop / use / equip against whatever list it bound.
[GlobalClass]
public partial class BackpackPanel : Control
{
	[Export] private PackedScene _slotScene;
	[Export] private Container _slotContainer;
	// Overrides a GridContainer's column count per instance (a belt is one row);
	// 0 keeps the scene's.
	[Export(PropertyHint.Range, "0,32,1")] private int _columns = 0;

	// Raw slot events, index = the slot's position in the grid (== the index into
	// the last Refresh list). The screen decides what a tap / hold / press means.
	public System.Action<int, ItemSlotPanel> onSlotFocused;
	public System.Action<int, ItemSlotPanel> onSlotButtonDown;
	public System.Action<int, ItemSlotPanel> onSlotButtonUp;

	// Every slot ever instanced; the first `_shown` are the live grid.
	readonly List<ItemSlotPanel> _slots = new();
	int _shown;

	public int SlotCount => _shown;

	public override void _Ready()
	{
		if (_columns > 0 && _slotContainer is GridContainer grid)
		{
			grid.Columns = _columns;
		}
	}

	void WireSlot(int index, ItemSlotPanel panel)
	{
		panel.onFocusEntered += p => onSlotFocused?.Invoke(index, p);
		panel.onButtonDown += p => onSlotButtonDown?.Invoke(index, p);
		panel.onButtonUp += p => onSlotButtonUp?.Invoke(index, p);
	}

	// Show exactly `count` slots, instancing any the grid hasn't had yet.
	void SetSlotCount(int count)
	{
		if (count > _slots.Count && (_slotScene == null || _slotContainer == null))
		{
			GD.PushError($"BackpackPanel '{Name}': {count} slots wanted but no slot scene / container to build them in.");
			count = _slots.Count;
		}
		for (int i = _slots.Count; i < count; i++)
		{
			ItemSlotPanel panel = _slotScene.Instantiate<ItemSlotPanel>();
			_slotContainer.AddChild(panel);
			_slots.Add(panel);
			WireSlot(i, panel);
		}
		for (int i = 0; i < _slots.Count; i++)
		{
			_slots[i].Visible = i < count;
		}
		_shown = count;
	}

	// Repaint from `items`: one slot per entry, slot i showing items[i]. The list
	// may be sparse (a grid, with null holes) or dense — the panel just indexes it
	// positionally. `stackCounts`, when given, supplies the badge count per slot
	// for views whose rows don't map 1:1 onto a single stack (the almanac's merged
	// carried + stash materials).
	public void Refresh(IReadOnlyList<ItemState> items, IReadOnlyList<int> stackCounts = null)
	{
		SetSlotCount(items?.Count ?? 0);
		for (int i = 0; i < _shown; i++)
		{
			int count = (stackCounts != null && i < stackCounts.Count) ? stackCounts[i] : -1;
			_slots[i].SetItem(items[i], count);
		}
	}

	public ItemSlotPanel GetSlot(int index)
	{
		return index >= 0 && index < _shown ? _slots[index] : null;
	}

	public int IndexOf(ItemSlotPanel panel)
	{
		int index = _slots.IndexOf(panel);
		return index < _shown ? index : -1;
	}

	// First empty slot, or the first slot if all are occupied, or null if the grid
	// has no slots — the select-mode auto-target destination.
	public ItemSlotPanel FirstEmptyOrFirst()
	{
		for (int i = 0; i < _shown; i++)
		{
			if (_slots[i].Item == null)
			{
				return _slots[i];
			}
		}
		return GetSlot(0);
	}

	// First slot holding an item, or null if the grid is empty — the cooking
	// screen's auto-highlight target when no recipe can be pre-selected.
	public ItemSlotPanel FirstOccupied()
	{
		for (int i = 0; i < _shown; i++)
		{
			if (_slots[i].Item != null)
			{
				return _slots[i];
			}
		}
		return null;
	}

	public void SetFocusable(bool focusable)
	{
		foreach (ItemSlotPanel p in _slots)
		{
			p.SetFocusable(focusable);
		}
	}

	public void ClearVisuals()
	{
		foreach (ItemSlotPanel p in _slots)
		{
			p.SetGhost(null);
			p.SetDimmed(false);
		}
	}

	// The live slots, in grid order.
	public IEnumerable<ItemSlotPanel> EnumerateSlots()
	{
		for (int i = 0; i < _shown; i++)
		{
			yield return _slots[i];
		}
	}
}
