using System.Collections.Generic;

// Per-recipe persistent progress for a single player run. Lives in
// WorldSimState.DiscoveredRecipes, keyed by the shared RecipeData resource.
public class DiscoveredRecipeState
{
	public bool discoveredHighQuality;

	// For each ingredient ItemData, the smallest input count that ever
	// successfully produced the standard-quality output. Lets the UI hint
	// at the lower bound the player has confirmed by trial.
	public readonly Dictionary<ItemData, int> minSuccessfulIngredientCounts = new();
}
