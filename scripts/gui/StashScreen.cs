using Godot;
using System.Collections.Generic;
using Godot.Collections;

// Modal stash screen — moves items between the player's Inventory and a
// chest's persistent ChestSimState.Contents. Interaction mirrors
// InventoryScreen / MerchantScreen: tap ui_accept on either side to "pick
// up" an item (select mode auto-targets the first empty slot on the
// opposite side); tap again on a valid destination to commit; ui_cancel
// aborts the pickup, or closes the screen when no selection is in flight.
// Hold ui_accept on a stackable opens the count picker for partial moves.
//
// Mutations land directly on _chest.SimState.Contents so changes persist
// across chunk eviction and save/load without an explicit sync-back.
[GlobalClass]
public partial class StashScreen : Control
{
	[Export] private InventoryPanel _playerInventory;
	[Export] private Array<ItemSlotPanel> _stashSlotPanels;
	[Export] private DropCountPanel _dropCountPanel;
	[Export] private ItemInfoPanel _itemInfoPanelStash;
	[Export] private ItemInfoPanel _itemInfoPanelInventory;

	GameClient _gameClient;
	Player _player;
	List<ItemState> _contents;

	enum EFocusedPanel { None, PlayerInventory, Stash }
	EFocusedPanel _focusedPanel = EFocusedPanel.None;
	ItemSlotPanel _focusedSlot;
	ItemState _focusedItem;
	int _focusedSlotIndex;

	// Select-mode state mirrors MerchantScreen. The source can be either side
	// — both player inventory and stash use the same select-then-commit flow,
	// with an auto-targeted slot on the opposite side so equip / unequip-style
	// moves are one tap-tap.
	ItemSlotPanel _selectedSource;
	EFocusedPanel _selectedSourceCategory;
	int _selectedSourceIndex;
	ItemState _selectedItem;
	int _selectedAmount;
	bool InSelectMode => _selectedItem != null;

	// Hold-to-count timer for stash slots. Player inventory slots use
	// InventoryPanel's own primary-hold path (onPrimaryHoldComplete).
	const float HoldSeconds = 0.5f;
	ItemSlotPanel _pressedSlot;
	float _holdTimer;
	bool _holdFired;

	// Drop hold timer for stash-side focus. Player inventory focus uses
	// InventoryPanel's own TickDropHold; we mirror that here for stash slots
	// because the panel only ticks when its own slot owns focus.
	float _stashSecondaryHold;
	bool _stashSecondaryHoldFired;
	// Latched between a tertiary press and release on a stash slot so the
	// release fires OnInputReleased only when we actually started the use.
	bool _stashTertiaryStarted;

