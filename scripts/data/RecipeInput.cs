using Godot;

// One authored ingredient slot in a recipe. The accepted range is
// [count + minCountRange, count + maxCountRange]. Set the low bound to 0 or
// below to mark the ingredient as OPTIONAL — the matcher treats absence as a
// provided amount of 0, so the recipe still fires without it. A count > 0 on
// an optional ingredient still controls the high-quality "exact" trigger: a
// recipe authored as (count=1, min=-1, max=0) will only register as exact
// when the player provides exactly 1, not 0.
[GlobalClass]
public partial class RecipeInput : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
	[Export] public int minCountRange = 0;
	[Export] public int maxCountRange = 0;
}
