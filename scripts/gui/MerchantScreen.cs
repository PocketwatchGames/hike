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
// Trading is all this screen does — no equipping, using or dropping.
//   A  — send the stack across the trade: inventory <-> Give (offer / take
//        back), stock <-> Get (request / return). Hold to send only some.
//        On the Trade / Gift button, press it.
//   B  — close the screen.
[GlobalClass]
public partial class MerchantScreen : Control
{
	// What the merchant says, typed out over the top of the screen, and the tab
	// above it naming them.
	[Export] private TypingText _merchantRemark;
	[Export] private Control _merchantNameTab;
	[Export] private Label _merchantNameLabel;
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
	[Export] private ButtonHint _hintSend;
	[Export(PropertyHint.Range, "1,24,1")] private int _giveSlots = 3;
	[Export(PropertyHint.Range, "1,24,1")] private int _getSlots = 3;
	// The stock grid grows past this to fit a merchant with more stacks.
	[Export(PropertyHint.Range, "1,48,1")] private int _stockMinSlots = 9;
	// How long A must be held on a stack to choose how many to send.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;
	// A trade the merchant values at this many times what they hand over is
	// remarked on as generous rather than merely fair.
	[Export(PropertyHint.Range, "1,5,0.05")] private float _generousDealRatio = 1.5f;

	const string SendAction = "ui_select";

	enum ESide
	{
		None,
		// A slot of one of the member's grids — the backpack or a sack.
		Carried,
		Give,
		Stock,
		Get,
	}

	readonly struct Slot
	{
		public readonly ESide side;
		// The member's grid, for a Carried slot.
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
		public bool Is(Slot other) => side == other.side && grid == other.grid && index == other.index;
	}

	Action _onClose;
	GameClient _gameClient;
	Player _player;
	Mob _merchant;
	bool _trading;
	// The moment the remark last spoke, so an unchanged appraisal isn't retyped.
	string _lastMoment;

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

	// A held on a slot: where, for how long, and whether the hold already fired
	// (so its release isn't also a tap). A is the slot button's own press.
	Slot _sendPressed = Slot.None;
	float _sendHeld;
	bool _sendHoldFired;

	bool PickerOpen => _countPanel != null && _countPanel.IsOpen;

	public override void _Ready()
	{
		Visible = false;
		WirePanel(_stockGrid, ESide.Stock);
		WirePanel(_giveGrid, ESide.Give);
		WirePanel(_getGrid, ESide.Get);
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onGridSlotFocused += (grid, index, _) => OnSlotFocused(new Slot(ESide.Carried, index, grid));
			_inventoryPanel.onGridSlotButtonDown += (grid, index, _) => OnSendDown(new Slot(ESide.Carried, index, grid));
			_inventoryPanel.onGridSlotButtonUp += (grid, index, _) => OnSendUp(new Slot(ESide.Carried, index, grid));
			_inventoryPanel.onTabsFocused += () => OnSlotFocused(Slot.None);
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
		panel.onSlotButtonDown += (index, _) => OnSendDown(new Slot(side, index));
		panel.onSlotButtonUp += (index, _) => OnSendUp(new Slot(side, index));
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
		ResetHold();
		UpdateMerchantName();
		if (_getPanel != null)
		{
			_getPanel.Visible = _trading;
		}
		if (_merchantInventoryPanel != null)
		{
			_merchantInventoryPanel.Visible = _trading;
		}
		Visible = true;
		Say(trade ? ShopMoment.GreetTrade : ShopMoment.GreetGift);
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
		ResetHold();
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
		ItemSlotPanel start = _inventoryPanel?.FirstOccupied() ?? _inventoryPanel?.FirstSlot();
		start?.GrabFocus();
	}

	// ---- Sides -----------------------------------------------------------------

	IItemGrid Grid(Slot slot)
	{
		return slot.side switch
		{
			ESide.Carried => slot.grid,
			ESide.Give => _give,
			ESide.Stock => _stock,
			ESide.Get => _get,
			_ => null,
		};
	}

	ItemState ItemAt(Slot slot)
	{
		return Grid(slot)?.At(slot.index);
	}

	// Where Send moves a stack: across the trade, within its owner's side.
	IItemGrid SendTarget(ESide side, ItemState item)
	{
		return side switch
		{
			ESide.Carried => _give,
			ESide.Give => _player?.Inventory?.Backpack,
			ESide.Stock => _get,
			ESide.Get => _stock,
			_ => null,
		};
	}

	// ---- Focus -----------------------------------------------------------------

