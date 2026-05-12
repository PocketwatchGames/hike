using Godot;
using System.Collections.Generic;
using Godot.Collections;

// The interactive inventory body — slots, button hints, and the verb logic
// (equip / use / drop). Authored as its own scene so the modal wrapper
// (InventoryScreen) can stay focused on open/close lifecycle and InputSuppressed
// plumbing. The panel is dormant until InventoryScreen.Open() calls Bind(player)
// on it; Unbind() detaches it from the player's inventory signals and stops
// reacting to input.
[GlobalClass]
public partial class InventoryPanel : Control
{
	[Export] private ItemSlotPanel _armorHeadPanel;
	[Export] private ItemSlotPanel _armorBodyPanel;
	[Export] private ItemSlotPanel _weaponLeftPanel;
	[Export] private ItemSlotPanel _weaponRightPanel;
	[Export] private Array<ItemSlotPanel> _consumablePanels;
	[Export] private Array<ItemSlotPanel> _backpackPanels;
	[Export] private ButtonHint _buttonHintEquip;
	[Export] private ButtonHint _buttonHintUse;
	[Export] private ButtonHint _buttonHintDrop;

	// Input actions wired to the three menu verbs. Equip rides on the slot
	// button's own Pressed signal (ui_accept / mouse click), so we only need
	// custom actions for the Use hold and the half-second Drop hold.
	[Export] private StringName _equipHintAction = "ui_accept";
	[Export] private StringName _useAction = "UseItem";
	[Export] private StringName _dropAction = "Drop";

	// Fires whenever the focused slot's currently-displayed ItemState changes —
	// either because focus moved to a different slot, or because the focused
	// slot's contents were mutated (used / dropped / equipped) and Refresh
	// re-bound a different item to the same panel. ItemState may be null when
	// the focused slot is empty or no slot has focus. InventoryScreen forwards
	// this to the side ItemInfoPanel.
	public System.Action<ItemState> onFocusedItemChanged;

	// Fires when the drop key has been held past DropHoldSeconds on a focused
	// item. InventoryScreen pops the DropCountPanel in response. Tap-drop
	// (release before threshold) is handled internally — drops a single unit
	// directly and does not fire this signal.
	public System.Action<ItemState> onDropHoldComplete;

	// External gate flipped by InventoryScreen while the DropCountPanel is up.
	// Suspends drop hold/tap processing so the still-held key doesn't re-fire
	// the hold once the sub-panel is on screen.
	public bool DropLocked { get; set; }

	const float DropHoldSeconds = 0.5f;

	Player _player;
	Inventory _inventory;
	ItemSlotPanel _focused;
	ItemState _lastFocusedItem;
	float _dropHold;
	// True between a successful Use press and its release. Lets the release
	// event reach the runner only when we actually started something, and lets
	// focus changes / Unbind() abort the in-flight action cleanly.
	bool _useStarted;
	// Bind/Unbind gate. Slot signal subscriptions, input handling, and per-
	// frame ticks all key off this so the panel stays inert before the screen
	// has shown it.
	bool _active;

	public override void _Ready()
	{
		WirePanel(_armorHeadPanel);
		WirePanel(_armorBodyPanel);
		WirePanel(_weaponLeftPanel);
		WirePanel(_weaponRightPanel);
		WirePanels(_consumablePanels);
		WirePanels(_backpackPanels);

		_buttonHintEquip?.SetHint(_equipHintAction, "Equip");
		_buttonHintUse?.SetHint(_useAction, "Use");
		_buttonHintDrop?.SetHint(_dropAction, "Drop");

		UpdateButtonHints();
	}

