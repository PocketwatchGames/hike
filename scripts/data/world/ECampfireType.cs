// Names a campfire/cooking station type. Scopes recipes to a specific kind of
// station so a campfire only matches cooking recipes (and, later, a smelter only
// matches smelting recipes). Wire values are stored on RecipeData and Campfire
// .tres / .tscn resources — append new entries, never reuse old numbers, so
// existing authored data keeps loading after new station types are added.
public enum ECampfireType
{
	Cooking,
}
