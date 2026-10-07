using Godot;
using System.Collections.Generic;

// Inventory tab rendered inside AlmanacScreen: player stats, the weapons equipped
// in the left / right slots (ItemInfoPanel viewers), and the whole backpack slot
// for slot — highlighting a slot reads its item out in the detail panel below the
// grid. The backpack is editable:
//   A  — select the highlighted item; with one selected, move it to the
//        highlighted slot (swapping with whatever is there). Cancel deselects.
//   X  — use / equip / unequip the highlighted item, per its kind and state.
//   Y  — hold to drop the highlighted item at the member's feet.
// The three button hints belong to AlmanacScreen, which hands them over
// (BindActionHints) and hides them on every other tab.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _meleePanel;
	[Export] private ItemInfoPanel _rangedPanel;
	[Export] private BackpackPanel _backpackPanel;
	[Export] private ItemInfoPanel _highlightPanel;
	// Asks how many when a hold-to-drop lands on a stack.
	[Export] private ItemCountPanel _countPanel;
	// How long Y must be held before the highlighted item drops.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _dropHoldSeconds = 0.6f;

	// A's glyph follows the primary-verb convention of the other inventory-style
	// screens; the press itself arrives as the slot button's own activation.
	const string SelectAction = "ui_select";
	const string UseAction = "MenuSecondary";
	const string DropAction = "MenuTertiary";

	GameClient _gameClient;
	Player _player;
	ButtonHint _hintSelect;
	ButtonHint _hintDrop;
	ButtonHint _hintUse;

	// Backpack indices: the slot under the cursor, and the slot picked up for a
	// move (-1 = none).
	int _focusedIndex = -1;
	int _selectedIndex = -1;
	float _dropHeld;
	// Latched once a hold has dropped something, so keeping X down doesn't drop
	// the next item that slides under the cursor.
	bool _dropFired;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public void BindActionHints(ButtonHint select, ButtonHint drop, ButtonHint use)
	{
		_hintSelect = select;
		_hintDrop = drop;
		_hintUse = use;
		UpdateHints();
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused += OnSlotFocused;
			_backpackPanel.onSlotButtonUp += OnSlotActivated;
		}
		_highlightPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused -= OnSlotFocused;
			_backpackPanel.onSlotButtonUp -= OnSlotActivated;
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
			_selectedIndex = -1;
			_focusedIndex = -1;
			ResetDropHold();
			_highlightPanel?.SetItem(null);
		}
	}

	// Put keyboard / gamepad focus on the first occupied slot, so the grid is
	// navigable the moment the tab opens (and the highlight panel starts filled).
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		_backpackPanel?.FirstOccupied()?.GrabFocus();
	}

	Inventory Inv => _player?.Inventory;

	ItemState ItemAt(int index)
	{
		Inventory inv = Inv;
		return inv != null && index >= 0 && index < inv.Backpack.Count ? inv.Backpack[index] : null;
	}

	// A backpack slot took focus (D-pad / keyboard, or the mouse hovering it —
	// ItemSlotPanel grabs focus on MouseEntered). Its item fills the detail panel.
	// Not force-identified: an unidentified reagent stays unread here, the same as
	// everywhere else.
	void OnSlotFocused(int index, ItemSlotPanel panel)
	{
		_focusedIndex = index;
		ResetDropHold();
		_highlightPanel?.SetItem(panel?.Item);
		UpdateHints();
	}

	// A on a slot (or a click): pick up the item there, or put the picked-up one
	// down here — onto an empty slot, into a matching stack, or swapping.
	void OnSlotActivated(int index, ItemSlotPanel panel)
	{
		Inventory inv = Inv;
		if (inv == null)
		{
			return;
		}
		if (_selectedIndex < 0)
		{
			if (ItemAt(index) != null)
			{
				SetSelection(index);
			}
			return;
		}
		int from = _selectedIndex;
		SetSelection(-1);
		ItemState moving = ItemAt(from);
		if (moving != null)
		{
			inv.MoveWithin(from, index, moving.stackCount);
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!IsVisibleInTree())
		{
			return;
		}
		// Ahead of AlmanacScreen (a child's unhandled input runs first), so cancel
		// backs out of a move instead of closing the almanac.
		if (_selectedIndex >= 0 && e.IsActionPressed("ui_cancel"))
		{
			SetSelection(-1);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_selectedIndex < 0 && e.IsActionPressed(UseAction))
		{
			UseFocused();
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || (_countPanel != null && _countPanel.IsOpen))
		{
			return;
		}
		TickDropHold((float)delta);
	}

	void UseFocused()
	{
		UseOrToggleEquip(_player, ItemAt(_focusedIndex));
	}

	void TickDropHold(float dt)
	{
		ItemState item = _selectedIndex < 0 ? ItemAt(_focusedIndex) : null;
		if (item == null || !Input.IsActionPressed(DropAction))
		{
			ResetDropHold();
			return;
		}
		if (_dropFired)
		{
			return;
		}
		_dropHeld += dt;
		_hintDrop?.SetProgress(Mathf.Clamp(_dropHeld / _dropHoldSeconds, 0f, 1f));
		if (_dropHeld >= _dropHoldSeconds)
		{
			_dropFired = true;
			_dropHeld = 0f;
			_hintDrop?.SetProgress(0f);
			if (item.stackCount > 1 && _countPanel != null)
			{
				Inventory inv = Inv;
				_countPanel.Open(item.stackCount, count => inv.Drop(item, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
			}
			else
			{
				Inv.Drop(item);
			}
		}
	}

	void ResetDropHold()
	{
		_dropHeld = 0f;
		_dropFired = false;
		_hintDrop?.SetProgress(0f);
	}

	void SetSelection(int index)
	{
		_selectedIndex = index;
		ResetDropHold();
		ApplySlotStates();
		UpdateHints();
	}

	// Per-slot overlays the grid's plain item repaint doesn't know about: the
	// picked-up slot dims, and equipped items carry the equipped marker.
	void ApplySlotStates()
	{
		if (_backpackPanel == null)
		{
			return;
		}
		Inventory inv = Inv;
		int i = 0;
		foreach (ItemSlotPanel slot in _backpackPanel.EnumerateSlots())
		{
			slot.SetDimmed(i == _selectedIndex);
			slot.SetEquipped(inv != null && inv.IsEquipped(slot.Item));
			i++;
		}
	}

	void UpdateHints()
	{
		ItemState focused = ItemAt(_focusedIndex);
		if (_selectedIndex >= 0)
		{
			ShowHint(_hintSelect, SelectAction, "Move");
			ShowHint(_hintUse, UseAction, null);
			ShowHint(_hintDrop, DropAction, null);
			return;
		}
		ShowHint(_hintSelect, SelectAction, focused != null ? "Select" : null);
		ShowHint(_hintUse, UseAction, UseVerb(_player, focused));
		ShowHint(_hintDrop, DropAction, focused != null ? "Drop" : null);
	}

	// A null label hides the hint.
	static void ShowHint(ButtonHint hint, string action, string label)
	{
		if (hint == null)
		{
			return;
		}
		hint.Visible = label != null;
		if (label != null)
		{
			hint.SetHint(action, label);
		}
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

	// Repaint the equipped-weapon viewers and the backpack from the live
	// inventory. Bound to Inventory.onChanged so an ammo change shows immediately.
	void Refresh()
	{
		Inventory inv = Inv;
		_meleePanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponLeft), forceIdentified: true);
		_rangedPanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponRight), forceIdentified: true);
		_backpackPanel?.Refresh(inv?.Backpack);
		// A selected stack can vanish under the cursor (spoiled, spent elsewhere).
		if (_selectedIndex >= 0 && ItemAt(_selectedIndex) == null)
		{
			_selectedIndex = -1;
		}
		ApplySlotStates();
		RefreshHighlight();
		UpdateHints();
	}

	// ---- Item verbs, shared with StashScreen and MerchantScreen -------------

	// The Y verb: use an instant item (mud, a meal) on the member, or start a
	// press-to-commit timeline (drinking a potion) — the menu stays open while it
	// plays — else equip / unequip gear. Does nothing for an item with no verb (a
	// material, or a timeline that needs the button held, which only the hotbar
	// can drive).
	public static void UseOrToggleEquip(Player player, ItemState item)
	{
		if (item?.data == null || player?.Inventory == null)
		{
			return;
		}
		if (item.data is IInstantUseItem { CanUseInstantly: true })
		{
			player.UseInstantItem(item);
		}
		else if (UsableFromMenu(player, item))
		{
			player.StartUseAction(item);
		}
		else if (item.data.IsEquippable)
		{
			player.Inventory.ToggleEquip(item);
		}
	}

	// The Y hint's label for `item`, null when it has no verb.
	public static string UseVerb(Player player, ItemState item)
	{
		Inventory inv = player?.Inventory;
		if (item?.data == null || inv == null)
		{
			return null;
		}
		if (item.data is IInstantUseItem { CanUseInstantly: true } || UsableFromMenu(player, item))
		{
			return "Use";
		}
		if (item.data.IsEquippable)
		{
			return inv.IsEquipped(item) ? "Unequip" : "Equip";
		}
		return null;
	}

	// A timeline the menu can start: unequippable (gear runs from its slot),
	// press-to-commit (a menu never sends the release a held action needs), and on
	// the controlled member — an idle one's runner doesn't tick (camp's stash can
	// show one).
	static bool UsableFromMenu(Player player, ItemState item)
	{
		return player.IsActive && !item.data.IsEquippable
			&& item.data is IUsableItem { ActionProfile.commitOnPress: true };
	}

	// True when `item` may equip into `destSlot` — its category's slot matches.
	public static bool EquipCompatible(EInventorySlot destSlot, ItemState item)
	{
		return item?.data != null && item.data.EquipSlotKind == destSlot;
	}
}
