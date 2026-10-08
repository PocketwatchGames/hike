using Godot;
using System.Collections.Generic;

// The interactive inventory body — slot grid, button hints, focus tracking,
// and the input plumbing that turns presses into verb callbacks. The panel
// itself owns NO verb behavior; the controlling screen (InventoryScreen for
// gameplay equip/use/drop, CookingScreen for cook/drop) wires
// onPrimaryTap / onPrimaryHoldComplete / onTertiaryPressed / onTertiaryReleased /
// onSecondaryTap / onSecondaryHoldComplete plus the button-hint labels so this script
// can be reused under any modal that lays out the player's items.
//
// The panel is dormant until the screen calls Bind(player) on it; Unbind()
// detaches from inventory signals and stops reacting to input. A screen that
// owns its own input (StashScreen, InventoryScreen) never binds: it listens to
// BeltGrid / BackpackGrid / EquipPanel directly and repaints through Paint.
[GlobalClass]
public partial class InventoryPanel : Control
{
	[Export] private ItemSlotPanel _shieldPanel;
	[Export] private ItemSlotPanel _armorBodyPanel;
	[Export] private ItemSlotPanel _weaponLeftPanel;
	[Export] private ItemSlotPanel _weaponRightPanel;
	// The belt and backpack grids. Their slots are owned by each BackpackPanel,
	// sized to the inventory's grids; this panel only listens to their events and
	// repaints them from the bound inventory.
	[Export] private BackpackPanel _belt;
	[Export] private BackpackPanel _backpack;
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _buttonHintSecondary;
	[Export] private ButtonHint _buttonHintTertiary;

	// Input actions surfaced as button-hint glyphs. The Primary action drives
	// ui_select tap/hold detection through ButtonDown/ButtonUp on each slot; the
	// Secondary verb (drop) is a polled tap/hold on _dropAction; the Tertiary verb
	// (use) is a press/release on _useAction.
	[Export] private StringName _primaryAction = "ui_select";
	[Export] private StringName _dropAction = "MenuTertiary";
	[Export] private StringName _useAction = "MenuSecondary";

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
	public System.Action<ItemSlotPanel, ItemState> onTertiaryPressed;
	public System.Action onTertiaryReleased;
	public System.Action<ItemSlotPanel, ItemState> onSecondaryTap;
	public System.Action<ItemSlotPanel, ItemState> onSecondaryHoldComplete;

	// Button hint references — screen sets `.Visible` / `.ActionName` /
	// `.SetHint(...)` to control labels per-context. Visibility is left to
	// the screen: the panel does NOT auto-hide hints based on item presence.
	public ButtonHint ButtonHintPrimary => _buttonHintPrimary;
	public ButtonHint ButtonHintSecondary => _buttonHintSecondary;
	public ButtonHint ButtonHintTertiary => _buttonHintTertiary;

	public StringName PrimaryAction => _primaryAction;
	public StringName SecondaryAction => _dropAction;
	public StringName TertiaryAction => _useAction;

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
	// Panel-identity half of the EmitFocusedItem dedupe. A focus move from
	// panel A (potion) → panel B (same potion kind) needs to fire even though
	// the item reference matches — listeners drive button-hint labels off the
	// panel (e.g., Equip vs Unequip flips on backpack vs equip slot), so a
	// content-only compare suppresses meaningful focus moves.
	ItemSlotPanel _lastFocusedPanel;
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
	// True between a tertiary press and its release. Lets the release
	// callback fire only when we actually started something, and lets focus
	// changes / Unbind() abort the in-flight callback chain cleanly.
	bool _tertiaryStarted;
	// Bind/Unbind gate. Signal subscriptions, input handling, and per-frame
	// ticks all key off this so the panel stays inert before the screen has
	// shown it.
	bool _active;

	public override void _Ready()
	{
		WirePanel(_shieldPanel);
		WirePanel(_armorBodyPanel);
		WirePanel(_weaponLeftPanel);
		WirePanel(_weaponRightPanel);
		WireGrid(_belt);
		WireGrid(_backpack);

		// Seed every hint with its bound action's glyph. The screen overrides
		// `ActionName` per-context (Equip / Cook / Use / Drop) but the glyph
		// stays driven by the same input action regardless of the label.
		_buttonHintPrimary?.SetHint(_primaryAction, _buttonHintPrimary.ActionName);
		_buttonHintSecondary?.SetHint(_dropAction, _buttonHintSecondary.ActionName);
		_buttonHintTertiary?.SetHint(_useAction, _buttonHintTertiary.ActionName);
	}

