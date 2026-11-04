using Godot;
using System;
using System.Collections.Generic;

// Crafting at a crafting bench, on two tabs (TabLeft / TabRight) over one shared
// job queue. Crafting: the learned recipes (CraftingPanel), the maker's items
// (InventoryPanel — backpack, sacks and the stash) and the focused thing's info.
// Repair: the maker's items again, with everything that can't be repaired
// dimmed, and the focused piece's repair cost and hours.
//
// Opened with an onClose callback it dismisses itself on ui_cancel; without one
// its host owns ui_cancel.
//
// Crafting and repairing take in-world time, so both queue a job on the bench
// (CraftQueue, at most CraftQueue.Capacity) rather than happening on the spot.
// A job's cost is paid when it is queued — from the maker's carried materials,
// then the stash — so what is affordable already leaves out everything queued.
// Whatever comes off the bench goes to the stash: finished jobs (the queue waits
// at a finished job the stash has no room for), refunds, and salvage.
//
// On a recipe:      A  — queue a craft of it. Only learned recipes are listed.
// On a queued job:  A  — remove it, refunding its cost (and a repair's gear).
// On an item slot:
//   A  — on the Repair tab, queue a repair of the focused gear to full
//        (GearState.ComposeRepairCost and ComposeRepairHours, shown in the info
//        panel). Always known; the Crafting tab never repairs.
//   Y  — use the item.
//   LT — salvage it into the stash, when it can be salvaged. A tap salvages one
//        unit; hold to choose how many.
//   RT — drop it at the maker's feet. A tap drops the stack; hold to choose how
//        many.
[GlobalClass]
public partial class CraftingScreen : Control
{
	[Export] public GameClient gameClient;
	// The Crafting tab: its page, its label in the tab row, and on the page the
	// maker's backpack and sacks, then the party stash, and the info panel.
	[Export] private Control _craftingPage;
	[Export] private Control _craftingTab;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	// The Repair tab, laid out the same way.
	[Export] private Control _repairPage;
	[Export] private Control _repairTab;
	[Export] private InventoryPanel _repairInventoryPanel;
	[Export] private ItemInfoPanel _repairInfoPanel;
	[Export] private ButtonHint _tabLeftHint;
	[Export] private ButtonHint _tabRightHint;
	[Export] private CraftingPanel _craftingPanel;
	// The A / primary button hint: Craft on a recipe, Remove on a queued job,
	// Repair on repairable gear (Repair tab).
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _hintUse;
	[Export] private ButtonHint _hintSalvage;
	[Export] private ButtonHint _hintDrop;
	[Export] private ItemCountPanel _countPanel;
	// The tab label tint while its tab is not shown.
	[Export] private Color _inactiveTabTint = new Color(0.5f, 0.5f, 0.5f);
	// How long Salvage / Drop must be held on a stack to choose how many.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;

	const string PrimaryHintAction = "ui_select";
	const string UseAction = "MenuTertiary";
	const string SalvageAction = "MenuQuaternary";
	const string DropAction = "MenuQuinary";
	const string TabLeftAction = "TabLeft";
	const string TabRightAction = "TabRight";

	enum ETab
	{
		Crafting,
		Repair,
	}

	// An item slot: one of the maker's grids or the stash, and the slot in it.
	readonly struct Slot
	{
		public readonly IItemGrid grid;
		public readonly int index;
		public readonly ItemSlotPanel panel;

		public Slot(IItemGrid grid, int index, ItemSlotPanel panel)
		{
			this.grid = grid;
			this.index = index;
			this.panel = panel;
		}

		public static readonly Slot None = new(null, -1, null);
		public bool IsNone => grid == null;
	}

	Action _onClose;
	Player _maker;
	CraftingBench _bench;
	ETab _tab;

