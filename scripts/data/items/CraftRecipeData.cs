using Godot;
using Godot.Collections;

// What making an item at a crafting bench takes (ItemData.recipe): a flat cost,
// paid when the job is queued, and the in-world time the bench spends on it.
// Nothing is matched — only a learned recipe can be crafted
// (SimState.IsRecipeDiscovered), so unlike a meal's RecipeData there is no
// accepted range or priority.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class CraftRecipeData : Resource
{
	[Export] public Array<MaterialCount> inputs = new();

	// How many of the item one craft makes — a stack when the item stacks, that
	// many separate items when it doesn't.
	[Export(PropertyHint.Range, "1,99,1,or_greater")] public int count = 1;

	// In-world hours the bench works on one craft.
	[Export(PropertyHint.Range, "0,48,0.25,or_greater")] public float hours = 1f;

	public bool HasInputs => inputs != null && inputs.Count > 0;
}
