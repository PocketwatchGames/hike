using Godot;
using System;
using System.Collections.Generic;

// Modal cooking station. Wraps an InventoryPanel (the player's bag) and a
// CookingPanel (the forge's input slots + commit button + progress bar),
// binds both to the forge passed into Open(), and owns the per-verb
// callbacks for both panels:
//
//   InventoryPanel side — primary = Cook (tap moves 1 to the forge's
//   cooking slots, hold opens the count picker), Drop unchanged, Secondary
//   hidden.
//
//   CookingPanel side — primary = Remove (tap returns 1 to inventory, hold
//   opens the count picker). Cook button toggles between starting a cook
//   job and cancelling the active one.
//
// The forge owns the persistent cooking state (ForgeSimState.ForgeSlots
// and ActiveForgeJob); this screen is a thin view + verb dispatcher over
// that. On close:
//   * If a cook job is in flight, the forge keeps the slots (consumed on
//     completion, which may now happen offscreen — Forge.CompleteForgeJob
//     drops the output as Loot at the forge when no screen is bound).
//   * If idle, every populated slot is returned to the player's inventory
//     (or dropped at the player's feet if no space).
[GlobalClass]
public partial class CookingScreen : Control
{
	[Export] public GameClient gameClient;
	[Export] private InventoryPanel _inventoryPanel;
	[Export] private CookingPanel _cookingPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	[Export] private DropCountPanel _dropCountPanel;

	Action _onClose;
	Player _player;
	// The forge (if any) we're currently bound to. Null until Open() is
	// called with a Forge reference; cleared on Close().
	Forge _forge;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onPrimaryTap += OnInventoryCookTap;
			_inventoryPanel.onPrimaryHoldComplete += OnInventoryCookHold;
			_inventoryPanel.onTertiaryPressed += OnInventoryUsePressed;
			_inventoryPanel.onTertiaryReleased += OnInventoryUseReleased;
			_inventoryPanel.onSecondaryTap += OnInventoryDropTap;
			_inventoryPanel.onSecondaryHoldComplete += OnInventoryDropHold;
			_inventoryPanel.onFocusedItemChanged += OnInventoryFocusChanged;

