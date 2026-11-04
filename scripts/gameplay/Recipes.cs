using System.Collections.Generic;
using Godot.Collections;

// Pure recipe matcher. Given a station's experimentation inputs (any number of
// slots, each holding an ItemState or null) and its recipe list, returns what
// those ingredients make: a meal at a cookpot (SimData.meals, RecipeData), a
// crafted item at a bench (SimData.recipes, CraftRecipeData). Both reduce to
// the same rows — an item and the [low, high] amount it accepts — so one set of
// rules serves both; a crafting row is an exact amount (low == high).
// Match rules:
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
// items, not by a per-match quality flag. When multiple recipes
// match the same inputs, the recipe with the highest authored `priority`
// wins; ties broken by smallest total range (more specific). Final tie
// resolves to whichever appears first.
public static class Recipes
{
	public static MealData TryMatch(IReadOnlyList<ItemState> inputs, Array<MealData> meals)
	{
		return TryMatch(inputs, meals, (meal, rows) => MealRows(meal?.recipe, rows));
	}

	// The item a crafting bench makes from `inputs` among `recipes` (each an
	// ItemData carrying a CraftRecipeData), or null. Amounts are exact.
	public static ItemData TryMatchCraft(IReadOnlyList<ItemState> inputs, Array<ItemData> recipes)
	{
		return TryMatch(inputs, recipes, (item, rows) => CraftRows(item?.recipe, rows));
	}

	// One ingredient as the matcher reads it: `item` (matched up its parent
	// chain) supplied in an amount within [low, high].
	readonly struct Row
	{
		public readonly ItemData item;
		public readonly int low;
		public readonly int high;

		public Row(ItemData item, int low, int high)
		{
			this.item = item;
			this.low = low;
			this.high = high;
		}
	}

	// Fill `rows` from a meal recipe; returns its priority. False-y (no rows)
	// when it has no inputs.
	static int MealRows(RecipeData recipe, List<Row> rows)
	{
		if (recipe?.inputs == null)
		{
			return 0;
		}
		foreach (RecipeInput ri in recipe.inputs)
		{
			rows.Add(new Row(ri?.item, ri?.count ?? 0, (ri?.count ?? 0) + (ri?.range ?? 0)));
		}
		return recipe.priority;
	}

	static int CraftRows(CraftRecipeData recipe, List<Row> rows)
	{
		if (recipe?.inputs == null)
		{
			return 0;
		}
		foreach (MaterialCount mc in recipe.inputs)
		{
			rows.Add(new Row(mc?.item, mc?.count ?? 0, mc?.count ?? 0));
		}
		return 0;
	}

