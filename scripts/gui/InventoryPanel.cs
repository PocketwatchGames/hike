using Godot;
using System.Collections.Generic;
using Godot.Collections;

// The interactive inventory body — slot grid, button hints, focus tracking,
// and the input plumbing that turns presses into verb callbacks. The panel
// itself owns NO verb behavior; the controlling screen (InventoryScreen for
// gameplay equip/use/drop, CookingScreen for cook/drop) wires
// onPrimaryTap / onPrimaryHoldComplete / onSecondaryPressed / onSecondaryReleased /
// onDropTap / onDropHoldComplete plus the button-hint labels so this script
// can be reused under any modal that lays out the player's items.
//
// The panel is dormant until the screen calls Bind(player) on it; Unbind()
// detaches from inventory signals and stops reacting to input.
[GlobalClass]
public partial class InventoryPanel : Control
{
	[Export] private ItemSlotPanel _armorHeadPanel;
	[Export] private ItemSlotPanel _armorBodyPanel;
	[Export] private ItemSlotPanel _weaponLeftPanel;
	[Export] private ItemSlotPanel _weaponRightPanel;
	[Export] private Array<ItemSlotPanel> _consumablePanels;
	[Export] private Array<ItemSlotPanel> _backpackPanels;
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _buttonHintSecondary;
	[Export] private ButtonHint _buttonHintDrop;

	// Input actions surfaced as button-hint glyphs. The Primary action drives
	// ui_select tap/hold detection through ButtonDown/ButtonUp on each slot;
	// Secondary uses a custom action with press/release semantics; Drop uses
	// a custom action with tap/hold semantics.
	[Export] private StringName _primaryAction = "ui_select";
	[Export] private StringName _secondaryAction = "MenuSecondary";
	[Export] private StringName _dropAction = "MenuTertiary";

	// Fires whenever the focused slot's currently-displayed ItemState changes —
	// either because focus moved to a different slot, or because the focused
	// slot's contents mutated (used / dropped / equipped) and Refresh re-bound
	// a different item to the same panel. Screen subscribes to refresh the
	// side info panel AND update verb button-hint labels (e.g. Equip ↔ Unequip).
	public System.Action<ItemSlotPanel, ItemState> onFocusedItemChanged;

	// Verb callbacks — wired by the controlling screen. null = the panel
	// silently ignores that verb (no progress fill, no fire). Hold callbacks
	// are independent of tap callbacks: a tap-only verb leaves the hold one
	// null and never accumulates a hold timer.
	public System.Action<ItemSlotPanel, ItemState> onPrimaryTap;
	public System.Action<ItemSlotPanel, ItemState> onPrimaryHoldComplete;
	public System.Action<ItemSlotPanel, ItemState> onSecondaryPressed;
	public System.Action onSecondaryReleased;
	public System.Action<ItemSlotPanel, ItemState> onDropTap;
	public System.Action<ItemSlotPanel, ItemState> onDropHoldComplete;

	// Button hint references — screen sets `.Visible` / `.ActionName` /
	// `.SetHint(...)` to control labels per-context. Visibility is left to
	// the screen: the panel does NOT auto-hide hints based on item presence.
	public ButtonHint ButtonHintPrimary => _buttonHintPrimary;
	public ButtonHint ButtonHintSecondary => _buttonHintSecondary;
	public ButtonHint ButtonHintDrop => _buttonHintDrop;

	public StringName PrimaryAction => _primaryAction;
	public StringName SecondaryAction => _secondaryAction;
	public StringName DropAction => _dropAction;

	public ItemSlotPanel FocusedPanel => _focused;
	public ItemState FocusedItem => _focused?.Item;
	public Player Player => _player;
	public Inventory Inventory => _inventory;

	// External gate flipped by the screen while a sub-modal (count picker)
	// is on screen. Suspends every hold/tap tick so the still-held key can't
	// re-fire while the picker has focus.
	public bool HoldLocked { get; set; }

	const float HoldSeconds = 0.5f;

