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
// Interaction model: ui_accept on the player inventory or a give slot enters
// select-mode (ghost follows focus, auto-target the opposite side); a second
// ui_accept commits the move; ui_cancel aborts. Slots on the merchant side
// (merchant inventory / get panel) bypass select-mode: ui_accept there moves
// instantly because there's no per-position choice on the merchant pile.
// Hold ui_accept opens the count picker for partial-stack moves on the same
// destinations.
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
	// Snapshot of the merchant's shop side for this session. Populated at
	// Open from _merchant.Inventory (skipping secret entries). Mutated freely
	// during the session; written back to the durable mob inventory only on a
	// successful trade commit, so a cancel leaves the mob's actual stock
	// untouched.
	readonly List<ItemState> _merchantItems = new();
	readonly System.Collections.Generic.Dictionary<ItemData, MobInventoryItem> _merchantSourceByData = new();

	enum EFocusedPanel { None, PlayerInventory, MerchantInventory, Give, Get }
	EFocusedPanel _focusedPanel = EFocusedPanel.None;
	ItemSlotPanel _focusedSlot;
	ItemState _focusedItem;
	int _focusedSlotIndex;

	// Select-mode state mirrors InventoryScreen. _selectedSource is the panel
	// the player picked up from; _selectedItem is the ItemState; _selectedAmount
	// is how many units (full stack on tap, chosen count on hold). The source
	// can only ever be a player-inventory slot or a give slot — merchant-side
	// slots use instant-move and never enter select mode.
	ItemSlotPanel _selectedSource;
	EFocusedPanel _selectedSourceCategory;
	int _selectedSourceIndex;
	ItemState _selectedItem;
	int _selectedAmount;
	bool InSelectMode => _selectedItem != null;

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
			_playerInventory.onSecondaryTap += OnInventorySecondaryTap;
			_playerInventory.onSecondaryHoldComplete += OnInventorySecondaryHoldComplete;
			_playerInventory.onTertiaryPressed += OnInventoryTertiaryPressed;
			_playerInventory.onTertiaryReleased += OnInventoryTertiaryReleased;
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
			_playerInventory.onSecondaryTap -= OnInventorySecondaryTap;
			_playerInventory.onSecondaryHoldComplete -= OnInventorySecondaryHoldComplete;
			_playerInventory.onTertiaryPressed -= OnInventoryTertiaryPressed;
			_playerInventory.onTertiaryReleased -= OnInventoryTertiaryReleased;
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
		_player?.ClearInteractive();
		ClearStaging();
		ClearSelection();
		PopulateMerchantSnapshot();
		UpdateMerchantInfo();
		ApplyModeVisibility();
		if (_playerInventory != null)
		{
			_playerInventory.ButtonHintSecondary?.SetHint(_playerInventory.SecondaryAction, "Drop");
			_playerInventory.ButtonHintTertiary?.SetHint(_playerInventory.TertiaryAction, "Use");
		}
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
		ClearSelection();
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
			// First ui_cancel cancels a pending selection (if any); a clean
			// state closes the screen.
			if (InSelectMode)
			{
				CancelSelect();
			}
			else
			{
				Close();
			}
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
		TickTertiaryCharge();
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
	}

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
		RefreshGhostOnFocus();
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
		RefreshGhostOnFocus();
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	void UpdateInfoPanels()
	{
		if (InSelectMode)
		{
			// In select mode the info panels track the selected item, but on
			// the side that matches its source — so the player can keep their
			// eye on what they're moving even as the cursor wanders.
			bool sourceIsGiveSide = _selectedSourceCategory == EFocusedPanel.PlayerInventory
				|| _selectedSourceCategory == EFocusedPanel.Give;
			_itemInfoPanelGive?.SetItem(sourceIsGiveSide ? _selectedItem : null);
			_itemInfoPanelGet?.SetItem(sourceIsGiveSide ? null : _selectedItem);
			return;
		}
		bool getSide = _focusedPanel == EFocusedPanel.MerchantInventory || _focusedPanel == EFocusedPanel.Get;
		bool giveSide = _focusedPanel == EFocusedPanel.PlayerInventory || _focusedPanel == EFocusedPanel.Give;
		_itemInfoPanelGet?.SetItem(getSide ? _focusedItem : null);
		_itemInfoPanelGive?.SetItem(giveSide ? _focusedItem : null);
	}

	void UpdateButtonHint()
	{
		ButtonHint primary = _playerInventory?.ButtonHintPrimary;
		ButtonHint drop = _playerInventory?.ButtonHintSecondary;
		ButtonHint use = _playerInventory?.ButtonHintTertiary;
		string primaryLabel = string.Empty;
		bool primaryVisible = _focusedItem != null || (InSelectMode && _focusedSlot != null);
		bool dropVisible = false;
		bool useVisible = false;
		if (InSelectMode)
		{
			// Drop / Use are hidden mid-selection — committing the move
			// resolves the item's fate, no other verb makes sense.
			primaryLabel = ResolveDestinationLabel();
			primaryVisible = !string.IsNullOrEmpty(primaryLabel);
		}
		else
		{
			switch (_focusedPanel)
			{
				case EFocusedPanel.PlayerInventory:
					primaryLabel = "Select";
					// Drop / Use only on the player-inventory side. Items staged
					// in the give pile are conceptually offered to the merchant
					// already; consuming or dropping them mid-trade muddles the
					// negotiation, so we restrict the verbs to items the player
					// still firmly owns.
					dropVisible = _focusedItem != null;
					useVisible = _focusedItem != null && CanUseItem(_focusedItem);
					break;
				case EFocusedPanel.MerchantInventory: primaryLabel = "Request"; break;
				case EFocusedPanel.Give: primaryLabel = "Select"; break;
				case EFocusedPanel.Get: primaryLabel = "Return"; break;
				default:
					primaryLabel = string.Empty;
					primaryVisible = false;
					break;
			}
		}
		if (primary != null)
		{
			primary.ActionName = primaryLabel;
			primary.Visible = primaryVisible;
			primary.SetProgress(0f);
		}
		if (drop != null)
		{
			drop.Visible = dropVisible;
			if (!dropVisible) { drop.SetProgress(0f); }
		}
		if (use != null)
		{
			use.Visible = useVisible;
			if (!useVisible) { use.SetProgress(0f); }
		}
	}

	static bool CanUseItem(ItemState item)
	{
		return item is ConsumableState consumable && consumable.data?.actionProfile != null;
	}

	// What ui_accept will do on the currently-focused slot during select mode.
	// Empty string = no valid move (hint hidden). Drop onto source labels as
	// "Cancel" so the user knows they can pick another destination by moving
	// the cursor first. Cross-side moves (inv↔give) show "Move"; same-side
	// player-inventory moves piggyback on the inventory screen's verbs
	// (Equip / Unequip / Move) so the player can rearrange equipment without
	// having to close the merchant screen.
	string ResolveDestinationLabel()
	{
		if (_focusedSlot == null) { return string.Empty; }
		if (_focusedSlot == _selectedSource) { return "Cancel"; }
		if (_selectedSourceCategory == EFocusedPanel.PlayerInventory
			&& _focusedPanel == EFocusedPanel.PlayerInventory)
		{
			return ResolveInventoryMoveLabel(_focusedSlot);
		}
		return IsValidSelectDestination(_focusedSlot, _focusedPanel) ? "Move" : string.Empty;
	}

	// Mirrors InventoryScreen's destination resolver for player-inventory ↔
	// player-inventory moves. Returns the verb the commit would perform, or
	// empty if no valid operation exists.
	string ResolveInventoryMoveLabel(ItemSlotPanel dest)
	{
		if (_playerInventory == null || _selectedItem == null) { return string.Empty; }
		bool sourceBackpack = _playerInventory.IsBackpackPanel(_selectedSource);
		bool destBackpack = _playerInventory.IsBackpackPanel(dest);
		EInventorySlot destEquip = _playerInventory.GetEquipSlotKind(dest);
		EInventorySlot sourceEquip = _playerInventory.GetEquipSlotKind(_selectedSource);
		if (sourceBackpack && destBackpack) { return "Move"; }
		if (sourceBackpack)
		{
			return InventoryScreen.EquipCompatible(destEquip, _selectedItem) ? "Equip" : string.Empty;
		}
		if (destBackpack) { return "Unequip"; }
		if (sourceEquip == EInventorySlot.Equipment && destEquip == EInventorySlot.Equipment) { return "Move"; }
		if (InventoryScreen.CanSwapEquipSlots(sourceEquip, destEquip, _selectedItem, _player?.Inventory)) { return "Move"; }
		return string.Empty;
	}

	void RefreshGhostOnFocus()
	{
		ClearAllGhosts();
		if (!InSelectMode) { return; }
		// ClearAllGhosts wipes both ghost AND dim, so the source loses its
		// dimmed-out indicator on every focus change. Re-apply it here so the
		// player keeps seeing where they picked the item up from until they
		// commit or cancel.
		_selectedSource?.SetDimmed(true);
		if (_focusedSlot != null && IsValidSelectDestination(_focusedSlot, _focusedPanel))
		{
			_focusedSlot.SetGhost(_selectedItem);
		}
	}

	bool IsValidSelectDestination(ItemSlotPanel panel, EFocusedPanel category)
	{
		if (panel == _selectedSource) { return true; }
		// Cross-trade moves: player inventory ↔ give panel.
		if ((_selectedSourceCategory == EFocusedPanel.PlayerInventory && category == EFocusedPanel.Give)
			|| (_selectedSourceCategory == EFocusedPanel.Give && category == EFocusedPanel.PlayerInventory))
		{
			return true;
		}
		// Same-side player-inventory rearrangement (equip / unequip / swap /
		// hotbar reorder). Only consider it valid when the move would
		// actually do something — otherwise we'd paint a ghost on a slot
		// where ui_accept is a no-op.
		if (_selectedSourceCategory == EFocusedPanel.PlayerInventory
			&& category == EFocusedPanel.PlayerInventory)
		{
			return !string.IsNullOrEmpty(ResolveInventoryMoveLabel(panel));
		}
		return false;
	}

	void ClearAllGhosts()
	{
		_playerInventory?.ClearSelectVisuals();
		ClearGhosts(_giveSlotPanels);
		ClearGhosts(_getSlotPanels);
		ClearGhosts(_merchantInventorySlotPanels);
	}

	static void ClearGhosts(Array<ItemSlotPanel> panels)
	{
		if (panels == null) { return; }
		foreach (ItemSlotPanel p in panels)
		{
			p?.SetGhost(null);
			p?.SetDimmed(false);
		}
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
		HandleSlotTap(panel, category, index);
	}

	void HandleSlotTap(ItemSlotPanel panel, EFocusedPanel category, int index)
	{
		if (InSelectMode)
		{
			CommitMove(panel, category, index);
			return;
		}
		ItemState item = panel?.Item;
		if (item == null)
		{
			return;
		}
		switch (category)
		{
			case EFocusedPanel.MerchantInventory:
				// Instant request: move one unit from the merchant snapshot to
				// the get pile. No select mode — the merchant side is just one
				// pile with no per-slot identity to choose between.
				MoveMerchantToGet(index, item, 1);
				RefreshAllSlots();
				break;
			case EFocusedPanel.Get:
				// Instant return: undo a previously-requested unit.
				MoveGetToMerchant(index, 1);
				RefreshAllSlots();
				break;
			case EFocusedPanel.Give:
				EnterSelectMode(panel, item, item.stackCount, category, index);
				break;
		}
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
			HandleHoldComplete(_pressedSlot, _focusedPanel, _focusedSlotIndex, item);
		}
	}

	void HandleHoldComplete(ItemSlotPanel panel, EFocusedPanel category, int index, ItemState item)
	{
		if (InSelectMode)
		{
			// Hold inside select mode commits the move (same as tap) so the
			// user doesn't get stuck after a held release.
			CommitMove(panel, category, index);
			return;
		}
		switch (category)
		{
			case EFocusedPanel.MerchantInventory:
				OpenInstantMoveCountPicker(item, count =>
				{
					MoveMerchantToGet(index, item, count);
					RefreshAllSlots();
				}, prompt: "Request how many?");
				break;
			case EFocusedPanel.Get:
				OpenInstantMoveCountPicker(item, count =>
				{
					MoveGetToMerchant(index, count);
					RefreshAllSlots();
				}, prompt: "Return how many?");
				break;
			case EFocusedPanel.Give:
				OpenSelectCountPicker(panel, item, category, index);
				break;
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
	// Player-inventory verb wiring (Select / hold-Select).
	// -------------------------------------------------------------------

	void OnInventoryPrimaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.PlayerInventory, -1);
			return;
		}
		if (item == null) { return; }
		EnterSelectMode(panel, item, item.stackCount, EFocusedPanel.PlayerInventory, -1);
	}

	void OnInventoryPrimaryHold(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode)
		{
			CommitMove(panel, EFocusedPanel.PlayerInventory, -1);
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		if (item == null)
		{
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		if (item.data == null || !item.data.IsStackable || item.stackCount <= 1)
		{
			EnterSelectMode(panel, item, item.stackCount, EFocusedPanel.PlayerInventory, -1);
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		OpenSelectCountPicker(panel, item, EFocusedPanel.PlayerInventory, -1);
	}

	// -------------------------------------------------------------------
	// Select mode entry / cancel / commit.
	// -------------------------------------------------------------------

	void EnterSelectMode(ItemSlotPanel sourcePanel, ItemState item, int amount, EFocusedPanel category, int index)
	{
		_selectedSource = sourcePanel;
		_selectedSourceCategory = category;
		_selectedSourceIndex = index;
		_selectedItem = item;
		_selectedAmount = Mathf.Max(1, amount);
		sourcePanel?.SetDimmed(true);
		ItemSlotPanel autoTarget = FindAutoTargetForSelect(category);
		if (autoTarget != null && autoTarget != sourcePanel)
		{
			autoTarget.GrabFocus();
		}
		else
		{
			RefreshGhostOnFocus();
		}
		UpdateInfoPanels();
		UpdateButtonHint();
	}

	// Auto-target the first empty slot on the opposite side of the trade —
	// give panel for player-inventory sources, player backpack for give-panel
	// sources. Falls back to the first slot on the opposite side if all are
	// full so the cursor still lands somewhere predictable.
	ItemSlotPanel FindAutoTargetForSelect(EFocusedPanel sourceCategory)
	{
		if (sourceCategory == EFocusedPanel.PlayerInventory)
		{
			return FindFirstEmptySlot(_giveSlotPanels) ?? FirstOf(_giveSlotPanels);
		}
		if (sourceCategory == EFocusedPanel.Give)
		{
			// Returning a staged offer goes back to the player's hands — first
			// backpack panel is the predictable landing zone, matching
			// InventoryScreen's equip-slot-source convention.
			return _playerInventory?.GetFirstBackpackPanel();
		}
		return null;
	}

	static ItemSlotPanel FindFirstEmptySlot(Array<ItemSlotPanel> panels)
	{
		if (panels == null) { return null; }
		foreach (ItemSlotPanel p in panels)
		{
			if (p != null && p.Item == null) { return p; }
		}
		return null;
	}

	static ItemSlotPanel FirstOf(Array<ItemSlotPanel> panels)
	{
		if (panels == null || panels.Count == 0) { return null; }
		return panels[0];
	}

	void CancelSelect()
	{
		ItemSlotPanel source = _selectedSource;
		ClearSelection();
		ClearAllGhosts();
		UpdateInfoPanels();
		UpdateButtonHint();
		source?.GrabFocus();
	}

	void ClearSelection()
	{
		_selectedItem = null;
		_selectedAmount = 0;
		_selectedSource = null;
		_selectedSourceCategory = EFocusedPanel.None;
		_selectedSourceIndex = -1;
	}

	void CommitMove(ItemSlotPanel dest, EFocusedPanel destCategory, int destIndex)
	{
		if (_selectedItem == null || dest == null)
		{
			CancelSelect();
			return;
		}
		if (dest == _selectedSource)
		{
			CancelSelect();
			return;
		}
		if (!IsValidSelectDestination(dest, destCategory))
		{
			return;
		}
		bool moved = ExecuteSelectMove(destCategory, destIndex);
		if (!moved)
		{
			RefreshGhostOnFocus();
			return;
		}
		// Belt-and-suspenders cleanup: wipe every ghost / dim from select
		// mode before clearing state. RefreshAllSlots runs with InSelectMode
		// already false and won't re-apply select visuals, so any residual
		// overlays from the in-flight selection would otherwise stick.
		ClearAllGhosts();
		ClearSelection();
		RefreshAllSlots();
	}

	bool ExecuteSelectMove(EFocusedPanel destCategory, int destIndex)
	{
		int amount = Mathf.Min(_selectedAmount, _selectedItem?.stackCount ?? 0);
		if (amount <= 0) { return false; }
		switch (_selectedSourceCategory, destCategory)
		{
			case (EFocusedPanel.PlayerInventory, EFocusedPanel.Give):
				MoveInventoryToGive(_selectedItem, amount);
				return true;
			case (EFocusedPanel.Give, EFocusedPanel.PlayerInventory):
				MoveGiveToInventory(_selectedSourceIndex, amount);
				return true;
			case (EFocusedPanel.PlayerInventory, EFocusedPanel.PlayerInventory):
				// Same-side rearrangement — equip / unequip / hotbar reorder /
				// weapon hand swap. Mirrors the inventory screen so the
				// player can manage equipment mid-trade.
				return ExecuteInventoryMove(_focusedSlot);
			case (EFocusedPanel.Give, EFocusedPanel.Give):
				// Moving staged items between give slots adds no value — refuse.
				return false;
		}
		return false;
	}

	// Player-inventory same-side move. Routes the selected item to its
	// destination via Inventory's public API — equipping, unequipping, or
	// reordering within backpack / consumable hotbar — without bouncing
	// through the trade staging. Partial-stack splits aren't handled here:
	// non-stackables (armor / weapons) have stackCount 1, and partial moves
	// to consumable / backpack slots within the same side aren't a common
	// merchant-screen flow.
	bool ExecuteInventoryMove(ItemSlotPanel dest)
	{
		Inventory inv = _player?.Inventory;
		if (inv == null || _playerInventory == null || dest == null) { return false; }
		bool sourceBackpack = _playerInventory.IsBackpackPanel(_selectedSource);
		bool destBackpack = _playerInventory.IsBackpackPanel(dest);
		EInventorySlot destEquip = _playerInventory.GetEquipSlotKind(dest);
		EInventorySlot sourceEquip = _playerInventory.GetEquipSlotKind(_selectedSource);
		if (sourceBackpack && destBackpack)
		{
			int srcIdx = _playerInventory.GetBackpackPanelIndex(_selectedSource);
			int dstIdx = _playerInventory.GetBackpackPanelIndex(dest);
			if (srcIdx < 0 || dstIdx < 0) { return false; }
			return inv.TrySwapInBackpack(srcIdx, dstIdx);
		}
		if (sourceBackpack)
		{
			// The Equipment slot is the attuned alchemy spell (set at the alchemy
			// campfire screen, not here) — not a drop target for carried items.
			if (destEquip == EInventorySlot.Equipment)
			{
				return false;
			}
			if (InventoryScreen.EquipCompatible(destEquip, _selectedItem))
			{
				return inv.TryEquip(_selectedItem, destEquip);
			}
			return false;
		}
		if (destBackpack)
		{
			// The attuned spell can't be moved out to the backpack.
			if (sourceEquip == EInventorySlot.Equipment)
			{
				return false;
			}
			return inv.TryUnequip(sourceEquip);
		}
		if (sourceEquip == EInventorySlot.Equipment || destEquip == EInventorySlot.Equipment)
		{
			return false;
		}
		if (InventoryScreen.CanSwapEquipSlots(sourceEquip, destEquip, _selectedItem, inv))
		{
			return inv.TrySwapEquipSlots(sourceEquip, destEquip);
		}
		return false;
	}

	// -------------------------------------------------------------------
	// Count picker plumbing — shared between select-mode and instant-mode.
	// -------------------------------------------------------------------

	void OpenSelectCountPicker(ItemSlotPanel panel, ItemState item, EFocusedPanel category, int index)
	{
		if (_dropCountPanel == null || item == null) { return; }
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0)
				{
					EnterSelectMode(panel, item, count, category, index);
				}
			},
			onCancel: CloseCountPicker,
			prompt: "Select how many?");
	}

	void OpenInstantMoveCountPicker(ItemState item, Action<int> apply, string prompt)
	{
		if (_dropCountPanel == null || item == null) { return; }
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0)
				{
					apply(count);
				}
			},
			onCancel: CloseCountPicker,
			prompt: prompt);
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
	// Underlying transfer logic — one entry point per direction.
	// -------------------------------------------------------------------

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
		item.Consume(placed);
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
		item.Consume(placed);
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
		toReturn.SetCount(requested);
		int added = _player.Inventory.TryAdd(toReturn);
		if (added <= 0)
		{
			return;
		}
		item.Consume(added);
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
		item.Consume(requested);
		if (item.stackCount <= 0)
		{
			_getItems.RemoveAt(slotIndex);
		}
		SetConversation("Not what you wanted?");
	}

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
				// Trade staging lists are keyed by ItemData only (spoil-agnostic);
				// the real inventory/merchant decrement happens oldest-first.
				existing.AddUnits(moved, 0);
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
			fresh.SetCount(amount);
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
		float giveValue = _merchant.CalculatePersonalValue(_giveItems);
		float getValue = 0f;
		foreach (ItemState s in _getItems)
		{
			if (s?.data != null)
			{
				getValue += _merchant.PerUnitValue(s.data) * s.stackCount;
			}
		}
		if (getValue >= giveValue)
		{
			SetConversation("That trade isn't worth my while.");
			return;
		}
		List<ItemState> accepted = ExtractAcceptableFromGive(out _, out _);
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
				overflow.SetCount(initial - added);
				DropAtMerchant(overflow);
			}
		}
		_getItems.Clear();
		float loyaltyGained = giveValue - getValue;
		List<LoyaltyGift> awarded = _merchant.AcceptGift(accepted, loyaltyGained, _player);
		ApplyAwardedGifts(awarded);
		FinalizeMerchantInventoryAfterCommit(accepted);
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
			split.SetCount(units);
			accepted.Add(split);
			loyaltyGained += _merchant.PerUnitValue(stack.data) * units;
			if (units >= stack.stackCount)
			{
				_giveItems.RemoveAt(i);
			}
			else
			{
				stack.Consume(units);
				anyLeftover = true;
			}
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
			if (gift == null) { continue; }
			if (gift.item != null)
			{
				ItemState state = gift.item.CreateState();
				state.SetCount(Mathf.Max(1, gift.count));
				int initial = state.stackCount;
				int added = _player.Inventory?.TryAdd(state) ?? 0;
				if (added < initial)
				{
					ItemState overflow = gift.item.CreateState();
					overflow.SetCount(initial - added);
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
		if (item == null || _merchant == null || _player?.Sim == null)
		{
			return;
		}
		Vector3 basePos = _merchant.GlobalPosition + Vector3.Up * 0.5f;
		Vector3 offset = new Vector3((GD.Randf() - 0.5f) * 0.6f, 0f, (GD.Randf() - 0.5f) * 0.6f);
		Vector3 impulse = new Vector3((GD.Randf() - 0.5f) * 2f, 1.5f, (GD.Randf() - 0.5f) * 2f);
		_player.Sim.DropItem(item, basePos + offset, impulse, requireInteract: true);
	}

	// -------------------------------------------------------------------
	// Open / Close cleanup.
	// -------------------------------------------------------------------

	void ClearStaging()
	{
		_giveItems.Clear();
		_getItems.Clear();
		_merchantItems.Clear();
		_merchantSourceByData.Clear();
	}

	void PopulateMerchantSnapshot()
	{
		if (_merchant?.Inventory == null)
		{
			return;
		}
		foreach (MobInventoryItem entry in _merchant.Inventory)
		{
			if (entry == null || entry.secret) { continue; }
			if (entry.item?.data == null || entry.item.stackCount <= 0) { continue; }
			ItemState snapshot = entry.item.data.CreateState();
			snapshot.SetCount(entry.item.stackCount);
			_merchantItems.Add(snapshot);
			_merchantSourceByData[entry.item.data] = entry;
		}
	}

	void FinalizeMerchantInventoryAfterCommit(IList<ItemState> sold)
	{
		WriteBackMerchantInventory();
		AddSoldItemsToMerchantInventory(sold);
		_merchantItems.Clear();
		_merchantSourceByData.Clear();
		PopulateMerchantSnapshot();
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

	void WriteBackMerchantInventory()
	{
		if (_merchant?.Inventory == null || _merchantSourceByData.Count == 0)
		{
			return;
		}
		var remaining = new System.Collections.Generic.Dictionary<ItemData, int>();
		foreach (ItemState s in _merchantItems)
		{
			if (s?.data == null || s.stackCount <= 0) { continue; }
			remaining.TryGetValue(s.data, out int prior);
			remaining[s.data] = prior + s.stackCount;
		}
		var inv = _merchant.Inventory;
		foreach (var kv in _merchantSourceByData)
		{
			MobInventoryItem entry = kv.Value;
			if (entry?.item == null) { continue; }
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
				overflow.SetCount(initial - added);
				_player.Sim?.DropItem(
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
		if (_focusedSlot != null)
		{
			_focusedItem = _focusedSlot.Item;
		}
		// Re-apply select visuals so a RefreshAll mid-selection doesn't wipe
		// the dimmed source / ghost preview.
		if (InSelectMode)
		{
			_selectedSource?.SetDimmed(true);
			RefreshGhostOnFocus();
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

	// -------------------------------------------------------------------
	// Drop / Use — player inventory only. The give pile, get pile, and
	// merchant inventory all hide these hints (see UpdateButtonHint) and
	// the underlying InventoryPanel callbacks only fire when its own slot
	// owns focus, so we don't need polling for the other sides.
	// -------------------------------------------------------------------

	void OnInventorySecondaryTap(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null)
		{
			return;
		}
		_player?.Inventory?.Drop(item, 1);
	}

	void OnInventorySecondaryHoldComplete(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item == null || _dropCountPanel == null || _playerInventory == null)
		{
			if (_playerInventory != null) { _playerInventory.HoldLocked = false; }
			return;
		}
		Inventory inv = _player?.Inventory;
		if (inv == null)
		{
			return;
		}
		if (item.stackCount <= 1)
		{
			inv.Drop(item, 1);
			_playerInventory.HoldLocked = false;
			return;
		}
		LockSlotsFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count =>
			{
				CloseCountPicker();
				if (count > 0) { inv.Drop(item, count); }
			},
			onCancel: CloseCountPicker,
			prompt: "Drop how many?");
	}

	void OnInventoryTertiaryPressed(ItemSlotPanel panel, ItemState item)
	{
		if (InSelectMode || item is not ConsumableState consumable || _player == null)
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
		runner.TryStart(data.actionProfile, new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Equipment,
		});
	}

	void OnInventoryTertiaryReleased()
	{
		if (InSelectMode)
		{
			return;
		}
		_player?.Runner?.OnInputReleased();
	}

	// Mirror the HUD hotbar / InventoryScreen charge-progress fill on the
	// Use hint while the runner is charging the focused consumable.
	void TickTertiaryCharge()
	{
		ButtonHint use = _playerInventory?.ButtonHintTertiary;
		if (use == null || !use.Visible || InSelectMode)
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
		if (action.phase != EActionPhase.Charging || action.context.primaryItem != _focusedItem)
		{
			use.SetProgress(0f);
			return;
		}
		use.SetProgress(runner.CurrentChargeT);
	}
}