			_inventoryPanel.ButtonHintPrimary?.SetHint(_inventoryPanel.PrimaryAction, "Cook");
			_inventoryPanel.ButtonHintSecondary?.SetHint(_inventoryPanel.SecondaryAction, "Drop");
			_inventoryPanel.ButtonHintTertiary?.SetHint(_inventoryPanel.TertiaryAction, "Use");
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.ButtonHintPrimary = _inventoryPanel?.ButtonHintPrimary;
			_cookingPanel.onPrimaryTap += OnCookingRemoveTap;
			_cookingPanel.onPrimaryHoldComplete += OnCookingRemoveHold;
			_cookingPanel.onFocusedItemChanged += OnCookingFocusChanged;
			_cookingPanel.onCookPressed += OnCookCommit;
			_cookingPanel.onCancelPressed += OnCookCancel;
			_cookingPanel.onRecipeSelected += OnRecipeSelected;
		}
		_itemInfoPanel?.SetItem(null);
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.onPrimaryTap -= OnInventoryCookTap;
			_inventoryPanel.onPrimaryHoldComplete -= OnInventoryCookHold;
			_inventoryPanel.onTertiaryPressed -= OnInventoryUsePressed;
			_inventoryPanel.onTertiaryReleased -= OnInventoryUseReleased;
			_inventoryPanel.onSecondaryTap -= OnInventoryDropTap;
			_inventoryPanel.onSecondaryHoldComplete -= OnInventoryDropHold;
			_inventoryPanel.onFocusedItemChanged -= OnInventoryFocusChanged;
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.onPrimaryTap -= OnCookingRemoveTap;
			_cookingPanel.onPrimaryHoldComplete -= OnCookingRemoveHold;
			_cookingPanel.onFocusedItemChanged -= OnCookingFocusChanged;
			_cookingPanel.onCookPressed -= OnCookCommit;
			_cookingPanel.onCancelPressed -= OnCookCancel;
			_cookingPanel.onRecipeSelected -= OnRecipeSelected;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
	}

	// Cook tab of the camp screen. CampScreen owns all global gating
	// (InputSuppressed, HUD, mouse, camp pose) — this screen just binds to the
	// forge and toggles its own visibility, like an AlmanacScreen sub-screen.
	public void Open(Player player, Forge forge = null, Action onClose = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_forge = forge;
		_onClose = onClose;
		// Make sure a stale announcement from a prior cook isn't visible on
		// re-open.
		_cookingPanel?.HideAnnouncement();
		Visible = true;
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		// Idle close: items return to the player; mid-cook close: leave them
		// in the forge for the running job to consume. Either way clear the
		// deliveryCallback so any completion that happens after this frame
		// drops loot at the forge instead of trying to push into a detached
		// inventory.
		ReturnInputsIfIdle();
		DetachFromForge();
		Visible = false;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			_inventoryPanel?.Bind(_player);
			AttachToForge();
		}
		else
		{
			_inventoryPanel?.Unbind();
			_cookingPanel?.Unbind();
			CloseCountPanel();
		}
	}

	void AttachToForge()
	{
		if (_forge == null || _cookingPanel == null)
		{
			return;
		}
		_cookingPanel.Bind(_forge.ForgeSlots);
		_forge.deliveryCallback = OnCookOutputDelivered;
		_forge.onForgeJobChanged += OnForgeJobChanged;
		// Recipe-list disabled states track inventory contents — subscribe
		// so an inventory mutation (item taken into slots, output delivered,
		// drop, etc.) immediately re-evaluates which buttons are clickable.
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged += OnInventoryChanged;
		}
		// Seed the panel with the current job state so a re-open mid-cook
		// shows the in-progress bar immediately.
		OnForgeJobChanged(_forge.ActiveForgeJob);
		RefreshRecipeList();
	}

	void DetachFromForge()
	{
		if (_forge == null)
		{
			return;
		}
		_forge.deliveryCallback = null;
		_forge.onForgeJobChanged -= OnForgeJobChanged;
		if (_player?.Inventory != null)
		{
			_player.Inventory.onChanged -= OnInventoryChanged;
		}
		_forge = null;
	}

	void OnInventoryChanged()
	{
		RefreshRecipeList();
	}

	void RefreshRecipeList()
	{
		if (_cookingPanel == null || _forge == null || _player == null)
		{
			return;
		}
		SimData simData = _player.World?.SimData;
		WorldSimState worldSim = _player.World?.WorldState?.SimState;
		if (simData == null)
		{
			return;
		}
		_cookingPanel.RefreshRecipes(simData.recipes, worldSim, _player.Inventory, _forge.ForgeType);
	}

	// A recipe button was clicked. Reconcile the cooking slots with the
	// recipe target in-place: items already in the right slots stay put
	// (so the user doesn't see them flash out and back in), excess and
	// off-recipe contents return to the inventory, and any deficit pulls
	// fresh stock from the inventory. Loads the recipe's exact authored
	// count for each required ingredient — the matcher accepts anything
	// inside [count - range, count + range], so the target count is
	// always a valid match for the recipe that produced it.
	void OnRecipeSelected(RecipeData recipe)
	{
		if (recipe == null || recipe.inputs == null || _cookingPanel == null || _player?.Inventory == null || _forge == null || IsCooking)
		{
			return;
		}
		var remaining = new System.Collections.Generic.Dictionary<ItemData, int>();
		for (int i = 0; i < recipe.inputs.Count; i++)
		{
			RecipeInput input = recipe.inputs[i];
			if (input?.item == null)
			{
				continue;
			}
			int needed = input.count;
			if (needed > 0)
			{
				remaining[input.item] = needed;
			}
		}

		// Pass 1: walk slots — keep up to `remaining[kind]` per slot, return
		// the rest. Decrement remaining for what we kept so a second slot of
		// the same kind only keeps what's still wanted.
		IReadOnlyList<ItemState> slots = _cookingPanel.Inputs;
		for (int i = 0; i < slots.Count; i++)
		{
			ItemState s = slots[i];
			if (s?.data == null || s.stackCount <= 0)
			{
				continue;
			}
			remaining.TryGetValue(s.data, out int wanted);
			int keep = Mathf.Min(s.stackCount, wanted);
			int excess = s.stackCount - keep;
			if (excess > 0)
			{
				ItemState removed = _cookingPanel.TryRemove(i, excess);
				if (removed != null)
				{
					AddOrDropAtPlayer(removed);
				}
			}
			if (wanted > 0)
			{
				remaining[s.data] = wanted - keep;
			}
		}

		// Pass 2: pull deficits from the player's inventory into the slots.
		foreach (var kv in remaining)
		{
			if (kv.Value > 0)
			{
				LoadIngredientIntoSlots(kv.Key, kv.Value);
			}
		}
		RefreshRecipeList();
	}

	// Move up to `amount` units of `itemKind` from the player's inventory
	// into the cooking slots. Walks every matching stack the player owns
	// (backpack, hotbar, equipped — EnumerateAll covers all of them) and
	// stops as soon as the requested amount is placed or the slots refuse
	// more. A snapshot list is used because Inventory.Remove during the
	// walk would otherwise mutate the underlying collection. Donors are
	// sorted smallest-stack-first so partial stacks consolidate into the
	// cooking slot instead of cracking a fresh large stack open.
	void LoadIngredientIntoSlots(ItemData itemKind, int amount)
	{
		if (itemKind == null || amount <= 0 || _cookingPanel == null || _player?.Inventory == null)
		{
			return;
		}
		Inventory inventory = _player.Inventory;
		var donors = new List<ItemState>();
		foreach (ItemState s in inventory.EnumerateAll())
		{
			if (s?.data == itemKind && s.stackCount > 0)
			{
				donors.Add(s);
			}
		}
		donors.Sort((a, b) => a.stackCount.CompareTo(b.stackCount));
		for (int i = 0; i < donors.Count && amount > 0; i++)
		{
			ItemState s = donors[i];
			int take = Mathf.Min(amount, s.stackCount);
			int placed = _cookingPanel.TryAdd(s, take);
			if (placed <= 0)
			{
				break;
			}
			s.stackCount -= placed;
			amount -= placed;
			if (s.stackCount <= 0)
			{
				inventory.Remove(s);
			}
			else
			{
				inventory.NotifyChanged();
			}
		}
	}

	// CampScreen owns ui_cancel for closing; intercept it here only while the
	// count picker is open so the first cancel dismisses the picker rather than
	// the whole camp screen.
	public override void _UnhandledInput(InputEvent e)
	{
		if (!Visible)
		{
			return;
		}
		if (e.IsActionPressed("ui_cancel") && _dropCountPanel != null && _dropCountPanel.Visible)
		{
			CloseCountPanel();
			GetViewport().SetInputAsHandled();
		}
	}

	// -------------------------------------------------------------------
	// Inventory-side (Cook) verbs.
	// -------------------------------------------------------------------

	void OnInventoryFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanel?.SetItem(item);
		UpdateInventoryHints(item);
	}

	void UpdateInventoryHints(ItemState item)
	{
		if (_inventoryPanel == null)
		{
			return;
		}
		bool cooking = _forge != null && _forge.ActiveForgeJob != null;
		bool hasItem = item != null && !cooking;
		ButtonHint primary = _inventoryPanel.ButtonHintPrimary;
		ButtonHint secondary = _inventoryPanel.ButtonHintSecondary;
		ButtonHint tertiary = _inventoryPanel.ButtonHintTertiary;
		if (primary != null)
		{
			primary.Visible = hasItem;
			primary.ActionName = "Cook";
			primary.SetProgress(0f);
		}
		if (secondary != null)
		{
			secondary.Visible = hasItem;
			secondary.SetProgress(0f);
		}
		if (tertiary != null)
		{
			// Use is independent of the forge — drinking a potion mid-cook is
			// fine — so don't gate it on `cooking`.
			tertiary.Visible = item != null && CanUseItem(item);
			tertiary.ActionName = "Use";
			tertiary.SetProgress(0f);
		}
	}

	static bool CanUseItem(ItemState item)
	{
		return item is ConsumableState consumable && consumable.data?.actionProfile != null;
	}

	void OnInventoryCookTap(ItemSlotPanel panel, ItemState item)
	{
		CookOne(item, 1);
	}

	void OnInventoryCookHold(ItemSlotPanel panel, ItemState item)
	{
		if (item == null || _dropCountPanel == null || IsCooking)
		{
			return;
		}
		LockPanelFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => { CookOne(item, count); CloseCountPanel(); },
			onCancel: CloseCountPanel,
			prompt: "Cook how many?");
	}

	// Move up to `count` units of `item` from the player's inventory into
	// the forge's cooking slots (via the bound panel).
	void CookOne(ItemState item, int count)
	{
		if (item == null || count <= 0 || _cookingPanel == null || _player?.Inventory == null || IsCooking)
		{
			return;
		}
		int requested = Mathf.Min(count, item.stackCount);
		int placed = _cookingPanel.TryAdd(item, requested);
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
	}

	void OnInventoryDropTap(ItemSlotPanel panel, ItemState item)
	{
		if (item == null)
		{
			return;
		}
		_player?.Inventory?.Drop(item, 1);
	}

	void OnInventoryDropHold(ItemSlotPanel panel, ItemState item)
	{
		if (item == null || _dropCountPanel == null)
		{
			return;
		}
		Inventory inventory = _player?.Inventory;
		if (inventory == null)
		{
			return;
		}
		LockPanelFocus();
		_dropCountPanel.Visible = true;
		_dropCountPanel.Init(
			maxCount: item.stackCount,
			onConfirm: count => { if (count > 0) inventory.Drop(item, count); CloseCountPanel(); },
			onCancel: CloseCountPanel,
			prompt: "Drop how many?");
	}

	// -------------------------------------------------------------------
	// Cooking-side (Remove) verbs.
	// -------------------------------------------------------------------

	void OnCookingFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanel?.SetItem(item);
		UpdateInventoryHints(item);
		bool cooking = _forge != null && _forge.ActiveForgeJob != null;
		ButtonHint primary = _inventoryPanel?.ButtonHintPrimary;
		if (primary != null)
		{
			primary.ActionName = "Remove";
			primary.Visible = item != null && !cooking;
		}
		if (_inventoryPanel?.ButtonHintTertiary != null)
		{
			_inventoryPanel.ButtonHintTertiary.Visible = false;
		}
		// Use is only meaningful while focus is on the inventory panel; the
		// cooking slots can't hold consumables the player would drink.
		if (_inventoryPanel?.ButtonHintSecondary != null)
		{
			_inventoryPanel.ButtonHintSecondary.Visible = false;
		}
	}

	void OnInventoryUsePressed(ItemSlotPanel panel, ItemState item)
	{
		if (item is not ConsumableState consumable || _player == null)
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
		var context = new ActionContext
		{
			verb = EActionVerb.Use,
			primaryItem = item,
			sourceSlot = EInventorySlot.Consumable,
		};
		runner.TryStart(data.actionProfile, context);
	}

	void OnInventoryUseReleased()
	{
		_player?.Runner?.OnInputReleased();
	}

	// Mirror the HUD hotbar's charge-progress fill on the Use hint while the
	// runner is charging the focused consumable. Without this the player gets
	// no visual cue that Use is hold-to-fire. Same pattern as InventoryScreen.
	public override void _Process(double delta)
	{
		if (!Visible || _inventoryPanel == null)
		{
			return;
		}
		ButtonHint use = _inventoryPanel.ButtonHintTertiary;
		if (use == null || !use.Visible)
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
		if (action.phase != EActionPhase.Charging || action.context.primaryItem != _inventoryPanel.FocusedItem)
		{
			use.SetProgress(0f);
			return;
		}
		use.SetProgress(runner.CurrentChargeT);
	}

	void OnCookingRemoveTap(int index, ItemSlotPanel panel, ItemState item)
	{
		RemoveFromCooking(index, 1);
	}

	void OnCookingRemoveHold(int index, ItemSlotPanel panel, ItemState item)
	{
		if (item == null || _dropCountPanel == null || IsCooking)
		{
			return;
		}
		LockPanelFocus();
		_dropCountPanel.Visible = true;
		int max = item.stackCount;
		_dropCountPanel.Init(
			maxCount: max,
			onConfirm: count => { RemoveFromCooking(index, count); CloseCountPanel(); },
			onCancel: CloseCountPanel,
			prompt: "Remove how many?");
	}

	void RemoveFromCooking(int index, int count)
	{
		if (count <= 0 || _cookingPanel == null || _player?.Inventory == null || IsCooking)
		{
			return;
		}
		ItemState removed = _cookingPanel.TryRemove(index, count);
		if (removed == null)
		{
			return;
		}
		AddOrDropAtPlayer(removed);
	}

	// Try the inventory; drop the remainder at the player's feet.
	void AddOrDropAtPlayer(ItemState item)
	{
		if (item == null || item.data == null || item.stackCount <= 0 || _player == null)
		{
			return;
		}
		Inventory inventory = _player.Inventory;
		int initial = item.stackCount;
		int added = inventory?.TryAdd(item) ?? 0;
		if (added >= initial)
		{
			return;
		}
		int leftover = initial - added;
		ItemState dropClone = item.data.CreateState();
		dropClone.stackCount = leftover;
		_player.World?.DropItem(dropClone, _player.GlobalPosition + Vector3.Up * 0.5f, Vector3.Up * 1.5f, requireInteract: true);
	}

	// -------------------------------------------------------------------
	// Cook button — commit (start a job) or cancel (stop the active one).
	// -------------------------------------------------------------------

	bool IsCooking => _forge != null && _forge.ActiveForgeJob != null;

	void OnCookCommit()
	{
		if (_cookingPanel == null || _player == null || _forge == null || IsCooking)
		{
			return;
		}
		SimData simData = _player.World?.SimData;
		if (simData == null)
		{
			return;
		}
		IReadOnlyList<ItemState> inputs = _cookingPanel.Inputs;
		bool anyInputs = false;
		for (int i = 0; i < inputs.Count; i++)
		{
			if (inputs[i] != null && inputs[i].stackCount > 0) { anyInputs = true; break; }
		}
		if (!anyInputs)
		{
			return;
		}
		Cooking.MatchResult match = Cooking.TryMatch(inputs, simData.recipes, _forge.ForgeType);
		if (!match.IsValid)
		{
			// Failed cook — drain ingredients and announce the failure.
			// No timer / no produced item, so the announcement fires
			// immediately at commit time.
			_cookingPanel.DrainInputs();
			_cookingPanel.ShowAnnouncement("Cooking failed: Yuck!", null);
			return;
		}

		// Items remain in the slots — Forge.CompleteForgeJob drains them on
		// timer expiry, which lets Cancel preserve the inputs. Discovery
		// is recorded by the forge at completion so a cancel mid-cook
		// doesn't credit the recipe.
		_forge.StartForgeJob(match.recipe, match.OutputItem);
	}

	void OnCookCancel()
	{
		_forge?.CancelForgeJob();
	}

	// Forge fires this every physics tick while a job is in flight and once
	// with null when the job ends (complete OR cancel). Updates the cook
	// button label, progress bar, and the inventory-side hint visibilities.
	void OnForgeJobChanged(ForgeJob job)
	{
		if (_cookingPanel != null)
		{
			float progress = job?.Progress01 ?? 0f;
			_cookingPanel.SetCookingActive(job != null, progress);
			if (job == null)
			{
				// Cook ended — the forge cleared ForgeSlots on completion
				// (and left them alone on Cancel). Re-sync the slot visuals
				// to whatever the backing array now holds; on completion
				// this clears the stale icons in the input slots.
				_cookingPanel.Refresh();
			}
		}
		// Re-evaluate the inventory's button hints — Cook/Remove/Drop are
		// hidden while cooking is in flight.
		UpdateInventoryHints(_inventoryPanel?.FocusedItem);
	}

	// Forge deliveryCallback: called on completion only when this screen is
	// bound. Try to fit the output in the player's inventory; spill any
	// remainder onto the ground at the player's feet. Also fires the
	// "cooking complete" / "new recipe discovered" announcement.
	void OnCookOutputDelivered(ForgeCompletion completion)
	{
		if (completion.output == null || _player == null)
		{
			return;
		}
		ItemState state = completion.output.CreateState();
		state.stackCount = 1;
		AddOrDropAtPlayer(state);

		// Route through WorldSimState so the announcement shows "unknown food"
		// until the player has actually used one — discovering the recipe and
		// learning what it is are decoupled.
		WorldSimState worldSim = _player?.World?.WorldState?.SimState;
		string outputName = worldSim != null
			? worldSim.GetItemDisplayName(completion.output)
			: completion.output.displayName.ToString();
		string text = completion.wasNewDiscovery
			? $"New Recipe Discovered: {outputName}"
			: $"Cooking Complete: {outputName}";
		_cookingPanel?.ShowAnnouncement(text, completion.output.inventorySprite);
		// Forge.CompleteForgeJob just recorded the discovery — surface any
		// new recipe button right now. Inventory.onChanged from the output
		// add above already triggers this, but a full inventory (output
		// goes to drop) bypasses that path.
		RefreshRecipeList();
	}

	// -------------------------------------------------------------------
	// Misc plumbing.
	// -------------------------------------------------------------------

	void LockPanelFocus()
	{
		_inventoryPanel?.SetSlotsFocusable(false);
		_cookingPanel?.SetSlotsFocusable(false);
	}

	void CloseCountPanel()
	{
		if (_dropCountPanel != null)
		{
			_dropCountPanel.Visible = false;
		}
		if (_inventoryPanel != null)
		{
			_inventoryPanel.HoldLocked = false;
			_inventoryPanel.SetSlotsFocusable(true);
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.HoldLocked = false;
			_cookingPanel.SetSlotsFocusable(true);
		}
		if (_cookingPanel?.FocusedPanel != null)
		{
			_cookingPanel.RestoreFocus();
		}
		else
		{
			_inventoryPanel?.RestoreFocus();
		}
	}

	// Pull any populated slots back into the player's inventory — only
	// when no cook is in flight. Mid-cook slots stay (they're being
	// consumed); the forge's completion handler drops the produced item at
	// the forge if the screen isn't around to take it.
	void ReturnInputsIfIdle()
	{
		if (_cookingPanel == null || _player == null || _forge == null)
		{
			return;
		}
		if (_forge.ActiveForgeJob != null)
		{
			return;
		}
		List<ItemState> drained = _cookingPanel.DrainInputs();
		for (int i = 0; i < drained.Count; i++)
		{
			AddOrDropAtPlayer(drained[i]);
		}
	}
}
