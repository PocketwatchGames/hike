using Godot;
using Godot.Collections;

// One recipe = one output. Standard and high-quality variants of the same
// dish are authored as two separate RecipeData files: the high-quality
// variant uses range=0 on each ingredient (must hit count exactly) and
// names the high-quality output; the standard variant uses range>0 on
// some ingredients and names the standard output. Cooking.TryMatch picks
// the highest-priority match (ties broken by lowest total range) so
// exact ingredient counts unlock the high-quality output, and looser
// counts fall through to the standard.
[GlobalClass]
public partial class RecipeData : Resource
{
	[Export] public EForgeType forgeType;
	[Export] public ItemData outputItem;
	[Export] public Array<RecipeInput> inputs = new();
	// Higher priority wins when multiple recipes match the same inputs.
	// Use to force a high-quality variant over a standard one, or to author
	// an explicit fallback recipe at a low priority. Ties resolve to the
	// recipe with the smallest sum of input ranges (most specific).
	[Export] public int priority = 0;
}
