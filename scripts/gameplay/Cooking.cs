using System.Collections.Generic;
using Godot;
using Godot.Collections;

// Pure recipe matcher. Given the current cooking inputs (any number of slots,
// each holding an ItemState or null), the master recipe list from SimData,
// and the forge type performing the cook, returns the first matching recipe
// along with whether the high-quality output should be produced. Match rules:
//   * recipe.forgeType must equal the supplied forgeType — recipes are
//     scoped to a station (e.g. cooking-only recipes never match at a
//     smelter).
//   * Every authored ingredient in the recipe must appear in the inputs with
//     a count inside [count + minCountRange, count + maxCountRange].
//   * The inputs must contain NO ingredient kinds outside the recipe.
//   * High-quality matches iff every ingredient's count equals exactly
//     recipe.count AND the recipe declares an outputHighQuality.
// Aggregates duplicate ingredient slots so two stacks of the same item count
// together as a single input bucket.
public static class Cooking
{
	public readonly struct MatchResult
	{
		public readonly RecipeData recipe;
		public readonly bool isHighQuality;
		public MatchResult(RecipeData recipe, bool isHighQuality)
		{
			this.recipe = recipe;
			this.isHighQuality = isHighQuality;
		}
		public bool IsValid => recipe != null;
		public ItemData OutputItem
		{
			get
			{
				if (recipe == null) { return null; }
				return isHighQuality && recipe.outputHighQuality != null
					? recipe.outputHighQuality
					: recipe.outputStandard;
			}
		}
	}

	public static MatchResult TryMatch(IReadOnlyList<ItemState> inputs, Array<RecipeData> recipes, EForgeType forgeType)
	{
		if (inputs == null || recipes == null || recipes.Count == 0)
		{
			return default;
		}
		var totals = new System.Collections.Generic.Dictionary<ItemData, int>();
		for (int i = 0; i < inputs.Count; i++)
		{
			ItemState s = inputs[i];
			if (s?.data == null || s.stackCount <= 0)
			{
				continue;
			}
			totals.TryGetValue(s.data, out int existing);
			totals[s.data] = existing + s.stackCount;
		}
		if (totals.Count == 0)
		{
			return default;
		}
		for (int r = 0; r < recipes.Count; r++)
		{
			RecipeData recipe = recipes[r];
			if (recipe?.inputs == null || recipe.inputs.Count == 0)
			{
				continue;
			}
			if (recipe.forgeType != forgeType)
			{
				continue;
			}
			if (recipe.inputs.Count != totals.Count)
			{
				// Player has the wrong number of ingredient kinds — extras
				// or missing buckets disqualify the recipe outright.
				continue;
			}
			bool inRange = true;
			bool exact = true;
			for (int i = 0; i < recipe.inputs.Count; i++)
			{
				RecipeInput ri = recipe.inputs[i];
				if (ri?.item == null)
				{
					inRange = false;
					break;
				}
				if (!totals.TryGetValue(ri.item, out int provided))
				{
					inRange = false;
					break;
				}
				int low = ri.count + ri.minCountRange;
				int high = ri.count + ri.maxCountRange;
				if (provided < low || provided > high)
				{
					inRange = false;
					break;
				}
				if (provided != ri.count)
				{
					exact = false;
				}
			}
			if (!inRange)
			{
				continue;
			}
			bool wantsHigh = exact && recipe.outputHighQuality != null;
			return new MatchResult(recipe, wantsHigh);
		}
		return default;
	}

	// Record discovery of a recipe / quality tier into WorldSimState. Updates
	// the per-ingredient minSuccessfulIngredientCounts so the UI can later
	// hint at the lower bound the player has confirmed by trial.
	public static void RecordDiscovery(WorldSimState sim, in MatchResult match, IReadOnlyList<ItemState> inputs)
	{
		if (sim == null || !match.IsValid)
		{
			return;
		}
		if (!sim.DiscoveredRecipes.TryGetValue(match.recipe, out DiscoveredRecipeState state))
		{
			state = new DiscoveredRecipeState();
			sim.DiscoveredRecipes[match.recipe] = state;
		}
		if (match.isHighQuality)
		{
			state.discoveredHighQuality = true;
		}
		if (inputs != null)
		{
			for (int i = 0; i < inputs.Count; i++)
			{
				ItemState s = inputs[i];
				if (s?.data == null || s.stackCount <= 0)
				{
					continue;
				}
				if (!state.minSuccessfulIngredientCounts.TryGetValue(s.data, out int prev) || s.stackCount < prev)
				{
					state.minSuccessfulIngredientCounts[s.data] = s.stackCount;
				}
			}
		}
	}
}
