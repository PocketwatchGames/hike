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
//   * Every authored ingredient must satisfy the provided count being inside
//     [count + minCountRange, count + maxCountRange]. An ingredient is
//     OPTIONAL when its low bound is <= 0 — absence then counts as a
//     provided amount of 0 and the recipe still matches. Required
//     ingredients (low > 0) must appear in the inputs.
//   * The inputs must contain NO ingredient kinds outside the recipe.
//   * High-quality matches iff every ingredient's provided count equals
//     exactly recipe.count AND the recipe declares an outputHighQuality.
//     An optional ingredient with count > 0 must be present at exactly
//     that count to satisfy the exact-match criterion.
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
				int low = ri.count + ri.minCountRange;
				int high = ri.count + ri.maxCountRange;
				if (!totals.TryGetValue(ri.item, out int provided))
				{
					// Absent ingredient. Treat as provided=0 — this is in
					// range iff the recipe author set low <= 0, which is the
					// signal that the ingredient is optional.
					provided = 0;
				}
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
			// Reject if the player has piled in an ingredient kind this
			// recipe doesn't author. The old "inputs.Count == totals.Count"
			// check did this for us, but it also rejected omitted optional
			// ingredients — so it's replaced with a one-way subset check.
			bool hasExtras = false;
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
					hasExtras = true;
					break;
				}
			}
			if (hasExtras)
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
