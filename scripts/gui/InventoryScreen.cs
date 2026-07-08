using Godot;

// Inventory tab rendered inside AlmanacScreen. Shows the controlled member's
// EQUIP SLOTS (helmet / armor / weapons / 3-slot equipment hotbar, via
// InventoryPanel) alongside the carried MATERIAL backpack (via BackpackPanel).
// It's primarily a viewer: the player can Use an equipped equipment item and
// Drop a carried material. Equipping weapons / armor / equipment happens on the
// camp Stash screen, not here (the material backpack can't hold gear, and the
// equipment stash isn't reachable in the field).
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private InventoryPanel _panel;
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private BackpackPanel _backpackPanel;
	[Export] private ButtonHint _dropHint;

	GameClient _gameClient;
	Player _player;

	// The focused backpack slot (material side), tracked so the Drop poll knows
	// what to act on — BackpackPanel forwards only raw focus/press events.
	int _focusedBackpackIndex = -1;
	ItemState _focusedBackpackItem;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_panel != null)
		{
			_panel.onFocusedItemChanged += OnEquipFocusChanged;
			_panel.onTertiaryPressed += OnUsePressed;
			_panel.onTertiaryReleased += OnUseReleased;
			_panel.ButtonHintPrimary?.SetHint(_panel.PrimaryAction, "Select");
			_panel.ButtonHintTertiary?.SetHint(_panel.TertiaryAction, "Use");
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused += OnBackpackFocused;
		}
		_itemInfoPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_panel != null)
		{
			_panel.onFocusedItemChanged -= OnEquipFocusChanged;
			_panel.onTertiaryPressed -= OnUsePressed;
			_panel.onTertiaryReleased -= OnUseReleased;
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused -= OnBackpackFocused;
		}
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_player = _gameClient?.Player;
			_panel?.Bind(_player);
			_statsPanel?.SetPlayer(_player);
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged += RefreshBackpack;
			}
			RefreshBackpack();
		}
		else
		{
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged -= RefreshBackpack;
			}
			_panel?.Unbind();
			_focusedBackpackIndex = -1;
			_focusedBackpackItem = null;
		}
	}

	void RefreshBackpack()
	{
		_backpackPanel?.Refresh(_player?.Inventory?.Backpack);
		// The focused backpack item may have changed under the cursor (a drop
		// shifted the list) — re-resolve it so Drop acts on what's shown.
		if (_focusedBackpackIndex >= 0)
		{
			_focusedBackpackItem = _backpackPanel?.GetSlot(_focusedBackpackIndex)?.Item;
			if (_focusedBackpackItem != null)
			{
				_itemInfoPanel?.SetItem(_focusedBackpackItem);
			}
		}
	}

	void OnEquipFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_focusedBackpackIndex = -1;
		_focusedBackpackItem = null;
		_itemInfoPanel?.SetItem(item);
		UpdateDropHint(null);
		// The tertiary (Use) event only fires while its hint is visible — show it
		// exactly for a usable equipment item.
		if (_panel?.ButtonHintTertiary != null)
		{
			_panel.ButtonHintTertiary.Visible = item is ConsumableState c && c.data?.actionProfile != null;
		}
	}

	void OnBackpackFocused(int index, ItemSlotPanel panel)
	{
		_focusedBackpackIndex = index;
		_focusedBackpackItem = panel?.Item;
		_itemInfoPanel?.SetItem(_focusedBackpackItem);
		UpdateDropHint(_focusedBackpackItem);
	}

	void UpdateDropHint(ItemState material)
	{
		if (_dropHint == null)
		{
			return;
		}
		_dropHint.Visible = material != null;
	}

	// Use = fire an equipped equipment item's action (drink a potion, light a
	// torch). Only the Equipment hotbar slots carry a usable ConsumableState.
	void OnUsePressed(ItemSlotPanel panel, ItemState item)
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
			sourceSlot = EInventorySlot.Equipment,
		};
		runner.TryStart(data.actionProfile, context);
	}

	void OnUseReleased()
	{
		_player?.Runner?.OnInputReleased();
	}

	// Drop the focused material (backpack side). BackpackPanel forwards no
	// secondary event, so poll the Drop action here while a material is focused.
	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible || _focusedBackpackItem == null || _player?.Inventory == null)
		{
			return;
		}
		if (e.IsActionPressed("MenuSecondary"))
		{
			_player.Inventory.Drop(_focusedBackpackItem, 1);
			GetViewport().SetInputAsHandled();
		}
	}

	// ---- Equip-compat helpers, shared with MerchantScreen ------------------

	// True when `item` may equip into `destSlot` — its category's slot matches.
	public static bool EquipCompatible(EInventorySlot destSlot, ItemState item)
	{
		return item?.data != null && item.data.EquipSlotKind == destSlot;
	}

	// True when the items in two singular equip slots could trade places. Weapons
	// and armor are category-locked to one slot each, so this is only ever true
	// for a same-slot no-op; the Equipment hotbar is excluded (index-addressed).
	public static bool CanSwapEquipSlots(EInventorySlot sourceEquip, EInventorySlot destEquip, ItemState selectedItem, Inventory inv)
	{
		if (sourceEquip == EInventorySlot.None || destEquip == EInventorySlot.None) { return false; }
		if (sourceEquip == EInventorySlot.Equipment || destEquip == EInventorySlot.Equipment) { return false; }
		if (!EquipCompatible(destEquip, selectedItem)) { return false; }
		ItemState destOccupant = inv?.GetEquipped(destEquip);
		if (destOccupant != null && !EquipCompatible(sourceEquip, destOccupant)) { return false; }
		return true;
	}
}
