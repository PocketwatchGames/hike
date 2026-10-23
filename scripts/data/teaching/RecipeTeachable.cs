using Godot;

// Teaches a recipe — adds the item it makes to SimState.DiscoveredRecipes so it
// shows up in its station's recipe list before the player has ever made it. Each
// scroll points at exactly one item; standard and high-quality variants of a
// dish are separate items, so each is taught on its own.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RecipeTeachable : TeachableConcept
{
    [Export] public ItemData recipe;

    public override string GetDisplayName()
    {
        return recipe?.displayName.ToString() ?? string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || recipe == null)
        {
            return false;
        }
        SimState sim = player.Sim?.WorldState?.SimState;
        if (sim == null)
        {
            return false;
        }
        return sim.DiscoverRecipe(recipe);
    }

    public override bool IsKnown(Player player)
    {
        if (recipe == null)
        {
            return false;
        }
        return player?.Sim?.WorldState?.SimState?.IsRecipeDiscovered(recipe) ?? false;
    }
}