	Player _player;
	Inventory _inventory;
	ItemSlotPanel _focused;
	ItemState _lastFocusedItem;
	// Currently-pressed slot for the primary verb (set on ButtonDown, cleared
	// on ButtonUp). Drives the hold timer in _Process.
	ItemSlotPanel _primaryPressed;
	float _primaryHold;
	// Latched between a successful onPrimaryHoldComplete fire and the next
	// ButtonUp so the release isn't also treated as a tap.
	bool _primaryHoldFired;
	float _dropHold;
	// Latched on Bind when the Drop action was already held (e.g. the same
	// gamepad button is bound to both Interact and Drop — pressing Y to
	// open the campfire's cooking screen lands here with Drop reading
	// pressed). Tick suppresses drop processing until we observe Drop
	// released at least once, so the inherited press doesn't fire a tap
	// or hold on the freshly-opened panel.
	bool _dropAwaitingRelease;
	// True between a secondary press and its release. Lets the release
	// callback fire only when we actually started something, and lets focus
	// changes / Unbind() abort the in-flight callback chain cleanly.
	bool _secondaryStarted;
	// Bind/Unbind gate. Signal subscriptions, input handling, and per-frame
	// ticks all key off this so the panel stays inert before the screen has
	// shown it.
	bool _active;

	public override void _Ready()
	{
		WirePanel(_armorHeadPanel);
		WirePanel(_armorBodyPanel);
		WirePanel(_weaponLeftPanel);
		WirePanel(_weaponRightPanel);
		WirePanels(_consumablePanels);
		WirePanels(_backpackPanels);

		// Seed every hint with its bound action's glyph. The screen overrides
		// `ActionName` per-context (Equip / Cook / Use / Drop) but the glyph
		// stays driven by the same input action regardless of the label.
		_buttonHintPrimary?.SetHint(_primaryAction, _buttonHintPrimary.ActionName);
		_buttonHintSecondary?.SetHint(_secondaryAction, _buttonHintSecondary.ActionName);
		_buttonHintDrop?.SetHint(_dropAction, _buttonHintDrop.ActionName);
	}

