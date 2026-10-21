using Godot;
using System;
using System.Collections.Generic;

// Cooking at an ICookingStation: the camp screen's Cook tab (the lit campfire),
// or standalone from the HUD at a cookpot. Left = the station's experimentation
// slots + commit button (CookingPanel); center = the cook's belt and backpack and,
// at camp only, the party stash (BackpackPanels over Inventory.Belt / Backpack and
// SimState.PartyStash), whose materials are the ingredient source.
//
// Opened with an onClose callback (standalone) it dismisses itself on ui_cancel;
// opened without one (the camp tab) its host owns ui_cancel.
//
// Cooking is INSTANT — there is no cook job or timer. A recipe is a cookable
// ConsumableData, and cooking it grants one into the cook's backpack (then the
// stash, then dropped at their feet if neither has room); eating it is an
// ordinary item use. Two paths cook:
//   * Recipe list: tapping a discovered, affordable recipe spends its reagents
//     from the cook's carried materials, then the stash, and grants the item.
//   * Experimentation: tap materials into the slots and press Cook — the slot
//     contents are consumed instantly. A valid match discovers the recipe and
//     grants the item; a failed mix is just wasted ("Yuck").
//
// On a belt / backpack / stash slot, as on the stash screen:
//   Y  — use the item (eat a meal, drink a potion).
//   RT — drop the stack at the cook's feet. Hold to drop only some of it.
[GlobalClass]
public partial class CookingScreen : Control
{
	[Export] public GameClient gameClient;
	// The cook's belt and backpack and the party stash, slot for slot; only their
	// materials can be cooked.
	[Export] private BackpackPanel _beltPanel;
	[Export] private BackpackPanel _backpackPanel;
	// One grid per sack the cook carries, under the backpack.
	[Export] private SackPanels _sackPanels;
	[Export] private BackpackPanel _stashPanel;
	[Export] private CookingPanel _cookingPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	// The A / primary button hint. Its label tracks the commit button: "Cook"
	// while ingredients are loaded, "Continue" (leave camp) when the slots are
	// empty.
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _hintUse;
	[Export] private ButtonHint _hintDrop;
	[Export] private ItemCountPanel _countPanel;
	// How long Drop must be held on a stack to choose how many.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;

	// Glyph action driving the primary button hint (the A button); matches the
	// primary-verb convention used by the inventory-style screens.
	const string PrimaryHintAction = "ui_select";
	const string UseAction = "MenuTertiary";
	const string DropAction = "MenuQuinary";

	Action _onClose;
	Player _player;
	ICookingStation _station;

