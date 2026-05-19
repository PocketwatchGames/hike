using Godot;

// Inventory tab rendered inside AlmanacScreen. The panel hands us slot focus
// events, primary tap (equip/unequip), secondary press/release (use), and
// drop tap/hold — this screen owns the actual behavior for each verb plus
// the button-hint labels. The Almanac wrapper owns InputSuppressed, mouse
// mode, hud visibility, and the ui_cancel close; this screen just binds /
// unbinds when its tab is shown.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private InventoryPanel _panel;
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private DropCountPanel _dropCountPanel;

	GameClient _gameClient;
	Player _player;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_panel != null)
		{
			// Primary verb: tap = equip/unequip. No hold on the gameplay
			// inventory screen.
			_panel.onPrimaryTap += OnPrimaryTap;
			// Secondary verb: held UseItem fires the consumable's action.
			_panel.onTertiaryPressed += OnTertiaryPressed;
			_panel.onTertiaryReleased += OnTertiaryReleased;
			// Drop tap drops a single unit; hold opens the count picker.
			_panel.onSecondaryTap += OnSecondaryTap;
			_panel.onSecondaryHoldComplete += OnSecondaryHoldComplete;
			// Focus pulse: refresh side info panel + per-slot Equip/Unequip
			// label.
			_panel.onFocusedItemChanged += OnFocusedItemChanged;

			// Seed button-hint labels. Per-slot label flips happen in
			// OnFocusedItemChanged.
			_panel.ButtonHintPrimary?.SetHint(_panel.PrimaryAction, "Equip");
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
			_panel.onTertiaryPressed -= OnTertiaryPressed;
			_panel.onTertiaryReleased -= OnTertiaryReleased;
			_panel.onSecondaryTap -= OnSecondaryTap;
			_panel.onSecondaryHoldComplete -= OnSecondaryHoldComplete;
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
		ButtonHint drop = _panel?.ButtonHintSecondary;
		ButtonHint use = _panel?.ButtonHintTertiary;
		if (primary != null)
		{
			primary.Visible = hasItem && CanEquipOrUnequip(item);
			primary.ActionName = inBackpack ? "Equip" : "Unequip";
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
				EInventorySlot target = weapon.maxAmmo > 0 ? EInventorySlot.WeaponRight : EInventorySlot.WeaponLeft;
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

	void OnTertiaryPressed(ItemSlotPanel panel, ItemState item)
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

	void OnTertiaryReleased()
	{
		_player?.Runner?.OnInputReleased();
	}

	// Mirror the HUD hotbar's charge-progress fill on the inventory's Use hint
	// while the runner is charging the focused consumable. Without this the
	// player gets no visual cue that Use is hold-to-fire, taps the button, and
	// the runner aborts before reaching the tier — looks like nothing happened.
	public override void _Process(double delta)
	{
		if (!Visible || _panel == null)
		{
			return;
		}
		ButtonHint use = _panel.ButtonHintTertiary;
		if (use == null || !use.Visible)
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
		if (item == null)
		{
			return;
		}
		_panel?.Inventory?.Drop(item, 1);
	}

	// Hold-drop on the inventory panel — pop the count picker so the player
	// can pick how many to drop from a stack.
	void OnSecondaryHoldComplete(ItemSlotPanel panel, ItemState item)
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
		// Single-item stacks skip the count picker — there's nothing to pick.
		if (item.stackCount <= 1)
		{
			inventory.Drop(item, 1);
			_panel.HoldLocked = false;
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

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			// Fetch the player from gameClient on demand instead of subscribing
			// to onPlayerSpawned in _Ready — the Almanac wrapper sets our
			// gameClient field on Open() (before our tab is shown), and by
			// the time this tab is ever shown the player has long since spawned.
			_player = _gameClient?.Player;
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
}
