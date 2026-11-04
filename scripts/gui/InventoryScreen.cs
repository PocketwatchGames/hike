using Godot;

// Inventory tab rendered inside AlmanacScreen: player stats, the member's equip
// slots and belt, the whole backpack and each carried sack slot for slot —
// highlighting a slot reads its item out in the detail panel. The equip slots
// and the belt only point at backpack stacks, so their items show in both
// places. Everything is editable:
//   A  — select the highlighted item; with one selected, move it to the
//        highlighted slot. Grid to grid (backpack or sack) swaps with whatever is
//        there. An equip slot takes only its own kind of gear and a belt slot
//        only a usable item, from the backpack's top level; one selected from an
//        equip or belt slot and put down on a grid slot comes off that slot.
//        Cancel deselects.
//   X  — equip / unequip the highlighted gear, or put a usable item on or off
//        the belt.
//   Y  — use / light / put out the highlighted item, per its kind and state.
//   RT — drop the highlighted item at the member's feet: a tap drops the whole
//        stack, a hold asks how many.
// The button hints belong to AlmanacScreen, which hands them over
// (BindActionHints) and hides them on every other tab.
[GlobalClass]
public partial class InventoryScreen : Control
{
	[Export] private PlayerStatsPanel _statsPanel;
	[Export] private PlayerVitalsHud _vitals;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private ItemSlotPanel _shieldPanel;
	[Export] private ItemSlotPanel _armorBodyPanel;
	[Export] private ItemSlotPanel _weaponLeftPanel;
	[Export] private ItemSlotPanel _weaponRightPanel;
	// The ammo the right-hand weapon fires.
	[Export] private ItemSlotPanel _ammoPanel;
	// The lantern slot — also the HUD's first hotbar entry.
	[Export] private ItemSlotPanel _lanternPanel;
	// The belt — the rest of the HUD hotbar — one slot per Inventory.BeltSlots.
	[Export] private BackpackPanel _beltPanel;
	[Export] private ItemInfoPanel _highlightPanel;
	// Asks how many when a hold-to-drop lands on a stack.
	[Export] private ItemCountPanel _countPanel;
	// How long Drop must be held to ask how many instead of dropping the stack.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _dropHoldSeconds = 0.6f;

	// A's glyph follows the primary-verb convention of the other inventory-style
	// screens; the press itself arrives as the slot button's own activation.
	const string SelectAction = "ui_select";
	const string EquipAction = "MenuSecondary";
	const string UseAction = "MenuTertiary";
	const string DropAction = "MenuQuinary";

	enum ESide
	{
		None,
		// A slot of one of the member's grids — belt, backpack or a sack.
		Carried,
		// The equip slots; the index is the EInventorySlot.
		Equip,
		// The belt; the index is the belt slot.
		Belt,
	}

	// A slot of the member's inventory: a grid and its index, an equip slot, or a
	// belt slot.
	readonly struct Slot
	{
		public readonly ESide side;
		public readonly IItemGrid grid;
		public readonly int index;

		public Slot(ESide side, int index, IItemGrid grid = null)
		{
			this.side = side;
			this.grid = grid;
			this.index = index;
		}

		public static readonly Slot None = new(ESide.None, -1);
		public bool IsNone => side == ESide.None;
		public bool equip => side == ESide.Equip;
		public bool Is(Slot other) => side == other.side && grid == other.grid && index == other.index;
		public EInventorySlot EquipSlot => equip ? (EInventorySlot)index : EInventorySlot.None;
	}

	GameClient _gameClient;
	Player _player;
	ButtonHint _hintSelect;
	ButtonHint _hintEquip;
	ButtonHint _hintUse;
	ButtonHint _hintDrop;

