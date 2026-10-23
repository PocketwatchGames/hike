using Godot;
using System;
using System.Collections.Generic;

// Crafting at a crafting bench. Left = the bench's job queue and the learned
// recipes (CraftingPanel); center = the maker's equipment, belt, backpack and
// sacks (InventoryPanel) and the party stash; right = the focused thing's info,
// with what it requires and how long it takes. Equipment is shown because worn
// gear is what most needs repair.
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
//   X  — queue a repair of the focused gear to full (GearState.ComposeRepairCost
//        and ComposeRepairHours, shown in the info panel). Always known.
//   Y  — use the item.
//   RT — salvage it into the stash when it can be salvaged, otherwise drop it at
//        the maker's feet. A tap salvages one unit / drops the stack; hold to
//        choose how many.
[GlobalClass]
public partial class CraftingScreen : Control
{
	[Export] public GameClient gameClient;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private BackpackPanel _stashPanel;
	[Export] private CraftingPanel _craftingPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	// The A / primary button hint: Craft on a recipe, Remove on a queued job.
	[Export] private ButtonHint _buttonHintPrimary;
	[Export] private ButtonHint _hintUse;
	// Salvage or Drop — one button, labelled by what the focused item allows.
	[Export] private ButtonHint _hintDrop;
	[Export] private ButtonHint _hintRepair;
	[Export] private ItemCountPanel _countPanel;
	// How long Salvage / Drop must be held on a stack to choose how many.
	[Export(PropertyHint.Range, "0.1,3,0.05")] private float _holdSeconds = 0.5f;

	const string PrimaryHintAction = "ui_select";
	const string UseAction = "MenuTertiary";
	const string DropAction = "MenuQuinary";
	// The equip verb's button on the inventory screens.
	const string RepairAction = "MenuSecondary";

	enum ESide
	{
		None,
		Stash,
		// A slot of one of the maker's grids — belt, backpack or a sack.
		Carried,
		// The maker's equip slots; the index is the EInventorySlot.
		Equip,
	}

	readonly struct Slot
	{
		public readonly ESide side;
		// The maker's grid, for a Carried slot.
		public readonly Inventory.CarriedGrid grid;
		public readonly int index;
		public readonly ItemSlotPanel panel;

		public Slot(ESide side, int index, ItemSlotPanel panel, Inventory.CarriedGrid grid = null)
		{
			this.side = side;
			this.grid = grid;
			this.index = index;
			this.panel = panel;
		}

		public static readonly Slot None = new(ESide.None, -1, null);
		public bool IsNone => side == ESide.None;
	}

	Action _onClose;
	Player _maker;
	CraftingBench _bench;

