using Godot;
using System;
using System.Collections.Generic;

// Cook tab of the camp screen. Left = the forge's cooking slots + commit button
// (CookingPanel); center = the party MATERIAL stash (BackpackPanel over
// SimState.PartyMaterialStash), the ingredient source. Materials the party
// carried in are drained into that stash on camping (GameClient.NotifyCampedAt),
// so cooking always pulls from it, never from a carried backpack.
//
// Tap a material to move one unit into the cooking slots; tap a cooking slot to
// send one back. The cooked dish (Equipment) is delivered to the party equipment
// stash or a free hotbar slot — never the material backpack. CampScreen owns the
// global gating; this screen binds to the forge + stash and toggles visibility.
[GlobalClass]
public partial class CraftingScreen : Control
{
	[Export] public GameClient gameClient;
	// Ingredient source: the party material stash.
	[Export] private BackpackPanel _backpackPanel;
	[Export] private CookingPanel _cookingPanel;
	[Export] private ItemInfoPanel _itemInfoPanel;

	Action _onClose;
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

	public void Open(Player player, Campfire forge = null, Action onClose = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_forge = forge;
		_onClose = onClose;
		_cookingPanel?.HideAnnouncement();
		Visible = true;
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
		}
		else
		{
			_cookingPanel?.Unbind();
		}
	}

	// ---- Party stash accessors --------------------------------------------

	List<ItemState> MaterialStash => _player?.Sim?.WorldState?.SimState?.PartyMaterialStash;
	List<ItemState> EquipmentStash => _player?.Sim?.WorldState?.SimState?.PartyEquipmentStash;

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
		SimData simData = _player.Sim?.SimData;
		SimState worldSim = _player.Sim?.WorldState?.SimState;
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
		SimData simData = _player.Sim?.SimData;
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
	}

	// Delivered on cook completion while this screen is bound. Cooked dishes are
	// Equipment, so they go to a free hotbar slot or the party equipment stash —
	// never the material backpack; a material output goes to the material stash.
	void OnCookOutputDelivered(CampfireCompletion completion)
	{
		if (completion.output == null || _player == null)
		{
			return;
		}
		ItemState state = completion.output.CreateState();
		state.stackCount = 1;
		DeliverOutput(state);

		SimState worldSim = _player.Sim?.WorldState?.SimState;
		string outputName = worldSim != null
			? worldSim.GetItemDisplayName(completion.output)
			: completion.output.displayName.ToString();
		string text = completion.wasNewDiscovery
			? $"New Recipe Discovered: {outputName}"
			: $"Cooking Complete: {outputName}";
		_cookingPanel?.ShowAnnouncement(text, completion.output.inventorySprite);
		RefreshRecipeList();
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
		// Non-material output goes to the party equipment stash — there is no
		// consumable hotbar to deliver into anymore.
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
