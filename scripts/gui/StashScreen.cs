using Godot;
using System.Collections.Generic;

// Stash tab of the camp screen. Left = the party equipment stash
// (SimState.PartyEquipmentStash, a BackpackPanel over that list); right =
// the controlled member's equip slots (InventoryPanel). Tap a stash item to
// equip it into its category slot — a piece already worn there is displaced back
// into the stash. Tap an equipped EQUIPMENT hotbar item to send it back to the
// stash; weapons / armor / helmets can't be unequipped (they only change by
// being replaced), so tapping them is a no-op.
//
// Mutations land directly on the shared PartyEquipmentStash list (the live
// SimState store), so they persist across chunk eviction and save/load.
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

	// Tap a stash item → equip it. A displaced occupant returns to the stash via
	// the Inventory equip path, so we refresh the whole list afterward.
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
		// The item now lives in a slot — pull it out of the stash list. (Any
		// displaced piece was already pushed back onto the list by Inventory.)
		_stash.Remove(item);
		RefreshStash();
	}

	bool EquipFromStash(ItemState item)
	{
		Inventory inv = _player.Inventory;
		switch (item.data.Category)
		{
			case EItemCategory.WeaponMelee:
			case EItemCategory.WeaponRanged:
			case EItemCategory.Armor:
			case EItemCategory.Helmet:
				return inv.TryEquip(item, item.data.EquipSlotKind);
			case EItemCategory.Equipment:
				// The single consumable slot is the attuned alchemy spell (set at the
				// alchemy campfire screen), not a stash-equip target — Equipment-category
				// items (cooked dishes, etc.) stay in the stash.
				return false;
			default:
				return false;
		}
	}

	// ---- Equip-slot side (unequip) ----------------------------------------

	void OnInventoryFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanelInventory?.SetItem(item);
		_itemInfoPanelStash?.SetItem(null);
	}

	// Tap an equipped item → nothing here is stashable now: weapons / armor /
	// helmets are permanent until replaced, and the Equipment "slot" is the attuned
	// alchemy spell (managed at the alchemy campfire screen, not sent to the stash).
	void OnInventoryPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
	}
}
