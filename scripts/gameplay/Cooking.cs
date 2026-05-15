using System.Collections.Generic;
using Godot.Collections;

// Pure recipe matcher. Given the current cooking inputs (any number of slots,
// each holding an ItemState or null), the master recipe list from SimData,
// and the forge type performing the cook, returns the most-specific matching
// recipe. Match rules:
//   * recipe.forgeType must equal the supplied forgeType — recipes are
//     scoped to a station (e.g. cooking-only recipes never match at a
//     smelter).
//   * Every authored ingredient must satisfy the provided count being inside
//     [count - range, count + range] (low bound clamped at 0). An ingredient
//     is OPTIONAL when its low bound is <= 0 — absence then counts as a
//     provided amount of 0 and the recipe still matches. Required
//     ingredients (low > 0) must appear in the inputs.
//   * The inputs must contain NO ingredient kinds outside the recipe.
//
// Tier variation (standard vs high-quality output) is expressed by separate
// RecipeData files, not by a per-match quality flag — a high-quality recipe
// is just a recipe whose ingredients all have range=0. When multiple
// recipes match the same inputs, the one with the smallest total range wins
// (i.e. the more specific one). Ties resolve to whichever appears first.
public static class Cooking
{
	public readonly struct MatchResult
	{
		public readonly RecipeData recipe;
		public MatchResult(RecipeData recipe)
		{
			this.recipe = recipe;
		}
		public bool IsValid => recipe != null;
		public ItemData OutputItem => recipe?.outputItem;
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
		RecipeData bestRecipe = null;
		int bestSpecificity = int.MaxValue;
		for (int r = 0; r < recipes.Count; r++)
		{
			RecipeData recipe = recipes[r];
			if (!Matches(recipe, totals, forgeType))
			{
				continue;
			}
			int spec = TotalRange(recipe);
			if (spec < bestSpecificity)
			{
				bestSpecificity = spec;
				bestRecipe = recipe;
			}
		}
		return bestRecipe != null ? new MatchResult(bestRecipe) : default;
	}

	static bool Matches(RecipeData recipe, System.Collections.Generic.Dictionary<ItemData, int> totals, EForgeType forgeType)
	{
		if (recipe?.inputs == null || recipe.inputs.Count == 0)
		{
			return false;
		}
		if (recipe.forgeType != forgeType)
		{
			return false;
		}
		for (int i = 0; i < recipe.inputs.Count; i++)
		{
			RecipeInput ri = recipe.inputs[i];
			if (ri?.item == null)
			{
				return false;
			}
			int low = ri.count - ri.range;
			int high = ri.count + ri.range;
			if (!totals.TryGetValue(ri.item, out int provided))
			{
				// Absent ingredient. Treat as provided=0 — in range iff
				// low <= 0, which is the signal that the ingredient is
				// optional.
				provided = 0;
			}
			if (provided < low || provided > high)
			{
				return false;
			}
		}
		// Reject if the player has piled in an ingredient kind this recipe
		// doesn't author.
		foreach (ItemData kind in totals.Keys)
		{
			bool found = false;
			for (int i = 0; i < recipe.inputs.Count; i++)
			{
				if (recipe.inputs[i]?.item == kind)
				{
					found = true;
					break;
				}
			}
			if (!found)
			{
				return false;
			}
		}
		return true;
	}

	// Sum of per-ingredient range. Lower = more specific. A recipe with
	// every input pinned to range=0 has specificity 0, so it always wins
	// over a looser recipe sharing the same ingredients.
	static int TotalRange(RecipeData recipe)
	{
		int total = 0;
		for (int i = 0; i < recipe.inputs.Count; i++)
		{
			RecipeInput ri = recipe.inputs[i];
			if (ri != null) { total += ri.range; }
		}
		return total;
	}

	// Record discovery via the WorldSimState bus so the announcement
	// pipeline picks up the first-time discovery.
	public static void RecordDiscovery(WorldSimState sim, in MatchResult match)
	{
		if (sim == null || !match.IsValid)
		{
			return;
		}
		sim.DiscoverRecipe(match.recipe);
	}
}
