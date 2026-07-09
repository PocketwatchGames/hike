using Godot;

// Teaches a recipe — adds it to WorldSimState.DiscoveredRecipes so it
// shows up in the cookbook / forge UI before the player has ever cooked
// it, AND identifies the recipe's output item so the cookbook button
// reads with its real name instead of "Unknown Food".
//
// Standard and high-quality variants of a dish are separate RecipeData
// files, so a "scroll of grilled kun kun" teaches the
// standard recipe; a hypothetical "scroll of succulent grilled kun kun"
// would teach the high-quality recipe (with range=0 ingredients). Each
// scroll points at exactly one recipe — no per-tier flag.
[GlobalClass]
public partial class RecipeTeachable : TeachableConcept
{
    [Export] public RecipeData recipe;

    public override string GetDisplayName()
    {
        return recipe?.outputItem != null ? recipe.outputItem.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || recipe == null)
        {
            return false;
        }
        WorldSimState sim = player.World?.WorldState?.SimState;
        if (sim == null)
        {
            return false;
        }
        // identifyOutput=true silently identifies the output item before
        // the recipe banner fires, so the banner reads with the real name
        // (no separate redundant "Item Identified" banner follows).
        return sim.DiscoverRecipe(recipe, identifyOutput: true);
    }

    public override bool IsKnown(Player player)
    {
        if (recipe == null)
        {
            return false;
        }
        return player?.World?.WorldState?.SimState?.IsRecipeDiscovered(recipe) ?? false;
    }
}
