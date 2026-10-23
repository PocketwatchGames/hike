using Godot;
using Godot.Collections;

// A recipe: the ingredients that make something. Embedded on what it makes — an
// item crafted at a bench (ItemData.recipe) or a meal cooked in a pot
// (MealData.recipe) — and matched by Recipes.TryMatch against that station's
// list. Knowing it is keyed by what it makes (SimState.IsRecipeDiscovered).
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RecipeData : Resource
{
	// Each input accepts [count, count + range], so an exact-count variant (all
	// range 0) and a looser one can be authored as two items over the same
	// ingredients. Identity is matched up the ItemData.parent chain.
	[Export] public Array<RecipeInput> inputs = new();

	// Higher wins when several recipes match the same inputs; ties go to the
	// smallest total range (the most specific).
	[Export] public int priority;

	public bool HasInputs => inputs != null && inputs.Count > 0;
}
