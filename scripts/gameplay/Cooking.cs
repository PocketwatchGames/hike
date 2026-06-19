using System.Collections.Generic;
using Godot.Collections;

// Pure recipe matcher. Given the current cooking inputs (any number of slots,
// each holding an ItemState or null), the master recipe list from SimData,
// and the forge type performing the cook, returns the best matching recipe.
// Match rules:
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
// Ingredient identity is matched up the ItemData.parent chain: a supplied
// stack is credited to its item AND every ancestor, so a recipe input naming
// a parent (e.g. goblin_meat) is satisfied by any descendant (forest /
// desert goblin meat), while a recipe naming the descendant stays specific.
// An item counts as "outside the recipe" only when neither it nor any of its
// ancestors is named — so a subspecies meat the recipe never mentions is
// still allowed when its parent species meat is an authored ingredient.
//
// Tier variation (standard vs high-quality output) is expressed by separate
// RecipeData files, not by a per-match quality flag. When multiple recipes
// match the same inputs, the recipe with the highest authored `priority`
// wins; ties broken by smallest total range (more specific). Final tie
// resolves to whichever appears first.
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
		// Credit each supplied stack to its item and every ancestor so an input
		// naming any level of the chain sees the aggregated count. suppliedKinds
		// tracks only the physically-supplied items, for the extra-ingredient
		// rejection below (which must not trip on credited ancestors).
		var totals = new System.Collections.Generic.Dictionary<ItemData, int>();
		var suppliedKinds = new System.Collections.Generic.HashSet<ItemData>();
		for (int i = 0; i < inputs.Count; i++)
		{
			ItemState s = inputs[i];
			if (s?.data == null || s.stackCount <= 0)
			{
				continue;
			}
			suppliedKinds.Add(s.data);
			foreach (ItemData kind in Chain(s.data))
			{
				totals.TryGetValue(kind, out int existing);
				totals[kind] = existing + s.stackCount;
			}
		}
		if (suppliedKinds.Count == 0)
		{
			return default;
		}
		RecipeData bestRecipe = null;
		int bestPriority = int.MinValue;
		int bestSpecificity = int.MaxValue;
		for (int r = 0; r < recipes.Count; r++)
		{
			RecipeData recipe = recipes[r];
			if (!Matches(recipe, totals, suppliedKinds, forgeType))
			{
				continue;
			}
			int spec = TotalRange(recipe);
			if (recipe.priority > bestPriority || (recipe.priority == bestPriority && spec < bestSpecificity))
			{
				bestPriority = recipe.priority;
				bestSpecificity = spec;
				bestRecipe = recipe;
			}
		}
		return bestRecipe != null ? new MatchResult(bestRecipe) : default;
	}

	static bool Matches(RecipeData recipe, System.Collections.Generic.Dictionary<ItemData, int> totals, System.Collections.Generic.HashSet<ItemData> suppliedKinds, EForgeType forgeType)
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
			int low = ri.count;
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
		// Reject if the player has piled in a supplied item this recipe doesn't
		// author — at any level of its parent chain (a subspecies meat is fine
		// when the recipe names its species meat).
		foreach (ItemData kind in suppliedKinds)
		{
			if (!CoveredBy(recipe, kind))
			{
				return false;
			}
		}
		return true;
	}

	// True if the supplied item or any of its ancestors is an authored
	// ingredient of the recipe.
	static bool CoveredBy(RecipeData recipe, ItemData kind)
	{
		foreach (ItemData d in Chain(kind))
		{
			for (int i = 0; i < recipe.inputs.Count; i++)
			{
				if (recipe.inputs[i]?.item == d)
				{
					return true;
				}
			}
		}
		return false;
	}

	// Walks an item's parent chain, the item itself first, guarding against
	// authoring cycles.
	static System.Collections.Generic.IEnumerable<ItemData> Chain(ItemData item)
	{
		var seen = new System.Collections.Generic.HashSet<ItemData>();
		for (ItemData d = item; d != null && seen.Add(d); d = d.parent)
		{
			yield return d;
		}
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
