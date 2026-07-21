using Godot;

// Inventory tab rendered inside AlmanacScreen. A read-only readout of the
// controlled member: player stats, the weapons equipped in the melee / ranged
// slots (ItemInfoPanel viewers), and the carried MATERIAL backpack. Purely
// informational — there is no interaction here. Equipping weapons / armor /
// equipment happens on the camp Stash screen; dropping materials happens
// elsewhere.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private ItemInfoPanel _meleePanel;
	[Export] private ItemInfoPanel _armorPanel;
	[Export] private ItemInfoPanel _rangedPanel;
	[Export] private ItemInfoPanel _spellPanel;
	[Export] private BackpackPanel _backpackPanel;

	GameClient _gameClient;
	Player _player;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_player = _gameClient?.Player;
			_statsPanel?.SetPlayer(_player);
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged += Refresh;
			}
			Refresh();
		}
		else if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= Refresh;
		}
	}

	// Repaint the equipped-weapon viewers and the material backpack from the
	// live inventory. Bound to Inventory.onChanged so an ammo change shows
	// immediately.
	void Refresh()
	{
		Inventory inv = _player?.Inventory;
		_armorPanel?.SetItem(inv?.GetEquipped(EInventorySlot.Armor), forceIdentified: true);
		_meleePanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponMelee), forceIdentified: true);
		_rangedPanel?.SetItem(inv?.GetWeapon(EInventorySlot.WeaponRanged), forceIdentified: true);
		// The attuned alchemy spell (the active consumable); its SpellData reagents
		// surface as the panel's Required Reagents row. Hidden when nothing is attuned.
		_spellPanel?.SetItem(inv?.GetEquipped(EInventorySlot.Equipment), forceIdentified: true);
		_backpackPanel?.Refresh(inv?.Backpack);
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