	void OnSlotFocused(Slot slot)
	{
		_focused = slot;
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

	// ---- A: send across the trade ----------------------------------------------

	void OnSendDown(Slot slot)
	{
		_sendPressed = slot;
		_sendHeld = 0f;
		_sendHoldFired = false;
	}

	void OnSendUp(Slot slot)
	{
		Slot pressed = _sendPressed;
		bool fired = _sendHoldFired;
		_sendPressed = Slot.None;
		_sendHoldFired = false;
		_hintSend?.SetProgress(0f);
		ItemState item = ItemAt(slot);
		// Dragging off the pressed slot before letting go reads as a cancel.
		if (fired || PickerOpen || !pressed.Is(slot) || item == null)
		{
			return;
		}
		Send(slot, item.stackCount);
	}

	void TickSendHold(float dt)
	{
		if (_sendPressed.IsNone || _sendHoldFired)
		{
			return;
		}
		ItemState item = ItemAt(_sendPressed);
		if (item == null)
		{
			return;
		}
		_sendHeld += dt;
		_hintSend?.SetProgress(Mathf.Clamp(_sendHeld / _holdSeconds, 0f, 1f));
		if (_sendHeld < _holdSeconds)
		{
			return;
		}
		_sendHoldFired = true;
		_hintSend?.SetProgress(0f);
		Slot slot = _sendPressed;
		if (item.stackCount <= 1)
		{
			Send(slot, item.stackCount);
			return;
		}
		_countPanel?.Open(item.stackCount, count => Send(slot, count), prompt: Loc.Get(Loc.Keys.stash_send_how_many));
	}

	// A sack goes over the counter empty, or not at all — its contents would be
	// traded away unvalued.
	bool RefuseFilledSack(ItemState item)
	{
		if (!SackState.IsFilled(item))
		{
			return false;
		}
		Say(ShopMoment.EmptySack);
		return true;
	}

	void Send(Slot slot, int count)
	{
		bool sent;
		if (SendTarget(slot.side, ItemAt(slot)) == _give && RefuseFilledSack(ItemAt(slot)))
		{
			sent = false;
		}
		else
		{
			sent = count > 0 && ItemTransfer.Send(Grid(slot), slot.index, count, SendTarget(slot.side, ItemAt(slot))) > 0;
		}
		if (sent)
		{
			RemarkOnOffer();
		}
		Refresh();
	}

	// The merchant appraises the deal as it now stands on the counter, not the
	// move that got it there. A line already being said is not restarted.
	void RemarkOnOffer()
	{
		string moment = AppraiseOffer();
		if (moment != null && moment != _lastMoment)
		{
			Say(moment);
		}
	}

	string AppraiseOffer()
	{
		if (_merchant == null || _give == null)
		{
			return null;
		}
		int giving = _give.OccupiedCount();
		int getting = _get?.OccupiedCount() ?? 0;
		if (giving == 0 && getting == 0)
		{
			return ShopMoment.TakeBack;
		}
		if (getting > 0)
		{
			if (giving == 0)
			{
				return ShopMoment.PickStock;
			}
			// The same comparison CommitTrade refuses on.
			float giveValue = _merchant.CalculatePersonalValue(Occupied(_give));
			float getValue = 0f;
			foreach (ItemState s in Occupied(_get))
			{
				getValue += _merchant.PerUnitValue(s.data) * s.stackCount;
			}
			if (getValue >= giveValue)
			{
				return ShopMoment.DealBad;
			}
			return giveValue >= getValue * _generousDealRatio ? ShopMoment.DealGenerous : ShopMoment.DealFair;
		}
		int offered = 0;
		int acceptable = 0;
		foreach (ItemState s in Occupied(_give))
		{
			offered += s.stackCount;
			acceptable += _merchant.AcceptableUnits(s.data, s.stackCount);
		}
		if (acceptable == 0)
		{
			return ShopMoment.OfferUnwanted;
		}
		if (acceptable < offered)
		{
			return ShopMoment.OfferPartial;
		}
		return _trading ? ShopMoment.OfferTrade : ShopMoment.OfferGift;
	}

	// ---- Input / tick ----------------------------------------------------------

	public override void _Process(double delta)
	{
		if (!Visible || _player == null || PickerOpen)
		{
			return;
		}
		TickSendHold((float)delta);
	}

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel") && !PickerOpen)
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}

	void ResetHold()
	{
		_sendPressed = Slot.None;
		_sendHeld = 0f;
		_sendHoldFired = false;
		_hintSend?.SetProgress(0f);
	}

	// ---- Merchant remark -------------------------------------------------------

