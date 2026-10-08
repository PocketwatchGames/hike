using Godot;

// Stash tab of the camp screen: the party stash (SimState.PartyStash) beside the
// chosen member's inventory — belt, backpack and equip slots — items moved to and fro
// between them.
//   A  — pick up the stack under the cursor; with one picked up, put it down
//        here (any grid): an empty slot takes it, a matching stack merges,
//        anything else swaps. An equip slot takes only its own kind of gear.
//        Hold on a stack to pick up only some of it.
//   RT — send the stack under the cursor to the other side, where it fits.
//        Hold on a stack to send only some of it.
//   Y  — hold to drop the stack under the cursor at the member's feet; on a
//        stack, the hold asks how many.
//   X  — use / equip / unequip, on the member's own items only.
//   B  — put the pick-up back; with nothing picked up, CampScreen backs out.
// Every move goes through ItemTransfer, so the stash screen holds no move rules.
[GlobalClass]
public partial class StashScreen : Control
{
	[Export] private BackpackPanel _stashPanel;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private ItemCountPanel _countPanel;
	[Export] private ButtonHint _hintSelect;
	[Export] private ButtonHint _hintSend;
	[Export] private ButtonHint _hintDrop;
	[Export] private ButtonHint _hintUse;
	// How long A, RT or Y must be held on a stack to choose how many.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;

	const string SelectAction = "ui_select";
	const string SendAction = "MenuQuaternary";
	const string DropAction = "MenuTertiary";
	const string UseAction = "MenuSecondary";

	enum ESide
	{
		None,
		Stash,
		Belt,
		Backpack,
		// The member's equip slots; the index is the EInventorySlot.
		Equip,
	}

	readonly struct Slot
	{
		public readonly ESide side;
		public readonly int index;

		public Slot(ESide side, int index)
		{
			this.side = side;
			this.index = index;
		}

		public static readonly Slot None = new(ESide.None, -1);
		public bool IsNone => side == ESide.None;
		public bool Is(Slot other) => side == other.side && index == other.index;
	}

	Player _player;
	ItemGrid _stash;

	Slot _focused = Slot.None;
	// The picked-up stack and how many of its units are moving.
	Slot _picked = Slot.None;
	int _pickedCount;

	// A held on a slot: where, for how long, and whether the hold already fired
	// (so its release isn't also a tap). A is the slot button's own press.
	Slot _selectPressed = Slot.None;
	float _selectHeld;
	bool _selectHoldFired;
	readonly PolledHold _send = new(SendAction);
	readonly PolledHold _drop = new(DropAction);

	public override void _Ready()
	{
		Visible = false;
		WirePanel(_stashPanel, ESide.Stash);
		WirePanel(_inventoryPanel?.BeltGrid, ESide.Belt);
		WirePanel(_inventoryPanel?.BackpackGrid, ESide.Backpack);
		if (_inventoryPanel != null)
		{
			foreach (EInventorySlot equip in InventoryPanel.EquipSlots)
			{
				ItemSlotPanel panel = _inventoryPanel.EquipPanel(equip);
				if (panel == null)
				{
					continue;
				}
				var slot = new Slot(ESide.Equip, (int)equip);
				panel.onFocusEntered += _ => OnSlotFocused(slot);
				panel.onButtonDown += _ => OnSelectDown(slot);
				panel.onButtonUp += _ => OnSelectUp(slot);
			}
		}
		_itemInfoPanel?.SetItem(null);
	}

	void WirePanel(BackpackPanel panel, ESide side)
	{
		if (panel == null)
		{
			return;
		}
		panel.onSlotFocused += (index, _) => OnSlotFocused(new Slot(side, index));
		panel.onSlotButtonDown += (index, _) => OnSelectDown(new Slot(side, index));
		panel.onSlotButtonUp += (index, _) => OnSelectUp(new Slot(side, index));
	}