	public override void _ExitTree()
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
		}
	}

	// Attach to a player's inventory: subscribe to slot/consumable signals,
	// fill every slot with the current state, and grab focus on the first
	// focusable panel so gamepad navigation has a starting point.
	public void Bind(Player player)
	{
		if (player == null)
		{
			return;
		}
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
			_inventory.onChanged -= OnInventoryGenericChanged;
		}
		_player = player;
		_inventory = player.Inventory;
		if (_inventory != null)
		{
			_inventory.onSlotChanged += OnInventoryChanged;
			_inventory.onActiveConsumableChanged += OnActiveConsumableChanged;
			// Generic pulse fires for stack-count mutations (e.g. consumable
			// Use's DecrementStack event) that the slot signals don't cover.
			_inventory.onChanged += OnInventoryGenericChanged;
		}
		_active = true;
		RefreshAll();
		ItemSlotPanel start = _focused ?? FindFirstFocusable();
		start?.GrabFocus();
		UpdateButtonHints();
		// Force a focused-item pulse so InventoryScreen's side panels can sync
		// on Open even if focus is already where it needs to be (which would
		// otherwise skip the FocusEntered → OnPanelFocused → fire path).
		EmitFocusedItem(force: true);
	}

	// Detach from the player's inventory. Cancels any in-flight hold state
	// (drop timer, runner-driven use) so a later Bind starts clean.
	public void Unbind()
	{
		CancelHeldActions();
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
			_inventory.onChanged -= OnInventoryGenericChanged;
		}
		_inventory = null;
		_player = null;
		_active = false;
		// Reset cached focused-item so the next Bind doesn't suppress the
		// initial fire by comparing against a stale value.
		_lastFocusedItem = null;
	}

	void WirePanel(ItemSlotPanel panel)
	{
		if (panel == null)
		{
			return;
		}
		panel.onFocusEntered += OnPanelFocused;
		panel.onPressed += OnPanelPressed;
	}

	void WirePanels(Array<ItemSlotPanel> panels)
	{
		if (panels == null)
		{
			return;
		}
		foreach (ItemSlotPanel panel in panels)
		{
			WirePanel(panel);
		}
	}

	void OnInventoryChanged(EInventorySlot _) => RefreshAll();
	void OnActiveConsumableChanged(int _) => RefreshAll();
	void OnInventoryGenericChanged() => RefreshAll();

	void RefreshAll()
	{
		if (_inventory == null)
		{
			UpdateButtonHints();
			return;
		}

		_armorHeadPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.ArmorHead));
		_armorBodyPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.ArmorBody));
		_weaponLeftPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.WeaponLeft));
		_weaponRightPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.WeaponRight));

		if (_consumablePanels != null)
		{
			IReadOnlyList<ItemState> slots = _inventory.ConsumableSlots;
			for (int i = 0; i < _consumablePanels.Count; i++)
			{
				_consumablePanels[i]?.SetItem(i < slots.Count ? slots[i] : null);
			}
		}
		if (_backpackPanels != null)
		{
			IReadOnlyList<ItemState> backpack = _inventory.Backpack;
			for (int i = 0; i < _backpackPanels.Count; i++)
			{
				_backpackPanels[i]?.SetItem(i < backpack.Count ? backpack[i] : null);
			}
		}
		UpdateButtonHints();
		// Focused slot may now hold a different item (e.g. a drop shifted the
		// backpack list under the focus index). Pulse so the side info panel
		// reflects the new content; EmitFocusedItem suppresses no-op fires.
		EmitFocusedItem();
	}

	void OnPanelFocused(ItemSlotPanel panel)
	{
		_focused = panel;
		CancelHeldActions();
		UpdateButtonHints();
		EmitFocusedItem();
	}

	// Fires onFocusedItemChanged when the focused slot's item differs from
	// the last value we broadcast. `force` skips the equality check — used on
	// Bind so the side panels populate even if focus didn't move.
	void EmitFocusedItem(bool force = false)
	{
		ItemState current = _focused?.Item;
		if (!force && current == _lastFocusedItem)
		{
			return;
		}
		_lastFocusedItem = current;
		onFocusedItemChanged?.Invoke(current);
	}

	// Slot button press (ui_accept / mouse click) is the Equip verb — the
	// button hint on the same input action drives the visible label.
	void OnPanelPressed(ItemSlotPanel panel)
	{
		ItemState item = panel?.Item;
		if (item == null)
		{
			return;
		}
		DoToggleEquip(panel, item);
	}

	// Flip focusability on every slot. Used by InventoryScreen to keep
	// ui_left/right from traversing focus onto inventory slots while a
	// sub-modal (DropCountPanel) is up.
	public void SetSlotsFocusable(bool focusable)
	{
		_armorHeadPanel?.SetFocusable(focusable);
		_armorBodyPanel?.SetFocusable(focusable);
		_weaponLeftPanel?.SetFocusable(focusable);
		_weaponRightPanel?.SetFocusable(focusable);
		ApplyFocusable(_consumablePanels, focusable);
		ApplyFocusable(_backpackPanels, focusable);
	}

	static void ApplyFocusable(Array<ItemSlotPanel> panels, bool focusable)
	{
		if (panels == null)
		{
			return;
		}
		foreach (ItemSlotPanel panel in panels)
		{
			panel?.SetFocusable(focusable);
		}
	}

	// Put focus back on the last-focused slot — used after a sub-modal that
	// stole focus closes.
	public void RestoreFocus()
	{
		ItemSlotPanel target = _focused ?? FindFirstFocusable();
		target?.GrabFocus();
	}

	ItemSlotPanel FindFirstFocusable()
	{
		if (_backpackPanels != null)
		{
			foreach (ItemSlotPanel panel in _backpackPanels)
			{
				if (panel != null) { return panel; }
			}
		}
		return _armorHeadPanel ?? _armorBodyPanel ?? _weaponLeftPanel ?? _weaponRightPanel;
	}

	bool IsBackpackPanel(ItemSlotPanel panel)
	{
		return panel != null && _backpackPanels != null && _backpackPanels.Contains(panel);
	}

	void UpdateButtonHints()
	{
		ItemState item = _focused?.Item;
		bool hasItem = item != null;
		bool inBackpack = IsBackpackPanel(_focused);

		if (_buttonHintEquip != null)
		{
			_buttonHintEquip.Visible = hasItem && CanEquipOrUnequip(item);
			_buttonHintEquip.ActionName = inBackpack ? "Equip" : "Unequip";
		}
		if (_buttonHintUse != null)
		{
			_buttonHintUse.Visible = hasItem && CanUseItem(item);
			_buttonHintUse.SetProgress(0f);
		}
		if (_buttonHintDrop != null)
		{
			_buttonHintDrop.Visible = hasItem;
			_buttonHintDrop.SetProgress(0f);
		}
	}

	static bool CanEquipOrUnequip(ItemState item)
	{
		return item?.data is ArmorData or WeaponData or ConsumableData;
	}

	static bool CanUseItem(ItemState item)
	{
		return item is ConsumableState consumable && consumable.data?.actionProfile != null;
	}

	public override void _Process(double delta)
	{
		if (!_active || _player == null)
		{
			return;
		}

		ItemState item = _focused?.Item;
		bool dropActionRegistered = item != null && InputMap.HasAction(_dropAction);
		bool dropHeld = !DropLocked
			&& dropActionRegistered
			&& Input.IsActionPressed(_dropAction);
		if (dropHeld)
		{
			_dropHold += (float)delta;
			float progress = Mathf.Clamp(_dropHold / DropHoldSeconds, 0f, 1f);
			_buttonHintDrop?.SetProgress(progress);
			if (_dropHold >= DropHoldSeconds)
			{
				// Threshold crossed — fire the hold signal and lock so the
				// still-held key doesn't keep re-firing on subsequent frames.
				// InventoryScreen clears DropLocked when the count panel closes.
				_dropHold = 0f;
				_buttonHintDrop?.SetProgress(0f);
				DropLocked = true;
				onDropHoldComplete?.Invoke(item);
			}
		}
		else if (_dropHold > 0f)
		{
			// Released before threshold — tap. Drop a single unit of the
			// stack (non-stackable items drop in full since min stack is 1).
			if (dropActionRegistered)
			{
				_inventory?.Drop(item, 1);
			}
			_dropHold = 0f;
			_buttonHintDrop?.SetProgress(0f);
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!_active)
		{
			return;
		}

		ItemState item = _focused?.Item;
		if (InputMap.HasAction(_useAction))
		{
			if (e.IsActionPressed(_useAction))
			{
				if (item != null)
				{
					DoUseStart(item);
				}
				GetViewport().SetInputAsHandled();
				return;
			}
			if (e.IsActionReleased(_useAction))
			{
				DoUseRelease();
				GetViewport().SetInputAsHandled();
				return;
			}
		}
	}

	void CancelHeldActions()
	{
		_dropHold = 0f;
		_buttonHintDrop?.SetProgress(0f);
		if (_useStarted)
		{
			_player?.Runner?.TryAbort();
			_useStarted = false;
		}
	}

	void DoToggleEquip(ItemSlotPanel panel, ItemState item)
	{
		if (_inventory == null || item == null)
		{
			return;
		}
		if (IsBackpackPanel(panel))
		{
			EquipFromBackpack(item);
		}
		else
		{
			UnequipToBackpack(item);
		}
		RefreshAll();
	}

	void EquipFromBackpack(ItemState item)
	{
		switch (item.data)
		{
			case ArmorData armor:
				_inventory.TryEquip(item, armor.armorSlot);
				break;
			case WeaponData weapon:
				// Two-hand layout: ranged (ammo-bearing) lands in the right
				// slot, melee in the left — matches Player.Initialize's
				// PlayerSpawnData wiring since WeaponData itself doesn't
				// author a target slot.
				EInventorySlot target = weapon.useAmmo ? EInventorySlot.WeaponRight : EInventorySlot.WeaponLeft;
				_inventory.TryEquip(item, target);
				break;
			case ConsumableData:
				_inventory.TryMoveToConsumableSlot(item);
				break;
		}
	}

	void UnequipToBackpack(ItemState item)
	{
		EInventorySlot? slot = _inventory.GetEquippedSlot(item);
		if (slot.HasValue && slot.Value != EInventorySlot.Consumable)
		{
			_inventory.TryUnequip(slot.Value);
			return;
		}
		// GetEquippedSlot only reports the ACTIVE consumable hotbar slot.
		// Items in inactive hotbar slots have to be removed by scanning the
		// hotbar directly.
		_inventory.TryRemoveFromConsumableSlot(item);
	}

	void DoUseStart(ItemState item)
	{
		if (item is not ConsumableState consumable)
		{
			return;
		}
		ConsumableData data = consumable.data;
		if (data?.actionProfile == null)
		{
			return;
		}
		ActionRunner runner = _player?.Runner;
		if (runner == null || runner.IsBusy)
		{
			return;
		}
		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Consumable,
		};
		if (runner.TryStart(data.actionProfile, context))
		{
			_useStarted = true;
		}
	}

	void DoUseRelease()
	{
		if (!_useStarted)
		{
			return;
		}
		_player?.Runner?.OnInputReleased();
		_useStarted = false;
	}

	void DoDrop(ItemState item)
	{
		_inventory?.Drop(item);
		RefreshAll();
	}
}
