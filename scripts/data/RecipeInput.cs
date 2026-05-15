using Godot;

// One authored ingredient slot in a recipe. The accepted count is
// [count - range, count + range], with the low bound clamped at 0
// (an ingredient is OPTIONAL when low <= 0 — absence is treated as a
// provided amount of 0 and the recipe still matches).
//
// Tier variation is expressed by separate RecipeData files rather than
// by per-ingredient quality flags: a recipe whose ingredients all have
// range=0 is the "exact" / high-quality variant; one with range>0 on
// some ingredients is the "loose" / standard variant. Both can target
// the same dish with different output items.
[GlobalClass]
public partial class RecipeInput : Resource
{
	[Export] public ItemData item;
	[Export] public int count = 1;
	[Export] public int range = 0;
}
