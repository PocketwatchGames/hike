using Godot;

// Inventory tab rendered inside AlmanacScreen. The interaction model is
// select-and-place: tap ui_accept on a slot to "pick up" the item (ghost
// follows the cursor, source slot dims); tap ui_accept on a destination to
// commit the move; ui_cancel to abort. Auto-targets the natural destination
// (corresponding equip slot for a backpack item, first backpack slot for an
// equipped item) so the common equip / unequip flow is one tap-tap. Hold
// ui_accept on a stackable to enter select mode with a chosen unit count.
//
// Secondary (Drop) and Tertiary (Use) remain in browse mode but are hidden
// while a selection is in flight — you can't act on the item with another
// verb until you finish or cancel the move.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private InventoryPanel _panel;
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private DropCountPanel _dropCountPanel;

	GameClient _gameClient;
	Player _player;

	// Select-mode state. _selectedItem is the ItemState the user "picked up"
	// (still owned by the inventory until commit / cancel); _selectedAmount is
	// how many units they grabbed (full stack on tap; chosen count on hold).
	// _selectedSourcePanel remembers where they grabbed from so commit can
	// detect "drop onto source" as a cancel.
	ItemSlotPanel _selectedSourcePanel;
	ItemState _selectedItem;
	int _selectedAmount;
	bool InSelectMode => _selectedItem != null;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_panel != null)
		{
			_panel.onPrimaryTap += OnPrimaryTap;
			_panel.onPrimaryHoldComplete += OnPrimaryHoldComplete;
			_panel.onTertiaryPressed += OnTertiaryPressed;
			_panel.onTertiaryReleased += OnTertiaryReleased;
			_panel.onSecondaryTap += OnSecondaryTap;
			_panel.onSecondaryHoldComplete += OnSecondaryHoldComplete;
			_panel.onFocusedItemChanged += OnFocusedItemChanged;

			_panel.ButtonHintPrimary?.SetHint(_panel.PrimaryAction, "Select");
			_panel.ButtonHintSecondary?.SetHint(_panel.SecondaryAction, "Drop");
			_panel.ButtonHintTertiary?.SetHint(_panel.TertiaryAction, "Use");
		}
		_itemInfoPanel?.SetItem(null);
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
	}

	public override void _ExitTree()
	{
		if (_panel != null)
		{
			_panel.onPrimaryTap -= OnPrimaryTap;
			_panel.onPrimaryHoldComplete -= OnPrimaryHoldComplete;
			_panel.onTertiaryPressed -= OnTertiaryPressed;
			_panel.onTertiaryReleased -= OnTertiaryReleased;
			_panel.onSecondaryTap -= OnSecondaryTap;
			_panel.onSecondaryHoldComplete -= OnSecondaryHoldComplete;
			_panel.onFocusedItemChanged -= OnFocusedItemChanged;
		}
	}

	// AlmanacScreen owns ui_cancel for closing; we intercept it ahead of the
	// wrapper while a selection is in flight so the first ui_cancel cancels
	// the move and a second one closes the screen.
	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible || !InSelectMode)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			CancelSelect();
			GetViewport().SetInputAsHandled();
		}
	}

	void OnFocusedItemChanged(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			// In select mode the side panel keeps showing the selected item
			// (the thing being moved), not whatever happens to be under the
			// cursor. The ghost preview on the focused slot communicates the
			// destination.
			_itemInfoPanel?.SetItem(_selectedItem);
			RefreshGhostOnFocus(panel);
			UpdateButtonHints(panel, item);
			return;
		}
		_itemInfoPanel?.SetItem(item);
		UpdateButtonHints(panel, item);
	}

	// Refresh the three button-hint widgets for the currently-focused slot.
	// Browse mode: Primary is Equip/Unequip on equippable items, Use on the
	// tertiary side. Select mode: Primary is Move/Equip/Unequip on the
	// destination slot; Secondary/Tertiary hidden (no drop/use mid-move).
	void UpdateButtonHints(ItemSlotPanel panel, ItemState item)
	{
		ButtonHint primary = _panel?.ButtonHintPrimary;
		ButtonHint drop = _panel?.ButtonHintSecondary;
		ButtonHint use = _panel?.ButtonHintTertiary;
		if (InSelectMode)
		{
			if (primary != null)
			{
				string label = ResolveDestinationLabel(panel);
				primary.ActionName = label;
				primary.Visible = !string.IsNullOrEmpty(label);
				primary.SetProgress(0f);
			}
			if (drop != null) { drop.Visible = false; }
			if (use != null) { use.Visible = false; }
			return;
		}
		bool hasItem = item != null;
		bool inBackpack = _panel != null && _panel.IsBackpackPanel(panel);
		if (primary != null)
		{
			primary.Visible = hasItem;
			if (hasItem)
			{
				if (CanEquipOrUnequip(item))
				{
					primary.ActionName = inBackpack ? "Equip" : "Unequip";
				}
				else
				{
					primary.ActionName = "Select";
				}
			}
			primary.SetProgress(0f);
		}
		if (drop != null)
		{
			drop.Visible = hasItem;
			drop.SetProgress(0f);
		}
		if (use != null)
		{
			use.Visible = hasItem && CanUseItem(item);
			use.SetProgress(0f);
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

	// Verb label for a candidate destination during select mode. Empty string
	// means "no valid move from source to this slot" — caller hides the hint.
	string ResolveDestinationLabel(ItemSlotPanel dest)
	{
		if (_selectedItem == null || dest == null || _panel == null)
		{
			return string.Empty;
		}
		if (dest == _selectedSourcePanel)
		{
			// Drop back on source = cancel; keep the hint legible so the user
			// knows what'll happen.
			return "Cancel";
		}
		bool sourceBackpack = _panel.IsBackpackPanel(_selectedSourcePanel);
		bool destBackpack = _panel.IsBackpackPanel(dest);
		EInventorySlot destEquip = _panel.GetEquipSlotKind(dest);
		if (sourceBackpack && destBackpack)
		{
			return "Move";
		}
		if (sourceBackpack)
		{
			return EquipCompatible(destEquip, _selectedItem) ? "Equip" : string.Empty;
		}
		if (destBackpack)
		{
			return "Unequip";
		}
		// Equip slot → equip slot. Consumable ↔ consumable goes through
		// TryMoveToConsumableSlot; non-consumable equip slots use the
		// TrySwapEquipSlots path when both items fit both slots (today this
		// only fires for weapon L ↔ R — armor pieces are tied to one
		// armorSlot, so head ↔ body fails the compatibility check).
		EInventorySlot sourceEquip = _panel.GetEquipSlotKind(_selectedSourcePanel);
		if (sourceEquip == EInventorySlot.Consumable && destEquip == EInventorySlot.Consumable)
		{
			return "Move";
		}
		if (CanSwapEquipSlots(sourceEquip, destEquip))
		{
			return "Move";
		}
		return string.Empty;
	}

	// Equip-slot ↔ equip-slot swap is valid when the selected item fits the
	// destination AND the item currently in the destination (if any) fits the
	// source. Practically this only matches weapon L ↔ R today. Public so the
	// MerchantScreen can reuse the same predicate for its player-inventory
	// side, where the player can also rearrange equipment mid-trade.
	public static bool CanSwapEquipSlots(EInventorySlot sourceEquip, EInventorySlot destEquip, ItemState selectedItem, Inventory inv)
	{
		if (sourceEquip == EInventorySlot.None || destEquip == EInventorySlot.None) { return false; }
		if (sourceEquip == EInventorySlot.Consumable || destEquip == EInventorySlot.Consumable) { return false; }
		if (!EquipCompatible(destEquip, selectedItem)) { return false; }
		ItemState destOccupant = inv?.GetEquipped(destEquip);
		if (destOccupant != null && !EquipCompatible(sourceEquip, destOccupant)) { return false; }
		return true;
	}

	bool CanSwapEquipSlots(EInventorySlot sourceEquip, EInventorySlot destEquip)
	{
		return CanSwapEquipSlots(sourceEquip, destEquip, _selectedItem, _player?.Inventory);
	}

	public static bool EquipCompatible(EInventorySlot destSlot, ItemState item)
	{
		if (item?.data == null) { return false; }
		switch (item.data)
		{
			case ArmorData armor:
				return destSlot == armor.armorSlot;
			case WeaponData weapon:
				return destSlot == weapon.CanonicalSlot;
			case ConsumableData:
				return destSlot == EInventorySlot.Consumable;
		}
		return false;
	}

	// Update ghost overlay: shown on the currently-focused panel, hidden
	// everywhere else. Source panel stays dimmed throughout select mode.
	void RefreshGhostOnFocus(ItemSlotPanel focused)
	{
		if (_panel == null) { return; }
		foreach (ItemSlotPanel p in _panel.EnumerateAllSlots())
		{
			p.SetGhost(p == focused && InSelectMode ? _selectedItem : null);
		}
	}

	void OnPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel);
			return;
		}
		if (item == null || panel == null)
		{
			return;
		}
		EnterSelectMode(panel, item, item.stackCount);
	}

	void OnPrimaryHoldComplete(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			// Hold inside select mode commits the move (treat like a tap) so
			// a held release doesn't strand the user with no action.
			CommitMove(panel);
			if (_panel != null) { _panel.HoldLocked = false; }
			return;
		}
		if (item == null || panel == null)
		{
			if (_panel != null) { _panel.HoldLocked = false; }
			return;
		}
		// Non-stackables (or stackCount == 1) just enter select mode with the
		// full stack — no point opening the picker for a single unit.
		if (item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			EnterSelectMode(panel, item, item.stackCount);
			if (_panel != null) { _panel.HoldLocked = false; }
			return;
		}
		OpenCountPicker(panel, item);
	}

	void OpenCountPicker(ItemSlotPanel panel, ItemState item)
	{
		if (_dropCountPanel == null) { return; }
		_panel.SetSlotsFocusable(false);
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => OnCountConfirmed(panel, item, count),
			onCancel: OnCountCancelled,
			prompt: "Select how many?");
	}

	void OnCountConfirmed(ItemSlotPanel panel, ItemState item, int count)
	{
		CloseCountPicker();
		if (count > 0)
		{
			EnterSelectMode(panel, item, count);
		}
	}

	void OnCountCancelled()
	{
		CloseCountPicker();
	}

	void CloseCountPicker()
	{
		if (_dropCountPanel != null) { _dropCountPanel.Visible = false; }
		if (_panel != null)
		{
			_panel.HoldLocked = false;
			_panel.SetSlotsFocusable(true);
			_panel.RestoreFocus();
		}
	}

	void EnterSelectMode(ItemSlotPanel sourcePanel, ItemState item, int amount)
	{
		_selectedSourcePanel = sourcePanel;
		_selectedItem = item;
		_selectedAmount = Mathf.Max(1, amount);
		sourcePanel?.SetDimmed(true);
		// Auto-target the natural destination so the common one-tap-then-tap
		// equip / unequip flow is fast.
		ItemSlotPanel autoTarget = _panel?.FindAutoTargetForSelect(sourcePanel, item) ?? sourcePanel;
		if (autoTarget != null && autoTarget != sourcePanel)
		{
			autoTarget.GrabFocus();
		}
		RefreshGhostOnFocus(_panel?.FocusedPanel);
		UpdateButtonHints(_panel?.FocusedPanel, _panel?.FocusedItem);
		_itemInfoPanel?.SetItem(_selectedItem);
	}

	void CancelSelect()
	{
		if (_panel != null)
		{
			_panel.ClearSelectVisuals();
		}
		ItemSlotPanel source = _selectedSourcePanel;
		_selectedItem = null;
		_selectedAmount = 0;
		_selectedSourcePanel = null;
		UpdateButtonHints(_panel?.FocusedPanel, _panel?.FocusedItem);
		_itemInfoPanel?.SetItem(_panel?.FocusedItem);
		// Restore focus to where the player picked up the item — feels less
		// disorienting than leaving the cursor wherever it happened to be.
		source?.GrabFocus();
	}

	void CommitMove(ItemSlotPanel dest)
	{
		Inventory inv = _player?.Inventory;
		if (inv == null || _selectedItem == null)
		{
			CancelSelect();
			return;
		}
		// Drop onto source = cancel.
		if (dest == _selectedSourcePanel)
		{
			CancelSelect();
			return;
		}
		bool partial = _selectedItem.data != null
			&& _selectedItem.data.IsStackable
			&& _selectedAmount < _selectedItem.stackCount;
		bool success = partial
			? ExecutePartialMove(inv, dest)
			: ExecuteFullMove(inv, dest);
		if (success)
		{
			ClearSelectionState();
			_panel?.ClearSelectVisuals();
			_panel?.RefreshAll();
			UpdateButtonHints(_panel?.FocusedPanel, _panel?.FocusedItem);
			_itemInfoPanel?.SetItem(_panel?.FocusedItem);
		}
		// Failed moves leave the player in select mode so they can retry.
		else
		{
			RefreshGhostOnFocus(_panel?.FocusedPanel);
		}
	}

	void ClearSelectionState()
	{
		_selectedItem = null;
		_selectedAmount = 0;
		_selectedSourcePanel = null;
	}

	bool ExecuteFullMove(Inventory inv, ItemSlotPanel dest)
	{
		bool sourceBackpack = _panel.IsBackpackPanel(_selectedSourcePanel);
		bool destBackpack = _panel.IsBackpackPanel(dest);
		EInventorySlot destEquip = _panel.GetEquipSlotKind(dest);
		EInventorySlot sourceEquip = _panel.GetEquipSlotKind(_selectedSourcePanel);
		if (sourceBackpack && destBackpack)
		{
			int srcIdx = _panel.GetBackpackPanelIndex(_selectedSourcePanel);
			int dstIdx = _panel.GetBackpackPanelIndex(dest);
			if (srcIdx < 0 || dstIdx < 0) { return false; }
			// Sparse backpack: panel index == backpack index, and the
			// destination may be an empty slot (the swap just leaves the
			// source slot empty in turn).
			return inv.TrySwapInBackpack(srcIdx, dstIdx);
		}
		if (sourceBackpack)
		{
			if (destEquip == EInventorySlot.Consumable)
			{
				return inv.TryMoveToConsumableSlot(_selectedItem, _panel.GetConsumableIndex(dest));
			}
			if (EquipCompatible(destEquip, _selectedItem))
			{
				return inv.TryEquip(_selectedItem, destEquip);
			}
			return false;
		}
		if (destBackpack)
		{
			if (sourceEquip == EInventorySlot.Consumable)
			{
				return inv.TryRemoveFromConsumableSlot(_selectedItem);
			}
			return inv.TryUnequip(sourceEquip);
		}
		// Equip → equip. Consumable hotbar uses TryMoveToConsumableSlot;
		// non-consumable equip slots go through TrySwapEquipSlots (compatibility
		// already vetted by CanSwapEquipSlots in the destination resolver, so
		// we only re-check the kinds here to be defensive).
		if (sourceEquip == EInventorySlot.Consumable && destEquip == EInventorySlot.Consumable)
		{
			return inv.TryMoveToConsumableSlot(_selectedItem, _panel.GetConsumableIndex(dest));
		}
		if (CanSwapEquipSlots(sourceEquip, destEquip))
		{
			return inv.TrySwapEquipSlots(sourceEquip, destEquip);
		}
		return false;
	}

	// Partial-stack split + merge / place. Restricted to backpack-to-backpack
	// merges and backpack ↔ consumable-slot routes — equip slots take whole
	// stacks only, so a partial selection of armor / weapons is conceptually
	// invalid (and CountPicker doesn't open for non-stackables anyway).
	bool ExecutePartialMove(Inventory inv, ItemSlotPanel dest)
	{
		bool sourceBackpack = _panel.IsBackpackPanel(_selectedSourcePanel);
		bool destBackpack = _panel.IsBackpackPanel(dest);
		EInventorySlot destEquip = _panel.GetEquipSlotKind(dest);
		int amount = Mathf.Min(_selectedAmount, _selectedItem.stackCount);
		if (amount <= 0) { return false; }
		if (sourceBackpack && destBackpack)
		{
			int dstIdx = _panel.GetBackpackPanelIndex(dest);
			if (dstIdx < 0) { return false; }
			int moved = inv.TrySplitMergeInBackpack(_selectedItem, amount, dstIdx);
			return moved > 0;
		}
		if (destEquip == EInventorySlot.Consumable)
		{
			int idx = _panel.GetConsumableIndex(dest);
			ItemState fresh = _selectedItem.data.CreateState();
			fresh.stackCount = amount;
			int placed = inv.TryAddToConsumableSlot(fresh, idx);
			if (placed <= 0) { return false; }
			_selectedItem.stackCount -= placed;
			if (_selectedItem.stackCount <= 0)
			{
				inv.Remove(_selectedItem);
			}
			else
			{
				inv.NotifyChanged();
			}
			return true;
		}
		// Consumable → backpack partial: split into a fresh stack appended to
		// the backpack via TryAdd. The Inventory list is unordered for this
		// purpose; if the user wanted to merge into a specific same-kind
		// backpack stack we'd need a different code path.
		if (destBackpack)
		{
			ItemState fresh = _selectedItem.data.CreateState();
			fresh.stackCount = amount;
			int added = inv.TryAdd(fresh);
			if (added <= 0) { return false; }
			_selectedItem.stackCount -= added;
			if (_selectedItem.stackCount <= 0)
			{
				inv.Remove(_selectedItem);
			}
			else
			{
				inv.NotifyChanged();
			}
			return true;
		}
		return false;
	}

	void OnTertiaryPressed(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode) { return; }
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

	void OnTertiaryReleased()
	{
		if (InSelectMode) { return; }
		_player?.Runner?.OnInputReleased();
	}

	// Mirror the HUD hotbar's charge-progress fill on the inventory's Use hint
	// while the runner is charging the focused consumable. Without this the
	// player gets no visual cue that Use is hold-to-fire.
	public override void _Process(double delta)
	{
		if (!Visible || _panel == null)
		{
			return;
		}
		ButtonHint use = _panel.ButtonHintTertiary;
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
		if (action.phase != EActionPhase.Charging || action.context.primaryItem != _panel.FocusedItem)
		{
			use.SetProgress(0f);
			return;
		}
		use.SetProgress(runner.CurrentChargeT);
	}

	void OnSecondaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null)
		{
			return;
		}
		_panel?.Inventory?.Drop(item, 1);
	}

	// Hold-drop on the inventory panel — pop the count picker so the player
	// can pick how many to drop from a stack.
	void OnSecondaryHoldComplete(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null || _dropCountPanel == null || _panel == null)
		{
			if (_panel != null) { _panel.HoldLocked = false; }
			return;
		}
		Inventory inventory = _panel.Inventory;
		if (inventory == null)
		{
			return;
		}
		if (item.stackCount <= 1)
		{
			inventory.Drop(item, 1);
			_panel.HoldLocked = false;
			return;
		}
		_panel.SetSlotsFocusable(false);
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => OnDropCountConfirmed(inventory, item, count),
			onCancel: OnDropCountCancelled,
			prompt: "Drop how many?");
	}

	void OnDropCountConfirmed(Inventory inventory, ItemState item, int count)
	{
		if (count > 0)
		{
			inventory.Drop(item, count);
		}
		CloseCountPicker();
	}

	void OnDropCountCancelled()
	{
		CloseCountPicker();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_player = _gameClient?.Player;
			_panel?.Bind(_player);
			_statsPanel?.SetPlayer(_player);
		}
		else
		{
			// Closing the screen drops any in-flight selection so the next
			// open starts clean.
			if (InSelectMode)
			{
				_panel?.ClearSelectVisuals();
				ClearSelectionState();
			}
			_panel?.Unbind();
			CloseCountPicker();
		}
	}
}