	// The item slot under the cursor: its grid and panel. The panel is checked for
	// focus at each press, so a cursor moved onto the cooking panel acts on nothing.
	IItemGrid _focusedGrid;
	int _focusedIndex = -1;
	ItemSlotPanel _focusedPanel;
	readonly PolledHold _drop = new(DropAction);
	// What Open subscribed to, so Close detaches the same objects.
	Inventory _watchedInventory;
	ItemGrid _watchedStash;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		if (_beltPanel != null)
		{
			_beltPanel.onSlotFocused += OnBeltFocused;
			_beltPanel.onSlotButtonUp += OnBeltMaterialTap;
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused += OnBackpackFocused;
			_backpackPanel.onSlotButtonUp += OnBackpackMaterialTap;
		}
		if (_sackPanels != null)
		{
			_sackPanels.onSlotFocused += OnItemSlotFocused;
			_sackPanels.onSlotButtonUp += OnSackMaterialTap;
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused += OnStashFocused;
			_stashPanel.onSlotButtonUp += OnStashMaterialTap;
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.onPrimaryTap += OnCookingRemoveTap;
			_cookingPanel.onFocusedItemChanged += OnCookingFocusChanged;
			_cookingPanel.onCookPressed += OnCookCommit;
			_cookingPanel.onRecipeSelected += OnRecipeSelected;
			_cookingPanel.onRecipeFocused += OnRecipeFocused;
		}
		_itemInfoPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_beltPanel != null)
		{
			_beltPanel.onSlotFocused -= OnBeltFocused;
			_beltPanel.onSlotButtonUp -= OnBeltMaterialTap;
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused -= OnBackpackFocused;
			_backpackPanel.onSlotButtonUp -= OnBackpackMaterialTap;
		}
		if (_sackPanels != null)
		{
			_sackPanels.onSlotFocused -= OnItemSlotFocused;
			_sackPanels.onSlotButtonUp -= OnSackMaterialTap;
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused -= OnStashFocused;
			_stashPanel.onSlotButtonUp -= OnStashMaterialTap;
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.onPrimaryTap -= OnCookingRemoveTap;
			_cookingPanel.onFocusedItemChanged -= OnCookingFocusChanged;
			_cookingPanel.onCookPressed -= OnCookCommit;
			_cookingPanel.onRecipeSelected -= OnRecipeSelected;
			_cookingPanel.onRecipeFocused -= OnRecipeFocused;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
	}

	public void Open(Player player, ICookingStation station, Action onClose = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_station = station;
		if (_stashPanel != null)
		{
			_stashPanel.Visible = station?.IsCamp == true;
		}
		_onClose = onClose;
		// A potion drunk from here spends partway through its timeline, after the
		// press that started it.
		_watchedInventory = CookInventory;
		_watchedStash = Stash;
		if (_watchedInventory != null)
		{
			_watchedInventory.onChanged += OnItemsChanged;
		}
		if (_watchedStash != null)
		{
			_watchedStash.onChanged += OnItemsChanged;
		}
		ClearFocusedSlot();
		_drop.Arm(_hintDrop);
		_cookingPanel?.HideAnnouncement();
		Visible = true;
		// Auto-highlight: see ApplyInitialFocus. Deferred so the just-
		// shown nodes are visible-in-tree (GrabFocus needs that) and so it runs after
		// the visibility-change refresh has rebuilt the recipe list / repainted the
		// stash.
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	// Choose the initial keyboard/gamepad focus when the tab opens, in priority
	// order: the first item in the backpack, otherwise the first item anywhere
	// else carried or stashed, otherwise a recipe the party can currently cook,
	// otherwise the Cook button.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		ItemSlotPanel firstItem = _backpackPanel?.FirstOccupied() ?? _sackPanels?.FirstOccupied() ?? _beltPanel?.FirstOccupied() ?? _stashPanel?.FirstOccupied();
		if (firstItem != null)
		{
			firstItem.GrabFocus();
			return;
		}
		if (_cookingPanel != null && _cookingPanel.GrabFirstAvailableRecipeFocus())
		{
			return;
		}
		_cookingPanel?.GrabCookButtonFocus();
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		ReturnInputs();
		if (_watchedInventory != null)
		{
			_watchedInventory.onChanged -= OnItemsChanged;
		}
		if (_watchedStash != null)
		{
			_watchedStash.onChanged -= OnItemsChanged;
		}
		_watchedInventory = null;
		_watchedStash = null;
		_countPanel?.Dismiss();
		ClearFocusedSlot();
		_station = null;
		Visible = false;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			if (_station != null)
			{
				_cookingPanel?.Bind(_station.CookingSlots);
			}
			RefreshMaterials();
			RefreshRecipeList();
			UpdatePrimaryHint();
		}
		else
		{
			_cookingPanel?.Unbind();
		}
	}

	SimState WorldSim => _player?.Sim?.WorldState?.SimState;
	Inventory CookInventory => _player?.Inventory;
	// The party stash is the camp's; away from camp there is none to draw on.
	ItemGrid Stash => _station?.IsCamp == true ? WorldSim?.PartyStash : null;

	// Only materials can be cooked, so everything else is greyed.
	void RefreshMaterials()
	{
		RefreshMaterials(_beltPanel, CookInventory?.Belt?.Slots);
		RefreshMaterials(_backpackPanel, CookInventory?.Backpack?.Slots);
		RefreshMaterials(_stashPanel, Stash?.Slots);
		if (_sackPanels != null)
		{
			_sackPanels.Refresh(CookInventory);
			foreach ((Inventory.CarriedGrid _, BackpackPanel panel) in _sackPanels.EnumeratePanels())
			{
				MarkUncookable(panel);
			}
		}
	}

	static void RefreshMaterials(BackpackPanel panel, IReadOnlyList<ItemState> slots)
	{
		if (panel == null)
		{
			return;
		}
		panel.Refresh(slots);
		MarkUncookable(panel);
	}

	static void MarkUncookable(BackpackPanel panel)
	{
		foreach (ItemSlotPanel slot in panel.EnumerateSlots())
		{
			slot.SetUnavailable(slot.Item != null && !IsCookable(slot.Item));
		}
	}

	static bool IsCookable(ItemState item) => item?.data != null && item.data.IsMaterial;

	void RefreshRecipeList()
	{
		if (_cookingPanel == null || _station == null || _player == null)
		{
			return;
		}
		SimData simData = _player.Sim?.SimData;
		SimState worldSim = WorldSim;
		if (simData == null)
		{
			return;
		}
		var materials = new List<ItemState>(_player.Materials(Stash));
		_cookingPanel.RefreshRecipes(simData.recipes, worldSim, materials, _station.CampfireType);
	}

	void OnItemsChanged()
	{
		RefreshMaterials();
		RefreshRecipeList();
		_itemInfoPanel?.SetItem(FocusedItem());
		UpdateItemHints();
	}

	// ---- Material side (add to cooking slots) ------------------------------

	void OnBeltFocused(int index, ItemSlotPanel panel)
	{
		OnItemSlotFocused(CookInventory?.Belt, index, panel);
	}

	void OnBackpackFocused(int index, ItemSlotPanel panel)
	{
		OnItemSlotFocused(CookInventory?.Backpack, index, panel);
	}

	void OnStashFocused(int index, ItemSlotPanel panel)
	{
		OnItemSlotFocused(Stash, index, panel);
	}

	// Every item shows its info, not only materials: any of them can be used or dropped.
	void OnItemSlotFocused(IItemGrid grid, int index, ItemSlotPanel panel)
	{
		_focusedGrid = grid;
		_focusedIndex = index;
		_focusedPanel = panel;
		_drop.Reset(_hintDrop);
		_itemInfoPanel?.SetItem(FocusedItem());
		UpdateItemHints();
	}

	void ClearFocusedSlot()
	{
		_focusedGrid = null;
		_focusedIndex = -1;
		_focusedPanel = null;
		UpdateItemHints();
	}

	// Null while the cursor is anywhere but an item slot.
	ItemState FocusedItem()
	{
		if (_focusedGrid == null || _focusedPanel == null || !_focusedPanel.HasButtonFocus())
		{
			return null;
		}
		return _focusedGrid.At(_focusedIndex);
	}

	bool PickerOpen => _countPanel != null && _countPanel.IsOpen;

	void OnBeltMaterialTap(int index, ItemSlotPanel panel)
	{
		CookMaterial(CookInventory?.Belt, index, 1);
	}

	void OnBackpackMaterialTap(int index, ItemSlotPanel panel)
	{
		CookMaterial(CookInventory?.Backpack, index, 1);
	}

	void OnStashMaterialTap(int index, ItemSlotPanel panel)
	{
		CookMaterial(Stash, index, 1);
	}

	void OnSackMaterialTap(Inventory.CarriedGrid grid, int index, ItemSlotPanel panel)
	{
		CookMaterial(grid, index, 1);
	}

	// Move up to `count` units of the material at `grid[index]` into the cooking
	// slots.
	void CookMaterial(IItemGrid grid, int index, int count)
	{
		if (grid == null || _cookingPanel == null)
		{
			return;
		}
		ItemState src = grid.At(index);
		if (!IsCookable(src))
		{
			return;
		}
		int requested = Mathf.Min(count, src.stackCount);
		int placed = _cookingPanel.TryAdd(src, requested);
		if (placed <= 0)
		{
			return;
		}
		grid.Take(index, placed);
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// ---- Cooking side (return to the backpack) ------------------------------

	void OnCookingFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		ClearFocusedSlot();
		_itemInfoPanel?.SetItem(item);
	}

	void OnCookingRemoveTap(int index, ItemSlotPanel panel, ItemState item)
	{
		if (_cookingPanel == null)
		{
			return;
		}
		ItemState removed = _cookingPanel.TryRemove(index, 1);
		if (removed == null)
		{
			return;
		}
		ReturnToBackpack(removed);
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// Recipe list tap: cook this discovered recipe now — pay its reagents from the
	// cook's carried materials, then the stash, and grant the item. An unaffordable recipe's button is disabled
	// upstream, so a spend that still fails here just declines silently.
	void OnRecipeSelected(ConsumableData recipe)
	{
		SimState worldSim = WorldSim;
		if (recipe == null || worldSim == null || !worldSim.IsRecipeDiscovered(recipe))
		{
			return;
		}
		if (_player == null || !_player.SpendReagents(recipe.recipe?.inputs, Stash))
		{
			return;
		}
		GrantCooked(recipe);
	}

	// Preview the item a recipe cooks into, with its ingredients as the reagent
	// row. The recipe is discovered, so its output reads identified.
	void OnRecipeFocused(ConsumableData recipe)
	{
		ClearFocusedSlot();
		if (recipe == null)
		{
			return;
		}
		_itemInfoPanel?.SetItem(recipe.CreateState(), forceIdentified: true, reagents: recipe.recipe?.inputs);
	}

	// ---- Cook button -------------------------------------------------------

	// Instant experimentation cook: the loaded slot contents are consumed either
	// way. A valid match discovers the recipe (no separate "unidentified" phase)
	// AND grants the item; a failed mix is wasted ("Yuck").
	void OnCookCommit()
	{
		if (_cookingPanel == null || _player == null || _station == null)
		{
			return;
		}
		SimData simData = _player.Sim?.SimData;
		if (simData == null)
		{
			return;
		}
		if (!HasAnyInput())
		{
			// Nothing loaded — the button does nothing (leaving camp is ui_cancel).
			return;
		}
		Cooking.MatchResult match = Cooking.TryMatch(_cookingPanel.Inputs, simData.recipes, _station.CampfireType);
		SimState worldSim = WorldSim;
		_cookingPanel.DrainInputs();
		if (!match.IsValid || worldSim == null)
		{
			_cookingPanel.ShowAnnouncement("Cooking failed: Yuck!", null);
			UpdatePrimaryHint();
			return;
		}
		if (worldSim.DiscoverRecipe(match.output) && _station.IsCamp)
		{
			// Learned AT camp, so commit it to the shared party pool right away
			// (the same bank a camp visit does) — camp knowledge is party knowledge,
			// visible to every character. Discovery otherwise lands only in the active
			// member's provisional store.
			worldSim.BankActiveKnowledge();
		}
		GrantCooked(match.output);
	}

	// Hand one of the cooked item to the cook. Reagents are already spent by the
	// caller. The cook knows what they made, so an unidentified kind (a potion)
	// is identified.
	void GrantCooked(ConsumableData output)
	{
		if (output == null || _player == null)
		{
			return;
		}
		WorldSim?.IdentifyItem(output);
		ReturnToBackpack(output.CreateState());
		_cookingPanel?.ShowAnnouncement($"Cooked {output.displayName}", output.inventorySprite);
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// The primary (A) action only ever cooks the loaded ingredients; it's
	// disabled with the slots empty (leaving camp is ui_cancel, not this button).
	void UpdatePrimaryHint()
	{
		bool canCook = HasAnyInput();
		_buttonHintPrimary?.SetHint(PrimaryHintAction, "Cook");
		_cookingPanel?.SetCookEnabled(canCook);
	}

	bool HasAnyInput()
	{
		IReadOnlyList<ItemState> inputs = _cookingPanel?.Inputs;
		if (inputs == null)
		{
			return false;
		}
		for (int i = 0; i < inputs.Count; i++)
		{
			if (inputs[i] != null && inputs[i].stackCount > 0)
			{
				return true;
			}
		}
		return false;
	}

	// Pull the experimentation slots back into the backpack on close so nothing
	// is silently lost.
	void ReturnInputs()
	{
		if (_cookingPanel == null || _station == null)
		{
			return;
		}
		List<ItemState> drained = _cookingPanel.DrainInputs();
		for (int i = 0; i < drained.Count; i++)
		{
			ReturnToBackpack(drained[i]);
		}
		RefreshMaterials();
	}

	// Into the cook's backpack, then the stash; whatever neither has room for is
	// dropped at the cook's feet.
	void ReturnToBackpack(ItemState item)
	{
		Inventory inv = CookInventory;
		if (inv == null || item == null)
		{
			return;
		}
		int wanted = item.stackCount;
		if (inv.TryAdd(item) >= wanted)
		{
			return;
		}
		ItemState leftover = Stash != null ? Stash.Add(item) : item;
		if (leftover != null)
		{
			_player.Sim?.DropItem(leftover, _player.GlobalPosition + Vector3.Up * 0.5f, Vector3.Zero, requireInteract: true);
		}
	}

	// ---- Use / drop ----------------------------------------------------------

	void UpdateItemHints()
	{
		ItemState item = FocusedItem();
		ShowHint(_hintUse, UseAction, InventoryScreen.UseVerb(_player, item));
		ShowHint(_hintDrop, DropAction, item != null ? Loc.Get(Loc.Keys.stash_drop) : null);
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

	// The member's own grids are the inventory's; only the stash is a foreign source.
	void Use()
	{
		InventoryScreen.Use(_player, FocusedItem(), _focusedGrid == Stash ? Stash : null);
		OnItemsChanged();
	}

	void Drop(IItemGrid grid, int index, int count)
	{
		if (grid == null || count <= 0)
		{
			return;
		}
		_player?.DropAtFeet(grid.Take(index, count));
		OnItemsChanged();
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || _player == null || PickerOpen)
		{
			return;
		}
		ItemState item = FocusedItem();
		IItemGrid grid = _focusedGrid;
		int index = _focusedIndex;
		PolledHold.EResult drop = _drop.Tick(item != null, (float)delta, _holdSeconds, _hintDrop);
		if (drop == PolledHold.EResult.Tap || (drop == PolledHold.EResult.Hold && item.stackCount <= 1))
		{
			Drop(grid, index, item.stackCount);
		}
		else if (drop == PolledHold.EResult.Hold)
		{
			_countPanel?.Open(item.stackCount, count => Drop(grid, index, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
		}
	}

	public override void _UnhandledInput(InputEvent e)
	{
		// Children (CookingPanel) see ui_cancel first and consume it while they
		// have an in-flight selection.
		if (!Visible || PickerOpen)
		{
			return;
		}
		if (e.IsActionPressed(UseAction) && InventoryScreen.UseVerb(_player, FocusedItem()) != null)
		{
			Use();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (_onClose != null && e.IsActionPressed("ui_cancel"))
		{
			Close();
			GetViewport().SetInputAsHandled();
		}
	}
}
