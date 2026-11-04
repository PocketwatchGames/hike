using Godot;
using System.Collections.Generic;

// The items a screen lays out, one grid at a time behind a row of icon tabs: the
// member's backpack, one tab per carried sack, and — on a screen that draws on it
// (the cookpot, the crafting bench) — the party stash last. The selected tab is
// kept by grid, so a sack coming or going never moves the player off the grid
// they were on. The equip slots and the belt are not here — they only point at
// backpack stacks, so the backpack slot of an equipped or belted item is framed
// and a belted one wears its hotbar number. The panel owns NO verb behavior: the
// controlling screen listens to the grid slot events and repaints through Paint.
[GlobalClass]
public partial class InventoryPanel : VBoxContainer
{
	[Export] private TabBar _tabs;
	// Shows the selected tab's grid.
	[Export] private BackpackPanel _grid;
	// Tab icons; a tab with none shows its name instead. A sack's tab is its item
	// sprite.
	[Export] private Texture2D _backpackIcon;
	[Export] private Texture2D _stashIcon;
	[Export] private StringName _backpackTitleLocKey = "panel_backpack";
	[Export] private StringName _stashTitleLocKey = "panel_stash";

	// Raw slot events from the grid shown, with the grid and slot index. A screen
	// tells the stash from the member's own grids by comparing against the stash it
	// painted.
	public System.Action<IItemGrid, int, ItemSlotPanel> onGridSlotFocused;
	public System.Action<IItemGrid, int, ItemSlotPanel> onGridSlotButtonDown;
	public System.Action<IItemGrid, int, ItemSlotPanel> onGridSlotButtonUp;
	// The player picked another tab: the slots now show a different grid, so a
	// screen re-applies whatever it overlays on them. A focused slot re-raises
	// onGridSlotFocused with its new grid first.
	public System.Action onTabChanged;
	// Focus moved onto the tab bar: no slot is highlighted any more, so a screen
	// clears whatever it shows for one (the item info panel).
	public System.Action onTabsFocused;

	// The tabs, in order: the backpack, each sack, then the stash.
	readonly List<IItemGrid> _grids = new();
	int _selected;
	Inventory _painted;
	IItemGrid _paintedStash;
	// Set while Paint rewrites the tab bar, whose own selection changes are not
	// the player's.
	bool _syncingTabs;

	public override void _Ready()
	{
		if (_grid != null)
		{
			_grid.onSlotFocused += (index, slot) => Raise(onGridSlotFocused, index, slot);
			_grid.onSlotButtonDown += (index, slot) => Raise(onGridSlotButtonDown, index, slot);
			_grid.onSlotButtonUp += (index, slot) => Raise(onGridSlotButtonUp, index, slot);
		}
		if (_tabs != null)
		{
			_tabs.TabChanged += OnTabChanged;
			_tabs.FocusEntered += () => onTabsFocused?.Invoke();
		}
	}

	void Raise(System.Action<IItemGrid, int, ItemSlotPanel> handler, int index, ItemSlotPanel slot)
	{
		IItemGrid grid = ShownGrid;
		if (grid != null)
		{
			handler?.Invoke(grid, index, slot);
		}
	}

	// The grid the slots show, or null before anything is painted.
	public IItemGrid ShownGrid => _selected < _grids.Count ? _grids[_selected] : null;

	public BackpackPanel GridPanel => _grid;

	// Repaint from `inventory`'s backpack and sacks, then `stash` when given, on
	// the tab the player was on (the backpack when that grid is gone).
	public void Paint(Inventory inventory, IItemGrid stash = null)
	{
		IItemGrid selected = ShownGrid;
		_grids.Clear();
		if (inventory != null)
		{
			_grids.AddRange(inventory.Carried);
		}
		if (stash != null)
		{
			_grids.Add(stash);
		}
		_painted = inventory;
		_paintedStash = stash;
		_selected = Mathf.Max(0, _grids.IndexOf(selected));
		SyncTabs();
		ShowSelected();
	}

	void SyncTabs()
	{
		if (_tabs == null)
		{
			return;
		}
		_syncingTabs = true;
		_tabs.TabCount = _grids.Count;
		for (int i = 0; i < _grids.Count; i++)
		{
			Texture2D icon = TabIcon(_grids[i]);
			string name = TabName(_grids[i]);
			_tabs.SetTabIcon(i, icon);
			_tabs.SetTabTitle(i, icon != null ? string.Empty : name);
			_tabs.SetTabTooltip(i, name);
		}
		if (_grids.Count > 0)
		{
			_tabs.CurrentTab = _selected;
		}
		_syncingTabs = false;
	}

	Texture2D TabIcon(IItemGrid grid)
	{
		if (grid is Inventory.CarriedGrid { sack: SackState sack })
		{
			return sack.data.inventorySprite;
		}
		return grid == _paintedStash ? _stashIcon : _backpackIcon;
	}

	string TabName(IItemGrid grid)
	{
		if (grid is Inventory.CarriedGrid { sack: SackState sack })
		{
			return SackName(sack);
		}
		return Loc.Get(grid == _paintedStash ? _stashTitleLocKey : _backpackTitleLocKey);
	}

	// The name the item panel would show for the sack itself.
	static string SackName(SackState sack)
	{
		SimState worldSim = Sim.Current?.WorldState?.SimState;
		return worldSim != null ? worldSim.GetItemDisplayName(sack) : sack.data.displayName.ToString();
	}

	void OnTabChanged(long tab)
	{
		if (_syncingTabs || tab < 0 || tab >= _grids.Count || tab == _selected)
		{
			return;
		}
		_selected = (int)tab;
		ShowSelected();
		ItemSlotPanel focused = FocusedSlot(out int index);
		if (focused != null)
		{
			Raise(onGridSlotFocused, index, focused);
		}
		onTabChanged?.Invoke();
	}

	// Fill the slots from the selected grid. Only the backpack carries the lit /
	// equipped / belt marks — nothing in a sack or the stash can be any of them.
	void ShowSelected()
	{
		if (_grid == null)
		{
			return;
		}
		IItemGrid grid = ShownGrid;
		_grid.Refresh(grid?.Slots);
		if (grid != null)
		{
			_grid.SetTitle(TabName(grid));
		}
		bool backpack = _painted != null && grid == _painted.Backpack;
		foreach (ItemSlotPanel slot in _grid.EnumerateSlots())
		{
			ItemState item = slot.Item;
			int belt = backpack ? _painted.BeltIndexOf(item) : -1;
			slot.SetActive(backpack && _painted.IsLit(item));
			slot.SetEquipped(belt >= 0 || (backpack && _painted.IsEquipped(item)));
			slot.SetBeltNumber(belt >= 0 ? Inventory.HotbarIndexOfBelt(belt) + 1 : 0);
		}
	}

	ItemSlotPanel FocusedSlot(out int index)
	{
		index = 0;
		foreach (ItemSlotPanel slot in _grid.EnumerateSlots())
		{
			if (slot.HasButtonFocus())
			{
				return slot;
			}
			index++;
		}
		return null;
	}

	// The first slot of the grid shown that holds an item; null when it is empty.
	public ItemSlotPanel FirstOccupied() => _grid?.FirstOccupied();

	// The first slot of the grid shown, or null.
	public ItemSlotPanel FirstSlot() => _grid?.GetSlot(0);

	// Clear ghost + dim state on every slot — used when a screen closes so the
	// panel doesn't carry stale visual flags.
	public void ClearSelectVisuals()
	{
		_grid?.ClearVisuals();
	}
}
