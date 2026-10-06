using Godot;
using System;
using System.Collections.Generic;

// Cook tab of the camp screen. Left = the forge's experimentation slots + commit
// button (CookingPanel); center = the cook's backpack (BackpackPanel over
// Inventory.Backpack), whose materials are the ingredient source.
//
// Cooking is INSTANT and per-character — there is no cook job or timer. Eating a
// meal applies its recipe's status effect to the chosen character (replacing any
// prior meal) and returns to the camp hub (onMealChosen). Two paths eat:
//   * Recipe list: tapping a discovered, affordable recipe spends its reagents
//     from the stash and eats it.
//   * Experimentation: tap materials into the slots and press Cook — the slot
//     contents are consumed instantly. A valid match discovers the recipe and
//     eats it; a failed mix is just wasted ("Yuck").
[GlobalClass]
public partial class CookingScreen : Control
{
	[Export] public GameClient gameClient;
	// The cook's backpack, slot for slot; only its materials can be cooked.
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
	// Fires when the chosen character eats a meal (recipe picked, or a successful
	// experimental cook). CampScreen returns to the hub in response.
	Action _onMealChosen;
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
			_cookingPanel.onRecipeSelected -= OnRecipeSelected;
		}
	}

	void OnPlayerSpawned(Player player)
	{
		_player = player;
	}

	public void Open(Player player, Campfire forge = null, Action onClose = null, Action onMealChosen = null)
	{
		if (player != null)
		{
			_player = player;
		}
		_forge = forge;
		_onClose = onClose;
		_onMealChosen = onMealChosen;
		_cookingPanel?.HideAnnouncement();
		Visible = true;
		// Auto-highlight priority: a recipe the party can currently cook, else the
		// first available ingredient, else the commit button. Deferred so the just-
		// shown nodes are visible-in-tree (GrabFocus needs that) and so it runs after
		// the visibility-change refresh has rebuilt the recipe list / repainted the
		// stash.
		Callable.From(ApplyInitialFocus).CallDeferred();
	}

	// Choose the initial keyboard/gamepad focus when the tab opens, in priority
	// order: a recipe the party can currently cook, otherwise the first available
	// ingredient, otherwise the Cook button.
	void ApplyInitialFocus()
	{
		if (!Visible)
		{
			return;
		}
		if (_cookingPanel != null && _cookingPanel.GrabFirstAvailableRecipeFocus())
		{
			return;
		}
		ItemSlotPanel firstIngredient = _backpackPanel?.FirstOccupied();
		if (firstIngredient != null)
		{
			firstIngredient.GrabFocus();
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
		_forge = null;
		Visible = false;
		_onMealChosen = null;
		Action cb = _onClose;
		_onClose = null;
		cb?.Invoke();
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			if (_forge != null)
			{
				_cookingPanel?.Bind(_forge.CampfireSlots);
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

	void RefreshMaterials()
	{
		_backpackPanel?.Refresh(CookInventory?.Backpack);
	}

	void RefreshRecipeList()
	{
		if (_cookingPanel == null || _forge == null || _player == null)
		{
			return;
		}
		SimData simData = _player.Sim?.SimData;
		SimState worldSim = WorldSim;
		if (simData == null)
		{
			return;
		}
		var materials = new List<ItemState>(_player.CarriedMaterials());
		_cookingPanel.RefreshRecipes(simData.recipes, worldSim, materials, _forge.CampfireType);
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

	// Move up to `count` units of the material at backpack `index` into the
	// cooking slots.
	void CookMaterial(int index, int count)
	{
		Inventory inv = CookInventory;
		if (inv == null || index < 0 || index >= inv.Backpack.Count || _cookingPanel == null)
		{
			return;
		}
		ItemState src = inv.Backpack[index];
		if (src?.data == null || !src.data.IsMaterial)
		{
			return;
		}
		int requested = Mathf.Min(count, src.stackCount);
		int placed = _cookingPanel.TryAdd(src, requested);
		if (placed <= 0)
		{
			return;
		}
		src.Consume(placed);
		if (src.stackCount <= 0)
		{
			inv.Remove(src);
		}
		else
		{
			inv.NotifyChanged();
		}
		RefreshMaterials();
		RefreshRecipeList();
		UpdatePrimaryHint();
	}

	// ---- Cooking side (return to the backpack) ------------------------------

	void OnCookingFocusChanged(ItemSlotPanel panel, ItemState item)
	{
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

	// Recipe list tap: eat this discovered recipe now — pay its reagents from the
	// backpack and apply its effect to the chosen character — then return to the hub.
	// An unaffordable recipe's button is disabled upstream, so a spend that still
	// fails here just declines silently.
	void OnRecipeSelected(RecipeData recipe)
	{
		SimState worldSim = WorldSim;
		if (recipe == null || worldSim == null || !worldSim.IsRecipeDiscovered(recipe))
		{
			return;
		}
		if (_player == null || !_player.SpendReagents(recipe.inputs))
		{
			return;
		}
		EatMeal(recipe);
	}

	// ---- Cook button -------------------------------------------------------

	// Instant experimentation cook: the loaded slot contents are consumed either
	// way. A valid match discovers the recipe (no separate "unidentified" phase)
	// AND eats it, returning to the hub; a failed mix is wasted ("Yuck") and stays.
	void OnCookCommit()
	{
		if (_cookingPanel == null || _player == null || _forge == null)
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
		Cooking.MatchResult match = Cooking.TryMatch(_cookingPanel.Inputs, simData.recipes, _forge.CampfireType);
		SimState worldSim = WorldSim;
		_cookingPanel.DrainInputs();
		if (!match.IsValid || worldSim == null)
		{
			_cookingPanel.ShowAnnouncement("Cooking failed: Yuck!", null);
			UpdatePrimaryHint();
			return;
		}
		if (worldSim.DiscoverRecipe(match.recipe))
		{
			// Learned AT the campfire, so commit it to the shared party pool right away
			// (the same bank a camp visit does) — campfire knowledge is party knowledge,
			// visible to every character. Discovery otherwise lands only in the active
			// member's provisional store.
			worldSim.BankActiveKnowledge();
		}
		EatMeal(match.recipe);
	}

	// Apply a recipe's meal effect to the chosen character (replacing any prior
	// meal) and hand back to CampScreen, which returns to the hub. Reagents are
	// already spent by the caller. Meal effects are marked EEffectCategory.Meal, so
	// RemoveMealStatusEffects clears whatever they last ate before the new one lands.
	void EatMeal(RecipeData recipe)
	{
		if (recipe?.statusEffects != null && _player != null)
		{
			_player.RemoveMealStatusEffects();
			foreach (StatusEffectData effect in recipe.statusEffects)
			{
				if (effect != null)
				{
					_player.AddStatusEffect(effect);
				}
			}
		}
		_onMealChosen?.Invoke();
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
		if (_cookingPanel == null || _forge == null)
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

	// The units came out of this backpack, so they fit back unless it was
	// rearranged meanwhile; any that don't are dropped at the cook's feet.
	void ReturnToBackpack(ItemState item)
	{
		Inventory inv = CookInventory;
		if (inv == null || item == null)
		{
			return;
		}
		int wanted = item.stackCount;
		if (inv.TryAdd(item) < wanted)
		{
			_player.Sim?.DropItem(item, _player.GlobalPosition + Vector3.Up * 0.5f, Vector3.Zero, requireInteract: true);
		}
	}
}
