using Godot;

// One authored ingredient slot in a recipe: a MaterialCount whose count is the
// low bound of the accepted window [count, count + range]. An ingredient is
// OPTIONAL when count is 0 — absence then counts as a provided amount of 0 and
// the recipe still matches. Spending a recipe pays `count`.
//
// Tier variation is expressed by separate items rather than by per-ingredient
// quality flags: a recipe whose ingredients all have range=0 is the "exact" /
// high-quality variant; one with range>0 on some ingredients is the "loose" /
// standard variant.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RecipeInput : MaterialCount
{
	[Export] public int range = 0;
}