	// What `inputs` make among `candidates`, or null when nothing matches.
	// `rowsOf` fills a candidate's rows and returns its priority.
	static T TryMatch<[Godot.MustBeVariant] T>(IReadOnlyList<ItemState> inputs, Array<T> candidates, System.Func<T, List<Row>, int> rowsOf) where T : Godot.GodotObject
	{
		if (inputs == null || candidates == null || candidates.Count == 0)
		{
			return null;
		}
		// Credit each supplied stack to its item and every ancestor so an input
		// naming any level of the chain sees the aggregated count. suppliedKinds
		// tracks only the physically-supplied items, for the extra-ingredient
		// rejection below (which must not trip on credited ancestors).
		var totals = new System.Collections.Generic.Dictionary<ItemData, int>();
		var suppliedKinds = new HashSet<ItemData>();
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
			return null;
		}
		T bestRecipe = null;
		int bestPriority = int.MinValue;
		int bestSpecificity = int.MaxValue;
		var rows = new List<Row>();
		int candidateCount = candidates.Count;
		for (int r = 0; r < candidateCount; r++)
		{
			T made = candidates[r];
			rows.Clear();
			int priority = rowsOf(made, rows);
			if (!Matches(rows, totals, suppliedKinds))
			{
				continue;
			}
			int spec = TotalRange(rows);
			if (priority > bestPriority || (priority == bestPriority && spec < bestSpecificity))
			{
				bestPriority = priority;
				bestSpecificity = spec;
				bestRecipe = made;
			}
		}
		return bestRecipe;
	}

	static bool Matches(List<Row> rows, System.Collections.Generic.Dictionary<ItemData, int> totals, HashSet<ItemData> suppliedKinds)
	{
		if (rows.Count == 0)
		{
			return false;
		}
		foreach (Row row in rows)
		{
			if (row.item == null)
			{
				return false;
			}
			// An absent ingredient counts as provided 0 — in range iff low <= 0,
			// which is the signal that the ingredient is optional.
			totals.TryGetValue(row.item, out int provided);
			if (provided < row.low || provided > row.high)
			{
				return false;
			}
		}
		// Reject if the player has piled in a supplied item this recipe doesn't
		// author — at any level of its parent chain (a subspecies meat is fine
		// when the recipe names its species meat).
		foreach (ItemData kind in suppliedKinds)
		{
			if (!CoveredBy(rows, kind))
			{
				return false;
			}
		}
		return true;
	}

	// True if the supplied item or any of its ancestors is an authored
	// ingredient of the recipe.
	static bool CoveredBy(List<Row> rows, ItemData kind)
	{
		foreach (ItemData d in Chain(kind))
		{
			foreach (Row row in rows)
			{
				if (row.item == d)
				{
					return true;
				}
			}
		}
		return false;
	}

	// True when a supplied stack's item satisfies a reagent/ingredient item —
	// i.e. the reagent is the stack's item or any of its ancestors. Same identity
	// rule the matcher uses; exposed so reagent spending (Player.SpendReagents)
	// deducts from the right stacks.
	public static bool Satisfies(ItemData stackItem, ItemData reagentItem)
	{
		if (stackItem == null || reagentItem == null)
		{
			return false;
		}
		foreach (ItemData d in Chain(stackItem))
		{
			if (d == reagentItem)
			{
				return true;
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
	// every input pinned to range 0 has specificity 0, so it always wins
	// over a looser recipe sharing the same ingredients.
	static int TotalRange(List<Row> rows)
	{
		int total = 0;
		foreach (Row row in rows)
		{
			total += row.high - row.low;
		}
		return total;
	}

	// How many times a flat cost (a recipe's inputs, a repair) can be paid from
	// `pool`. For each row, the affordable count is floor(available / count); the
	// cost can be paid the minimum of those across all rows.
	// Availability sums the stackCount of every pool stack whose item (up its
	// parent chain) matches the reagent, so a reagent naming a parent species
	// meat is paid by any descendant — the same identity rule TryMatch uses.
	// Empty / null reagents (or a null pool) yields 0.
	public static int CountAffordable(IReadOnlyList<MaterialCount> reagents, System.Collections.Generic.IEnumerable<ItemState> pool)
	{
		if (reagents == null || reagents.Count == 0 || pool == null)
		{
			return 0;
		}
		// Sum available amounts, credited up the parent chain like TryMatch.
		var totals = new System.Collections.Generic.Dictionary<ItemData, int>();
		foreach (ItemState s in pool)
		{
			if (s?.data == null || s.stackCount <= 0)
			{
				continue;
			}
			foreach (ItemData kind in Chain(s.data))
			{
				totals.TryGetValue(kind, out int existing);
				totals[kind] = existing + s.stackCount;
			}
		}
		int affordable = int.MaxValue;
		for (int i = 0; i < reagents.Count; i++)
		{
			MaterialCount r = reagents[i];
			if (r?.item == null || r.count <= 0)
			{
				continue;
			}
			totals.TryGetValue(r.item, out int have);
			affordable = System.Math.Min(affordable, have / r.count);
		}
		return affordable == int.MaxValue ? 0 : affordable;
	}
}
