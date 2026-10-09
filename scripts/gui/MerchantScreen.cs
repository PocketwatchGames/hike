using Godot;
using System;
using System.Collections.Generic;

// Modal merchant + gifting screen: the member's inventory, a Give pile they stage
// offers in, and — when trading — the merchant's stock and a Get pile of what
// they ask for. Gift mode hides the merchant's two sides.
//
// The member's inventory and the Give pile are the player's side; the stock and
// the Get pile are the merchant's. Nothing crosses between the two until a
// commit, and every move goes through ItemTransfer, as on the stash screen.
//   A  — pick up the stack under the cursor; with one picked up, put it down
//        here, on the same owner's side. Hold on a stack to pick up only some.
//   X  — equip / unequip, on the member's own items only.
//   Y  — use, on the member's own items only.
//   LT — send the stack across the trade: inventory <-> Give, stock <-> Get.
//        Hold on a stack to send only some of it.
//   RT — drop one of the player's stacks at the member's feet.
//        Hold on a stack to drop only some of it.
//   B  — put the pick-up back; with nothing picked up, close the screen.
[GlobalClass]
public partial class MerchantScreen : Control
{
	[Export] private TextureRect _merchantPortrait;
	[Export] private Label _merchantNameLabel;
	[Export] private Label _merchantConversationLabel;
	// The containers hidden in gift mode.
	[Export] private Control _merchantInventoryPanel;
	[Export] private Control _getPanel;
	[Export] private Button _tradeButton;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private BackpackPanel _stockGrid;
	[Export] private BackpackPanel _giveGrid;
	[Export] private BackpackPanel _getGrid;
	[Export] private ItemCountPanel _countPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private ButtonHint _hintSelect;
	[Export] private ButtonHint _hintEquip;
	[Export] private ButtonHint _hintUse;
	[Export] private ButtonHint _hintSend;
	[Export] private ButtonHint _hintDrop;
	[Export(PropertyHint.Range, "1,24,1")] private int _giveSlots = 3;
	[Export(PropertyHint.Range, "1,24,1")] private int _getSlots = 3;
	// The stock grid grows past this to fit a merchant with more stacks.
	[Export(PropertyHint.Range, "1,48,1")] private int _stockMinSlots = 9;
	// How long Select, Send or Drop must be held on a stack to choose how many.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;

	const string SelectAction = "ui_select";
	const string EquipAction = "MenuSecondary";
	const string UseAction = "MenuTertiary";
	const string SendAction = "MenuQuaternary";
	const string DropAction = "MenuQuinary";

	enum ESide
	{
		None,
		Belt,
		Backpack,
		// The member's equip slots; the index is the EInventorySlot.
		Equip,
		Give,
		Stock,
		Get,
	}

	readonly struct Slot
	{
		public readonly ESide side;
		public readonly int index;

		public Slot(ESide side, int index)
		{
			this.side = side;
			this.index = index;
		}

		public static readonly Slot None = new(ESide.None, -1);
		public bool IsNone => side == ESide.None;
		public bool Is(Slot other) => side == other.side && index == other.index;
	}

	Action _onClose;
	GameClient _gameClient;
	Player _player;
	Mob _merchant;
	bool _trading;

	// Staged piles. Give holds stacks detached from the member's inventory, Get
	// stacks detached from the stock; both go back where they came from on close.
	ItemGrid _give;
	ItemGrid _get;
	// This session's copy of the merchant's shop side (secret entries skipped),
	// written back to the mob only on a successful commit so a cancel leaves its
	// stock untouched.
	ItemGrid _stock;
	readonly Dictionary<ItemData, MobInventoryItem> _stockSourceByData = new();

	Slot _focused = Slot.None;
	// The picked-up stack and how many of its units are moving.
	Slot _picked = Slot.None;
	int _pickedCount;

	// A held on a slot: where, for how long, and whether the hold already fired
	// (so its release isn't also a tap). A is the slot button's own press.
	Slot _selectPressed = Slot.None;
	float _selectHeld;
	bool _selectHoldFired;
	readonly PolledHold _send = new(SendAction);
	readonly PolledHold _drop = new(DropAction);

