using Godot;

// Teaches a recipe — adds the cookable consumable to SimState.DiscoveredRecipes
// so it shows up in the cookbook before the player has ever cooked it. Each
// scroll points at exactly one consumable; standard and high-quality variants
// of a dish are separate consumables, so each is taught on its own.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class RecipeTeachable : TeachableConcept
{
    [Export] public ConsumableData recipe;

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