	// The item slot under the cursor. Its panel is checked for focus at each
	// press, so a cursor moved elsewhere acts on nothing.
	Slot _focused = Slot.None;
	// The recipe or queued job under the cursor, when it isn't an item slot.
	ItemData _focusedRecipe;
	int _focusedJob = -1;
	readonly PolledHold _salvage = new(SalvageAction);
	readonly PolledHold _drop = new(DropAction);
	// What Open subscribed to, so Close detaches the same objects.
	Inventory _watchedInventory;
	ItemGrid _watchedStash;
	Sim _watchedSim;
	// The queue as last painted, to repaint only when it changes.
	int _paintedJobCount = -1;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onGridSlotFocused += OnGridFocused;
		}
		if (_repairInventoryPanel != null)
		{
			_repairInventoryPanel.onGridSlotFocused += OnGridFocused;
			_repairInventoryPanel.onGridSlotButtonUp += OnRepairSlotPressed;
			_repairInventoryPanel.onTabChanged += DimUnrepairable;
		}
		_tabLeftHint?.SetHint(TabLeftAction, string.Empty);
		_tabRightHint?.SetHint(TabRightAction, string.Empty);
		if (_craftingPanel != null)
		{
			_craftingPanel.onRecipeSelected += OnRecipeSelected;
			_craftingPanel.onRecipeFocused += OnRecipeFocused;
			_craftingPanel.onQueueFocused += OnQueueFocused;
			_craftingPanel.onQueuePressed += OnQueuePressed;
		}
		_itemInfoPanel?.SetItem(null);
		_repairInfoPanel?.SetItem(null);
		ApplyTab();
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onGridSlotFocused -= OnGridFocused;
		}
		if (_repairInventoryPanel != null)
		{
			_repairInventoryPanel.onGridSlotFocused -= OnGridFocused;
			_repairInventoryPanel.onGridSlotButtonUp -= OnRepairSlotPressed;
			_repairInventoryPanel.onTabChanged -= DimUnrepairable;
		}
		if (_craftingPanel != null)
		{
			_craftingPanel.onRecipeSelected -= OnRecipeSelected;
			_craftingPanel.onRecipeFocused -= OnRecipeFocused;
			_craftingPanel.onQueueFocused -= OnQueueFocused;
			_craftingPanel.onQueuePressed -= OnQueuePressed;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_maker = player;
	}

	public void Open(Player player, CraftingBench bench, Action onClose = null)
	{
		if (player != null)
		{
			_maker = player;
		}
		_bench = bench;
		_onClose = onClose;
		// A potion drunk from here spends partway through its timeline, after the
		// press that started it; finished jobs land in the stash out from under us.
		_watchedInventory = _maker?.Inventory;
		_watchedStash = Stash;
		_watchedSim = _maker?.Sim;
		if (_watchedInventory != null)
		{
			_watchedInventory.onChanged += OnItemsChanged;
		}
		if (_watchedStash != null)
		{
			_watchedStash.onChanged += OnItemsChanged;
		}
		if (_watchedSim != null)
		{
			_watchedSim.onCraftJobDelivered += OnJobDelivered;
		}
		ClearFocus();
		_salvage.Arm(_hintSalvage);
		_drop.Arm(_hintDrop);
		_craftingPanel?.HideAnnouncement();
		_paintedJobCount = -1;
		_tab = ETab.Crafting;
		ApplyTab();
		Visible = true;
		// Deferred so the just-shown nodes are visible-in-tree (GrabFocus needs
		// that) and so it runs after the visibility-change refresh has rebuilt the
		// recipe list.
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	// The initial keyboard/gamepad focus for the tab shown: on Crafting a recipe
	// that can be queued now, else the first item; on Repair the first repairable
	// piece, else the first item.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		if (_tab == ETab.Repair)
		{
			(FirstRepairable() ?? _repairInventoryPanel?.FirstOccupied())?.GrabFocus();
			return;
		}
		if (_craftingPanel != null && _craftingPanel.GrabFirstAvailableRecipeFocus())
		{
			return;
		}
		_inventoryPanel?.FirstOccupied()?.GrabFocus();
	}

	ItemSlotPanel FirstRepairable()
	{
		if (_repairInventoryPanel == null)
		{
			return null;
		}
		foreach (ItemSlotPanel slot in _repairInventoryPanel.GridPanel.EnumerateSlots())
		{
			if (IsRepairable(slot.Item))
			{
				return slot;
			}
		}
		return null;
	}

	// ---- Tabs -------------------------------------------------------------------

	void CycleTab(int direction)
	{
		int count = Enum.GetValues<ETab>().Length;
		_tab = (ETab)(((int)_tab + direction + count) % count);
		ApplyTab();
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	// Show the current tab's page and light its label; focus starts over, so
	// nothing on the page left behind stays acted on.
	void ApplyTab()
	{
		SetTab(_craftingPage, _craftingTab, _tab == ETab.Crafting);
		SetTab(_repairPage, _repairTab, _tab == ETab.Repair);
		ClearFocus();
		ShowFocused();
		UpdateHints();
	}

	void SetTab(Control page, Control tab, bool active)
	{
		if (page != null)
		{
			page.Visible = active;
		}
		if (tab != null)
		{
			tab.Modulate = active ? Colors.White : _inactiveTabTint;
		}
	}

	ItemInfoPanel InfoPanel => _tab == ETab.Repair ? _repairInfoPanel : _itemInfoPanel;

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		if (_watchedInventory != null)
		{
			_watchedInventory.onChanged -= OnItemsChanged;
		}
		if (_watchedStash != null)
		{
			_watchedStash.onChanged -= OnItemsChanged;
		}
		if (_watchedSim != null)
		{
			_watchedSim.onCraftJobDelivered -= OnJobDelivered;
		}
		_watchedInventory = null;
		_watchedStash = null;
		_watchedSim = null;
		_countPanel?.Dismiss();
		ClearFocus();
		_bench = null;
		Visible = false;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			RefreshItems();
			RefreshRecipeList();
			RefreshQueue();
			UpdateHints();
		}
	}

	SimState WorldSim => _maker?.Sim?.WorldState?.SimState;
	ItemGrid Stash => WorldSim?.PartyStash;
	CraftQueue Queue => _bench?.SimState?.Queue;
	double Clock => _maker?.Sim?.WorldClockDays ?? 0.0;

	void RefreshItems()
	{
		_inventoryPanel?.Paint(_maker?.Inventory, Stash);
		_repairInventoryPanel?.Paint(_maker?.Inventory, Stash);
		DimUnrepairable();
	}

	// On the Repair tab everything that can't be repaired is greyed.
	void DimUnrepairable()
	{
		if (_repairInventoryPanel == null)
		{
			return;
		}
		foreach (ItemSlotPanel slot in _repairInventoryPanel.GridPanel.EnumerateSlots())
		{
			slot.SetUnavailable(slot.Item != null && !IsRepairable(slot.Item));
		}
	}

	static bool IsRepairable(ItemState item) => item is GearState { IsRepairable: true };

	// A recipe is available when it can be paid now and the queue has room.
	void RefreshRecipeList()
	{
		if (_craftingPanel == null || _maker == null)
		{
			return;
		}
		SimData simData = _maker.Sim?.SimData;
		SimState worldSim = WorldSim;
		if (simData == null || worldSim == null)
		{
			return;
		}
		bool queueOpen = Queue != null && !Queue.IsFull;
		var materials = new List<ItemState>(_maker.Materials(Stash));
		var rows = new List<CraftingPanel.RecipeRow>();
		int recipeCount = simData.recipes.Count;
		for (int i = 0; i < recipeCount; i++)
		{
			ItemData item = simData.recipes[i];
			if (item == null || item.recipe == null || !item.recipe.HasInputs || !worldSim.IsRecipeDiscovered(item))
			{
				continue;
			}
			bool available = queueOpen && Recipes.CountAffordable(item.recipe.inputs, materials) > 0;
			rows.Add(new CraftingPanel.RecipeRow(item, item.displayName.ToString(), available));
		}
		_craftingPanel.RefreshRecipes(rows);
	}

	void RefreshQueue()
	{
		_paintedJobCount = Queue?.Count ?? 0;
		_craftingPanel?.RefreshQueue(Queue, Clock);
	}

	void OnItemsChanged()
	{
		RefreshItems();
		RefreshRecipeList();
		ShowFocused();
		UpdateHints();
	}

	void OnQueueChanged()
	{
		RefreshQueue();
		OnItemsChanged();
	}

	void OnJobDelivered(CraftingBenchSimState bench, CraftJob job)
	{
		if (bench != _bench?.SimState)
		{
			return;
		}
		ItemState shown = job.Preview();
		_craftingPanel?.ShowAnnouncement(_maker.Sim.DeliveredText(job), shown?.data?.inventorySprite);
		OnQueueChanged();
	}

	// ---- Focus ----------------------------------------------------------------

	void OnGridFocused(IItemGrid grid, int index, ItemSlotPanel panel)
	{
		ClearFocus();
		_focused = new Slot(grid, index, panel);
		ShowFocused();
		UpdateHints();
	}

	void OnRecipeFocused(ItemData recipe)
	{
		ClearFocus();
		_focusedRecipe = recipe;
		ShowFocused();
		UpdateHints();
	}

	void OnQueueFocused(int index, ItemSlotPanel panel)
	{
		ClearFocus();
		_focusedJob = index;
		ShowFocused();
		UpdateHints();
	}

	void ClearFocus()
	{
		_focused = Slot.None;
		_focusedRecipe = null;
		_focusedJob = -1;
		_salvage.Reset(_hintSalvage);
		_drop.Reset(_hintDrop);
	}

	// Null while the cursor is anywhere but an item slot.
	ItemState FocusedItem()
	{
		if (_focused.IsNone || _focused.panel == null || !_focused.panel.HasButtonFocus())
		{
			return null;
		}
		return _focused.grid?.At(_focused.index);
	}

	CraftJob FocusedJob()
	{
		CraftQueue queue = Queue;
		return queue != null && _focusedJob >= 0 && _focusedJob < queue.Count ? queue.Jobs[_focusedJob] : null;
	}

	// The shown tab's info panel for whatever is focused, with what it requires
	// and takes: a recipe's inputs, hours and yield, a queued job's paid cost and
	// hours, what a held item salvages into, and on the Repair tab a repair's share
	// of cost and hours.
	void ShowFocused()
	{
		ItemInfoPanel info = InfoPanel;
		if (info == null)
		{
			return;
		}
		if (_focusedRecipe != null)
		{
			CraftRecipeData recipe = _focusedRecipe.recipe;
			ItemState made = _focusedRecipe.CreateState();
			made.SetCount(_focusedRecipe.IsStackable ? recipe?.count ?? 1 : 1);
			// The recipe is learned, so its output reads identified.
			info.SetItem(made, forceIdentified: true, reagents: recipe?.inputs, hours: recipe?.hours ?? -1.0, makes: recipe?.count ?? 1);
			return;
		}
		if (_focusedJob >= 0)
		{
			CraftJob job = FocusedJob();
			info.SetItem(job?.Preview(), forceIdentified: job != null && !job.IsRepair, reagents: job != null ? PaidRows(job) : null, hours: job?.hours ?? -1.0,
				makes: job != null && !job.IsRepair ? job.count : 0, repair: job != null && job.IsRepair);
			return;
		}
		ItemState item = FocusedItem();
		// Shown whenever the item has yields, even while a missing tool blocks the salvage.
		var salvage = item?.data is { IsSalvageable: true } data ? data.salvage : null;
		if (_tab == ETab.Repair && item is GearState { IsRepairable: true } gear)
		{
			info.SetItem(item, reagents: gear.ComposeRepairCost(), hours: gear.ComposeRepairHours(), salvage: salvage, repair: true);
			return;
		}
		info.SetItem(item, salvage: salvage);
	}

	static List<MaterialCount> PaidRows(CraftJob job)
	{
		var rows = new List<MaterialCount>();
		for (int i = 0; i < job.paid.Count; i++)
		{
			rows.Add(new MaterialCount { item = job.paid[i].item, count = job.paid[i].count });
		}
		return rows;
	}

	// ---- Crafting ---------------------------------------------------------------

	// Queue a craft of a learned recipe, paying its inputs now. An unavailable
	// recipe's button is disabled upstream, so a spend that still fails here just
	// declines silently.
	void OnRecipeSelected(ItemData item)
	{
		CraftQueue queue = Queue;
		CraftRecipeData recipe = item?.recipe;
		SimState worldSim = WorldSim;
		if (queue == null || queue.IsFull || recipe == null || !recipe.HasInputs || worldSim == null || !worldSim.IsRecipeDiscovered(item))
		{
			return;
		}
		var job = new CraftJob
		{
			made = item,
			count = Mathf.Max(1, recipe.count),
			hours = recipe.hours,
		};
		var spent = new List<SpentItem>();
		if (_maker == null || !_maker.SpendReagents(recipe.inputs, Stash, spent))
		{
			return;
		}
		job.paid.AddRange(spent);
		// The maker knows what they are making, so an unidentified kind (a potion)
		// is identified.
		worldSim.IdentifyItem(item);
		queue.Enqueue(job, Clock);
		OnQueueChanged();
	}

	// Remove a queued job, refunding it into the stash.
	void OnQueuePressed(int index)
	{
		CraftQueue queue = Queue;
		if (queue == null || index < 0 || index >= queue.Count)
		{
			return;
		}
		if (!queue.Cancel(index, Clock, Stash))
		{
			_craftingPanel?.ShowAnnouncement(Loc.Get(Loc.Keys.stash_no_space), null);
			return;
		}
		OnQueueChanged();
	}

	// ---- Repair ----------------------------------------------------------------

	// The focused piece when it can be repaired — only ever on the Repair tab.
	GearState FocusedRepairable()
	{
		return _tab == ETab.Repair && FocusedItem() is GearState { IsRepairable: true } gear ? gear : null;
	}

	// A on a Repair-tab slot (the slot is the focused one).
	void OnRepairSlotPressed(IItemGrid grid, int index, ItemSlotPanel panel)
	{
		if (!PickerOpen)
		{
			Repair();
		}
	}

	bool CanQueueRepair(GearState gear)
	{
		return gear != null && _maker != null && Queue != null && !Queue.IsFull
			&& Recipes.CountAffordable(gear.ComposeRepairCost(), _maker.Materials(Stash)) > 0;
	}

	// Pay for the repair and put the gear on the bench until it is done.
	void Repair()
	{
		GearState gear = FocusedRepairable();
		Slot slot = _focused;
		if (gear == null)
		{
			return;
		}
		if (!CanQueueRepair(gear))
		{
			bool queueFull = Queue == null || Queue.IsFull;
			_craftingPanel?.ShowAnnouncement(Loc.Get(queueFull ? Loc.Keys.craft_queue_full : Loc.Keys.item_repair_missing), gear.data?.inventorySprite);
			return;
		}
		var job = new CraftJob
		{
			gear = gear,
			hours = gear.ComposeRepairHours(),
		};
		var spent = new List<SpentItem>();
		if (!_maker.SpendReagents(gear.ComposeRepairCost(), Stash, spent))
		{
			return;
		}
		job.paid.AddRange(spent);
		slot.grid?.Take(slot.index, 1);
		Queue.Enqueue(job, Clock);
		_maker.Inventory.NotifyChanged();
		OnQueueChanged();
	}

	// ---- Use / salvage / drop ------------------------------------------------

	bool CanSalvage(ItemState item) => item != null && ItemTransfer.CanSalvage(item, _maker);

	void UpdateHints()
	{
		ItemState item = FocusedItem();
		string primary = null;
		if (_focusedRecipe != null && Queue != null && !Queue.IsFull && _maker != null
			&& Recipes.CountAffordable(_focusedRecipe.recipe?.inputs, _maker.Materials(Stash)) > 0)
		{
			primary = Loc.Get(Loc.Keys.station_craft);
		}
		else if (FocusedJob() != null)
		{
			primary = Loc.Get(Loc.Keys.craft_queue_remove);
		}
		else if (FocusedRepairable() != null)
		{
			// Shown even when it can't be queued; a press then announces why.
			primary = Loc.Get(Loc.Keys.item_repair);
		}
		ShowHint(_buttonHintPrimary, PrimaryHintAction, primary);
		ShowHint(_hintUse, UseAction, InventoryScreen.UseVerb(_maker, item));
		ShowHint(_hintSalvage, SalvageAction, CanSalvage(item) ? Loc.Get(Loc.Keys.item_salvage) : null);
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

	// The maker's own items are the inventory's; only the stash is a foreign source.
	void Use()
	{
		InventoryScreen.Use(_maker, FocusedItem(), _focused.grid == Stash ? Stash : null);
		OnItemsChanged();
	}

	// Salvage `count` units into the stash — all their yields or nothing.
	void Salvage(Slot slot, int count)
	{
		if (!ItemTransfer.SalvageInto(slot.grid, slot.index, count, _maker, Stash))
		{
			_craftingPanel?.ShowAnnouncement(Loc.Get(Loc.Keys.stash_no_space), null);
			return;
		}
		_maker.Inventory?.NotifyChanged();
		Stash?.NotifyChanged();
	}

	void Drop(Slot slot, int count)
	{
		if (count > 0)
		{
			_maker?.DropAtFeet(slot.grid?.Take(slot.index, count));
		}
		OnItemsChanged();
	}

	// LT salvages, RT drops. A salvage tap takes one unit and a drop tap the whole
	// stack; a hold on a stack asks how many.
	void TickHolds(float delta)
	{
		ItemState item = FocusedItem();
		Slot slot = _focused;
		PolledHold.EResult salvage = _salvage.Tick(CanSalvage(item), delta, _holdSeconds, _hintSalvage);
		if (salvage == PolledHold.EResult.Hold && item.stackCount > 1)
		{
			_countPanel?.Open(item.stackCount, count => Salvage(slot, count), prompt: Loc.Get(Loc.Keys.item_salvage_how_many));
			return;
		}
		if (salvage != PolledHold.EResult.None)
		{
			Salvage(slot, 1);
			return;
		}
		PolledHold.EResult drop = _drop.Tick(item != null, delta, _holdSeconds, _hintDrop);
		if (drop == PolledHold.EResult.Hold && item.stackCount > 1)
		{
			_countPanel?.Open(item.stackCount, count => Drop(slot, count), prompt: Loc.Get(Loc.Keys.item_drop_how_many));
			return;
		}
		if (drop != PolledHold.EResult.None)
		{
			Drop(slot, item.stackCount);
		}
	}

	public override void _Process(double delta)
	{
		if (!IsVisibleInTree() || _maker == null)
		{
			return;
		}
		// Settled here every frame, not only on the sim's sweep, so a finished job
		// never sits at a full bar.
		CraftingBenchSimState bench = _bench?.SimState;
		_maker.Sim?.SettleCraftQueue(bench);
		if (bench != null && bench.Queue.Count != _paintedJobCount)
		{
			OnQueueChanged();
		}
		_craftingPanel?.RefreshProgress(Queue, Clock);
		if (!PickerOpen)
		{
			TickHolds((float)delta);
		}
	}

	bool PickerOpen => _countPanel != null && _countPanel.IsOpen;

	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible || PickerOpen)
		{
			return;
		}
		if (e.IsActionPressed(UseAction) && InventoryScreen.UseVerb(_maker, FocusedItem()) != null)
		{
			Use();
			GetViewport().SetInputAsHandled();
			return;
		}
		if (e.IsActionPressed(TabLeftAction) || e.IsActionPressed(TabRightAction))
		{
			CycleTab(e.IsActionPressed(TabLeftAction) ? -1 : 1);
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