	bool PickerOpen => _countPanel != null && _countPanel.IsOpen;

	public override void _Ready()
	{
		Visible = false;
		WirePanel(_stockGrid, ESide.Stock);
		WirePanel(_giveGrid, ESide.Give);
		WirePanel(_getGrid, ESide.Get);
		WirePanel(_inventoryPanel?.BeltGrid, ESide.Belt);
		WirePanel(_inventoryPanel?.BackpackGrid, ESide.Backpack);
		if (_inventoryPanel != null)
		{
			foreach (EInventorySlot equip in InventoryPanel.EquipSlots)
			{
				ItemSlotPanel panel = _inventoryPanel.EquipPanel(equip);
				if (panel == null)
				{
					continue;
				}
				var slot = new Slot(ESide.Equip, (int)equip);
				panel.onFocusEntered += _ => OnSlotFocused(slot);
				panel.onButtonDown += _ => OnSelectDown(slot);
				panel.onButtonUp += _ => OnSelectUp(slot);
			}
		}
		if (_tradeButton != null)
		{
			_tradeButton.Pressed += OnTradeButtonPressed;
			_tradeButton.FocusEntered += OnTradeButtonFocused;
			_tradeButton.MouseEntered += OnTradeButtonMouseEntered;
		}
		_itemInfoPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_tradeButton != null)
		{
			_tradeButton.Pressed -= OnTradeButtonPressed;
			_tradeButton.FocusEntered -= OnTradeButtonFocused;
			_tradeButton.MouseEntered -= OnTradeButtonMouseEntered;
		}
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= Refresh;
		}
	}

	void WirePanel(BackpackPanel panel, ESide side)
	{
		if (panel == null)
		{
			return;
		}
		panel.onSlotFocused += (index, _) => OnSlotFocused(new Slot(side, index));
		panel.onSlotButtonDown += (index, _) => OnSelectDown(new Slot(side, index));
		panel.onSlotButtonUp += (index, _) => OnSelectUp(new Slot(side, index));
	}

	public void Open(Player player, Mob merchant, bool trade = true, Action onClose = null)
	{
		_player = player;
		_merchant = merchant;
		_trading = trade;
		_onClose = onClose;
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
		_give = new ItemGrid(_giveSlots);
		_get = new ItemGrid(_getSlots);
		PopulateStock();
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged += Refresh;
		}
		ClearPick();
		ResetHolds();
		UpdateMerchantInfo();
		if (_getPanel != null)
		{
			_getPanel.Visible = _trading;
		}
		if (_merchantInventoryPanel != null)
		{
			_merchantInventoryPanel.Visible = _trading;
		}
		SetConversation(trade ? "What have you brought me?" : "What would you like to trade?");
		Visible = true;
		Refresh();
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= Refresh;
		}
		ReturnGiveToInventory();
		_countPanel?.Dismiss();
		_stockGrid?.ClearVisuals();
		_giveGrid?.ClearVisuals();
		_getGrid?.ClearVisuals();
		_inventoryPanel?.ClearSelectVisuals();
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
		_focused = Slot.None;
		ClearPick();
		_give = null;
		_get = null;
		_stock = null;
		_stockSourceByData.Clear();
		_merchant = null;
		_player = null;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	// GrabFocus needs the slot visible-in-tree, hence deferred from Open.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		BackpackPanel belt = _inventoryPanel?.BeltGrid;
		BackpackPanel backpack = _inventoryPanel?.BackpackGrid;
		ItemSlotPanel start = belt?.FirstOccupied() ?? backpack?.FirstOccupied()
			?? belt?.GetSlot(0) ?? backpack?.GetSlot(0);
		start?.GrabFocus();
	}

	// ---- Sides -----------------------------------------------------------------

	IItemGrid Grid(ESide side)
	{
		return side switch
		{
			ESide.Belt => _player?.Inventory?.Belt,
			ESide.Backpack => _player?.Inventory?.Backpack,
			ESide.Give => _give,
			ESide.Stock => _stock,
			ESide.Get => _get,
			_ => null,
		};
	}

	ItemState ItemAt(Slot slot)
	{
		return slot.side == ESide.Equip
			? _player?.Inventory?.GetEquipped((EInventorySlot)slot.index)
			: Grid(slot.side)?.At(slot.index);
	}

	static bool IsMemberSide(ESide side) => side is ESide.Belt or ESide.Backpack or ESide.Equip;
	static bool IsPlayerSide(ESide side) => IsMemberSide(side) || side == ESide.Give;
	static bool IsMerchantSide(ESide side) => side is ESide.Stock or ESide.Get;

	static bool SameOwner(ESide a, ESide b)
	{
		return (IsPlayerSide(a) && IsPlayerSide(b)) || (IsMerchantSide(a) && IsMerchantSide(b));
	}

	// Where Send moves a stack: across the trade, within its owner's side.
	IItemGrid SendTarget(ESide side, ItemState item)
	{
		return side switch
		{
			ESide.Belt or ESide.Backpack or ESide.Equip => _give,
			ESide.Give => _player?.Inventory?.PreferredGrid(item?.data),
			ESide.Stock => _get,
			ESide.Get => _stock,
			_ => null,
		};
	}

	// ---- Focus -----------------------------------------------------------------

	void OnSlotFocused(Slot slot)
	{
		_focused = slot;
		_send.Reset(_hintSend);
		_drop.Reset(_hintDrop);
		_itemInfoPanel?.SetItem(ItemAt(slot));
		UpdateHints();
	}

	void OnTradeButtonFocused()
	{
		_focused = Slot.None;
		_itemInfoPanel?.SetItem(null);
		UpdateHints();
	}

	void OnTradeButtonMouseEntered()
	{
		_tradeButton?.GrabFocus();
	}

	// ---- A: pick up / put down -------------------------------------------------

	void OnSelectDown(Slot slot)
	{
		_selectPressed = slot;
		_selectHeld = 0f;
		_selectHoldFired = false;
	}

	void OnSelectUp(Slot slot)
	{
		Slot pressed = _selectPressed;
		bool fired = _selectHoldFired;
		_selectPressed = Slot.None;
		_selectHoldFired = false;
		_hintSelect?.SetProgress(0f);
		// Dragging off the pressed slot before letting go reads as a cancel.
		if (fired || PickerOpen || !pressed.Is(slot))
		{
			return;
		}
		Select(slot);
	}

	void TickSelectHold(float dt)
	{
		if (_selectPressed.IsNone || _selectHoldFired)
		{
			return;
		}
		_selectHeld += dt;
		_hintSelect?.SetProgress(Mathf.Clamp(_selectHeld / _holdSeconds, 0f, 1f));
		if (_selectHeld < _holdSeconds)
		{
			return;
		}
		_selectHoldFired = true;
		_hintSelect?.SetProgress(0f);
		Slot slot = _selectPressed;
		ItemState item = ItemAt(slot);
		// A hold only means something on a stack that isn't already picked up.
		if (!_picked.IsNone || item == null || item.stackCount <= 1)
		{
			Select(slot);
			return;
		}
		_countPanel?.Open(item.stackCount, count => Pick(slot, count), prompt: Loc.Get(Loc.Keys.stash_pick_how_many));
	}

	// A tap: pick up the whole stack, or put the picked-up one down here.
	void Select(Slot slot)
	{
		if (_picked.IsNone)
		{
			ItemState item = ItemAt(slot);
			if (item != null)
			{
				Pick(slot, item.stackCount);
			}
			return;
		}
		Slot from = _picked;
		int count = _pickedCount;
		if (!from.Is(slot) && !SameOwner(from.side, slot.side))
		{
			return;
		}
		ClearPick();
		if (!from.Is(slot) && MoveTo(from, count, slot))
		{
			OnMovedAcross(from.side, slot.side);
		}
		Refresh();
	}

	bool MoveTo(Slot from, int count, Slot to)
	{
		Inventory inv = _player?.Inventory;
		if (from.side == ESide.Equip && to.side == ESide.Equip)
		{
			return false;
		}
		if (to.side == ESide.Equip)
		{
			return InventoryScreen.EquipCompatible((EInventorySlot)to.index, ItemAt(from))
				&& ItemTransfer.Equip(Grid(from.side), from.index, inv);
		}
		if (from.side == ESide.Equip)
		{
			return ItemTransfer.Unequip(inv, (EInventorySlot)from.index, Grid(to.side), to.index);
		}
		return ItemTransfer.MoveTo(Grid(from.side), from.index, count, Grid(to.side), to.index);
	}

	void Pick(Slot slot, int count)
	{
		if (count <= 0 || ItemAt(slot) == null)
		{
			return;
		}
		_picked = slot;
		_pickedCount = count;
		ApplySlotStates();
		UpdateHints();
	}

	void ClearPick()
	{
		_picked = Slot.None;
		_pickedCount = 0;
		ApplySlotStates();
		UpdateHints();
	}

	// ---- Send across the trade / drop -----------------------------------------

	void TickPolledHolds(float dt)
	{
		ItemState item = _picked.IsNone ? ItemAt(_focused) : null;
		Slot slot = _focused;

		PolledHold.EResult send = _send.Tick(item != null, dt, _holdSeconds, _hintSend);
		if (send == PolledHold.EResult.Tap || (send == PolledHold.EResult.Hold && item.stackCount <= 1))
		{
			Send(slot, item.stackCount);
			return;
		}
		if (send == PolledHold.EResult.Hold)
		{
			_countPanel?.Open(item.stackCount, count => Send(slot, count), prompt: Loc.Get(Loc.Keys.stash_send_how_many));
			return;
		}

		ItemState droppable = IsPlayerSide(slot.side) ? item : null;
		PolledHold.EResult drop = _drop.Tick(droppable != null, dt, _holdSeconds, _hintDrop);
		if (drop == PolledHold.EResult.Tap || (drop == PolledHold.EResult.Hold && droppable.stackCount <= 1))
		{
			Drop(slot, droppable.stackCount);
			return;
		}
		if (drop == PolledHold.EResult.Hold)
		{
			_countPanel?.Open(droppable.stackCount, count => Drop(slot, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
		}
	}

	void Send(Slot slot, int count)
	{
		bool sent;
		if (slot.side == ESide.Equip)
		{
			sent = ItemTransfer.SendEquipped(_player?.Inventory, (EInventorySlot)slot.index, _give);
		}
		else
		{
			sent = count > 0 && ItemTransfer.Send(Grid(slot.side), slot.index, count, SendTarget(slot.side, ItemAt(slot))) > 0;
		}
		if (sent)
		{
			OnMovedAcross(slot.side, slot.side switch
			{
				ESide.Give => ESide.Backpack,
				ESide.Stock => ESide.Get,
				ESide.Get => ESide.Stock,
				_ => ESide.Give,
			});
		}
		Refresh();
	}

	void Drop(Slot slot, int count)
	{
		if (slot.side == ESide.Equip)
		{
			_player?.Inventory?.Drop(ItemAt(slot));
		}
		else if (count > 0)
		{
			_player?.DropAtFeet(Grid(slot.side)?.Take(slot.index, count));
		}
		Refresh();
	}

	// The merchant remarks on a stack crossing between a side and its pile.
	void OnMovedAcross(ESide from, ESide to)
	{
		if (IsMemberSide(from) && to == ESide.Give)
		{
			SetConversation(_trading ? "Yes... and what would you like in return?" : "Mmm, let me see...");
		}
		else if (from == ESide.Give && IsMemberSide(to))
		{
			SetConversation("Changed your mind?");
		}
		else if (from == ESide.Stock && to == ESide.Get)
		{
			SetConversation("That'll cost you.");
		}
		else if (from == ESide.Get && to == ESide.Stock)
		{
			SetConversation("Not what you wanted?");
		}
	}

	// ---- Input / tick ----------------------------------------------------------

	public override void _Process(double delta)
	{
		if (!Visible || _player == null || PickerOpen)
		{
			return;
		}
		TickSelectHold((float)delta);
		TickPolledHolds((float)delta);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel"))
		{
			if (PickerOpen)
			{
				return;
			}
			if (!_picked.IsNone)
			{
				ClearPick();
			}
			else
			{
				Close();
			}
			GetViewport().SetInputAsHandled();
			return;
		}
		if (PickerOpen)
		{
			return;
		}
		if (e.IsActionPressed(EquipAction) && EquipVerb() != null)
		{
			InventoryScreen.ToggleEquip(_player, ItemAt(_focused));
			GetViewport().SetInputAsHandled();
		}
		else if (e.IsActionPressed(UseAction) && UseVerb() != null)
		{
			InventoryScreen.Use(_player, ItemAt(_focused));
			GetViewport().SetInputAsHandled();
		}
	}

	void ResetHolds()
	{
		_selectPressed = Slot.None;
		_selectHeld = 0f;
		_selectHoldFired = false;
		_hintSelect?.SetProgress(0f);
		_send.Arm(_hintSend);
		_drop.Arm(_hintDrop);
	}

	// ---- Merchant header -------------------------------------------------------

	void UpdateMerchantInfo()
	{
		if (_merchant == null)
		{
			return;
		}
		MobData md = _merchant.mobData;
		if (_merchantNameLabel != null)
		{
			_merchantNameLabel.Text = md != null ? md.displayName.ToString() : string.Empty;
		}
		if (_merchantPortrait != null && md?.bestiaryPortrait != null)
		{
			_merchantPortrait.Texture = md.bestiaryPortrait;
		}
	}

	void SetConversation(string text)
	{
		if (_merchantConversationLabel == null)
		{
			return;
		}
		_merchantConversationLabel.Text = string.IsNullOrEmpty(text)
			? string.Empty
			: LanguageText.Render(text, _merchant?.SpokenLanguage, _player);
	}

	// ---- Trade / Gift button ---------------------------------------------------

	bool IsGiftCommit()
	{
		return _give != null && _give.OccupiedCount() > 0 && (_get == null || _get.OccupiedCount() == 0);
	}

	void OnTradeButtonPressed()
	{
		if (PickerOpen)
		{
			return;
		}
		ClearPick();
		if (IsGiftCommit())
		{
			CommitGift();
		}
		else
		{
			CommitTrade();
		}
	}

	void CommitGift()
	{
		if (_give.OccupiedCount() == 0)
		{
			SetConversation("You haven't offered anything.");
			return;
		}
		if (_merchant == null)
		{
			return;
		}
		if (!_merchant.HasReciprocableGift(_player))
		{
			SetConversation("I have nothing of value to give you in return.");
			return;
		}
		List<ItemState> accepted = TakeAcceptableFromGive(out bool anyLeftover, out float loyaltyGained);
		if (accepted.Count == 0)
		{
			SetConversation("I cannot accept any of these.");
			return;
		}
		List<LoyaltyGift> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		FinalizeMerchantInventoryAfterCommit(accepted);
		if (awarded.Count > 0)
		{
			SetConversation("Thank you so much, here's a gift for you!");
		}
		else if (anyLeftover)
		{
			SetConversation("Some of these I can't accept, but thank you for the rest.");
		}
		else
		{
			SetConversation("Thank you, this means a lot.");
		}
		Refresh();
	}

	void CommitTrade()
	{
		if (_give.OccupiedCount() == 0 && _get.OccupiedCount() == 0)
		{
			SetConversation("Nothing to trade.");
			return;
		}
		if (_merchant == null)
		{
			return;
		}
		float giveValue = _merchant.CalculatePersonalValue(Occupied(_give));
		float getValue = 0f;
		foreach (ItemState s in Occupied(_get))
		{
			getValue += _merchant.PerUnitValue(s.data) * s.stackCount;
		}
		if (getValue >= giveValue)
		{
			SetConversation("That trade isn't worth my while.");
			return;
		}
		List<ItemState> accepted = TakeAcceptableFromGive(out _, out _);
		for (int i = 0; i < _get.Capacity; i++)
		{
			ItemState received = _get.Take(i, int.MaxValue);
			if (received?.data == null)
			{
				continue;
			}
			int initial = received.stackCount;
			int added = _player?.Inventory?.TryAdd(received) ?? 0;
			if (added < initial)
			{
				DropAtMerchant(received);
			}
		}
		float loyaltyGained = giveValue - getValue;
		List<LoyaltyGift> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		FinalizeMerchantInventoryAfterCommit(accepted);
		SetConversation(awarded.Count > 0 ? "Pleasure doing business — and please, take this as well." : "Pleasure doing business.");
		Refresh();
	}

	static IEnumerable<ItemState> Occupied(ItemGrid grid)
	{
		foreach (ItemState s in grid.Slots)
		{
			if (s?.data != null && s.stackCount > 0)
			{
				yield return s;
			}
		}
	}

	// Detach from the Give pile every unit the merchant will take; the rest stays
	// staged for the player to take back.
	List<ItemState> TakeAcceptableFromGive(out bool anyLeftover, out float loyaltyGained)
	{
		anyLeftover = false;
		loyaltyGained = 0f;
		List<ItemState> accepted = new();
		if (_merchant == null)
		{
			return accepted;
		}
		for (int i = 0; i < _give.Capacity; i++)
		{
			ItemState stack = _give.At(i);
			if (stack?.data == null)
			{
				continue;
			}
			int units = _merchant.AcceptableUnits(stack.data, stack.stackCount);
			if (units < stack.stackCount)
			{
				anyLeftover = true;
			}
			if (units <= 0)
			{
				continue;
			}
			loyaltyGained += _merchant.PerUnitValue(stack.data) * units;
			accepted.Add(_give.Take(i, units));
		}
		return accepted;
	}

	void ApplyAwardedGifts(List<LoyaltyGift> awarded)
	{
		if (awarded == null || awarded.Count == 0 || _player == null)
		{
			return;
		}
		GameClient gc = GameClient.Current;
		foreach (LoyaltyGift gift in awarded)
		{
			if (gift == null)
			{
				continue;
			}
			if (gift.item != null)
			{
				ItemState state = gift.item.CreateState();
				state.SetCount(Mathf.Max(1, gift.count));
				int initial = state.stackCount;
				int added = _player.Inventory?.TryAdd(state) ?? 0;
				if (added < initial)
				{
					DropAtMerchant(state);
				}
				gc?.Announce(new Announcement
				{
					type = EAnnouncementType.GiftReceived,
					title = "Gift Received",
					subtitle = gift.item.displayName.ToString(),
					icon = gift.item.inventorySprite,
				});
			}
			if (gift.language != null && gift.languageComponents != ELanguageComponents.None)
			{
				_player.LearnLanguageComponents(gift.language, gift.languageComponents);
			}
		}
	}

	void DropAtMerchant(ItemState item)
	{
		if (item == null || item.stackCount <= 0 || _merchant == null || _player?.Sim == null)
		{
			return;
		}
		Vector3 basePos = _merchant.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 offset = new Vector3((GD.Randf() - 0.5f) * 0.6f, 0f, (GD.Randf() - 0.5f) * 0.6f);
		Vector3 impulse = new Vector3((GD.Randf() - 0.5f) * 2f, 1.5f, (GD.Randf() - 0.5f) * 2f);
		_player.Sim.DropItem(item, basePos + offset, impulse, requireInteract: true);
	}

	// ---- Merchant stock snapshot -------------------------------------------------

	void PopulateStock()
	{
		_stockSourceByData.Clear();
		var entries = new List<MobInventoryItem>();
		if (_merchant?.Inventory != null)
		{
			foreach (MobInventoryItem entry in _merchant.Inventory)
			{
				if (entry == null || entry.secret || entry.item?.data == null || entry.item.stackCount <= 0)
				{
					continue;
				}
				entries.Add(entry);
			}
		}
		_stock = new ItemGrid(Mathf.Max(_stockMinSlots, entries.Count));
		for (int i = 0; i < entries.Count; i++)
		{
			MobInventoryItem entry = entries[i];
			ItemState snapshot = entry.item.data.CreateState();
			snapshot.SetCount(entry.item.stackCount);
			_stock[i] = snapshot;
			_stockSourceByData[entry.item.data] = entry;
		}
	}

	void FinalizeMerchantInventoryAfterCommit(IList<ItemState> sold)
	{
		WriteBackMerchantInventory();
		AddSoldItemsToMerchantInventory(sold);
		PopulateStock();
	}

	void AddSoldItemsToMerchantInventory(IList<ItemState> sold)
	{
		if (_merchant?.Inventory == null || sold == null)
		{
			return;
		}
		foreach (ItemState taken in sold)
		{
			if (taken?.data == null || taken.stackCount <= 0)
			{
				continue;
			}
			int remaining = taken.stackCount;
			if (taken.data.IsStackable)
			{
				foreach (MobInventoryItem entry in _merchant.Inventory)
				{
					if (remaining <= 0)
					{
						break;
					}
					if (entry?.item?.data != taken.data || entry.secret)
					{
						continue;
					}
					int space = entry.item.RemainingStackSpace();
					if (space <= 0)
					{
						continue;
					}
					int moved = Mathf.Min(space, remaining);
					entry.item.AddUnits(moved, 0);
					remaining -= moved;
				}
			}
			if (remaining > 0)
			{
				ItemState fresh = taken.data.CreateState();
				fresh.SetCount(remaining);
				_merchant.Inventory.Add(new MobInventoryItem
				{
					item = fresh,
					loyaltyCost = 0f,
					secret = false,
				});
			}
		}
	}

	// The stock grid back onto the mob's entries, by kind: whatever was bought
	// out of a kind leaves its entry.
	void WriteBackMerchantInventory()
	{
		if (_merchant?.Inventory == null || _stockSourceByData.Count == 0)
		{
			return;
		}
		var remaining = new Dictionary<ItemData, int>();
		foreach (ItemState s in Occupied(_stock))
		{
			remaining.TryGetValue(s.data, out int prior);
			remaining[s.data] = prior + s.stackCount;
		}
		List<MobInventoryItem> inv = _merchant.Inventory;
		foreach (var kv in _stockSourceByData)
		{
			MobInventoryItem entry = kv.Value;
			if (entry?.item == null)
			{
				continue;
			}
			remaining.TryGetValue(kv.Key, out int total);
			if (total > 0)
			{
				entry.item.SetCount(total);
			}
			else
			{
				inv.Remove(entry);
			}
		}
	}

	void ReturnGiveToInventory()
	{
		if (_give == null)
		{
			return;
		}
		for (int i = 0; i < _give.Capacity; i++)
		{
			ItemState staged = _give.Take(i, int.MaxValue);
			if (staged?.data == null)
			{
				continue;
			}
			int initial = staged.stackCount;
			int added = _player?.Inventory?.TryAdd(staged) ?? 0;
			if (added < initial)
			{
				_player?.DropAtFeet(staged);
			}
		}
	}

	// ---- Repaint -----------------------------------------------------------------

	void Refresh()
	{
		_stockGrid?.Refresh(_stock?.Slots);
		_giveGrid?.Refresh(_give?.Slots);
		_getGrid?.Refresh(_get?.Slots);
		_inventoryPanel?.Paint(_player?.Inventory);
		// A picked stack can vanish under the cursor (spoiled, spent elsewhere).
		ItemState picked = ItemAt(_picked);
		if (!_picked.IsNone && (picked == null || picked.stackCount < _pickedCount))
		{
			_picked = Slot.None;
			_pickedCount = 0;
		}
		ApplySlotStates();
		_itemInfoPanel?.SetItem(ItemAt(_focused));
		if (_tradeButton != null)
		{
			_tradeButton.Text = IsGiftCommit() ? "Gift" : "Trade";
		}
		UpdateHints();
	}

	// The picked-up slot dims — the overlay the plain repaint doesn't know.
	void ApplySlotStates()
	{
		ApplySlotStates(_stockGrid, ESide.Stock);
		ApplySlotStates(_giveGrid, ESide.Give);
		ApplySlotStates(_getGrid, ESide.Get);
		ApplySlotStates(_inventoryPanel?.BeltGrid, ESide.Belt);
		ApplySlotStates(_inventoryPanel?.BackpackGrid, ESide.Backpack);
		if (_inventoryPanel != null)
		{
			foreach (EInventorySlot equip in InventoryPanel.EquipSlots)
			{
				_inventoryPanel.EquipPanel(equip)?.SetDimmed(_picked.side == ESide.Equip && _picked.index == (int)equip);
			}
		}
	}

	void ApplySlotStates(BackpackPanel panel, ESide side)
	{
		if (panel == null)
		{
			return;
		}
		int i = 0;
		foreach (ItemSlotPanel slot in panel.EnumerateSlots())
		{
			slot.SetDimmed(_picked.side == side && _picked.index == i);
			i++;
		}
	}

	void UpdateHints()
	{
		if (_focused.IsNone)
		{
			// On the Trade / Gift button, A presses it.
			bool onButton = _tradeButton != null && _tradeButton.HasFocus();
			ShowHint(_hintSelect, SelectAction, onButton && _picked.IsNone ? _tradeButton.Text : null);
			ShowHint(_hintEquip, EquipAction, null);
			ShowHint(_hintUse, UseAction, null);
			ShowHint(_hintSend, SendAction, null);
			ShowHint(_hintDrop, DropAction, null);
			return;
		}
		ItemState focused = ItemAt(_focused);
		if (!_picked.IsNone)
		{
			bool canPlace = _picked.Is(_focused) || SameOwner(_picked.side, _focused.side);
			ShowHint(_hintSelect, SelectAction, canPlace ? Loc.Get(Loc.Keys.stash_place) : null);
			ShowHint(_hintEquip, EquipAction, null);
			ShowHint(_hintUse, UseAction, null);
			ShowHint(_hintSend, SendAction, null);
			ShowHint(_hintDrop, DropAction, null);
			return;
		}
		ShowHint(_hintSelect, SelectAction, focused != null ? Loc.Get(Loc.Keys.stash_select) : null);
		ShowHint(_hintEquip, EquipAction, EquipVerb());
		ShowHint(_hintUse, UseAction, UseVerb());
		ShowHint(_hintSend, SendAction, focused != null ? SendVerb(_focused.side) : null);
		ShowHint(_hintDrop, DropAction, focused != null && IsPlayerSide(_focused.side) ? Loc.Get(Loc.Keys.stash_drop) : null);
	}

	static string SendVerb(ESide side)
	{
		return side switch
		{
			ESide.Give => Loc.Get(Loc.Keys.merchant_take_back),
			ESide.Stock => Loc.Get(Loc.Keys.merchant_request),
			ESide.Get => Loc.Get(Loc.Keys.merchant_return),
			_ => Loc.Get(Loc.Keys.merchant_offer),
		};
	}

	// Equip and Use act only on the member's own items, and not while something
	// is picked up.
	bool OnMemberItem => _picked.IsNone && IsMemberSide(_focused.side);

	string EquipVerb() => OnMemberItem ? InventoryScreen.EquipVerb(_player, ItemAt(_focused)) : null;

	string UseVerb() => OnMemberItem ? InventoryScreen.UseVerb(_player, ItemAt(_focused)) : null;

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
}
