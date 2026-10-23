using Godot;

// Teaches a cookpot meal — adds it to SimState.DiscoveredRecipes so it shows up
// in the cookpot's recipe list before the player has ever cooked it.
// [Tool]: reachable from ZoneData.zoneLoot through ItemCountRange — see
// the [Tool]-parent rule in the root CLAUDE.md.
[Tool]
[GlobalClass]
public partial class MealTeachable : TeachableConcept
{
    [Export] public MealData meal;

    public override string GetDisplayName()
    {
        return meal?.displayName.ToString() ?? string.Empty;
    }

    public override bool Teach(Player player)
    {
        SimState sim = player?.Sim?.WorldState?.SimState;
        if (sim == null || meal == null)
        {
            return false;
        }
        return sim.DiscoverRecipe(meal);
    }

    public override bool IsKnown(Player player)
    {
        if (meal == null)
        {
            return false;
        }
        return player?.Sim?.WorldState?.SimState?.IsRecipeDiscovered(meal) ?? false;
    }
}
