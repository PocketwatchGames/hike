using System.Collections.Generic;
using Godot;
using Godot.Collections;

// Recipe tab rendered inside AlmanacScreen. Lists every recipe the player
// has discovered this run (WorldSimState.DiscoveredRecipes) — one row per
// known output tier (standard always, high-quality only after the player
// has crafted it). Focusing a row populates the item info panel with the
// output and the ingredient slots with the recipe's authored inputs.
//
// View only — recipes are discovered by actually cooking at a forge
// (CookingScreen / Forge), not from this screen. The Almanac wrapper owns
// InputSuppressed / hud-visibility / ui_cancel handling; this screen just
// rebuilds when its tab is shown.
[GlobalClass]
public partial class RecipeScreen : Control
{
	GameClient _gameClient;
	[Export] PackedScene _recipeButtonScene;
	[Export] Control _recipeListContainer;
	[Export] ItemInfoPanel _itemInfoPanel;
	[Export] Control _recipePanel;
	[Export] Array<ItemSlotPanel> _ingredientSlots;
	[Export] Label _noRecipesLabel;

	public void Initialize(GameClient gameClient)
	{
		_gameClient = gameClient;
	}

	public override void _Ready()
	{
		VisibilityChanged += OnVisibilityChanged;
		ShowRecipeDetail(null, false);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Rebuild();
		}
	}

	// Walk SimData.Recipes, keep only the ones the player has discovered,
	// and stamp out one button per known output tier. Standard-quality buttons
	// always appear once a recipe is discovered; high-quality buttons only
	// once Cooking.RecordDiscovery has flipped discoveredHighQuality. The
	// container also owns the "No Recipes Discovered!" label as a sibling
	// child — we only free Button-typed children so the label survives.
	void Rebuild()
	{
		if (_recipeListContainer == null)
		{
			return;
		}
		foreach (Node child in _recipeListContainer.GetChildren())
		{
			if (child is Button)
			{
				child.QueueFree();
			}
		}

		SimData simData = _gameClient?.World?.SimData;
		WorldSimState worldSim = _gameClient?.World?.WorldState?.SimState;
		Button firstButton = null;
		RecipeData firstRecipe = null;
		bool firstIsHQ = false;
		if (simData != null && worldSim != null && simData.Recipes != null)
		{
			for (int i = 0; i < simData.Recipes.Count; i++)
			{
				RecipeData recipe = simData.Recipes[i];
				if (recipe == null)
				{
					continue;
				}
				if (!worldSim.DiscoveredRecipes.TryGetValue(recipe, out DiscoveredRecipeState state))
				{
					continue;
				}
				if (recipe.outputStandard != null)
				{
					Button b = CreateRecipeButton(recipe, false);
					if (firstButton == null) { firstButton = b; firstRecipe = recipe; firstIsHQ = false; }
				}
				if (state.discoveredHighQuality && recipe.outputHighQuality != null)
				{
					Button b = CreateRecipeButton(recipe, true);
					if (firstButton == null) { firstButton = b; firstRecipe = recipe; firstIsHQ = true; }
				}
			}
		}

		bool any = firstButton != null;
		if (_noRecipesLabel != null)
		{
			_noRecipesLabel.Visible = !any;
		}
		if (any)
		{
			// Populate the right-hand detail synchronously so the panel shows
			// the first recipe immediately. The deferred GrabFocus below moves
			// keyboard focus onto the button at end-of-frame; relying on its
			// FocusEntered signal to fill the panel would leave a blank state
			// in the meantime (and didn't reliably fire at all when the screen
			// was opened straight onto this tab).
			ShowRecipeDetail(firstRecipe, firstIsHQ);
			firstButton.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			ShowRecipeDetail(null, false);
		}
	}

	Button CreateRecipeButton(RecipeData recipe, bool isHighQuality)
	{
		if (_recipeButtonScene == null || _recipeListContainer == null)
		{
			return null;
		}
		ItemData output = isHighQuality ? recipe.outputHighQuality : recipe.outputStandard;
		if (output == null)
		{
			return null;
		}
		Button button = _recipeButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		button.Text = output.displayName.ToString();
		button.Icon = output.inventorySprite;
		RecipeData capturedRecipe = recipe;
		bool capturedHQ = isHighQuality;
		button.FocusEntered += () => ShowRecipeDetail(capturedRecipe, capturedHQ);
		// Mouse hover grabs focus so the right-hand info / ingredient view
		// tracks the cursor the same way D-pad navigation does.
		button.MouseEntered += button.GrabFocus;
		_recipeListContainer.AddChild(button);
		return button;
	}

	// Bind the right-hand info panel and ingredient slots to a single recipe
	// row. recipe = null clears everything (used at construction and when no
	// recipes are discovered).
	void ShowRecipeDetail(RecipeData recipe, bool isHighQuality)
	{
		ItemData output = recipe == null
			? null
			: (isHighQuality ? recipe.outputHighQuality : recipe.outputStandard);
		if (output != null)
		{
			ItemState state = output.CreateState();
			state.stackCount = 1;
			_itemInfoPanel?.SetItem(state);
		}
		else
		{
			_itemInfoPanel?.SetItem(null);
		}

		if (_recipePanel != null)
		{
			_recipePanel.Visible = recipe != null;
		}
		if (_ingredientSlots != null)
		{
			// Filter to ingredients the player has actually used in a
			// successful cook. An ingredient with no recorded minimum (or a
			// minimum of 0) is treated as undiscovered — typically an
			// optional ingredient the player has never tried — so it doesn't
			// appear at all, keeping the recipe's full shape a secret until
			// they experiment.
			DiscoveredRecipeState recipeState = null;
			if (recipe != null)
			{
				_gameClient?.World?.WorldState?.SimState?.DiscoveredRecipes?.TryGetValue(recipe, out recipeState);
			}
			var discovered = new List<RecipeInput>();
			if (recipe?.inputs != null)
			{
				for (int i = 0; i < recipe.inputs.Count; i++)
				{
					RecipeInput ri = recipe.inputs[i];
					if (ri?.item == null)
					{
						continue;
					}
					int min = 0;
					recipeState?.minSuccessfulIngredientCounts.TryGetValue(ri.item, out min);
					if (min > 0)
					{
						discovered.Add(ri);
					}
				}
			}
			for (int i = 0; i < _ingredientSlots.Count; i++)
			{
				ItemSlotPanel slot = _ingredientSlots[i];
				if (slot == null)
				{
					continue;
				}
				ItemState ingredient = null;
				if (i < discovered.Count)
				{
					RecipeInput ri = discovered[i];
					ingredient = ri.item.CreateState();
					ingredient.stackCount = ri.count;
				}
				slot.SetItem(ingredient);
			}
		}
	}
}
