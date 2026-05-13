using Godot;
using System;

// Modal wrapper around InventoryPanel for the gameplay inventory screen. The
// panel hands us slot focus events, primary tap (equip/unequip), secondary
// press/release (use), and drop tap/hold — this screen owns the actual
// behavior for each verb plus the button-hint labels, the InputSuppressed
// gate, and the ui_cancel close.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] public GameClient gameClient;
	[Export] private InventoryPanel _panel;
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private DropCountPanel _dropCountPanel;

	Action _onClose;
	Player _player;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		if (_panel != null)
		{
			// Primary verb: tap = equip/unequip. No hold on the gameplay
			// inventory screen.
			_panel.onPrimaryTap += OnPrimaryTap;
			// Secondary verb: held UseItem fires the consumable's action.
			_panel.onSecondaryPressed += OnSecondaryPressed;
			_panel.onSecondaryReleased += OnSecondaryReleased;
			// Drop tap drops a single unit; hold opens the count picker.
			_panel.onDropTap += OnDropTap;
			_panel.onDropHoldComplete += OnDropHoldComplete;
			// Focus pulse: refresh side info panel + per-slot Equip/Unequip
			// label.
			_panel.onFocusedItemChanged += OnFocusedItemChanged;

			// Seed button-hint labels. Per-slot label flips happen in
			// OnFocusedItemChanged.
			_panel.ButtonHintPrimary?.SetHint(_panel.PrimaryAction, "Equip");
			_panel.ButtonHintSecondary?.SetHint(_panel.SecondaryAction, "Use");
			_panel.ButtonHintDrop?.SetHint(_panel.DropAction, "Drop");
		}
		_itemInfoPanel?.SetItem(null);
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_panel != null)
		{
			_panel.onPrimaryTap -= OnPrimaryTap;
			_panel.onSecondaryPressed -= OnSecondaryPressed;
			_panel.onSecondaryReleased -= OnSecondaryReleased;
			_panel.onDropTap -= OnDropTap;
			_panel.onDropHoldComplete -= OnDropHoldComplete;
			_panel.onFocusedItemChanged -= OnFocusedItemChanged;
		}
	}

	void OnFocusedItemChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanel?.SetItem(item);
		UpdateButtonHints(panel, item);
	}

	// Refresh the three button-hint widgets for the currently-focused slot.
	// Primary flips between Equip/Unequip based on whether the slot is in
	// the backpack; Use only applies to consumables with an action profile.
	void UpdateButtonHints(ItemSlotPanel panel, ItemState item)
	{
		bool hasItem = item != null;
		bool inBackpack = _panel != null && _panel.IsBackpackPanel(panel);
		ButtonHint primary = _panel?.ButtonHintPrimary;
		ButtonHint secondary = _panel?.ButtonHintSecondary;
		ButtonHint drop = _panel?.ButtonHintDrop;
		if (primary != null)
		{
			primary.Visible = hasItem && CanEquipOrUnequip(item);
			primary.ActionName = inBackpack ? "Equip" : "Unequip";
		}
		if (secondary != null)
		{
			secondary.Visible = hasItem && CanUseItem(item);
			secondary.SetProgress(0f);
		}
		if (drop != null)
		{
			drop.Visible = hasItem;
			drop.SetProgress(0f);
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

	void OnPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (item == null || _panel == null)
		{
			return;
		}
		Inventory inventory = _panel.Inventory;
		if (inventory == null)
		{
			return;
		}
		if (_panel.IsBackpackPanel(panel))
		{
			EquipFromBackpack(inventory, item);
		}
		else
		{
			UnequipToBackpack(inventory, item);
		}
		_panel.RefreshAll();
	}

	static void EquipFromBackpack(Inventory inventory, ItemState item)
	{
		switch (item.data)
		{
			case ArmorData armor:
				inventory.TryEquip(item, armor.armorSlot);
				break;
			case WeaponData weapon:
				// Two-hand layout: ranged (ammo-bearing) lands in the right
				// slot, melee in the left — matches Player.Initialize's
				// PlayerSpawnData wiring since WeaponData itself doesn't
				// author a target slot.
				EInventorySlot target = weapon.useAmmo ? EInventorySlot.WeaponRight : EInventorySlot.WeaponLeft;
				inventory.TryEquip(item, target);
				break;
			case ConsumableData:
				inventory.TryMoveToConsumableSlot(item);
				break;
		}
	}

	static void UnequipToBackpack(Inventory inventory, ItemState item)
	{
		EInventorySlot? slot = inventory.GetEquippedSlot(item);
		if (slot.HasValue && slot.Value != EInventorySlot.Consumable)
		{
			inventory.TryUnequip(slot.Value);
			return;
		}
		// GetEquippedSlot only reports the ACTIVE consumable hotbar slot.
		// Items in inactive hotbar slots have to be removed by scanning the
		// hotbar directly.
		inventory.TryRemoveFromConsumableSlot(item);
	}

	void OnSecondaryPressed(ItemSlotPanel panel, ItemState item)
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

	void OnSecondaryReleased()
	{
		_player?.Runner?.OnInputReleased();
	}

	void OnDropTap(ItemSlotPanel panel, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		_panel?.Inventory?.Drop(item, 1);
	}

	// Hold-drop on the inventory panel — pop the count picker so the player
	// can pick how many to drop from a stack.
	void OnDropHoldComplete(ItemSlotPanel panel, ItemState item)
	{
		if (item == null || _dropCountPanel == null || _panel == null)
		{
			return;
		}
		Inventory inventory = _panel.Inventory;
		if (inventory == null)
		{
			return;
		}
		// Lock the inventory slots out of focus traversal so the analog stick
		// can't walk the highlight off the picker while it's up.
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
		CloseDropCountPanel();
	}

	void OnDropCountCancelled()
	{
		CloseDropCountPanel();
	}

	void CloseDropCountPanel()
	{
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_panel != null)
		{
			_panel.HoldLocked = false;
			// Re-enable focus on inventory slots and put it back on the slot
			// that was highlighted before the picker stole it.
			_panel.SetSlotsFocusable(true);
			_panel.RestoreFocus();
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
		// If the screen happens to already be visible when the player spawns
		// (load-order edge case), forward the bind immediately so the slots
		// populate without waiting for an Open() / Close() cycle.
		if (Visible)
		{
			_panel?.Bind(_player);
		}
	}

	public void Open(Action onClose = null)
	{
		_onClose = onClose;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = true;
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = false;
			}
		}
		Visible = true;
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		Visible = false;
		if (gameClient != null)
		{
			gameClient.InputSuppressed = false;
			if (gameClient.hud != null)
			{
				gameClient.hud.Visible = true;
			}
		}
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		Input.MouseMode = Visible ? Input.MouseModeEnum.Visible : Input.MouseModeEnum.Captured;
		if (Visible)
		{
			_panel?.Bind(_player);
		}
		else
		{
			_panel?.Unbind();
			// Closing the inventory drops any in-flight count-picker state so
			// the next open starts clean.
			CloseDropCountPanel();
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}
}
