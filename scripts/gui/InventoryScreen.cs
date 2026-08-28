using Godot;
using System.Collections.Generic;

// Inventory tab rendered inside AlmanacScreen. A read-only readout of the
// controlled member: player stats, the weapons equipped in the melee / ranged
// slots (ItemInfoPanel viewers), and the party's MATERIALS — highlighting a
// material slot reads it out in the detail panel below the grid. Purely
// informational — nothing here mutates the inventory. Equipping weapons / armor /
// equipment happens on the camp Stash screen; dropping materials happens
// elsewhere.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _meleePanel;
	[Export] private ItemInfoPanel _rangedPanel;
	[Export] private ItemInfoPanel _spellPanel;
	[Export] private BackpackPanel _backpackPanel;
	[Export] private ItemInfoPanel _highlightPanel;

	GameClient _gameClient;
	Player _player;

	// The material grid's rows: one entry per kind, with the summed count across
	// the carried backpack and the party stash.
	readonly List<ItemState> _materialRows = new();
	readonly List<int> _materialCounts = new();

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused += OnMaterialFocused;
		}
		_highlightPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused -= OnMaterialFocused;
		}
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_player = _gameClient?.Player;
			_statsPanel?.SetPlayer(_player);
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged += Refresh;
			}
			Refresh();
			// Deferred: GrabFocus needs the slot visible-in-tree, and the focus it
			// takes is what fills the highlight panel.
			Callable.From(ApplyInitialFocus).CallDeferred();
		}
		else
		{
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged -= Refresh;
			}
			_highlightPanel?.SetItem(null);
		}
	}

	// Put keyboard / gamepad focus on the first material the party owns, so the
	// grid is navigable the moment the tab opens (and the highlight panel starts
	// filled). Nothing to focus when the party carries no materials.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		_backpackPanel?.FirstOccupied()?.GrabFocus();
	}

	// A material slot took focus (D-pad / keyboard, or the mouse hovering it —
	// ItemSlotPanel grabs focus on MouseEntered). Its item fills the detail panel.
	// Not force-identified: an unidentified reagent stays unread here, the same as
	// everywhere else.
	void OnMaterialFocused(int index, ItemSlotPanel panel)
	{
		_highlightPanel?.SetItem(panel?.Item);
	}

	// Re-read the highlight from whichever slot currently holds focus. Called after
	// a repaint so a stack that was spent or merged away doesn't leave stale detail
	// on screen.
	void RefreshHighlight()
	{
		if (_highlightPanel == null || _backpackPanel == null)
		{
			return;
		}
		foreach (ItemSlotPanel slot in _backpackPanel.EnumerateSlots())
		{
			if (slot.HasButtonFocus())
			{
				_highlightPanel.SetItem(slot.Item);
				return;
			}
		}
		_highlightPanel.SetItem(null);
	}

	// Repaint the equipped-weapon viewers and the material backpack from the
	// live inventory. Bound to Inventory.onChanged so an ammo change shows
	// immediately.
	void Refresh()
	{
		Inventory inv = _player?.Inventory;
		_meleePanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponMelee), forceIdentified: true);
		_rangedPanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponRanged), forceIdentified: true);
		// The attuned alchemy spell (the active consumable); its SpellData reagents
		// surface as the panel's Required Reagents row. Hidden when nothing is attuned.
		_spellPanel?.SetItem(inv?.GetEquipped(EInventorySlot.Equipment), forceIdentified: true);
		RebuildMaterialRows(inv);
		_backpackPanel?.Refresh(_materialRows, _materialCounts);
		RefreshHighlight();
	}

	// The materials shown here are the PARTY's, not just what's on this member's
	// back: camping drains the backpack into the shared stash (Sim.CommitCamp), so
	// a carried-only readout reads as "everything is gone" after a camp stop. Both
	// stores feed one grid, with same-kind entries collapsed into a single row
	// carrying the summed count (a kind can hold a stack in each store). Carried
	// first, so the field inventory keeps a stable position as the stash grows.
	void RebuildMaterialRows(Inventory inv)
	{
		_materialRows.Clear();
		_materialCounts.Clear();
		AppendMaterials(inv?.Backpack);
		AppendMaterials(_player?.Sim?.WorldState?.SimState?.PartyMaterialStash);
	}

	void AppendMaterials(IReadOnlyList<ItemState> items)
	{
		if (items == null)
		{
			return;
		}
		for (int i = 0; i < items.Count; i++)
		{
			ItemState item = items[i];
			if (item?.data == null || item.stackCount <= 0)
			{
				continue;
			}
			int row = -1;
			if (item.data.IsStackable)
			{
				for (int r = 0; r < _materialRows.Count; r++)
				{
					if (_materialRows[r].CanStackWith(item))
					{
						row = r;
						break;
					}
				}
			}
			if (row >= 0)
			{
				_materialCounts[row] += item.stackCount;
			}
			else
			{
				_materialRows.Add(item);
				_materialCounts.Add(item.stackCount);
			}
		}
	}

	// ---- Equip-compat helpers, shared with MerchantScreen ------------------

	// True when `item` may equip into `destSlot` — its category's slot matches.
	public static bool EquipCompatible(EInventorySlot destSlot, ItemState item)
	{
		return item?.data != null && item.data.EquipSlotKind == destSlot;
	}

	// True when the items in two singular equip slots could trade places. Weapons
	// and armor are category-locked to one slot each, so this is only ever true
	// for a same-slot no-op; the Equipment hotbar is excluded (index-addressed).
	public static bool CanSwapEquipSlots(EInventorySlot sourceEquip, EInventorySlot destEquip, ItemState selectedItem, Inventory inv)
	{
		if (sourceEquip == EInventorySlot.None || destEquip == EInventorySlot.None) { return false; }
		if (sourceEquip == EInventorySlot.Equipment || destEquip == EInventorySlot.Equipment) { return false; }
		if (!EquipCompatible(destEquip, selectedItem)) { return false; }
		ItemState destOccupant = inv?.GetEquipped(destEquip);
		if (destOccupant != null && !EquipCompatible(sourceEquip, destOccupant)) { return false; }
		return true;
	}
}