	// The conversation's name once the party knows it, its description until
	// then; a merchant with no conversation goes by its species' name.
	void UpdateMerchantName()
	{
		if (_merchantNameTab == null || _merchantNameLabel == null)
		{
			return;
		}
		ConversationData conversation = _merchant?.SimState?.Conversation;
		string name;
		if (conversation != null)
		{
			StringName key = conversation.SpeakerLocKey(_gameClient?.Sim?.WorldState?.SimState);
			name = key != null ? Loc.Get(key) : null;
		}
		else
		{
			name = _merchant?.mobData?.displayName.ToString();
		}
		_merchantNameLabel.Text = name ?? string.Empty;
		_merchantNameTab.Visible = !string.IsNullOrEmpty(name);
	}

	// Types the merchant's line for a ShopMoment: their own sheet row, else
	// SimData.merchantDefaults'.
	void Say(string moment)
	{
		_lastMoment = moment;
		ConversationData defaults = _gameClient?.Sim?.SimData?.merchantDefaults;
		StringName key = ConversationData.ShopLineKey(_merchant?.SimState?.Conversation, defaults, moment);
		if (key == null)
		{
			GD.PushError($"MerchantScreen: no line for shop moment '{moment}' - add a 'shop:{moment}' row to the merchant_ character of the conversation sheet.");
			_merchantRemark?.Begin(null);
			return;
		}
		_merchantRemark?.Begin(LanguageText.RenderBbcode(Loc.Get(key), _merchant?.SpokenLanguage, _player));
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
			Say(ShopMoment.GiftNothingOffered);
			return;
		}
		if (_merchant == null)
		{
			return;
		}
		if (!_merchant.HasReciprocableGift(_player))
		{
			Say(ShopMoment.GiftNothingToReturn);
			return;
		}
		List<ItemState> accepted = TakeAcceptableFromGive(out bool anyLeftover, out float loyaltyGained);
		if (accepted.Count == 0)
		{
			Say(ShopMoment.GiftNoneAccepted);
			return;
		}
		List<LoyaltyGiftState> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		FinalizeMerchantInventoryAfterCommit(accepted);
		if (awarded.Count > 0)
		{
			Say(ShopMoment.GiftRewarded);
		}
		else if (anyLeftover)
		{
			Say(ShopMoment.GiftPartial);
		}
		else
		{
			Say(ShopMoment.GiftThanks);
		}
		Refresh();
	}

	void CommitTrade()
	{
		if (_give.OccupiedCount() == 0 && _get.OccupiedCount() == 0)
		{
			Say(ShopMoment.TradeNothing);
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
			Say(ShopMoment.TradeRefused);
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
			// Acquire, as a pickup does: gear whose equip slot is empty goes on.
			int added = _player?.Inventory?.Acquire(received) ?? 0;
			if (added < initial)
			{
				DropAtMerchant(received);
			}
		}
		float loyaltyGained = giveValue - getValue;
		List<LoyaltyGiftState> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		FinalizeMerchantInventoryAfterCommit(accepted);
		Say(awarded.Count > 0 ? ShopMoment.TradeDoneGift : ShopMoment.TradeDone);
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

	void ApplyAwardedGifts(List<LoyaltyGiftState> awarded)
	{
		if (awarded == null || awarded.Count == 0 || _player == null)
		{
			return;
		}
		GameClient gc = GameClient.Current;
		foreach (LoyaltyGiftState gift in awarded)
		{
			if (gift == null)
			{
				continue;
			}
			if (gift.item != null)
			{
				// Ejected like a chest's contents, not handed into the backpack: the
				// world pickup is where a scroll or potion does its real work.
				ItemState state = gift.item;
				ItemData given = state.data;
				if (_merchant != null)
				{
					_player.Sim?.EjectLoot(new List<ItemState> { state }, _merchant.GlobalPosition + Vector3.Up);
				}
				gc?.Announce(new Announcement
				{
					type = EAnnouncementType.GiftReceived,
					title = "Gift Received",
					subtitle = given.displayName.ToString(),
					icon = given.inventorySprite,
				});
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
		_itemInfoPanel?.SetItem(ItemAt(_focused));
		if (_tradeButton != null)
		{
			_tradeButton.Text = IsGiftCommit() ? "Gift" : "Trade";
		}
		UpdateHints();
	}

	void UpdateHints()
	{
		string label;
		if (_focused.IsNone)
		{
			// On the Trade / Gift button, A presses it.
			bool onButton = _tradeButton != null && _tradeButton.HasFocus();
			label = onButton ? _tradeButton.Text : null;
		}
		else
		{
			label = ItemAt(_focused) != null ? SendVerb(_focused.side) : null;
		}
		ShowHint(_hintSend, SendAction, label);
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
