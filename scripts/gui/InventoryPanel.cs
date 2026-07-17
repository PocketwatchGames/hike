using Godot;
using System.Collections.Generic;
using Godot.Collections;

// The interactive inventory body — slot grid, button hints, focus tracking,
// and the input plumbing that turns presses into verb callbacks. The panel
// itself owns NO verb behavior; the controlling screen (InventoryScreen for
// gameplay equip/use/drop, CookingScreen for cook/drop) wires
// onPrimaryTap / onPrimaryHoldComplete / onTertiaryPressed / onTertiaryReleased /
// onSecondaryTap / onSecondaryHoldComplete plus the button-hint labels so this script
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
	// Optional in-panel backpack grid. The material-carrying screens (inventory /
	// stash / cooking) render the backpack with a SEPARATE BackpackPanel and leave
	// this empty, so InventoryPanel here is just the equip-slot cluster; the legacy
	// MerchantScreen still drives its backpack through these slots.
	[Export] private Array<ItemSlotPanel> _backpackPanels;
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _buttonHintSecondary;
	[Export] private ButtonHint _buttonHintTertiary;

	// Input actions surfaced as button-hint glyphs. The Primary action drives
	// ui_select tap/hold detection through ButtonDown/ButtonUp on each slot;
	// Secondary uses a custom action with tap/hold semantics (drop); Tertiary
	// uses a custom action with press/release semantics (use).
	[Export] private StringName _primaryAction = "ui_select";
	[Export] private StringName _secondaryAction = "MenuSecondary";
	[Export] private StringName _tertiaryAction = "MenuTertiary";

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
	public StringName SecondaryAction => _secondaryAction;
	public StringName TertiaryAction => _tertiaryAction;

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
		_buttonHintTertiary?.SetHint(_tertiaryAction, _buttonHintTertiary.ActionName);
	}

	public override void _ExitTree()
	{
		if (_inventory != null)
		{
			_inventory.onSlotChanged -= OnInventoryChanged;
			_inventory.onConsumableChanged -= OnConsumableChanged;
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
			_inventory.onConsumableChanged -= OnConsumableChanged;
			_inventory.onChanged -= OnInventoryGenericChanged;
		}
		_player = player;
		_inventory = player.Inventory;
		if (_inventory != null)
		{
			_inventory.onSlotChanged += OnInventoryChanged;
			_inventory.onConsumableChanged += OnConsumableChanged;
			// Generic pulse fires for stack-count mutations (e.g. consumable
			// Use's DecrementStack event) that the slot signals don't cover.
			_inventory.onChanged += OnInventoryGenericChanged;
		}
		_active = true;
		// Inherited-press guard: when the same physical button drives both
		// Interact (open) and Drop (here), the press that opened the screen
		// still reads as Drop. Latch awaiting-release so the tick below
		// waits for a clean release before processing.
		_dropAwaitingRelease = InputMap.HasAction(_secondaryAction) && Input.IsActionPressed(_secondaryAction);
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
			_inventory.onConsumableChanged -= OnConsumableChanged;
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
	void OnConsumableChanged() => RefreshAll();
	void OnInventoryGenericChanged() => RefreshAll();

	public void RefreshAll()
	{
		if (_inventory == null)
		{
			return;
		}

		_armorHeadPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.Helmet));
		_armorBodyPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.Armor));
		_weaponLeftPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.WeaponMelee));
		_weaponRightPanel?.SetItem(_inventory.GetEquipped(EInventorySlot.WeaponRanged));

		if (_consumablePanels != null)
		{
			// The single consumable slot now holds the attuned alchemy spell's cast
			// instance (shown in the first panel); any further panels stay empty.
			ItemState attuned = _inventory.GetActiveConsumable();
			for (int i = 0; i < _consumablePanels.Count; i++)
			{
				_consumablePanels[i]?.SetItem(i == 0 ? attuned : null);
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

	// Resolve a panel to its EInventorySlot identity (Helmet, Armor, WeaponMelee,
	// WeaponRanged, Equipment, or None for backpack). For Equipment, the slot is
	// shared across the hotbar — use GetConsumableIndex to get the specific
	// position.
	public EInventorySlot GetEquipSlotKind(ItemSlotPanel panel)
	{
		if (panel == null) { return EInventorySlot.None; }
		if (panel == _armorHeadPanel) { return EInventorySlot.Helmet; }
		if (panel == _armorBodyPanel) { return EInventorySlot.Armor; }
		if (panel == _weaponLeftPanel) { return EInventorySlot.WeaponMelee; }
		if (panel == _weaponRightPanel) { return EInventorySlot.WeaponRanged; }
		if (_consumablePanels != null && _consumablePanels.Contains(panel))
		{
			return EInventorySlot.Equipment;
		}
		return EInventorySlot.None;
	}

	// Hotbar index for a consumable panel, -1 for any other panel kind.
	public int GetConsumableIndex(ItemSlotPanel panel)
	{
		if (panel == null || _consumablePanels == null) { return -1; }
		return _consumablePanels.IndexOf(panel);
	}

	// Backpack index for a backpack panel, -1 for any other panel kind. The
	// backpack is a sparse array under the hood (Inventory.Backpack[i] is
	// the item at slot i or null), so the panel's grid position maps
	// directly to the inventory's storage index — no list-vs-grid offset.
	public int GetBackpackPanelIndex(ItemSlotPanel panel)
	{
		if (panel == null || _backpackPanels == null) { return -1; }
		return _backpackPanels.IndexOf(panel);
	}

	// First EMPTY backpack slot, used as the auto-target when unequipping
	// (or moving items out of the equip slots / stash) in select mode. The
	// player's expectation is that the item lands in the first available
	// open position, not slot 0 displacing whatever lived there. Falls back
	// to the first backpack panel if every slot is occupied, then to
	// FindFirstFocusable if no backpack panels exist.
	public ItemSlotPanel GetFirstBackpackPanel()
	{
		if (_backpackPanels == null || _backpackPanels.Count == 0)
		{
			return FindFirstFocusable();
		}
		foreach (ItemSlotPanel p in _backpackPanels)
		{
			if (p != null && p.Item == null)
			{
				return p;
			}
		}
		return _backpackPanels[0];
	}

	// Resolve the auto-target slot for select mode: backpack items snap to
	// their natural equip slot (head/body/L/R/first-empty consumable); already-
	// equipped items snap to the first backpack slot. Items with no natural
	// target return null so the caller can fall back to the current focus.
	public ItemSlotPanel FindAutoTargetForSelect(ItemSlotPanel source, ItemState item)
	{
		if (item?.data == null) { return null; }
		bool sourceIsBackpack = IsBackpackPanel(source);
		if (sourceIsBackpack)
		{
			switch (item.data)
			{
				case ArmorData armor:
					return armor.armorSlot == EInventorySlot.Helmet ? _armorHeadPanel : _armorBodyPanel;
				case WeaponData weapon:
					return weapon.CanonicalSlot == EInventorySlot.WeaponRanged ? _weaponRightPanel : _weaponLeftPanel;
				case ConsumableData:
					return FindFirstEmptyConsumablePanel() ?? (_consumablePanels?.Count > 0 ? _consumablePanels[0] : null);
			}
			return null;
		}
		// Source is an equip slot — autotarget the first backpack position.
		return GetFirstBackpackPanel();
	}

	ItemSlotPanel FindFirstEmptyConsumablePanel()
	{
		if (_consumablePanels == null) { return null; }
		foreach (ItemSlotPanel p in _consumablePanels)
		{
			if (p != null && p.Item == null) { return p; }
		}
		return null;
	}

	// Walks every slot the panel manages so callers can apply ghost / dim
	// state uniformly (e.g., clear all ghosts before re-applying on the
	// currently-focused slot).
	public IEnumerable<ItemSlotPanel> EnumerateAllSlots()
	{
		if (_armorHeadPanel != null) { yield return _armorHeadPanel; }
		if (_armorBodyPanel != null) { yield return _armorBodyPanel; }
		if (_weaponLeftPanel != null) { yield return _weaponLeftPanel; }
		if (_weaponRightPanel != null) { yield return _weaponRightPanel; }
		if (_consumablePanels != null)
		{
			foreach (ItemSlotPanel p in _consumablePanels)
			{
				if (p != null) { yield return p; }
			}
		}
		if (_backpackPanels != null)
		{
			foreach (ItemSlotPanel p in _backpackPanels)
			{
				if (p != null) { yield return p; }
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
		bool dropActionRegistered = item != null && InputMap.HasAction(_secondaryAction);
		// Clear the inherited-press guard the first frame Drop reads
		// unpressed — only then will subsequent presses fire tap/hold.
		if (_dropAwaitingRelease)
		{
			if (!dropActionRegistered || !Input.IsActionPressed(_secondaryAction))
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
			&& Input.IsActionPressed(_secondaryAction)
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
		if (InputMap.HasAction(_tertiaryAction) && onTertiaryPressed != null)
		{
			// The tertiary action's binding can overlap a screen-level action
			// (e.g. AlmanacScreen's TabRight shares RB with MenuTertiary). Only
			// consume the press when the screen has actually surfaced the verb
			// for the focused item — otherwise the input falls through to the
			// wrapping screen and does what the player expects there.
			bool tertiaryAvailable = _buttonHintTertiary != null && _buttonHintTertiary.Visible;
			if (e.IsActionPressed(_tertiaryAction))
			{
				if (item != null && tertiaryAvailable)
				{
					_tertiaryStarted = true;
					onTertiaryPressed.Invoke(_focused, item);
					GetViewport().SetInputAsHandled();
				}
				return;
			}
			if (e.IsActionReleased(_tertiaryAction))
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
