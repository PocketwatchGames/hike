using Godot;
using System;
using System.Collections.Generic;
using Godot.Collections;

// Modal merchant + gifting screen. Two modes are driven by the `gifting`
// flag passed to Open:
//
//   Trade mode  — player and merchant each stage a side: player Offers items
//                 from their inventory into the Give panel and Requests items
//                 from the merchant's inventory into the Get panel; the Trade
//                 button commits the swap.
//
//   Gift mode   — getPanel and merchantInventoryPanel are hidden so only the
//                 Give panel is visible. The Give button hands the staged items
//                 to the NPC.
//
// Mobs have no inventory yet — the merchant-side stays empty in trade mode for
// now. The plumbing is symmetric so this can wire up to a real mob inventory
// without churn here.
//
// The primary action label tracks focus: Offer (player inventory), Request
// (merchant inventory), Remove (either staging panel). Mid-stack items
// transfer one unit on tap; holding opens DropCountPanel so the player can
// pick a larger count. Items the player can't fit after a trade scatter
// around the merchant via World.DropItem.
[GlobalClass]
public partial class MerchantScreen : Control
{
	[Export] private TextureRect _merchantPortrait;
	[Export] private Label _merchantNameLabel;
	[Export] private Label _merchantConversationLabel;
	[Export] private Control _merchantInventoryPanel;
	[Export] private Control _givePanel;
	[Export] private Control _getPanel;
	[Export] private Button _tradeButton;
	[Export] private InventoryPanel _playerInventory;
	[Export] private Array<ItemSlotPanel> _merchantInventorySlotPanels;
	[Export] private Array<ItemSlotPanel> _giveSlotPanels;
	[Export] private Array<ItemSlotPanel> _getSlotPanels;
	[Export] private DropCountPanel _dropCountPanel;
	[Export] private ItemInfoPanel _itemInfoPanelGive;
	[Export] private ItemInfoPanel _itemInfoPanelGet;

	Action _onClose;
	GameClient _gameClient;
	Player _player;
	Mob _merchant;
	bool trading;

	// Side-pile staging for the trade. Player inventory items move into
	// _giveItems; merchant inventory items move into _getItems. Both lists
	// hold fresh ItemStates whose stackCounts reflect just the moved units —
	// the originals in the player's inventory / merchant's inventory are
	// decremented in place.
	readonly List<ItemState> _giveItems = new();
	readonly List<ItemState> _getItems = new();
	// Merchant inventory mirror. Stays empty until mobs have inventory; left
	// in place so the request/remove paths work the day that lands.
	readonly List<ItemState> _merchantItems = new();

	enum EFocusedPanel { None, PlayerInventory, MerchantInventory, Give, Get }
	EFocusedPanel _focusedPanel = EFocusedPanel.None;
	ItemSlotPanel _focusedSlot;
	ItemState _focusedItem;
	int _focusedSlotIndex;

	// Hold-to-count timer for merchant / give / get slots. Player inventory
	// slots use InventoryPanel's own primary-hold path (onPrimaryHoldComplete).
	const float HoldSeconds = 0.5f;
	ItemSlotPanel _pressedSlot;
	float _holdTimer;
	bool _holdFired;

