using Godot;
using System;
using System.Collections.Generic;

// Cook tab of the camp screen. Left = the forge's cooking slots + commit button
// (CookingPanel); center = the party MATERIAL stash (BackpackPanel over
// WorldSimState.PartyMaterialStash), the ingredient source. Materials the party
// carried in are drained into that stash on camping (GameClient.NotifyCampedAt),
// so cooking always pulls from it, never from a carried backpack.
//
// Tap a material to move one unit into the cooking slots; tap a cooking slot to
// send one back. The cooked dish (Equipment) is delivered to the party equipment
// stash or a free hotbar slot — never the material backpack. CampScreen owns the
// global gating; this screen binds to the forge + stash and toggles visibility.
[GlobalClass]
public partial class CookingScreen : Control
{
	[Export] public GameClient gameClient;
	// Ingredient source: the party material stash.
	[Export] private BackpackPanel _backpackPanel;
	[Export] private CookingPanel _cookingPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;
	// The A / primary button hint. Its label tracks the commit button: "Cook"
	// while ingredients are loaded, "Continue" (leave camp) when the slots are
	// empty.
	[Export] private ButtonHint _buttonHintPrimary;

	// Glyph action driving the primary button hint (the A button); matches the
	// primary-verb convention used by the inventory-style screens.
	const string PrimaryHintAction = "ui_select";