	public override void _ExitTree()
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
			_inventory.onActiveConsumableChanged -= OnActiveConsumableChanged;
			_inventory.onChanged -= OnInventoryGenericChanged;
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
		// Inherited-press guard: when the same physical button drives both
		// Interact (open) and Drop (here), the press that opened the screen
		// still reads as Drop. Latch awaiting-release so the tick below
		// waits for a clean release before processing.
		_dropAwaitingRelease = InputMap.HasAction(_dropAction) && Input.IsActionPressed(_dropAction);
		RefreshAll();
		ItemSlotPanel start = _focused ?? FindFirstFocusable();
		start?.GrabFocus();
		// Force a focused-item pulse so the screen can sync side panels and
		// button hints on Open even if focus is already where it needs to be.
		EmitFocusedItem(force: true);
	}

	// Detach from the player's inventory. Cancels any in-flight hold/secondary
	// state so a later Bind starts clean.
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
		panel.onButtonDown += OnPanelButtonDown;
		panel.onButtonUp += OnPanelButtonUp;
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

	public void RefreshAll()
	{
		if (_inventory == null)
		{
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
		// Focused slot may now hold a different item (e.g. a drop shifted the
		// backpack list under the focus index). Pulse so the screen reflects
		// the new content; EmitFocusedItem suppresses no-op fires.
		EmitFocusedItem();
	}

	void OnPanelFocused(ItemSlotPanel panel)
	{
		_focused = panel;
		CancelHeldActions();
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
		onFocusedItemChanged?.Invoke(_focused, current);
	}

	void OnPanelButtonDown(ItemSlotPanel panel)
	{
		_primaryPressed = panel;
		_primaryHold = 0f;
		_primaryHoldFired = false;
		_buttonHintPrimary?.SetProgress(0f);
	}

	// Mouse / keyboard release on the focused slot. If the hold timer crossed
	// the threshold during the press, we already fired onPrimaryHoldComplete —
	// in that case eat the release. Otherwise this is a tap.
	void OnPanelButtonUp(ItemSlotPanel panel)
	{
		ItemSlotPanel pressed = _primaryPressed;
		_primaryPressed = null;
		_buttonHintPrimary?.SetProgress(0f);
		float held = _primaryHold;
		_primaryHold = 0f;
		bool fired = _primaryHoldFired;
		_primaryHoldFired = false;
		if (fired || HoldLocked || !_active)
		{
			return;
		}
		// Ignore the release if the user dragged focus off the originally
		// pressed slot before letting go — feels like a cancel gesture.
		if (pressed != null && pressed != panel)
		{
			return;
		}
		onPrimaryTap?.Invoke(panel, panel?.Item);
	}

	// Flip focusability on every slot. Used by the screen to keep ui_left/right
	// from traversing focus onto inventory slots while a sub-modal (count
	// picker) is up.
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

	public bool IsBackpackPanel(ItemSlotPanel panel)
	{
		return panel != null && _backpackPanels != null && _backpackPanels.Contains(panel);
	}

	public override void _Process(double delta)
	{
		if (!_active || _player == null)
		{
			return;
		}
		TickPrimaryHold((float)delta);
		TickDropHold((float)delta);
	}

	void TickPrimaryHold(float dt)
	{
		if (_primaryPressed == null || onPrimaryHoldComplete == null || HoldLocked || _primaryHoldFired)
		{
			return;
		}
		_primaryHold += dt;
		float progress = Mathf.Clamp(_primaryHold / HoldSeconds, 0f, 1f);
		_buttonHintPrimary?.SetProgress(progress);
		if (_primaryHold >= HoldSeconds)
		{
			ItemSlotPanel pressed = _primaryPressed;
			_primaryHoldFired = true;
			_primaryHold = 0f;
			_buttonHintPrimary?.SetProgress(0f);
			HoldLocked = true;
			onPrimaryHoldComplete.Invoke(pressed, pressed?.Item);
		}
	}

	void TickDropHold(float dt)
	{
		// Gate on actual focus ownership so the global Drop key doesn't
		// fire here while a sibling panel (CookingPanel) holds focus.
		ItemState item = _focused != null && _focused.HasButtonFocus() ? _focused.Item : null;
		bool dropActionRegistered = item != null && InputMap.HasAction(_dropAction);
		// Clear the inherited-press guard the first frame Drop reads
		// unpressed — only then will subsequent presses fire tap/hold.
		if (_dropAwaitingRelease)
		{
			if (!dropActionRegistered || !Input.IsActionPressed(_dropAction))
			{
				_dropAwaitingRelease = false;
			}
			else
			{
				return;
			}
		}
		bool dropHeld = !HoldLocked
			&& dropActionRegistered
			&& Input.IsActionPressed(_dropAction)
			&& onDropHoldComplete != null;
		if (dropHeld)
		{
			_dropHold += dt;
			float progress = Mathf.Clamp(_dropHold / HoldSeconds, 0f, 1f);
			_buttonHintDrop?.SetProgress(progress);
			if (_dropHold >= HoldSeconds)
			{
				_dropHold = 0f;
				_buttonHintDrop?.SetProgress(0f);
				HoldLocked = true;
				onDropHoldComplete.Invoke(_focused, item);
			}
		}
		else if (_dropHold > 0f)
		{
			// Released before threshold — tap.
			if (dropActionRegistered)
			{
				onDropTap?.Invoke(_focused, item);
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

		// Gate on actual focus ownership so the global secondary key doesn't
		// fire here while a sibling panel (CookingPanel) holds focus.
		bool focused = _focused != null && _focused.HasButtonFocus();
		ItemState item = focused ? _focused.Item : null;
		if (InputMap.HasAction(_secondaryAction) && onSecondaryPressed != null)
		{
			if (e.IsActionPressed(_secondaryAction))
			{
				if (item != null)
				{
					_secondaryStarted = true;
					onSecondaryPressed.Invoke(_focused, item);
					GetViewport().SetInputAsHandled();
				}
				return;
			}
			if (e.IsActionReleased(_secondaryAction))
			{
				if (_secondaryStarted)
				{
					_secondaryStarted = false;
					onSecondaryReleased?.Invoke();
					GetViewport().SetInputAsHandled();
				}
				return;
			}
		}
	}

	void CancelHeldActions()
	{
		_dropHold = 0f;
		_buttonHintDrop?.SetProgress(0f);
		_primaryHold = 0f;
		_primaryHoldFired = false;
		_primaryPressed = null;
		_buttonHintPrimary?.SetProgress(0f);
		if (_secondaryStarted)
		{
			_secondaryStarted = false;
			onSecondaryReleased?.Invoke();
		}
	}
}