	// CampScreen owns the global gating (input, HUD, mouse, camp pose); this
	// screen binds to the stash and the member's inventory.
	public void Open(Player player, ItemGrid stash)
	{
		_player = player;
		_stash = stash;
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged += Refresh;
		}
		ClearPick();
		ResetHolds();
		Visible = true;
		Refresh();
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= Refresh;
		}
		_countPanel?.Dismiss();
		_stashPanel?.ClearVisuals();
		_inventoryPanel?.ClearSelectVisuals();
		Visible = false;
		_stash = null;
		_player = null;
		_focused = Slot.None;
		ClearPick();
	}

	// GrabFocus needs the slot visible-in-tree, hence deferred from Open.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		BackpackPanel belt = _inventoryPanel?.BeltGrid;
		BackpackPanel backpack = _inventoryPanel?.BackpackGrid;
		ItemSlotPanel start = belt?.FirstOccupied() ?? backpack?.FirstOccupied() ?? _stashPanel?.FirstOccupied()
			?? belt?.GetSlot(0) ?? backpack?.GetSlot(0);
		start?.GrabFocus();
	}

	IItemGrid Grid(ESide side)
	{
		return side switch
		{
			ESide.Stash => _stash,
			ESide.Belt => _player?.Inventory?.Belt,
			ESide.Backpack => _player?.Inventory?.Backpack,
			_ => null,
		};
	}

	ItemState ItemAt(Slot slot)
	{
		return slot.side == ESide.Equip
			? _player?.Inventory?.GetEquipped((EInventorySlot)slot.index)
			: Grid(slot.side)?.At(slot.index);
	}

	// Where RT sends a stack: the member's side takes it into the grid it
	// prefers (spilling into the other), every member grid sends to the stash.
	IItemGrid SendTarget(ESide side, ItemState item)
	{
		return side == ESide.Stash ? _player?.Inventory?.PreferredGrid(item?.data) : _stash;
	}

	bool PickerOpen => _countPanel != null && _countPanel.IsOpen;

	// ---- Focus ---------------------------------------------------------------

	void OnSlotFocused(Slot slot)
	{
		_focused = slot;
		_send.Reset(_hintSend);
		_drop.Reset(_hintDrop);
		_itemInfoPanel?.SetItem(ItemAt(slot));
		UpdateHints();
	}

	// ---- A: pick up / put down -------------------------------------------------

	void OnSelectDown(Slot slot)
	{
		_selectPressed = slot;
		_selectHeld = 0f;
		_selectHoldFired = false;
	}

	void OnSelectUp(Slot slot)
	{
		Slot pressed = _selectPressed;
		bool fired = _selectHoldFired;
		_selectPressed = Slot.None;
		_selectHoldFired = false;
		_hintSelect?.SetProgress(0f);
		// Dragging off the pressed slot before letting go reads as a cancel.
		if (fired || PickerOpen || !pressed.Is(slot))
		{
			return;
		}
		Select(slot);
	}

	void TickSelectHold(float dt)
	{
		if (_selectPressed.IsNone || _selectHoldFired)
		{
			return;
		}
		_selectHeld += dt;
		_hintSelect?.SetProgress(Mathf.Clamp(_selectHeld / _holdSeconds, 0f, 1f));
		if (_selectHeld < _holdSeconds)
		{
			return;
		}
		_selectHoldFired = true;
		_hintSelect?.SetProgress(0f);
		Slot slot = _selectPressed;
		ItemState item = ItemAt(slot);
		// A hold only means something on a stack that isn't already picked up.
		if (!_picked.IsNone || item == null || item.stackCount <= 1)
		{
			Select(slot);
			return;
		}
		_countPanel?.Open(item.stackCount, count => Pick(slot, count), prompt: Loc.Get(Loc.Keys.stash_pick_how_many));
	}

	// A tap: pick up the whole stack, or put the picked-up one down here.
	void Select(Slot slot)
	{
		if (_picked.IsNone)
		{
			ItemState item = ItemAt(slot);
			if (item != null)
			{
				Pick(slot, item.stackCount);
			}
			return;
		}
		Slot from = _picked;
		int count = _pickedCount;
		ClearPick();
		if (!from.Is(slot))
		{
			MoveTo(from, count, slot);
		}
		Refresh();
	}

	void MoveTo(Slot from, int count, Slot to)
	{
		Inventory inv = _player?.Inventory;
		if (from.side == ESide.Equip && to.side == ESide.Equip)
		{
			return;
		}
		if (to.side == ESide.Equip)
		{
			if (InventoryScreen.EquipCompatible((EInventorySlot)to.index, ItemAt(from)))
			{
				ItemTransfer.Equip(Grid(from.side), from.index, inv);
			}
			return;
		}
		if (from.side == ESide.Equip)
		{
			ItemTransfer.Unequip(inv, (EInventorySlot)from.index, Grid(to.side), to.index);
			return;
		}
		ItemTransfer.MoveTo(Grid(from.side), from.index, count, Grid(to.side), to.index);
	}

	void Pick(Slot slot, int count)
	{
		if (count <= 0 || ItemAt(slot) == null)
		{
			return;
		}
		_picked = slot;
		_pickedCount = count;
		ApplySlotStates();
		UpdateHints();
	}

	void ClearPick()
	{
		_picked = Slot.None;
		_pickedCount = 0;
		ApplySlotStates();
		UpdateHints();
	}

	// ---- RT: send to the other side / Y: drop ----------------------------------

	void TickPolledHolds(float dt)
	{
		ItemState item = _picked.IsNone ? ItemAt(_focused) : null;
		Slot slot = _focused;

		PolledHold.EResult send = _send.Tick(item != null, dt, _holdSeconds, _hintSend);
		if (send == PolledHold.EResult.Tap || (send == PolledHold.EResult.Hold && item.stackCount <= 1))
		{
			Send(slot, item.stackCount);
			return;
		}
		if (send == PolledHold.EResult.Hold)
		{
			_countPanel?.Open(item.stackCount, count => Send(slot, count), prompt: Loc.Get(Loc.Keys.stash_send_how_many));
			return;
		}

		// Drop is hold-only: a tap must never throw anything away.
		if (_drop.Tick(item != null, dt, _holdSeconds, _hintDrop) != PolledHold.EResult.Hold)
		{
			return;
		}
		if (item.stackCount <= 1)
		{
			Drop(slot, item.stackCount);
			return;
		}
		_countPanel?.Open(item.stackCount, count => Drop(slot, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
	}

	void Send(Slot slot, int count)
	{
		if (slot.side == ESide.Equip)
		{
			ItemTransfer.SendEquipped(_player?.Inventory, (EInventorySlot)slot.index, _stash);
		}
		else if (count > 0)
		{
			ItemTransfer.Send(Grid(slot.side), slot.index, count, SendTarget(slot.side, ItemAt(slot)));
		}
		Refresh();
	}

	void Drop(Slot slot, int count)
	{
		if (slot.side == ESide.Equip)
		{
			_player?.Inventory?.Drop(ItemAt(slot));
		}
		else if (count > 0)
		{
			_player?.DropAtFeet(Grid(slot.side)?.Take(slot.index, count));
		}
		Refresh();
	}

	// ---- Input / tick ----------------------------------------------------------

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || _player == null || PickerOpen)
		{
			return;
		}
		TickSelectHold((float)delta);
		TickPolledHolds((float)delta);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!IsVisibleInTree())
		{
			return;
		}
		// Ahead of CampScreen (a child's unhandled input runs first), so cancel
		// puts the pick-up back instead of backing out of the stash.
		if (!_picked.IsNone && e.IsActionPressed("ui_cancel"))
		{
			ClearPick();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed(UseAction) && UseVerb() != null && !PickerOpen)
		{
			InventoryScreen.UseOrToggleEquip(_player, ItemAt(_focused));
			GetViewport().SetInputAsHandled();
		}
	}

	void ResetHolds()
	{
		_selectPressed = Slot.None;
		_selectHeld = 0f;
		_selectHoldFired = false;
		_hintSelect?.SetProgress(0f);
		_send.Arm(_hintSend);
		_drop.Arm(_hintDrop);
	}

	// ---- Repaint -----------------------------------------------------------------

	void Refresh()
	{
		_stashPanel?.Refresh(_stash?.Slots);
		_inventoryPanel?.Paint(_player?.Inventory);
		// A picked stack can vanish under the cursor (spoiled, spent elsewhere).
		ItemState picked = ItemAt(_picked);
		if (!_picked.IsNone && (picked == null || picked.stackCount < _pickedCount))
		{
			_picked = Slot.None;
			_pickedCount = 0;
		}
		ApplySlotStates();
		_itemInfoPanel?.SetItem(ItemAt(_focused));
		UpdateHints();
	}

	// The picked-up slot dims — the overlay the plain repaint doesn't know.
	void ApplySlotStates()
	{
		ApplySlotStates(_stashPanel, ESide.Stash);
		ApplySlotStates(_inventoryPanel?.BeltGrid, ESide.Belt);
		ApplySlotStates(_inventoryPanel?.BackpackGrid, ESide.Backpack);
		if (_inventoryPanel != null)
		{
			foreach (EInventorySlot equip in InventoryPanel.EquipSlots)
			{
				_inventoryPanel.EquipPanel(equip)?.SetDimmed(_picked.side == ESide.Equip && _picked.index == (int)equip);
			}
		}
	}

	void ApplySlotStates(BackpackPanel panel, ESide side)
	{
		if (panel == null)
		{
			return;
		}
		int i = 0;
		foreach (ItemSlotPanel slot in panel.EnumerateSlots())
		{
			slot.SetDimmed(_picked.side == side && _picked.index == i);
			i++;
		}
	}

	void UpdateHints()
	{
		ItemState focused = ItemAt(_focused);
		if (!_picked.IsNone)
		{
			ShowHint(_hintSelect, SelectAction, Loc.Get(Loc.Keys.stash_place));
			ShowHint(_hintSend, SendAction, null);
			ShowHint(_hintDrop, DropAction, null);
			ShowHint(_hintUse, UseAction, null);
			return;
		}
		ShowHint(_hintSelect, SelectAction, focused != null ? Loc.Get(Loc.Keys.stash_select) : null);
		string send = _focused.side == ESide.Stash ? Loc.Get(Loc.Keys.stash_take) : Loc.Get(Loc.Keys.stash_store);
		ShowHint(_hintSend, SendAction, focused != null ? send : null);
		ShowHint(_hintDrop, DropAction, focused != null ? Loc.Get(Loc.Keys.stash_drop) : null);
		ShowHint(_hintUse, UseAction, UseVerb());
	}

	// Y acts only on the member's own items, and not while something is picked up.
	string UseVerb()
	{
		if (!_picked.IsNone || _focused.side == ESide.Stash || _focused.IsNone)
		{
			return null;
		}
		return InventoryScreen.UseVerb(_player, ItemAt(_focused));
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
}