	public override void _Ready()
	{
		Visible = false;
		if (_playerInventory != null)
		{
			_playerInventory.onFocusedItemChanged += OnInventoryFocusChanged;
			_playerInventory.onPrimaryTap += OnInventoryPrimaryTap;
			_playerInventory.onPrimaryHoldComplete += OnInventoryPrimaryHold;
			_playerInventory.onSecondaryTap += OnInventorySecondaryTap;
			_playerInventory.onSecondaryHoldComplete += OnInventorySecondaryHoldComplete;
			_playerInventory.onTertiaryPressed += OnInventoryTertiaryPressed;
			_playerInventory.onTertiaryReleased += OnInventoryTertiaryReleased;
		}
		WireStashSlots();
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		_itemInfoPanelStash?.SetItem(null);
		_itemInfoPanelInventory?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_playerInventory != null)
		{
			_playerInventory.onFocusedItemChanged -= OnInventoryFocusChanged;
			_playerInventory.onPrimaryTap -= OnInventoryPrimaryTap;
			_playerInventory.onPrimaryHoldComplete -= OnInventoryPrimaryHold;
			_playerInventory.onSecondaryTap -= OnInventorySecondaryTap;
			_playerInventory.onSecondaryHoldComplete -= OnInventorySecondaryHoldComplete;
			_playerInventory.onTertiaryPressed -= OnInventoryTertiaryPressed;
			_playerInventory.onTertiaryReleased -= OnInventoryTertiaryReleased;
		}
	}

	void WireStashSlots()
	{
		if (_stashSlotPanels == null)
		{
			return;
		}
		for (int i = 0; i < _stashSlotPanels.Count; i++)
		{
			ItemSlotPanel panel = _stashSlotPanels[i];
			if (panel == null)
			{
				continue;
			}
			int index = i;
			panel.onFocusEntered += p => OnStashSlotFocused(p, index);
			panel.onButtonDown += OnStashSlotButtonDown;
			panel.onButtonUp += p => OnStashSlotButtonUp(p, index);
		}
	}

	public void Open(Player player, Chest chest)
	{
		_player = player;
		_contents = chest?.SimState?.Contents;
		_gameClient = GameClient.Current;
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = true;
			if (_gameClient.hud != null)
			{
				_gameClient.hud.Visible = false;
			}
		}
		Input.MouseMode = Input.MouseModeEnum.Visible;
		_player?.ClearInteractive();
		ClearSelection();
		if (_playerInventory != null)
		{
			_playerInventory.ButtonHintSecondary?.SetHint(_playerInventory.SecondaryAction, "Drop");
			_playerInventory.ButtonHintTertiary?.SetHint(_playerInventory.TertiaryAction, "Use");
		}
		Visible = true;
		_playerInventory?.Bind(_player);
		// Listen for inv mutations so DecrementStack on a stash consumable
		// (Use) removes the dead stack from Contents and refreshes the grid.
		// Subscribe AFTER Bind so we don't double-fire on the bind pulse.
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged += OnInventoryStateChanged;
		}
		RefreshStashSlots();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= OnInventoryStateChanged;
		}
		CloseCountPicker();
		ClearSelection();
		ClearAllGhosts();
		_playerInventory?.Unbind();
		Visible = false;
		if (_gameClient != null)
		{
			_gameClient.InputSuppressed = false;
			if (_gameClient.hud != null)
			{
				_gameClient.hud.Visible = true;
			}
		}
		Input.MouseMode = Input.MouseModeEnum.Captured;
		_focusedSlot = null;
		_focusedItem = null;
		_focusedPanel = EFocusedPanel.None;
		_contents = null;
		_player = null;
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			// First ui_cancel cancels a pending selection (if any); a clean
			// state closes the screen.
			if (InSelectMode)
			{
				CancelSelect();
			}
			else
			{
				Close();
			}
			GetViewport().SetInputAsHandled();
			return;
		}
		// Tertiary (Use) on a stash slot — InventoryPanel handles its own
		// side internally when its own slot has focus; here we cover the
		// stash-side focus case.
		if (_playerInventory == null || _focusedPanel != EFocusedPanel.Stash || InSelectMode)
		{
			return;
		}
		StringName tertAction = _playerInventory.TertiaryAction;
		if (!InputMap.HasAction(tertAction))
		{
			return;
		}
		if (e.IsActionPressed(tertAction))
		{
			if (_focusedItem != null && CanUseItem(_focusedItem))
			{
				_stashTertiaryStarted = true;
				UseConsumable(_focusedItem);
				GetViewport().SetInputAsHandled();
			}
			return;
		}
		if (e.IsActionReleased(tertAction))
		{
			if (_stashTertiaryStarted)
			{
				_stashTertiaryStarted = false;
				_player?.Runner?.OnInputReleased();
				GetViewport().SetInputAsHandled();
			}
		}
	}

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}
		TickHold((float)delta);
		TickStashSecondaryHold((float)delta);
		TickTertiaryCharge();
	}

	// -------------------------------------------------------------------
	// Focus tracking — drives info panels + button hint label.
	// -------------------------------------------------------------------

	void OnInventoryFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		CancelHoldTimer();
		_focusedSlot = panel;
		_focusedPanel = EFocusedPanel.PlayerInventory;
		_focusedSlotIndex = -1;
		_focusedItem = item;
		RefreshGhostOnFocus();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void OnStashSlotFocused(ItemSlotPanel panel, int index)
	{
		CancelHoldTimer();
		_focusedSlot = panel;
		_focusedPanel = EFocusedPanel.Stash;
		_focusedSlotIndex = index;
		_focusedItem = panel?.Item;
		RefreshGhostOnFocus();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void UpdateInfoPanels()
	{
		if (InSelectMode)
		{
			// In select mode the info panels track the selected item on the
			// side that matches its source — so the player can keep their eye
			// on what they're moving even as the cursor wanders.
			bool sourceIsInv = _selectedSourceCategory == EFocusedPanel.PlayerInventory;
			_itemInfoPanelInventory?.SetItem(sourceIsInv ? _selectedItem : null);
			_itemInfoPanelStash?.SetItem(sourceIsInv ? null : _selectedItem);
			return;
		}
		bool invSide = _focusedPanel == EFocusedPanel.PlayerInventory;
		bool stashSide = _focusedPanel == EFocusedPanel.Stash;
		_itemInfoPanelInventory?.SetItem(invSide ? _focusedItem : null);
		_itemInfoPanelStash?.SetItem(stashSide ? _focusedItem : null);
	}

	void UpdateButtonHint()
	{
		ButtonHint primary = _playerInventory?.ButtonHintPrimary;
		ButtonHint drop = _playerInventory?.ButtonHintSecondary;
		ButtonHint use = _playerInventory?.ButtonHintTertiary;
		string primaryLabel = string.Empty;
		bool primaryVisible = false;
		bool dropVisible = false;
		bool useVisible = false;
		if (InSelectMode)
		{
			// Mid-selection: Drop / Use are meaningless until the move resolves.
			primaryLabel = ResolveDestinationLabel();
			primaryVisible = !string.IsNullOrEmpty(primaryLabel);
		}
		else
		{
			bool hasItem = _focusedItem != null;
			switch (_focusedPanel)
			{
				case EFocusedPanel.PlayerInventory:
					primaryLabel = "Stash";
					primaryVisible = hasItem;
					dropVisible = hasItem;
					useVisible = hasItem && CanUseItem(_focusedItem);
					break;
				case EFocusedPanel.Stash:
					primaryLabel = "Take";
					primaryVisible = hasItem;
					// Stash items are still the player's — Drop sends them to
					// the world; Use consumes them in place via the runner.
					dropVisible = hasItem;
					useVisible = hasItem && CanUseItem(_focusedItem);
					break;
			}
		}
		if (primary != null)
		{
			primary.ActionName = primaryLabel;
			primary.Visible = primaryVisible;
			primary.SetProgress(0f);
		}
		if (drop != null)
		{
			drop.Visible = dropVisible;
			if (!dropVisible)
			{
				drop.SetProgress(0f);
			}
		}
		if (use != null)
		{
			use.Visible = useVisible;
			if (!useVisible)
			{
				use.SetProgress(0f);
			}
		}
	}

	static bool CanUseItem(ItemState item)
	{
		return item is ConsumableState consumable && consumable.data?.actionProfile != null;
	}

	// What ui_accept will do on the currently-focused slot during select mode.
	// Empty string = no valid move (hint hidden). Drop onto source labels as
	// "Cancel" so the user knows they can pick another destination by moving
	// the cursor first.
	string ResolveDestinationLabel()
	{
		if (_focusedSlot == null) { return string.Empty; }
		if (_focusedSlot == _selectedSource) { return "Cancel"; }
		return IsValidSelectDestination(_focusedPanel) ? "Move" : string.Empty;
	}

	bool IsValidSelectDestination(EFocusedPanel destCategory)
	{
		// Valid moves cross the divide: player inventory ↔ stash only.
		switch (_selectedSourceCategory, destCategory)
		{
			case (EFocusedPanel.PlayerInventory, EFocusedPanel.Stash):
			case (EFocusedPanel.Stash, EFocusedPanel.PlayerInventory):
				return true;
			default:
				return false;
		}
	}

	void RefreshGhostOnFocus()
	{
		ClearAllGhosts();
		if (InSelectMode)
		{
			_selectedSource?.SetDimmed(true);
			if (_focusedSlot != null && IsValidSelectDestination(_focusedPanel))
			{
				_focusedSlot.SetGhost(_selectedItem);
			}
		}
	}

	void ClearAllGhosts()
	{
		_playerInventory?.ClearSelectVisuals();
		if (_stashSlotPanels != null)
		{
			foreach (ItemSlotPanel p in _stashSlotPanels)
			{
				p?.SetGhost(null);
				p?.SetDimmed(false);
			}
		}
	}

	// -------------------------------------------------------------------
	// Stash slot press handling.
	// -------------------------------------------------------------------

	void OnStashSlotButtonDown(ItemSlotPanel panel)
	{
		_pressedSlot = panel;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
	}

	void OnStashSlotButtonUp(ItemSlotPanel panel, int index)
	{
		bool fired = _holdFired;
		ItemSlotPanel pressed = _pressedSlot;
		_pressedSlot = null;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
		if (fired)
		{
			return;
		}
		if (pressed != null && pressed != panel)
		{
			return;
		}
		HandleStashSlotTap(panel, index);
	}

	void HandleStashSlotTap(ItemSlotPanel panel, int index)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.Stash);
			return;
		}
		ItemState item = panel?.Item;
		if (item == null)
		{
			return;
		}
		EnterSelectMode(panel, item, item.stackCount, EFocusedPanel.Stash, index);
	}

	void TickHold(float dt)
	{
		if (_pressedSlot == null || _holdFired)
		{
			return;
		}
		ItemState item = _pressedSlot.Item;
		if (item == null || item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			return;
		}
		_holdTimer += dt;
		float progress = Mathf.Clamp(_holdTimer / HoldSeconds, 0f, 1f);
		_playerInventory?.ButtonHintPrimary?.SetProgress(progress);
		if (_holdTimer >= HoldSeconds)
		{
			_holdFired = true;
			_holdTimer = 0f;
			_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
			HandleHoldComplete(_pressedSlot, _focusedSlotIndex, item);
		}
	}

	void HandleHoldComplete(ItemSlotPanel panel, int index, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.Stash);
			return;
		}
		OpenSelectCountPicker(panel, item, EFocusedPanel.Stash, index);
	}

	void CancelHoldTimer()
	{
		_pressedSlot = null;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
	}

	// -------------------------------------------------------------------
	// Player-inventory verb wiring (Select / hold-Select).
	// -------------------------------------------------------------------

	void OnInventoryPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.PlayerInventory);
			return;
		}
		if (item == null) { return; }
		EnterSelectMode(panel, item, item.stackCount, EFocusedPanel.PlayerInventory, -1);
	}

	void OnInventoryPrimaryHold(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.PlayerInventory);
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		if (item == null)
		{
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		if (item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			EnterSelectMode(panel, item, item.stackCount, EFocusedPanel.PlayerInventory, -1);
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		OpenSelectCountPicker(panel, item, EFocusedPanel.PlayerInventory, -1);
	}

	// -------------------------------------------------------------------
	// Select mode entry / cancel / commit.
	// -------------------------------------------------------------------

	void EnterSelectMode(ItemSlotPanel sourcePanel, ItemState item, int amount, EFocusedPanel category, int index)
	{
		_selectedSource = sourcePanel;
		_selectedSourceCategory = category;
		_selectedSourceIndex = index;
		_selectedItem = item;
		_selectedAmount = Mathf.Max(1, amount);
		sourcePanel?.SetDimmed(true);
		ItemSlotPanel autoTarget = FindAutoTargetForSelect(category);
		if (autoTarget != null && autoTarget != sourcePanel)
		{
			autoTarget.GrabFocus();
		}
		else
		{
			RefreshGhostOnFocus();
		}
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	// Auto-target the natural destination on the opposite side: first empty
	// stash slot for inventory sources, first backpack panel for stash
	// sources. Falls back to the first slot if all empties are taken so the
	// cursor still lands somewhere predictable.
	ItemSlotPanel FindAutoTargetForSelect(EFocusedPanel sourceCategory)
	{
		if (sourceCategory == EFocusedPanel.PlayerInventory)
		{
			return FindFirstEmptyStashSlot() ?? FirstStashSlot();
		}
		if (sourceCategory == EFocusedPanel.Stash)
		{
			return _playerInventory?.GetFirstBackpackPanel();
		}
		return null;
	}

	ItemSlotPanel FindFirstEmptyStashSlot()
	{
		if (_stashSlotPanels == null) { return null; }
		foreach (ItemSlotPanel p in _stashSlotPanels)
		{
			if (p != null && p.Item == null) { return p; }
		}
		return null;
	}

	ItemSlotPanel FirstStashSlot()
	{
		if (_stashSlotPanels == null || _stashSlotPanels.Count == 0) { return null; }
		return _stashSlotPanels[0];
	}

	void CancelSelect()
	{
		ItemSlotPanel source = _selectedSource;
		ClearSelection();
		ClearAllGhosts();
		UpdateInfoPanels();
		UpdateButtonHint();
		source?.GrabFocus();
	}

	void ClearSelection()
	{
		_selectedItem = null;
		_selectedAmount = 0;
		_selectedSource = null;
		_selectedSourceCategory = EFocusedPanel.None;
		_selectedSourceIndex = -1;
	}

	void CommitMove(ItemSlotPanel dest, EFocusedPanel destCategory)
	{
		if (_selectedItem == null || dest == null)
		{
			CancelSelect();
			return;
		}
		if (dest == _selectedSource)
		{
			CancelSelect();
			return;
		}
		if (!IsValidSelectDestination(destCategory))
		{
			return;
		}
		bool moved = ExecuteSelectMove(dest, destCategory);
		if (!moved)
		{
			// Leave the player in select mode so they can retry — but refresh
			// the ghost in case the slot's contents shifted under the cursor.
			RefreshGhostOnFocus();
			return;
		}
		ClearAllGhosts();
		ClearSelection();
		RefreshStashSlots();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	bool ExecuteSelectMove(ItemSlotPanel dest, EFocusedPanel destCategory)
	{
		int amount = Mathf.Min(_selectedAmount, _selectedItem?.stackCount ?? 0);
		if (amount <= 0) { return false; }
		switch (_selectedSourceCategory, destCategory)
		{
			case (EFocusedPanel.PlayerInventory, EFocusedPanel.Stash):
				return MoveInventoryToStash(_selectedItem, amount);
			case (EFocusedPanel.Stash, EFocusedPanel.PlayerInventory):
				return MoveStashToInventory(dest, _selectedSourceIndex, amount);
		}
		return false;
	}

	// -------------------------------------------------------------------
	// Count picker plumbing.
	// -------------------------------------------------------------------

	void OpenSelectCountPicker(ItemSlotPanel panel, ItemState item, EFocusedPanel category, int index)
	{
		if (_dropCountPanel == null || item == null) { return; }
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0)
				{
					EnterSelectMode(panel, item, count, category, index);
				}
			},
			onCancel: CloseCountPicker,
			prompt: "Select how many?");
	}

	void CloseCountPicker()
	{
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_playerInventory != null)
		{
			_playerInventory.HoldLocked = false;
			_playerInventory.SetSlotsFocusable(true);
		}
		SetStashSlotsFocusable(true);
		if (_focusedSlot != null)
		{
			_focusedSlot.GrabFocus();
		}
		else
		{
			_playerInventory?.RestoreFocus();
		}
	}

	void LockSlotsFocus()
	{
		if (_playerInventory != null)
		{
			_playerInventory.SetSlotsFocusable(false);
			_playerInventory.HoldLocked = true;
		}
		SetStashSlotsFocusable(false);
	}

	void SetStashSlotsFocusable(bool focusable)
	{
		if (_stashSlotPanels == null) { return; }
		foreach (ItemSlotPanel panel in _stashSlotPanels)
		{
			panel?.SetFocusable(focusable);
		}
	}

	// -------------------------------------------------------------------
	// Underlying transfer logic — direct mutation of the chest's Contents
	// list (the live SimState held by the runtime Chest node, so changes
	// persist across chunk eviction and save/load).
	// -------------------------------------------------------------------

	bool MoveInventoryToStash(ItemState item, int amount)
	{
		if (_contents == null || item?.data == null || _player?.Inventory == null || _stashSlotPanels == null)
		{
			return false;
		}
		int placed = AddToStashList(item.data, amount);
		if (placed <= 0)
		{
			return false;
		}
		item.stackCount -= placed;
		if (item.stackCount <= 0)
		{
			_player.Inventory.Remove(item);
		}
		else
		{
			_player.Inventory.NotifyChanged();
		}
		return true;
	}

	bool MoveStashToInventory(ItemSlotPanel dest, int slotIndex, int amount)
	{
		if (_contents == null || slotIndex < 0 || slotIndex >= _contents.Count || _player?.Inventory == null)
		{
			return false;
		}
		ItemState stashItem = _contents[slotIndex];
		if (stashItem?.data == null)
		{
			return false;
		}
		Inventory inv = _player.Inventory;
		int requested = Mathf.Min(amount, stashItem.stackCount);
		if (requested <= 0)
		{
			return false;
		}
		bool fullMove = requested >= stashItem.stackCount;

		// If the player aimed at a specific backpack slot, honor it: swap with a
		// different-kind occupant, merge into a same-kind stack, or fill it if
		// empty. A refusal (full same-kind stack, or a partial drop onto a
		// different-kind occupant) falls through to first-empty placement below.
		int destIndex = _playerInventory != null ? _playerInventory.GetBackpackPanelIndex(dest) : -1;
		if (destIndex >= 0)
		{
			// Fresh state for the receiving side — stash items are re-created from
			// their ItemData on save/load anyway (see ChestSimState.Contents
			// comment), so we don't carry subclass-specific fields like
			// WeaponState.ammo across the move.
			ItemState fresh = stashItem.data.CreateState();
			fresh.stackCount = requested;
			int placed = inv.TryAddExternalToBackpackSlot(fresh, fullMove, destIndex, out ItemState displaced);
			if (placed > 0)
			{
				stashItem.stackCount -= placed;
				if (displaced != null)
				{
					// Swap: the stash item moved in whole, so its slot now holds
					// the inventory item it displaced.
					_contents[slotIndex] = displaced;
				}
				else if (stashItem.stackCount <= 0)
				{
					_contents.RemoveAt(slotIndex);
				}
				return true;
			}
		}

		// No targeted slot (or it refused the item): merge across existing stacks
		// and land any remainder in the first empty backpack slot. Fails cleanly
		// — leaving the item in the stash — if nothing fits.
		ItemState addFresh = stashItem.data.CreateState();
		addFresh.stackCount = requested;
		int added = inv.TryAdd(addFresh);
		if (added <= 0)
		{
			return false;
		}
		stashItem.stackCount -= added;
		if (stashItem.stackCount <= 0)
		{
			_contents.RemoveAt(slotIndex);
		}
		return true;
	}

	// Merge into existing same-kind stacks first, then allocate a new slot
	// for any remainder up to the slot-panel cap. Returns units actually
	// placed (0 = stash full + nothing to merge into).
	int AddToStashList(ItemData data, int amount)
	{
		if (_contents == null || data == null || amount <= 0 || _stashSlotPanels == null)
		{
			return 0;
		}
		int slotCap = _stashSlotPanels.Count;
		int initial = amount;
		if (data.IsStackable)
		{
			foreach (ItemState existing in _contents)
			{
				if (existing == null || existing.data != data)
				{
					continue;
				}
				int space = existing.RemainingStackSpace();
				if (space <= 0)
				{
					continue;
				}
				int moved = Mathf.Min(space, amount);
				existing.stackCount += moved;
				amount -= moved;
				if (amount <= 0)
				{
					break;
				}
			}
		}
		if (amount > 0 && _contents.Count < slotCap)
		{
			ItemState fresh = data.CreateState();
			fresh.stackCount = amount;
			_contents.Add(fresh);
			amount = 0;
		}
		return initial - amount;
	}

	// -------------------------------------------------------------------
	// Refresh slot displays from Contents.
	// -------------------------------------------------------------------

	void RefreshStashSlots()
	{
		if (_stashSlotPanels == null)
		{
			return;
		}
		for (int i = 0; i < _stashSlotPanels.Count; i++)
		{
			ItemState item = (_contents != null && i < _contents.Count) ? _contents[i] : null;
			_stashSlotPanels[i]?.SetItem(item);
		}
		if (_focusedSlot != null)
		{
			_focusedItem = _focusedSlot.Item;
		}
		if (InSelectMode)
		{
			_selectedSource?.SetDimmed(true);
			RefreshGhostOnFocus();
		}
	}

	// -------------------------------------------------------------------
	// Drop / Use — player inventory side (InventoryPanel callbacks).
	// -------------------------------------------------------------------

	void OnInventorySecondaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null)
		{
			return;
		}
		_player?.Inventory?.Drop(item, 1);
	}

	void OnInventorySecondaryHoldComplete(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null || _dropCountPanel == null || _playerInventory == null)
		{
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		Inventory inv = _player?.Inventory;
		if (inv == null)
		{
			return;
		}
		if (item.stackCount <= 1)
		{
			inv.Drop(item, 1);
			_playerInventory.HoldLocked = false;
			return;
		}
		OpenInventoryDropCountPicker(inv, item);
	}

	void OpenInventoryDropCountPicker(Inventory inv, ItemState item)
	{
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0) { inv.Drop(item, count); }
			},
			onCancel: CloseCountPicker,
			prompt: "Drop how many?");
	}

	void OnInventoryTertiaryPressed(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			return;
		}
		UseConsumable(item);
	}

	void OnInventoryTertiaryReleased()
	{
		if (InSelectMode)
		{
			return;
		}
		_player?.Runner?.OnInputReleased();
	}

	void UseConsumable(ItemState item)
	{
		if (item is not ConsumableState consumable || _player == null)
		{
			return;
		}
		ConsumableData data = consumable.data;
		if (data?.actionProfile == null)
		{
			return;
		}
		ActionRunner runner = _player.Runner;
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
		runner.TryStart(data.actionProfile, context);
	}

	// -------------------------------------------------------------------
	// Drop / Use — stash side. InventoryPanel only fires its secondary /
	// tertiary callbacks when its own slot has focus, so when the user is
	// looking at a stash slot we poll the same actions here.
	// -------------------------------------------------------------------

	void TickStashSecondaryHold(float dt)
	{
		bool gateOpen = _focusedPanel == EFocusedPanel.Stash
			&& !InSelectMode
			&& _focusedItem != null
			&& _playerInventory != null;
		if (!gateOpen)
		{
			if (_stashSecondaryHold > 0f || _stashSecondaryHoldFired)
			{
				_stashSecondaryHold = 0f;
				_stashSecondaryHoldFired = false;
				_playerInventory?.ButtonHintSecondary?.SetProgress(0f);
			}
			return;
		}
		StringName secAction = _playerInventory.SecondaryAction;
		if (!InputMap.HasAction(secAction))
		{
			return;
		}
		bool pressed = Input.IsActionPressed(secAction);
		ButtonHint sec = _playerInventory.ButtonHintSecondary;
		if (pressed)
		{
			if (_stashSecondaryHoldFired)
			{
				return;
			}
			_stashSecondaryHold += dt;
			float progress = Mathf.Clamp(_stashSecondaryHold / HoldSeconds, 0f, 1f);
			sec?.SetProgress(progress);
			if (_stashSecondaryHold >= HoldSeconds)
			{
				_stashSecondaryHoldFired = true;
				_stashSecondaryHold = 0f;
				sec?.SetProgress(0f);
				OnStashSecondaryHoldComplete(_focusedSlotIndex, _focusedItem);
			}
			return;
		}
		// Released. If hold already fired, just clear the latch; otherwise
		// treat as a tap.
		if (_stashSecondaryHoldFired)
		{
			_stashSecondaryHoldFired = false;
		}
		else if (_stashSecondaryHold > 0f)
		{
			DropFromStash(_focusedSlotIndex, 1);
		}
		_stashSecondaryHold = 0f;
		sec?.SetProgress(0f);
	}

	void OnStashSecondaryHoldComplete(int slotIndex, ItemState item)
	{
		if (_dropCountPanel == null || item == null)
		{
			return;
		}
		if (item.stackCount <= 1)
		{
			DropFromStash(slotIndex, 1);
			return;
		}
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0) { DropFromStash(slotIndex, count); }
			},
			onCancel: CloseCountPicker,
			prompt: "Drop how many?");
	}

	void DropFromStash(int slotIndex, int count)
	{
		if (_contents == null || slotIndex < 0 || slotIndex >= _contents.Count)
		{
			return;
		}
		ItemState item = _contents[slotIndex];
		if (item?.data == null || item.stackCount <= 0 || _player?.World == null)
		{
			return;
		}
		int amount = Mathf.Min(count, item.stackCount);
		if (amount <= 0)
		{
			return;
		}
		ItemState dropped = item.data.CreateState();
		dropped.stackCount = amount;
		Vector3 pos = _player.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 forward = -_player.GlobalTransform.Basis.Z;
		Vector3 impulse = forward * 2f + Vector3.Up * 1.5f;
		_player.World.DropItem(dropped, pos, impulse, requireInteract: true);
		item.stackCount -= amount;
		if (item.stackCount <= 0)
		{
			_contents.RemoveAt(slotIndex);
		}
		RefreshStashSlots();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	// Mirror InventoryScreen's tertiary-charge progress fill so the Use hint
	// shows the same charging feedback whether the focused item lives in the
	// player's inventory or in the stash.
	void TickTertiaryCharge()
	{
		ButtonHint use = _playerInventory?.ButtonHintTertiary;
		if (use == null || !use.Visible || InSelectMode)
		{
			return;
		}
		ActionRunner runner = _player?.Runner;
		if (runner == null)
		{
			use.SetProgress(0f);
			return;
		}
		ref readonly PlayerAction action = ref runner.Current;
		if (action.phase != EActionPhase.Charging || action.context.primaryItem != _focusedItem)
		{
			use.SetProgress(0f);
			return;
		}
		use.SetProgress(runner.CurrentChargeT);
	}

	// Inventory.onChanged fires from DoDecrementStack (and from any other
	// path that mutates a player-owned ItemState's stackCount in place). For
	// a stash item used via the runner, the canonical Inventory.Remove path
	// can't find it; the dead stack stays in _contents until we sweep it
	// here. Also refreshes the visible grid because a stash consumable that
	// just ticked down should reflect the new stackCount on screen.
	void OnInventoryStateChanged()
	{
		if (_contents != null)
		{
			for (int i = _contents.Count - 1; i >= 0; i--)
			{
				ItemState s = _contents[i];
				if (s != null && s.stackCount <= 0)
				{
					_contents.RemoveAt(i);
				}
			}
		}
		RefreshStashSlots();
		UpdateInfoPanels();
		UpdateButtonHint();
	}
}
