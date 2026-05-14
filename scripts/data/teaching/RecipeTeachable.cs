using Godot;

// Teaches a recipe — seeds an empty DiscoveredRecipeState in WorldSimState
// so the recipe shows up in the cookbook / forge UI before the player has
// ever cooked it, AND identifies the corresponding output item so the
// cookbook button reads "Grilled Kun Kun" instead of "Unknown Food". An
// opt-in flag also reveals the high-quality variant (and identifies that
// output) — used for higher-tier scrolls / NPC rewards that grant the
// refined recipe outright rather than gating it behind trial-and-error
// cooking.
[GlobalClass]
public partial class RecipeTeachable : TeachableConcept
{
    [Export] public RecipeData recipe;
    // When true, also flips DiscoveredRecipeState.discoveredHighQuality and
    // identifies recipe.outputHighQuality. Default false matches the basic
    // scroll case (player knows the dish exists, must still discover the
    // perfect ingredient counts through cooking).
    [Export] public bool teachesHighQuality;

    public override string GetDisplayName()
    {
        // Pick the output tier this concept teaches so the scroll name
        // matches what's revealed ("Scroll of Grilled Kun Kun" vs "Scroll
        // of Succulent Grilled Kun Kun"). Fall back to whichever tier exists
        // when only one is authored.
        ItemData output = teachesHighQuality && recipe?.outputHighQuality != null
            ? recipe.outputHighQuality
            : recipe?.outputStandard;
        output ??= recipe?.outputHighQuality ?? recipe?.outputStandard;
        return output != null ? output.displayName.ToString() : string.Empty;
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
        bool gainedSomething = false;
        if (!sim.DiscoveredRecipes.TryGetValue(recipe, out var state))
        {
            state = new DiscoveredRecipeState();
            sim.DiscoveredRecipes[recipe] = state;
            gainedSomething = true;
        }
        if (teachesHighQuality && !state.discoveredHighQuality)
        {
            state.discoveredHighQuality = true;
            gainedSomething = true;
        }
        // Identify the relevant outputs so RecipeScreen / CookingPanel show
        // real names rather than the "Unknown Food" placeholder. The
        // standard output is always identified once a recipe is known —
        // you can't say you "know the recipe for X" while X still reads as
        // a placeholder. The high-quality output only on a high-quality
        // teach; otherwise it stays unidentified until trial-and-error
        // cooking reveals it.
        if (recipe.outputStandard != null && sim.IdentifyItem(recipe.outputStandard))
        {
            gainedSomething = true;
        }
        if (teachesHighQuality && recipe.outputHighQuality != null && sim.IdentifyItem(recipe.outputHighQuality))
        {
            gainedSomething = true;
        }
        return gainedSomething;
    }
}