	Action _onClose;
	// Fires once a cook job completes and its output is delivered (CampScreen uses
	// it to leave camp). Distinct from _onClose, which fires on tab teardown.
	Action _onCooked;
	// Fires when the player presses the primary button with the slots empty —
	// nothing to cook, so "Continue" out of camp. CampScreen wires it to Close.
	Action _onContinue;
	Player _player;
	Campfire _forge;

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned += OnPlayerSpawned;
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused += OnMaterialFocused;
			_backpackPanel.onSlotButtonUp += OnMaterialTap;
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.onPrimaryTap += OnCookingRemoveTap;
			_cookingPanel.onFocusedItemChanged += OnCookingFocusChanged;
			_cookingPanel.onCookPressed += OnCookCommit;
			_cookingPanel.onCancelPressed += OnCookCancel;
			_cookingPanel.onRecipeSelected += OnRecipeSelected;
		}
		_itemInfoPanel?.SetItem(null);
	}

	public override void _ExitTree()
	{
		if (gameClient != null)
		{
			gameClient.onPlayerSpawned -= OnPlayerSpawned;
		}
		if (_backpackPanel != null)
		{
			_backpackPanel.onSlotFocused -= OnMaterialFocused;
			_backpackPanel.onSlotButtonUp -= OnMaterialTap;
		}
		if (_cookingPanel != null)
		{
			_cookingPanel.onPrimaryTap -= OnCookingRemoveTap;
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

	public void Open(Player player, Campfire forge = null, Action onClose = null, Action onCooked = null, Action onContinue = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_forge = forge;
		_onClose = onClose;
		_onCooked = onCooked;
		_onContinue = onContinue;
		_cookingPanel?.HideAnnouncement();
		Visible = true;
		// Focus the commit button so the primary A action (Cook / Continue) works
		// immediately on gamepad; deferred so the just-shown node is visible-in-
		// tree when GrabFocus runs.
		_cookingPanel?.CallDeferred(CookingPanel.MethodName.GrabCookButtonFocus);
	}

	public void Close()
	{
		if (!Visible)
		{
			return;
		}
		ReturnInputsIfIdle();
		DetachFromCampfire();
		Visible = false;
		_onCooked = null;
		_onContinue = null;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			AttachToCampfire();
			RefreshMaterials();
			UpdatePrimaryHint();
		}
		else
		{
			_cookingPanel?.Unbind();
		}
	}

	// ---- Party stash accessors --------------------------------------------

	List<ItemState> MaterialStash => _player?.World?.WorldState?.SimState?.PartyMaterialStash;
	List<ItemState> EquipmentStash => _player?.World?.WorldState?.SimState?.PartyEquipmentStash;

	void RefreshMaterials()
	{
		_backpackPanel?.Refresh(MaterialStash);
	}

	// ---- Forge binding -----------------------------------------------------

	void AttachToCampfire()
	{
		if (_forge == null || _cookingPanel == null)
		{
			return;
		}
		_cookingPanel.Bind(_forge.CampfireSlots);
		_forge.deliveryCallback = OnCookOutputDelivered;
		_forge.onCampfireJobChanged += OnCampfireJobChanged;
		OnCampfireJobChanged(_forge.ActiveCampfireJob);
		RefreshRecipeList();
	}

	void DetachFromCampfire()
	{
		if (_forge == null)
		{
			return;
		}
		_forge.deliveryCallback = null;
		_forge.onCampfireJobChanged -= OnCampfireJobChanged;
		_forge = null;
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
		_cookingPanel.RefreshRecipes(simData.recipes, worldSim, MaterialStash, _forge.CampfireType);
	}

	// ---- Material side (add to cooking slots) ------------------------------

	void OnMaterialFocused(int index, ItemSlotPanel panel)
	{
		_itemInfoPanel?.SetItem(panel?.Item);
	}

	void OnMaterialTap(int index, ItemSlotPanel panel)
	{
		CookMaterial(index, 1);
	}

	// Move up to `count` units of the material at stash `index` into the cooking
	// slots.
	void CookMaterial(int index, int count)
	{
		List<ItemState> stash = MaterialStash;
		if (stash == null || index < 0 || index >= stash.Count || _cookingPanel == null || IsCooking)
		{
			return;
		}
		ItemState src = stash[index];
		if (src?.data == null)
		{
			return;
		}
		int requested = Mathf.Min(count, src.stackCount);
		int placed = _cookingPanel.TryAdd(src, requested);
		if (placed <= 0)
		{
			return;
		}
		src.stackCount -= placed;
		if (src.stackCount <= 0)
		{
			stash.RemoveAt(index);
		}
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// ---- Cooking side (return to material stash) ---------------------------

	void OnCookingFocusChanged(ItemSlotPanel panel, ItemState item)
	{
		_itemInfoPanel?.SetItem(item);
	}

	void OnCookingRemoveTap(int index, ItemSlotPanel panel, ItemState item)
	{
		if (_cookingPanel == null || IsCooking)
		{
			return;
		}
		ItemState removed = _cookingPanel.TryRemove(index, 1);
		if (removed == null)
		{
			return;
		}
		ItemStash.Add(MaterialStash, removed);
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// Reconcile the cooking slots to a clicked recipe: keep what's already right,
	// return the excess to the material stash, and pull any deficit from it.
	void OnRecipeSelected(RecipeData recipe)
	{
		if (recipe?.inputs == null || _cookingPanel == null || _forge == null || IsCooking)
		{
			return;
		}
		var remaining = new Dictionary<ItemData, int>();
		foreach (RecipeInput input in recipe.inputs)
		{
			if (input?.item != null && input.count > 0)
			{
				remaining[input.item] = input.count;
			}
		}
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
				ItemStash.Add(MaterialStash, removed);
			}
			if (wanted > 0)
			{
				remaining[s.data] = wanted - keep;
			}
		}
		foreach (var kv in remaining)
		{
			if (kv.Value > 0)
			{
				LoadIngredientFromStash(kv.Key, kv.Value);
			}
		}
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// Move up to `amount` units of `itemKind` from the material stash into the
	// cooking slots. Smallest stacks first so partials consolidate.
	void LoadIngredientFromStash(ItemData itemKind, int amount)
	{
		List<ItemState> stash = MaterialStash;
		if (itemKind == null || amount <= 0 || _cookingPanel == null || stash == null)
		{
			return;
		}
		var donors = new List<ItemState>();
		foreach (ItemState s in stash)
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
				stash.Remove(s);
			}
		}
	}

	// ---- Cook button -------------------------------------------------------

	bool IsCooking => _forge != null && _forge.ActiveCampfireJob != null;

	void OnCookCommit()
	{
		if (_cookingPanel == null || _player == null || _forge == null || IsCooking)
		{
			return;
		}
		// One cooked meal per character per day. Backstop — CampScreen already
		// withholds the Cook tab from a fed member, so this is normally unreachable.
		if (_player.Member != null && _player.Member.HasEatenToday)
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
			// Nothing loaded — the primary button reads "Continue"; leave camp.
			_onContinue?.Invoke();
			return;
		}
		Cooking.MatchResult match = Cooking.TryMatch(inputs, simData.recipes, _forge.CampfireType);
		if (!match.IsValid)
		{
			_cookingPanel.DrainInputs();
			_cookingPanel.ShowAnnouncement("Cooking failed: Yuck!", null);
			return;
		}
		_forge.StartCampfireJob(match.recipe, match.OutputItem);
	}

	void OnCookCancel()
	{
		_forge?.CancelCampfireJob();
	}

	void OnCampfireJobChanged(CampfireJob job)
	{
		if (_cookingPanel != null)
		{
			float progress = job?.Progress01 ?? 0f;
			_cookingPanel.SetCookingActive(job != null, progress);
			if (job == null)
			{
				_cookingPanel.Refresh();
			}
		}
		UpdatePrimaryHint();
	}

	// Reflect the primary (A) action on the commit button and its hint. While a
	// cook is in flight the button is "Cancel"; idle it flips between "Cook"
	// (ingredients loaded) and "Continue" (empty slots — a press leaves camp).
	void UpdatePrimaryHint()
	{
		string label = IsCooking ? "Cancel" : (HasAnyInput() ? "Cook" : "Continue");
		_buttonHintPrimary?.SetHint(PrimaryHintAction, label);
		if (!IsCooking)
		{
			_cookingPanel?.SetIdleLabel(HasAnyInput() ? "Cook!" : "Continue");
		}
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

	// Delivered on cook completion while this screen is bound. Cooked dishes are
	// eaten the instant they come off the fire — the cook consumes the output and
	// its effects apply immediately, rather than stocking the inventory. A
	// non-consumable output (or a busy action runner) falls back to delivery:
	// Equipment goes to a free hotbar slot or the party equipment stash, a
	// material output to the material stash.
	void OnCookOutputDelivered(CampfireCompletion completion)
	{
		if (completion.output == null || _player == null)
		{
			return;
		}
		ItemState state = completion.output.CreateState();
		state.stackCount = 1;
		if (_player.TryConsumeImmediately(state))
		{
			// The cook ate what they made — spend their one meal for the day (the
			// camp Cook tab is withheld from a fed member until the next sunrise).
			if (_player.Member != null)
			{
				_player.Member.HasEatenToday = true;
			}
		}
		else
		{
			DeliverOutput(state);
		}

		WorldSimState worldSim = _player.World?.WorldState?.SimState;
		string outputName = worldSim != null
			? worldSim.GetItemDisplayName(completion.output)
			: completion.output.displayName.ToString();
		string text = completion.wasNewDiscovery
			? $"New Recipe Discovered: {outputName}"
			: $"Cooking Complete: {outputName}";
		_cookingPanel?.ShowAnnouncement(text, completion.output.inventorySprite);
		RefreshRecipeList();
		// A dish came off the fire — hand back to CampScreen (leaves camp). Last, so
		// the screen's own bookkeeping finishes before any re-entrant teardown.
		_onCooked?.Invoke();
	}

	void DeliverOutput(ItemState state)
	{
		if (state?.data == null)
		{
			return;
		}
		if (state.data.IsMaterial)
		{
			ItemStash.Add(MaterialStash, state);
			RefreshMaterials();
			return;
		}
		Inventory inv = _player?.Inventory;
		if (state.data.Category == EItemCategory.Equipment && inv != null && inv.TryAddEquipmentToHotbar(state))
		{
			return;
		}
		ItemStash.Add(EquipmentStash, state);
	}

	// Pull the cooking slots back into the material stash — only when no cook is
	// in flight (mid-cook slots are consumed by the running job).
	void ReturnInputsIfIdle()
	{
		if (_cookingPanel == null || _forge == null || IsCooking)
		{
			return;
		}
		List<ItemState> drained = _cookingPanel.DrainInputs();
		for (int i = 0; i < drained.Count; i++)
		{
			ItemStash.Add(MaterialStash, drained[i]);
		}
		RefreshMaterials();
	}
}
