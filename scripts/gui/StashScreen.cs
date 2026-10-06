using Godot;
using System.Collections.Generic;

// Stash tab of the camp screen. Left = the party stash (SimState.PartyStash, a
// BackpackPanel over that list); right = the controlled member's inventory
// (InventoryPanel). Tap a stash item to take it into the backpack. Not opened by
// anything at the moment — the stash is due a rework into a traditional stash.
[GlobalClass]
public partial class StashScreen : Control
{
	[Export] private InventoryPanel _playerInventory;
	[Export] private BackpackPanel _stashPanel;
	[Export] private ItemInfoPanel _itemInfoPanelStash;
	[Export] private ItemInfoPanel _itemInfoPanelInventory;

	Player _player;
	List<ItemState> _stash;

	public override void _Ready()
	{
		Visible = false;
		if (_playerInventory != null)
		{
			_playerInventory.onFocusedItemChanged += OnInventoryFocusChanged;
			_playerInventory.onPrimaryTap += OnInventoryPrimaryTap;
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused += OnStashFocused;
			_stashPanel.onSlotButtonUp += OnStashTap;
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
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused -= OnStashFocused;
			_stashPanel.onSlotButtonUp -= OnStashTap;
		}
	}

	// CampScreen owns the global gating (input, HUD, mouse, camp pose); this
	// screen just binds to the party equipment stash list and its own visibility.
	public void Open(Player player, List<ItemState> stash)
	{
		_player = player;
		_stash = stash;
		if (_playerInventory != null)
		{
			_playerInventory.ButtonHintPrimary?.SetHint(_playerInventory.PrimaryAction, "Unequip");
		}
		Visible = true;
		_playerInventory?.Bind(_player);
		RefreshStash();
		_itemInfoPanelStash?.SetItem(null);
		_itemInfoPanelInventory?.SetItem(null);
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		_playerInventory?.Unbind();
		_stashPanel?.ClearVisuals();
		Visible = false;
		_stash = null;
		_player = null;
	}

	void RefreshStash()
	{
		_stashPanel?.Refresh(_stash);
	}

	// ---- Stash side (equip) -----------------------------------------------

	void OnStashFocused(int index, ItemSlotPanel panel)
	{
		_itemInfoPanelStash?.SetItem(panel?.Item);
		_itemInfoPanelInventory?.SetItem(null);
	}

	// Tap a stash item → take it into the backpack, equipped when its slot is
	// free (Player.TakeItem).
	void OnStashTap(int index, ItemSlotPanel panel)
	{
		if (_stash == null || _player?.Inventory == null || index < 0 || index >= _stash.Count)
		{
			return;
		}
		ItemState item = _stash[index];
		if (item?.data == null)
		{
			return;
		}
		if (!EquipFromStash(item))
		{
			return;
		}
		_stash.Remove(item);
		RefreshStash();
	}

	bool EquipFromStash(ItemState item)
	{
		return _player.TakeItem(item);
	}

	// ---- Equip-slot side (unequip) ----------------------------------------

	void OnInventoryFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanelInventory?.SetItem(item);
		_itemInfoPanelStash?.SetItem(null);
	}

	// Tap an equipped item → nothing here is stashable now: weapons / armor /
	// helmets are permanent until replaced.
	void OnInventoryPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
	}
}