	public override void _ExitTree()
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
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
			_inventory.onChanged -= OnInventoryGenericChanged;
		}
		_player = player;
		_inventory = player.Inventory;
		if (_inventory != null)
		{
			_inventory.onSlotChanged += OnInventoryChanged;
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
			_inventory.onChanged -= OnInventoryGenericChanged;
		}
		_inventory = null;
		_player = null;
		_active = false;
		// Reset cached focus dedupe state so the next Bind doesn't suppress
		// the initial fire by comparing against stale values.
		_lastFocusedItem = null;
		_lastFocusedPanel = null;
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

	void WireGrid(BackpackPanel grid)
	{
		if (grid == null)
		{
			return;
		}
		grid.onSlotFocused += (_, p) => OnPanelFocused(p);
		grid.onSlotButtonDown += (_, p) => OnPanelButtonDown(p);
		grid.onSlotButtonUp += (_, p) => OnPanelButtonUp(p);
	}

	void OnInventoryChanged(EInventorySlot _) => RefreshAll();
	void OnInventoryGenericChanged() => RefreshAll();

	// The equip slots the panel shows, in display order.
	public static readonly EInventorySlot[] EquipSlots =
	{
		EInventorySlot.Armor, EInventorySlot.Shield, EInventorySlot.WeaponLeft, EInventorySlot.WeaponRight,
	};

	public BackpackPanel BeltGrid => _belt;
	public BackpackPanel BackpackGrid => _backpack;

	public ItemSlotPanel EquipPanel(EInventorySlot slot)
	{
		return slot switch
		{
			EInventorySlot.Shield => _shieldPanel,
			EInventorySlot.Armor => _armorBodyPanel,
			EInventorySlot.WeaponLeft => _weaponLeftPanel,
			EInventorySlot.WeaponRight => _weaponRightPanel,
			_ => null,
		};
	}

	// Repaint every slot from `inventory`, the lit lantern marked. Each grid shows
	// as many slots as the inventory's grid has. Needs no Bind.
	public void Paint(Inventory inventory)
	{
		foreach (EInventorySlot slot in EquipSlots)
		{
			EquipPanel(slot)?.SetItem(inventory?.GetEquipped(slot));
		}
		_belt?.Refresh(inventory?.Belt.Slots);
		_backpack?.Refresh(inventory?.Backpack.Slots);
		if (_belt != null)
		{
			foreach (ItemSlotPanel p in _belt.EnumerateSlots())
			{
				p.SetActive(inventory != null && inventory.IsLit(p.Item));
			}
		}
	}

	public void RefreshAll()
	{
		if (_inventory == null)
		{
			return;
		}

		Paint(_inventory);
		// Focused slot may now hold a different item (e.g. a drop shifted the
		// backpack list under the focus index). Pulse so the screen reflects
		// the new content; EmitFocusedItem suppresses no-op fires.
		EmitFocusedItem();
	}

	void OnPanelFocused(ItemSlotPanel panel)
	{
		_focused = panel;
		CancelHeldActions();
		// A real focus change always fires — even if both the panel and item
		// match the last broadcast, the player may have navigated to another
		// screen and back, and listeners (button hints, side info panels,
		// select-mode ghosts) need to re-sync.
		_lastFocusedPanel = _focused;
		_lastFocusedItem = _focused?.Item;
		onFocusedItemChanged?.Invoke(_focused, _lastFocusedItem);
	}

	// Used by RefreshAll to surface item-content changes inside the currently-
	// focused panel without re-firing for unrelated state. Suppresses when
	// both the panel AND its item are unchanged from the last broadcast.
	// `force` skips the check — used on Bind so the side panels populate even
	// if focus didn't move. Note that real focus changes go through
	// OnPanelFocused, which always fires unconditionally.
	//
	// Also gated on `_focused.HasButtonFocus()`: when the user has navigated
	// to a sibling panel we don't manage (give slot, stash slot, etc.), our
	// `_focused` lags behind the real OS focus. Surfacing it as a focus event
	// confuses screen-level listeners that own their own focus tracking and
	// would overwrite their real focus state with a stale one — most visibly,
	// a commit-time inventory mutation would re-fire onFocusedItemChanged
	// with the now-stale source slot and paint a "Cancel" ghost on it.
	void EmitFocusedItem(bool force = false)
	{
		if (!force && _focused != null && !_focused.HasButtonFocus())
		{
			return;
		}
		ItemState current = _focused?.Item;
		if (!force && _focused == _lastFocusedPanel && current == _lastFocusedItem)
		{
			return;
		}
		_lastFocusedPanel = _focused;
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
		_shieldPanel?.SetFocusable(focusable);
		_armorBodyPanel?.SetFocusable(focusable);
		_weaponLeftPanel?.SetFocusable(focusable);
		_weaponRightPanel?.SetFocusable(focusable);
		_belt?.SetFocusable(focusable);
		_backpack?.SetFocusable(focusable);
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
		return _belt?.GetSlot(0) ?? _backpack?.GetSlot(0) ?? _shieldPanel ?? _armorBodyPanel ?? _weaponLeftPanel ?? _weaponRightPanel;
	}

	// True for a slot of the belt or backpack grid, false for an equip slot.
	public bool IsGridPanel(ItemSlotPanel panel)
	{
		return GridIndexOf(panel, out _) >= 0;
	}

	// Resolve a panel to its EInventorySlot identity (Armor, Shield, WeaponLeft,
	// WeaponRight, or None for a grid slot).
	public EInventorySlot GetEquipSlotKind(ItemSlotPanel panel)
	{
		if (panel == null) { return EInventorySlot.None; }
		if (panel == _shieldPanel) { return EInventorySlot.Shield; }
		if (panel == _armorBodyPanel) { return EInventorySlot.Armor; }
		if (panel == _weaponLeftPanel) { return EInventorySlot.WeaponLeft; }
		if (panel == _weaponRightPanel) { return EInventorySlot.WeaponRight; }
		return EInventorySlot.None;
	}

	// The bound inventory's grid behind a grid panel and the slot index in it —
	// the panel's grid position IS the grid's storage index. False for an equip
	// slot, or when nothing is bound.
	public bool TryGetGridSlot(ItemSlotPanel panel, out Inventory.CarriedGrid grid, out int index)
	{
		index = GridIndexOf(panel, out bool belt);
		grid = index < 0 || _inventory == null ? null : belt ? _inventory.Belt : _inventory.Backpack;
		return grid != null;
	}

	int GridIndexOf(ItemSlotPanel panel, out bool belt)
	{
		belt = false;
		if (panel == null)
		{
			return -1;
		}
		int index = _belt?.IndexOf(panel) ?? -1;
		if (index >= 0)
		{
			belt = true;
			return index;
		}
		return _backpack?.IndexOf(panel) ?? -1;
	}

	// First EMPTY backpack slot, else the belt's, used as the auto-target when
	// unequipping (or moving items out of the equip slots / stash) in select mode.
	// The player's expectation is that the item lands in the first available open
	// position, not slot 0 displacing whatever lived there. Falls back to the
	// first backpack panel if every slot is occupied, then to FindFirstFocusable.
	public ItemSlotPanel GetFirstBackpackPanel()
	{
		ItemSlotPanel backpack = _backpack?.FirstEmptyOrFirst();
		if (backpack?.Item == null && backpack != null)
		{
			return backpack;
		}
		ItemSlotPanel belt = _belt?.FirstEmptyOrFirst();
		if (belt?.Item == null && belt != null)
		{
			return belt;
		}
		return backpack ?? belt ?? FindFirstFocusable();
	}

	// Resolve the auto-target slot for select mode: backpack items snap to
	// their natural equip slot (body/shield/L/R/first-empty consumable); already-
	// equipped items snap to the first backpack slot. Items with no natural
	// target return null so the caller can fall back to the current focus.
	public ItemSlotPanel FindAutoTargetForSelect(ItemSlotPanel source, ItemState item)
	{
		if (item?.data == null) { return null; }
		if (IsGridPanel(source))
		{
			switch (item.data)
			{
				case ArmorData armor:
					return armor.EquipSlotKind == EInventorySlot.Armor ? _armorBodyPanel : null;
				case ShieldData:
					return _shieldPanel;
				case WeaponData weapon:
					return weapon.CanonicalSlot == EInventorySlot.WeaponRight ? _weaponRightPanel : _weaponLeftPanel;
			}
			return null;
		}
		// Source is an equip slot — autotarget the first open grid position.
		return GetFirstBackpackPanel();
	}

	// Walks every slot the panel manages so callers can apply ghost / dim
	// state uniformly (e.g., clear all ghosts before re-applying on the
	// currently-focused slot).
	public IEnumerable<ItemSlotPanel> EnumerateAllSlots()
	{
		if (_shieldPanel != null) { yield return _shieldPanel; }
		if (_armorBodyPanel != null) { yield return _armorBodyPanel; }
		if (_weaponLeftPanel != null) { yield return _weaponLeftPanel; }
		if (_weaponRightPanel != null) { yield return _weaponRightPanel; }
		if (_belt != null)
		{
			foreach (ItemSlotPanel p in _belt.EnumerateSlots())
			{
				yield return p;
			}
		}
		if (_backpack != null)
		{
			foreach (ItemSlotPanel p in _backpack.EnumerateSlots())
			{
				yield return p;
			}
		}
	}

	// Clear ghost + dim state on every slot — used when entering/exiting select
	// mode or after a commit so the panel doesn't carry stale visual flags.
	public void ClearSelectVisuals()
	{
		foreach (ItemSlotPanel p in EnumerateAllSlots())
		{
			p.SetGhost(null);
			p.SetDimmed(false);
		}
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
			&& onSecondaryHoldComplete != null;
		if (dropHeld)
		{
			_dropHold += dt;
			float progress = Mathf.Clamp(_dropHold / HoldSeconds, 0f, 1f);
			_buttonHintSecondary?.SetProgress(progress);
			if (_dropHold >= HoldSeconds)
			{
				_dropHold = 0f;
				_buttonHintSecondary?.SetProgress(0f);
				HoldLocked = true;
				onSecondaryHoldComplete.Invoke(_focused, item);
			}
		}
		else if (_dropHold > 0f)
		{
			// Released before threshold — tap.
			if (dropActionRegistered)
			{
				onSecondaryTap?.Invoke(_focused, item);
			}
			_dropHold = 0f;
			_buttonHintSecondary?.SetProgress(0f);
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!_active)
		{
			return;
		}

		// Gate on actual focus ownership so the global tertiary key doesn't
		// fire here while a sibling panel (CookingPanel) holds focus.
		bool focused = _focused != null && _focused.HasButtonFocus();
		ItemState item = focused ? _focused.Item : null;
		if (InputMap.HasAction(_useAction) && onTertiaryPressed != null)
		{
			// The tertiary action's binding can overlap a screen-level action
			// (e.g. a screen-level action sharing its button). Only
			// consume the press when the screen has actually surfaced the verb
			// for the focused item — otherwise the input falls through to the
			// wrapping screen and does what the player expects there.
			bool tertiaryAvailable = _buttonHintTertiary != null && _buttonHintTertiary.Visible;
			if (e.IsActionPressed(_useAction))
			{
				if (item != null && tertiaryAvailable)
				{
					_tertiaryStarted = true;
					onTertiaryPressed.Invoke(_focused, item);
					GetViewport().SetInputAsHandled();
				}
				return;
			}
			if (e.IsActionReleased(_useAction))
			{
				if (_tertiaryStarted)
				{
					_tertiaryStarted = false;
					onTertiaryReleased?.Invoke();
					GetViewport().SetInputAsHandled();
				}
				return;
			}
		}
	}

	void CancelHeldActions()
	{
		_dropHold = 0f;
		_buttonHintSecondary?.SetProgress(0f);
		_primaryHold = 0f;
		_primaryHoldFired = false;
		_primaryPressed = null;
		_buttonHintPrimary?.SetProgress(0f);
		if (_tertiaryStarted)
		{
			_tertiaryStarted = false;
			onTertiaryReleased?.Invoke();
		}
	}
}