	public override void _Ready()
	{
		Visible = false;
		if (_playerInventory != null)
		{
			_playerInventory.onFocusedItemChanged += OnInventoryFocusChanged;
			_playerInventory.onPrimaryTap += OnInventoryPrimaryTap;
			_playerInventory.onPrimaryHoldComplete += OnInventoryPrimaryHold;
		}
		WireSlotPanels(_merchantInventorySlotPanels, EFocusedPanel.MerchantInventory);
		WireSlotPanels(_giveSlotPanels, EFocusedPanel.Give);
		WireSlotPanels(_getSlotPanels, EFocusedPanel.Get);
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_tradeButton != null)
		{
			_tradeButton.Pressed += OnTradeButtonPressed;
			// Clear info-panel focus when the trade/give button takes focus
			// (keyboard nav or mouse hover) so the side panels don't stay
			// stuck on the last hovered item while the player commits.
			_tradeButton.FocusEntered += OnTradeButtonFocused;
			_tradeButton.MouseEntered += OnTradeButtonMouseEntered;
		}
		_itemInfoPanelGive?.SetItem(null);
		_itemInfoPanelGet?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (_playerInventory != null)
		{
			_playerInventory.onFocusedItemChanged -= OnInventoryFocusChanged;
			_playerInventory.onPrimaryTap -= OnInventoryPrimaryTap;
			_playerInventory.onPrimaryHoldComplete -= OnInventoryPrimaryHold;
		}
		if (_tradeButton != null)
		{
			_tradeButton.Pressed -= OnTradeButtonPressed;
			_tradeButton.FocusEntered -= OnTradeButtonFocused;
			_tradeButton.MouseEntered -= OnTradeButtonMouseEntered;
		}
	}

	void OnTradeButtonFocused()
	{
		_focusedSlot = null;
		_focusedItem = null;
		_focusedPanel = EFocusedPanel.None;
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void OnTradeButtonMouseEntered()
	{
		_tradeButton?.GrabFocus();
	}

	void WireSlotPanels(Array<ItemSlotPanel> panels, EFocusedPanel category)
	{
		if (panels == null)
		{
			return;
		}
		for (int i = 0; i < panels.Count; i++)
		{
			ItemSlotPanel panel = panels[i];
			if (panel == null)
			{
				continue;
			}
			int index = i;
			panel.onFocusEntered += p => OnSlotFocused(p, category, index);
			panel.onButtonDown += p => OnSlotButtonDown(p);
			panel.onButtonUp += p => OnSlotButtonUp(p, category, index);
		}
	}

	public void Open(Player player, Mob merchant, bool trade = true, Action onClose = null)
	{
		_player = player;
		_merchant = merchant;
		trading = trade;
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
		// Drop the InteractHUD + highlight overlay that surrounded the NPC
		// before the screen opened — leaving them lit underneath the modal
		// reads as a layering bug.
		_player?.ClearInteractive();
		ClearStaging();
		UpdateMerchantInfo();
		ApplyModeVisibility();
		HidePlayerInventorySecondaryHints();
		RefreshAllSlots();
		SetConversation(trade ? "What have you brought me?" : "What would you like to trade?");
		Visible = true;
		_playerInventory?.Bind(_player);
		UpdateButtonHint();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		ReturnStagedItemsToInventory();
		CloseCountPicker();
		_playerInventory?.Unbind();
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
		_focusedSlot = null;
		_focusedItem = null;
		_focusedPanel = EFocusedPanel.None;
		_merchant = null;
		Action cb = _onClose;
		_onClose = null;
		_player = null;
		cb?.Invoke();
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

	public override void _Process(double delta)
	{
		if (!Visible)
		{
			return;
		}
		TickHold((float)delta);
	}

	// -------------------------------------------------------------------
	// Merchant header (portrait, name, conversation).
	// -------------------------------------------------------------------

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
		// Only swap to a real portrait if MobData has one; otherwise leave the
		// authored placeholder texture in the scene alone.
		if (_merchantPortrait != null && md?.bestiaryPortrait != null)
		{
			_merchantPortrait.Texture = md.bestiaryPortrait;
		}
	}

	void ApplyModeVisibility()
	{
		if (_getPanel != null)
		{
			_getPanel.Visible = trading;
		}
		if (_merchantInventoryPanel != null)
		{
			_merchantInventoryPanel.Visible = trading;
		}
		// Button label is dynamic — UpdateTradeButtonLabel runs after every
		// staging change (via RefreshAllSlots) and on Open's first refresh.
	}

	// Flips between Gift and Trade based purely on what's staged. Gift wins
	// only when the player has offered items AND requested nothing back; any
	// items on the get side promote the button to Trade. The rule is mode-
	// agnostic — in gift-only mode the get panel is hidden so the get list
	// stays empty, which naturally lands on Gift the moment the player
	// stages anything.
	void UpdateTradeButtonLabel()
	{
		if (_tradeButton == null)
		{
			return;
		}
		_tradeButton.Text = IsGiftCommit() ? "Gift" : "Trade";
	}

	bool IsGiftCommit()
	{
		return _giveItems.Count > 0 && _getItems.Count == 0;
	}

	// Conversation text is authored in plain English; the merchant "speaks"
	// in their native language, so anything the player hasn't yet learned in
	// that language gets scrambled by TextScrambler. Mirrors the lookup that
	// ConversationController uses for dialogue branches so both surfaces
	// agree on what's intelligible.
	void SetConversation(string text)
	{
		if (_merchantConversationLabel == null)
		{
			return;
		}
		_merchantConversationLabel.Text = LocalizeMerchantSpeech(text);
	}

	string LocalizeMerchantSpeech(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return text ?? string.Empty;
		}
		LanguageData lang = _merchant?.SpokenLanguage;
		if (lang == null || _player == null)
		{
			return text;
		}
		ELanguageComponents missing = ELanguageComponents.All & ~_player.GetLearnedComponents(lang);
		if (missing == ELanguageComponents.None)
		{
			return text;
		}
		return TextScrambler.Scramble(text, lang, missing);
	}

	// Drop / Use are meaningless on this screen — the inventory panel
	// keeps its primary hint (we drive its label below) but the other two
	// stay hidden so the player isn't prompted with stale verbs.
	void HidePlayerInventorySecondaryHints()
	{
		if (_playerInventory == null)
		{
			return;
		}
		if (_playerInventory.ButtonHintSecondary != null)
		{
			_playerInventory.ButtonHintSecondary.Visible = false;
		}
		if (_playerInventory.ButtonHintTertiary != null)
		{
			_playerInventory.ButtonHintTertiary.Visible = false;
		}
	}

	// -------------------------------------------------------------------
	// Focus tracking — drives info panels + button hint label.
	// -------------------------------------------------------------------

	void OnInventoryFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		CancelHoldTimer();
		_focusedSlot = panel;
		_focusedPanel = EFocusedPanel.PlayerInventory;
		_focusedSlotIndex = -1;
		_focusedItem = item;
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void OnSlotFocused(ItemSlotPanel panel, EFocusedPanel category, int index)
	{
		CancelHoldTimer();
		_focusedSlot = panel;
		_focusedPanel = category;
		_focusedSlotIndex = index;
		_focusedItem = panel?.Item;
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void UpdateInfoPanels()
	{
		bool getSide = _focusedPanel == EFocusedPanel.MerchantInventory || _focusedPanel == EFocusedPanel.Get;
		bool giveSide = _focusedPanel == EFocusedPanel.PlayerInventory || _focusedPanel == EFocusedPanel.Give;
		_itemInfoPanelGet?.SetItem(getSide ? _focusedItem : null);
		_itemInfoPanelGive?.SetItem(giveSide ? _focusedItem : null);
	}

	void UpdateButtonHint()
	{
		ButtonHint hint = _playerInventory?.ButtonHintPrimary;
		if (hint == null)
		{
			return;
		}
		string label;
		bool visible = _focusedItem != null;
		switch (_focusedPanel)
		{
			case EFocusedPanel.PlayerInventory:
				label = "Offer";
				break;
			case EFocusedPanel.MerchantInventory:
				label = "Request";
				break;
			case EFocusedPanel.Give:
			case EFocusedPanel.Get:
				label = "Remove";
				break;
			default:
				label = string.Empty;
				visible = false;
				break;
		}
		hint.ActionName = label;
		hint.Visible = visible;
		hint.SetProgress(0f);
	}

	// -------------------------------------------------------------------
	// Slot press handling for merchant / give / get panels.
	// -------------------------------------------------------------------

	void OnSlotButtonDown(ItemSlotPanel panel)
	{
		_pressedSlot = panel;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
	}

	void OnSlotButtonUp(ItemSlotPanel panel, EFocusedPanel category, int index)
	{
		bool fired = _holdFired;
		ItemSlotPanel pressed = _pressedSlot;
		_pressedSlot = null;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
		if (fired)
		{
			return;
		}
		if (pressed != null && pressed != panel)
		{
			return;
		}
		ItemState item = panel?.Item;
		if (item == null)
		{
			return;
		}
		TransferOne(category, index, item, 1);
	}

	void TickHold(float dt)
	{
		if (_pressedSlot == null || _holdFired)
		{
			return;
		}
		ItemState item = _pressedSlot.Item;
		if (item == null || item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			return;
		}
		_holdTimer += dt;
		float progress = Mathf.Clamp(_holdTimer / HoldSeconds, 0f, 1f);
		_playerInventory?.ButtonHintPrimary?.SetProgress(progress);
		if (_holdTimer >= HoldSeconds)
		{
			_holdFired = true;
			_holdTimer = 0f;
			_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
			OpenCountPicker(_focusedPanel, _focusedSlotIndex, item);
		}
	}

	void CancelHoldTimer()
	{
		_pressedSlot = null;
		_holdTimer = 0f;
		_holdFired = false;
		_playerInventory?.ButtonHintPrimary?.SetProgress(0f);
	}

	// -------------------------------------------------------------------
	// Player-inventory verb wiring (Offer one / Offer many).
	// -------------------------------------------------------------------

	void OnInventoryPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		TransferOne(EFocusedPanel.PlayerInventory, -1, item, 1);
	}

	void OnInventoryPrimaryHold(ItemSlotPanel panel, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		if (item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			TransferOne(EFocusedPanel.PlayerInventory, -1, item, 1);
			return;
		}
		OpenCountPicker(EFocusedPanel.PlayerInventory, -1, item);
	}

	// -------------------------------------------------------------------
	// Count picker plumbing — shared between inventory and merchant sides.
	// -------------------------------------------------------------------

	void OpenCountPicker(EFocusedPanel category, int index, ItemState item)
	{
		if (_dropCountPanel == null || item == null)
		{
			return;
		}
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => { TransferOne(category, index, item, count); CloseCountPicker(); },
			onCancel: CloseCountPicker,
			prompt: BuildHoldPrompt(category));
	}

	static string BuildHoldPrompt(EFocusedPanel category)
	{
		switch (category)
		{
			case EFocusedPanel.PlayerInventory: return "Offer how many?";
			case EFocusedPanel.MerchantInventory: return "Request how many?";
			case EFocusedPanel.Give:
			case EFocusedPanel.Get:
				return "Remove how many?";
			default: return "How many?";
		}
	}

	void CloseCountPicker()
	{
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_playerInventory != null)
		{
			_playerInventory.HoldLocked = false;
			_playerInventory.SetSlotsFocusable(true);
		}
		SetMerchantSlotsFocusable(true);
		if (_focusedSlot != null)
		{
			_focusedSlot.GrabFocus();
		}
		else
		{
			_playerInventory?.RestoreFocus();
		}
	}

	void LockSlotsFocus()
	{
		if (_playerInventory != null)
		{
			_playerInventory.SetSlotsFocusable(false);
			_playerInventory.HoldLocked = true;
		}
		SetMerchantSlotsFocusable(false);
	}

	void SetMerchantSlotsFocusable(bool focusable)
	{
		ApplyFocusable(_merchantInventorySlotPanels, focusable);
		ApplyFocusable(_giveSlotPanels, focusable);
		ApplyFocusable(_getSlotPanels, focusable);
	}

	static void ApplyFocusable(Array<ItemSlotPanel> panels, bool focusable)
	{
		if (panels == null)
		{
			return;
		}
		foreach (ItemSlotPanel panel in panels)
		{
			panel?.SetFocusable(focusable);
		}
	}

	// -------------------------------------------------------------------
	// Transfer logic — one entry point per focus category.
	// -------------------------------------------------------------------

	void TransferOne(EFocusedPanel from, int slotIndex, ItemState item, int amount)
	{
		if (item == null || amount <= 0)
		{
			return;
		}
		amount = Mathf.Min(amount, item.stackCount);
		switch (from)
		{
			case EFocusedPanel.PlayerInventory:
				MoveInventoryToGive(item, amount);
				break;
			case EFocusedPanel.MerchantInventory:
				MoveMerchantToGet(slotIndex, item, amount);
				break;
			case EFocusedPanel.Give:
				MoveGiveToInventory(slotIndex, amount);
				break;
			case EFocusedPanel.Get:
				MoveGetToMerchant(slotIndex, amount);
				break;
		}
		RefreshAllSlots();
	}

	void MoveInventoryToGive(ItemState item, int amount)
	{
		if (_player?.Inventory == null || _giveSlotPanels == null || item.data == null)
		{
			return;
		}
		int placed = AddToStagingList(_giveItems, item.data, amount, _giveSlotPanels.Count);
		if (placed <= 0)
		{
			return;
		}
		item.stackCount -= placed;
		if (item.stackCount <= 0)
		{
			_player.Inventory.Remove(item);
		}
		else
		{
			_player.Inventory.NotifyChanged();
		}
		SetConversation(!trading ? "Mmm, let me see..." : "Yes... and what would you like in return?");
	}

	void MoveMerchantToGet(int slotIndex, ItemState item, int amount)
	{
		if (_getSlotPanels == null || slotIndex < 0 || slotIndex >= _merchantItems.Count || item.data == null)
		{
			return;
		}
		int placed = AddToStagingList(_getItems, item.data, amount, _getSlotPanels.Count);
		if (placed <= 0)
		{
			return;
		}
		item.stackCount -= placed;
		if (item.stackCount <= 0)
		{
			_merchantItems.RemoveAt(slotIndex);
		}
		SetConversation("That'll cost you.");
	}

	void MoveGiveToInventory(int slotIndex, int amount)
	{
		if (slotIndex < 0 || slotIndex >= _giveItems.Count || _player?.Inventory == null)
		{
			return;
		}
		ItemState item = _giveItems[slotIndex];
		if (item?.data == null)
		{
			return;
		}
		int requested = Mathf.Min(amount, item.stackCount);
		ItemState toReturn = item.data.CreateState();
		toReturn.stackCount = requested;
		int added = _player.Inventory.TryAdd(toReturn);
		if (added <= 0)
		{
			return;
		}
		item.stackCount -= added;
		if (item.stackCount <= 0)
		{
			_giveItems.RemoveAt(slotIndex);
		}
		SetConversation("Changed your mind?");
	}

	void MoveGetToMerchant(int slotIndex, int amount)
	{
		if (slotIndex < 0 || slotIndex >= _getItems.Count)
		{
			return;
		}
		ItemState item = _getItems[slotIndex];
		if (item?.data == null)
		{
			return;
		}
		int requested = Mathf.Min(amount, item.stackCount);
		AddToStagingList(_merchantItems, item.data, requested, _merchantInventorySlotPanels?.Count ?? 0);
		item.stackCount -= requested;
		if (item.stackCount <= 0)
		{
			_getItems.RemoveAt(slotIndex);
		}
		SetConversation("Not what you wanted?");
	}

	// Merge `amount` units of `data` into the destination list. Stackable
	// items fill any existing same-kind entry first; the rest spills into a
	// fresh entry as long as `slotCap` has room. Returns the number of units
	// actually placed.
	static int AddToStagingList(List<ItemState> list, ItemData data, int amount, int slotCap)
	{
		if (list == null || data == null || amount <= 0)
		{
			return 0;
		}
		int initial = amount;
		if (data.IsStackable)
		{
			foreach (ItemState existing in list)
			{
				if (existing.data != data)
				{
					continue;
				}
				int space = existing.RemainingStackSpace();
				if (space <= 0)
				{
					continue;
				}
				int moved = Mathf.Min(space, amount);
				existing.stackCount += moved;
				amount -= moved;
				if (amount <= 0)
				{
					break;
				}
			}
		}
		if (amount > 0 && list.Count < slotCap)
		{
			ItemState fresh = data.CreateState();
			fresh.stackCount = amount;
			list.Add(fresh);
			amount = 0;
		}
		return initial - amount;
	}

	// -------------------------------------------------------------------
	// Trade / Give button.
	// -------------------------------------------------------------------

	void OnTradeButtonPressed()
	{
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
		if (_giveItems.Count == 0)
		{
			SetConversation("You haven't offered anything.");
			return;
		}
		if (_merchant == null)
		{
			return;
		}
		// Rejection cases leave the staged items in the give panel so the
		// player can adjust the offering — they only clear when something
		// actually gets accepted.
		if (!_merchant.HasReciprocableGift(_player))
		{
			SetConversation("I have nothing of value to give you in return.");
			return;
		}
		List<ItemState> accepted = ExtractAcceptableFromGive(out bool anyLeftover, out float loyaltyGained);
		if (accepted.Count == 0)
		{
			SetConversation("I cannot accept any of these.");
			return;
		}
		List<LoyaltyGift> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
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
		RefreshAllSlots();
	}

	void CommitTrade()
	{
		if (_giveItems.Count == 0 && _getItems.Count == 0)
		{
			SetConversation("Nothing to trade.");
			return;
		}
		if (_merchant == null)
		{
			return;
		}
		// Cap-aware: items past the 3-of-a-kind threshold count as zero
		// value, so a deal that hinges on capped units silently shrinks
		// here. Get-side is the mob's outgoing inventory — the cap (which
		// tracks the mob's incoming history) does NOT apply, so sum
		// PerUnitValue * stackCount directly.
		float giveValue = _merchant.CalculatePersonalValue(_giveItems);
		float getValue = 0f;
		foreach (ItemState s in _getItems)
		{
			if (s?.data != null)
			{
				getValue += _merchant.PerUnitValue(s.data) * s.stackCount;
			}
		}
		// Equal-value trades are refused — there's no upside for the mob,
		// and the player can still gift one side outright if they just
		// want to be generous. giveValue<=0 (no items the mob values)
		// also fails the inequality, so the early-exit covers that too.
		if (getValue >= giveValue)
		{
			SetConversation("That trade isn't worth my while.");
			return;
		}
		List<ItemState> accepted = ExtractAcceptableFromGive(out _, out _);
		// Merchant's side returns to the player; anything that won't fit
		// scatters around the merchant via World.DropItem.
		for (int i = 0; i < _getItems.Count; i++)
		{
			ItemState received = _getItems[i];
			if (received?.data == null)
			{
				continue;
			}
			int initial = received.stackCount;
			int added = _player?.Inventory?.TryAdd(received) ?? 0;
			if (added < initial)
			{
				ItemState overflow = received.data.CreateState();
				overflow.stackCount = initial - added;
				DropAtMerchant(overflow);
			}
		}
		_getItems.Clear();
		float loyaltyGained = giveValue - getValue;
		List<LoyaltyGift> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		if (awarded.Count > 0)
		{
			SetConversation("Pleasure doing business — and please, take this as well.");
		}
		else
		{
			SetConversation("Pleasure doing business.");
		}
		RefreshAllSlots();
	}

	// Per-stack partition of _giveItems. For each staged stack, asks the
	// merchant how many units it'll accept; pulls those units out into a
	// fresh ItemState (so AcceptGift's gift-count tally sees only the
	// valued units), and leaves any leftover units in place in the give
	// panel. Stacks the mob refuses entirely stay where they were —
	// nothing returns to the player's inventory here, so a player who
	// stages a worthless offering can simply edit the panel and retry.
	// Walks the list in reverse so RemoveAt is safe.
	List<ItemState> ExtractAcceptableFromGive(out bool anyLeftover, out float loyaltyGained)
	{
		anyLeftover = false;
		loyaltyGained = 0f;
		List<ItemState> accepted = new();
		if (_merchant == null)
		{
			return accepted;
		}
		for (int i = _giveItems.Count - 1; i >= 0; i--)
		{
			ItemState stack = _giveItems[i];
			if (stack?.data == null)
			{
				continue;
			}
			int units = _merchant.AcceptableUnits(stack.data, stack.stackCount);
			if (units <= 0)
			{
				anyLeftover = true;
				continue;
			}
			ItemState split = stack.data.CreateState();
			split.stackCount = units;
			accepted.Add(split);
			loyaltyGained += _merchant.PerUnitValue(stack.data) * units;
			if (units >= stack.stackCount)
			{
				_giveItems.RemoveAt(i);
			}
			else
			{
				stack.stackCount -= units;
				anyLeftover = true;
			}
		}
		return accepted;
	}

	// Player-side application of every gift the mob handed back in response
	// to a successful gift / favorable trade. Item gifts route into the
	// inventory (overflow scatters at the merchant); language gifts route
	// through Player.LearnLanguageComponents, which fires its own
	// LanguageLearned announcement, so we only emit the GiftReceived
	// announcement for the item path.
	void ApplyAwardedGifts(List<LoyaltyGift> awarded)
	{
		if (awarded == null || awarded.Count == 0 || _player == null)
		{
			return;
		}
		GameClient gc = GameClient.Current;
		foreach (LoyaltyGift gift in awarded)
		{
			if (gift == null) { continue; }
			if (gift.item != null)
			{
				ItemState state = gift.item.CreateState();
				state.stackCount = Mathf.Max(1, gift.count);
				int initial = state.stackCount;
				int added = _player.Inventory?.TryAdd(state) ?? 0;
				if (added < initial)
				{
					ItemState overflow = gift.item.CreateState();
					overflow.stackCount = initial - added;
					DropAtMerchant(overflow);
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
		if (item == null || _merchant == null || _player?.World == null)
		{
			return;
		}
		Vector3 basePos = _merchant.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 offset = new Vector3((GD.Randf() - 0.5f) * 0.6f, 0f, (GD.Randf() - 0.5f) * 0.6f);
		Vector3 impulse = new Vector3((GD.Randf() - 0.5f) * 2f, 1.5f, (GD.Randf() - 0.5f) * 2f);
		_player.World.DropItem(item, basePos + offset, impulse, requireInteract: true);
	}

	// -------------------------------------------------------------------
	// Open / Close cleanup.
	// -------------------------------------------------------------------

	void ClearStaging()
	{
		_giveItems.Clear();
		_getItems.Clear();
		_merchantItems.Clear();
	}

	// Anything still in the player's give pile on close goes back to their
	// inventory (overflow at their feet). Get-pile items get re-merged into
	// _merchantItems for symmetry; with no real mob inventory they just
	// vanish when the list clears next.
	void ReturnStagedItemsToInventory()
	{
		Inventory inv = _player?.Inventory;
		if (inv == null)
		{
			ClearStaging();
			return;
		}
		for (int i = 0; i < _giveItems.Count; i++)
		{
			ItemState staged = _giveItems[i];
			if (staged?.data == null || staged.stackCount <= 0)
			{
				continue;
			}
			int initial = staged.stackCount;
			int added = inv.TryAdd(staged);
			if (added < initial)
			{
				ItemState overflow = staged.data.CreateState();
				overflow.stackCount = initial - added;
				_player.World?.DropItem(
					overflow,
					_player.GlobalPosition + Vector3.Up * 0.5f,
					Vector3.Up * 1.5f,
					requireInteract: true);
			}
		}
		ClearStaging();
	}

	// -------------------------------------------------------------------
	// Refresh slot displays from staging lists.
	// -------------------------------------------------------------------

	void RefreshAllSlots()
	{
		RefreshSlotList(_giveSlotPanels, _giveItems);
		RefreshSlotList(_getSlotPanels, _getItems);
		RefreshSlotList(_merchantInventorySlotPanels, _merchantItems);
		// Focused slot may now hold a different item (or none) after a
		// transfer shifted the list under the focus index. Re-sync the
		// info panels and button hint so the visual state stays in step.
		if (_focusedSlot != null)
		{
			_focusedItem = _focusedSlot.Item;
		}
		UpdateInfoPanels();
		UpdateButtonHint();
		UpdateTradeButtonLabel();
	}

	static void RefreshSlotList(Array<ItemSlotPanel> panels, List<ItemState> items)
	{
		if (panels == null)
		{
			return;
		}
		for (int i = 0; i < panels.Count; i++)
		{
			panels[i]?.SetItem(i < items.Count ? items[i] : null);
		}
	}
}
