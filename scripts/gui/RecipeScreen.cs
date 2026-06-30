using Godot;
using Godot.Collections;

// Recipe tab rendered inside AlmanacScreen. Lists every recipe the player
// has discovered this run (WorldSimState.DiscoveredRecipes) — one row per
// discovered recipe. Standard and high-quality variants of the same dish
// are separate RecipeData files, so each appears as its own row when
// learned. Focusing a row populates the item info panel with the output
// and the ingredient slots with the recipe's authored inputs.
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
		ShowRecipeDetail(null);
	}

	void OnVisibilityChanged()
	{
		if (Visible)
		{
			Rebuild();
		}
	}

	// Walk SimData.Recipes, keep only the ones the player has discovered,
	// and stamp out one button per recipe. The container also owns the
	// "No Recipes Discovered!" label as a sibling child — we only free
	// Button-typed children so the label survives.
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
		if (simData != null && worldSim != null && simData.recipes != null)
		{
			for (int i = 0; i < simData.recipes.Count; i++)
			{
				RecipeData recipe = simData.recipes[i];
				if (recipe == null || !worldSim.DiscoveredRecipes.Contains(recipe))
				{
					continue;
				}
				if (recipe.outputItem != null)
				{
					Button b = CreateRecipeButton(recipe);
					if (firstButton == null) { firstButton = b; firstRecipe = recipe; }
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
			ShowRecipeDetail(firstRecipe);
			firstButton.CallDeferred(Control.MethodName.GrabFocus);
		}
		else
		{
			ShowRecipeDetail(null);
		}
	}

	Button CreateRecipeButton(RecipeData recipe)
	{
		if (_recipeButtonScene == null || _recipeListContainer == null)
		{
			return null;
		}
		ItemData output = recipe.outputItem;
		if (output == null)
		{
			return null;
		}
		Button button = _recipeButtonScene.Instantiate<Button>();
		if (button == null)
		{
			return null;
		}
		WorldSimState worldSim = _gameClient?.World?.WorldState?.SimState;
		button.Text = worldSim != null
			? worldSim.GetItemDisplayName(output)
			: output.displayName.ToString();
		button.Icon = output.inventorySprite;
		RecipeData captured = recipe;
		button.FocusEntered += () => ShowRecipeDetail(captured);
		// Mouse hover grabs focus so the right-hand info / ingredient view
		// tracks the cursor the same way D-pad navigation does.
		button.MouseEntered += button.GrabFocus;
		_recipeListContainer.AddChild(button);
		return button;
	}

	// Bind the right-hand info panel and ingredient slots to a single recipe
	// row. recipe = null clears everything (used at construction and when no
	// recipes are discovered).
	void ShowRecipeDetail(RecipeData recipe)
	{
		ItemData output = recipe?.outputItem;
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
			for (int i = 0; i < _ingredientSlots.Count; i++)
			{
				ItemSlotPanel slot = _ingredientSlots[i];
				if (slot == null)
				{
					continue;
				}
				ItemState ingredient = null;
				if (recipe?.inputs != null && i < recipe.inputs.Count)
				{
					RecipeInput ri = recipe.inputs[i];
					if (ri?.item != null && ri.count > 0)
					{
						ingredient = ri.item.CreateState();
						ingredient.stackCount = ri.count;
					}
				}
				slot.SetItem(ingredient);
			}
		}
	}
}