	// The item slot under the cursor. Its panel is checked for focus at each
	// press, so a cursor moved elsewhere acts on nothing.
	Slot _focused = Slot.None;
	// The recipe or queued job under the cursor, when it isn't an item slot.
	ItemData _focusedRecipe;
	int _focusedJob = -1;
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
			_inventoryPanel.onGridSlotFocused += OnCarriedFocused;
			foreach (EInventorySlot equip in InventoryPanel.EquipSlots)
			{
				ItemSlotPanel panel = _inventoryPanel.EquipPanel(equip);
				if (panel != null)
				{
					var slot = new Slot(ESide.Equip, (int)equip, panel);
					panel.onFocusEntered += _ => OnSlotFocused(slot);
				}
			}
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused += OnStashFocused;
		}
		if (_craftingPanel != null)
		{
			_craftingPanel.onRecipeSelected += OnRecipeSelected;
			_craftingPanel.onRecipeFocused += OnRecipeFocused;
			_craftingPanel.onQueueFocused += OnQueueFocused;
			_craftingPanel.onQueuePressed += OnQueuePressed;
		}
		_itemInfoPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onGridSlotFocused -= OnCarriedFocused;
		}
		if (_stashPanel != null)
		{
			_stashPanel.onSlotFocused -= OnStashFocused;
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
		_drop.Arm(_hintDrop);
		_craftingPanel?.HideAnnouncement();
		_paintedJobCount = -1;
		Visible = true;
		// Deferred so the just-shown nodes are visible-in-tree (GrabFocus needs
		// that) and so it runs after the visibility-change refresh has rebuilt the
		// recipe list.
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	// The initial keyboard/gamepad focus, in priority order: a recipe that can be
	// queued now, otherwise the first item in the backpack, otherwise the first
	// item anywhere else carried or stashed.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		if (_craftingPanel != null && _craftingPanel.GrabFirstAvailableRecipeFocus())
		{
			return;
		}
		ItemSlotPanel firstItem = _inventoryPanel?.BackpackGrid?.FirstOccupied() ?? _inventoryPanel?.BeltGrid?.FirstOccupied() ?? _stashPanel?.FirstOccupied();
		firstItem?.GrabFocus();
	}

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
		_inventoryPanel?.Paint(_maker?.Inventory);
		_stashPanel?.Refresh(Stash?.Slots);
	}

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

	void OnCarriedFocused(Inventory.CarriedGrid grid, int index, ItemSlotPanel panel)
	{
		OnSlotFocused(new Slot(ESide.Carried, index, panel, grid));
	}

	void OnStashFocused(int index, ItemSlotPanel panel)
	{
		OnSlotFocused(new Slot(ESide.Stash, index, panel));
	}

	void OnSlotFocused(Slot slot)
	{
		ClearFocus();
		_focused = slot;
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
		_drop.Reset(_hintDrop);
	}

	IItemGrid Grid(Slot slot)
	{
		return slot.side switch
		{
			ESide.Stash => Stash,
			ESide.Carried => slot.grid,
			_ => null,
		};
	}

	// Null while the cursor is anywhere but an item slot.
	ItemState FocusedItem()
	{
		if (_focused.IsNone || _focused.panel == null || !_focused.panel.HasButtonFocus())
		{
			return null;
		}
		return _focused.side == ESide.Equip
			? _maker?.Inventory?.GetEquipped((EInventorySlot)_focused.index)
			: Grid(_focused)?.At(_focused.index);
	}

	CraftJob FocusedJob()
	{
		CraftQueue queue = Queue;
		return queue != null && _focusedJob >= 0 && _focusedJob < queue.Count ? queue.Jobs[_focusedJob] : null;
	}

	// The info panel for whatever is focused, with what it requires and takes:
	// a recipe's inputs and hours, a repair's share of both, a queued job's paid
	// cost and hours.
	void ShowFocused()
	{
		if (_itemInfoPanel == null)
		{
			return;
		}
		if (_focusedRecipe != null)
		{
			CraftRecipeData recipe = _focusedRecipe.recipe;
			ItemState made = _focusedRecipe.CreateState();
			made.SetCount(_focusedRecipe.IsStackable ? recipe?.count ?? 1 : 1);
			// The recipe is learned, so its output reads identified.
			_itemInfoPanel.SetItem(made, forceIdentified: true, reagents: recipe?.inputs, hours: recipe?.hours ?? -1.0);
			return;
		}
		if (_focusedJob >= 0)
		{
			CraftJob job = FocusedJob();
			_itemInfoPanel.SetItem(job?.Preview(), forceIdentified: job != null && !job.IsRepair, reagents: job != null ? PaidRows(job) : null, hours: job?.hours ?? -1.0);
			return;
		}
		ItemState item = FocusedItem();
		if (item is GearState { IsRepairable: true } gear)
		{
			_itemInfoPanel.SetItem(item, reagents: gear.ComposeRepairCost(), hours: gear.ComposeRepairHours());
			return;
		}
		_itemInfoPanel.SetItem(item);
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

	GearState FocusedRepairable()
	{
		return FocusedItem() is GearState { IsRepairable: true } gear ? gear : null;
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
		if (!CanQueueRepair(gear))
		{
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
		if (slot.side == ESide.Equip)
		{
			_maker.Inventory.Remove(gear);
		}
		else
		{
			Grid(slot)?.Take(slot.index, 1);
		}
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
		ShowHint(_buttonHintPrimary, PrimaryHintAction, primary);
		ShowHint(_hintUse, UseAction, InventoryScreen.UseVerb(_maker, item));
		string dropVerb = item == null ? null : CanSalvage(item) ? Loc.Get(Loc.Keys.item_salvage) : Loc.Get(Loc.Keys.stash_drop);
		ShowHint(_hintDrop, DropAction, dropVerb);
		ShowHint(_hintRepair, RepairAction, CanQueueRepair(FocusedRepairable()) ? Loc.Get(Loc.Keys.item_repair) : null);
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
		InventoryScreen.Use(_maker, FocusedItem(), _focused.side == ESide.Stash ? Stash : null);
		OnItemsChanged();
	}

	// Salvage `count` units into the stash — all their yields or nothing.
	void Salvage(Slot slot, int count)
	{
		IItemGrid from = slot.side == ESide.Equip ? null : Grid(slot);
		if (!ItemTransfer.SalvageInto(from, slot.index, count, _maker, Stash))
		{
			_craftingPanel?.ShowAnnouncement(Loc.Get(Loc.Keys.stash_no_space), null);
			return;
		}
		_maker.Inventory?.NotifyChanged();
		Stash?.NotifyChanged();
	}

	void Drop(Slot slot, int count)
	{
		if (slot.side == ESide.Equip)
		{
			_maker?.Inventory?.Drop(_maker.Inventory.GetEquipped((EInventorySlot)slot.index));
		}
		else if (count > 0)
		{
			_maker?.DropAtFeet(Grid(slot)?.Take(slot.index, count));
		}
		OnItemsChanged();
	}

	// A tap salvages one unit, or drops the whole stack; a hold on a stack asks
	// how many.
	void TickDropHold(float delta)
	{
		ItemState item = FocusedItem();
		Slot slot = _focused;
		PolledHold.EResult press = _drop.Tick(item != null, delta, _holdSeconds, _hintDrop);
		if (press == PolledHold.EResult.None)
		{
			return;
		}
		bool salvage = CanSalvage(item);
		if (press == PolledHold.EResult.Hold && item.stackCount > 1)
		{
			string prompt = salvage ? Loc.Get(Loc.Keys.item_salvage_how_many) : Loc.Get(Loc.Keys.item_drop_how_many);
			_countPanel?.Open(item.stackCount, count =>
			{
				if (salvage)
				{
					Salvage(slot, count);
				}
				else
				{
					Drop(slot, count);
				}
			}, prompt: prompt);
			return;
		}
		if (salvage)
		{
			Salvage(slot, 1);
		}
		else
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
			TickDropHold((float)delta);
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
		if (e.IsActionPressed(RepairAction) && FocusedRepairable() != null)
		{
			Repair();
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