	// The slot under the cursor, and the slot picked up for a move.
	Slot _focused = Slot.None;
	Slot _selected = Slot.None;
	readonly PolledHold _drop = new(DropAction);

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public void BindActionHints(ButtonHint select, ButtonHint equip, ButtonHint use, ButtonHint drop)
	{
		_hintSelect = select;
		_hintEquip = equip;
		_hintUse = use;
		_hintDrop = drop;
		UpdateHints();
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onGridSlotFocused += (grid, index, _) => OnSlotFocused(new Slot(ESide.Carried, index, grid));
			_inventoryPanel.onGridSlotButtonUp += (grid, index, _) => OnSlotActivated(new Slot(ESide.Carried, index, grid));
			_inventoryPanel.onTabChanged += ApplySlotStates;
			_inventoryPanel.onTabsFocused += () => OnSlotFocused(Slot.None);
		}
		foreach (EInventorySlot equip in EquipSlots)
		{
			ItemSlotPanel panel = EquipPanel(equip);
			if (panel == null)
			{
				continue;
			}
			var slot = new Slot(ESide.Equip, (int)equip);
			panel.onFocusEntered += _ => OnSlotFocused(slot);
			panel.onButtonUp += _ => OnSlotActivated(slot);
		}
		if (_beltPanel != null)
		{
			_beltPanel.onSlotFocused += (index, _) => OnSlotFocused(new Slot(ESide.Belt, index));
			_beltPanel.onSlotButtonUp += (index, _) => OnSlotActivated(new Slot(ESide.Belt, index));
		}
		_highlightPanel?.SetItem(null);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_player = _gameClient?.Player;
			_statsPanel?.SetPlayer(_player);
			_vitals?.Bind(_player);
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged += Refresh;
			}
			Refresh();
			_drop.Arm(_hintDrop);
			// Deferred: GrabFocus needs the slot visible-in-tree, and the focus it
			// takes is what fills the highlight panel.
			Callable.From(ApplyInitialFocus).CallDeferred();
		}
		else
		{
			if (_player?.Inventory != null)
			{
				_player.Inventory.onChanged -= Refresh;
			}
			_vitals?.Bind(null);
			_selected = Slot.None;
			_focused = Slot.None;
			ResetDropHold();
			_highlightPanel?.SetItem(null);
		}
	}

	// Put keyboard / gamepad focus on the first occupied slot, so the grid is
	// navigable the moment the tab opens (and the highlight panel starts filled).
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		(_beltPanel?.FirstOccupied() ?? _inventoryPanel?.FirstOccupied())?.GrabFocus();
	}

	// The equip slots shown, in display order.
	static readonly EInventorySlot[] EquipSlots =
	{
		EInventorySlot.Lantern, EInventorySlot.Armor, EInventorySlot.Shield, EInventorySlot.WeaponLeft,
		EInventorySlot.WeaponRight, EInventorySlot.Ammo,
	};

	ItemSlotPanel EquipPanel(EInventorySlot slot)
	{
		return slot switch
		{
			EInventorySlot.Shield => _shieldPanel,
			EInventorySlot.Armor => _armorBodyPanel,
			EInventorySlot.WeaponLeft => _weaponLeftPanel,
			EInventorySlot.WeaponRight => _weaponRightPanel,
			EInventorySlot.Ammo => _ammoPanel,
			EInventorySlot.Lantern => _lanternPanel,
			_ => null,
		};
	}

	Inventory Inv => _player?.Inventory;

	ItemState ItemAt(Slot slot)
	{
		Inventory inv = Inv;
		if (inv == null || slot.IsNone)
		{
			return null;
		}
		return slot.side switch
		{
			ESide.Equip => inv.GetEquipped(slot.EquipSlot),
			ESide.Belt => inv.BeltAt(slot.index),
			_ => slot.grid?.At(slot.index),
		};
	}

	// A slot took focus (D-pad / keyboard, or the mouse hovering it —
	// ItemSlotPanel grabs focus on MouseEntered). Its item fills the detail panel.
	// Not force-identified: an unidentified reagent stays unread here, the same as
	// everywhere else.
	void OnSlotFocused(Slot slot)
	{
		_focused = slot;
		ResetDropHold();
		_highlightPanel?.SetItem(ItemAt(slot));
		UpdateHints();
	}

	// A on a slot (or a click): pick up the item there, or put the picked-up one
	// down here.
	void OnSlotActivated(Slot slot)
	{
		Inventory inv = Inv;
		if (inv == null)
		{
			return;
		}
		if (_selected.IsNone)
		{
			if (ItemAt(slot) != null)
			{
				SetSelection(slot);
			}
			return;
		}
		Slot from = _selected;
		SetSelection(Slot.None);
		if (!from.Is(slot))
		{
			MoveTo(inv, from, slot);
		}
	}

	void MoveTo(Inventory inv, Slot from, Slot to)
	{
		ItemState moving = ItemAt(from);
		if (moving == null)
		{
			return;
		}
		switch (to.side)
		{
			case ESide.Equip:
				if (EquipCompatible(to.EquipSlot, moving))
				{
					inv.Equip(moving);
				}
				return;
			case ESide.Belt:
				inv.SetBelt(to.index, moving);
				return;
		}
		switch (from.side)
		{
			case ESide.Equip:
				inv.Unequip(from.EquipSlot);
				return;
			case ESide.Belt:
				inv.SetBelt(from.index, null);
				return;
		}
		ItemTransfer.MoveTo(from.grid, from.index, moving.stackCount, to.grid, to.index);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!IsVisibleInTree())
		{
			return;
		}
		// Ahead of AlmanacScreen (a child's unhandled input runs first), so cancel
		// backs out of a move instead of closing the almanac.
		if (!_selected.IsNone && e.IsActionPressed("ui_cancel"))
		{
			SetSelection(Slot.None);
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_selected.IsNone && e.IsActionPressed(EquipAction))
		{
			ToggleEquip(_player, ItemAt(_focused));
			GetViewport().SetInputAsHandled();
		}
		else if (_selected.IsNone && e.IsActionPressed(UseAction))
		{
			Use(_player, ItemAt(_focused));
			GetViewport().SetInputAsHandled();
		}
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || (_countPanel != null && _countPanel.IsOpen))
		{
			return;
		}
		TickDropHold((float)delta);
	}

	void TickDropHold(float dt)
	{
		ItemState item = _selected.IsNone ? ItemAt(_focused) : null;
		PolledHold.EResult drop = _drop.Tick(item != null, dt, _dropHoldSeconds, _hintDrop);
		if (drop == PolledHold.EResult.Tap || (drop == PolledHold.EResult.Hold && (item.stackCount <= 1 || _countPanel == null)))
		{
			Inv.Drop(item);
		}
		else if (drop == PolledHold.EResult.Hold)
		{
			Inventory inv = Inv;
			_countPanel.Open(item.stackCount, count => inv.Drop(item, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
		}
	}

	void ResetDropHold()
	{
		_drop.Reset(_hintDrop);
	}

	void SetSelection(Slot slot)
	{
		_selected = slot;
		ResetDropHold();
		ApplySlotStates();
		UpdateHints();
	}

	// The picked-up slot dims — the overlay the plain repaint doesn't know.
	void ApplySlotStates()
	{
		if (_inventoryPanel == null)
		{
			return;
		}
		IItemGrid grid = _inventoryPanel.ShownGrid;
		int i = 0;
		foreach (ItemSlotPanel slot in _inventoryPanel.GridPanel.EnumerateSlots())
		{
			slot.SetDimmed(_selected.Is(new Slot(ESide.Carried, i, grid)));
			i++;
		}
		foreach (EInventorySlot equip in EquipSlots)
		{
			EquipPanel(equip)?.SetDimmed(_selected.Is(new Slot(ESide.Equip, (int)equip)));
		}
		if (_beltPanel != null)
		{
			int belt = 0;
			foreach (ItemSlotPanel slot in _beltPanel.EnumerateSlots())
			{
				slot.SetDimmed(_selected.Is(new Slot(ESide.Belt, belt)));
				belt++;
			}
		}
	}

	void UpdateHints()
	{
		ItemState focused = ItemAt(_focused);
		if (!_selected.IsNone)
		{
			ShowHint(_hintSelect, SelectAction, "Move");
			ShowHint(_hintEquip, EquipAction, null);
			ShowHint(_hintUse, UseAction, null);
			ShowHint(_hintDrop, DropAction, null);
			return;
		}
		ShowHint(_hintSelect, SelectAction, focused != null ? "Select" : null);
		ShowHint(_hintEquip, EquipAction, EquipVerb(_player, focused));
		ShowHint(_hintUse, UseAction, UseVerb(_player, focused));
		ShowHint(_hintDrop, DropAction, focused != null ? "Drop" : null);
	}

	// A null label hides the hint.
	static void ShowHint(ButtonHint hint, string action, string label)
	{
		if (hint == null)
		{
			return;
		}
		hint.Visible = label != null;
		if (label != null)
		{
			hint.SetHint(action, label);
		}
	}

	// Repaint the equip slots, the belt and the backpack from the live inventory.
	// Bound to Inventory.onChanged so an ammo change shows immediately. Re-reads
	// the highlight too, so a stack spent or merged away leaves no stale detail.
	void Refresh()
	{
		Inventory inv = Inv;
		_inventoryPanel?.Paint(inv);
		foreach (EInventorySlot equip in EquipSlots)
		{
			EquipPanel(equip)?.SetItem(inv?.GetEquipped(equip));
		}
		_lanternPanel?.SetActive(inv?.LitLantern != null);
		_beltPanel?.Refresh(inv?.BeltSlots);
		if (_beltPanel != null)
		{
			foreach (ItemSlotPanel slot in _beltPanel.EnumerateSlots())
			{
				slot.SetActive(inv != null && inv.IsLit(slot.Item));
			}
		}
		// A selected stack can vanish under the cursor (spoiled, spent elsewhere).
		if (!_selected.IsNone && ItemAt(_selected) == null)
		{
			_selected = Slot.None;
		}
		ApplySlotStates();
		_highlightPanel?.SetItem(ItemAt(_focused));
		UpdateHints();
	}

	// ---- Item verbs, shared with StashScreen and MerchantScreen -------------

	// The Equip verb: equip / unequip gear (a spare lantern and ammo included); put
	// a usable item (a potion, a torch — not a material) on or off the belt.
	public static void ToggleEquip(Player player, ItemState item)
	{
		if (EquipVerb(player, item) == null)
		{
			return;
		}
		if (item.data.IsEquippable)
		{
			player.Inventory.ToggleEquip(item);
		}
		else
		{
			player.Inventory.ToggleBelt(item);
		}
	}

	// The Equip hint's label for `item`, null when it has no verb — a material,
	// an item inside a sack, or a usable item with the belt full.
	public static string EquipVerb(Player player, ItemState item)
	{
		Inventory inv = player?.Inventory;
		if (item?.data == null || inv == null)
		{
			return null;
		}
		if (inv.IsEquipped(item) || inv.IsBelted(item))
		{
			return "Unequip";
		}
		return inv.CanEquip(item) || inv.CanBelt(item) ? "Equip" : null;
	}

	// The Use verb: use an instant item (mud, a meal) on the member, or start a
	// press-to-commit timeline (drinking a potion) — the menu stays open while it
	// plays — or light / put out the lantern-slot lantern or a belt torch. `source` holds the item
	// (null: the member's own inventory). Does nothing for an item with no verb
	// (gear, a material, or a timeline that needs the button held, which only the
	// hotbar can drive).
	public static void Use(Player player, ItemState item, IItemSource source = null)
	{
		Inventory inv = player?.Inventory;
		if (item?.data == null || inv == null)
		{
			return;
		}
		if (item is LanternState lantern && inv.IsLit(lantern))
		{
			player.SnuffLight();
		}
		else if (item is TorchState torch && TorchUsable(inv, torch))
		{
			player.ToggleTorch(torch);
		}
		else if (item is LanternState && inv.IsEquipped(item) && player.HasLanternOil)
		{
			player.LightLanternByHand();
		}
		else if (item.data is IInstantUseItem { CanUseInstantly: true })
		{
			player.UseInstantItem(item, source);
		}
		else if (UsableFromMenu(player, item))
		{
			player.StartUseAction(item, source);
		}
	}

	// A torch lights only on the belt.
	static bool TorchUsable(Inventory inv, TorchState torch) => inv.IsBelted(torch);

	// The Use hint's label for `item`, null when it has no verb.
	public static string UseVerb(Player player, ItemState item)
	{
		Inventory inv = player?.Inventory;
		if (item?.data == null || inv == null)
		{
			return null;
		}
		if (item is LanternState lantern && inv.IsLit(lantern))
		{
			return "Extinguish";
		}
		if (item is TorchState torch && TorchUsable(inv, torch))
		{
			return inv.IsLit(torch) ? "Extinguish" : "Light";
		}
		if (item is LanternState && inv.IsEquipped(item) && player.HasLanternOil)
		{
			return "Light";
		}
		if (item.data is IInstantUseItem { CanUseInstantly: true } || UsableFromMenu(player, item))
		{
			return "Use";
		}
		return null;
	}

	// A timeline the menu can start: not gear, press-to-commit (a menu never sends
	// the release a held action needs), and on the controlled member — an idle
	// one's runner doesn't tick (camp's stash can show one).
	static bool UsableFromMenu(Player player, ItemState item)
	{
		return player.IsActive && !item.data.IsEquippable
			&& item.data is IUsableItem { ActionProfile.commitOnPress: true };
	}

	// True when `item` may equip into `destSlot` — its category's slot matches.
	public static bool EquipCompatible(EInventorySlot destSlot, ItemState item)
	{
		return item?.data != null && item.data.EquipSlotKind == destSlot;
	}
}
