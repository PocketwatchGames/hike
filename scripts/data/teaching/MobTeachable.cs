using Godot;

// Marks a species variant as bestiary-discovered. Used for initial knowledge
// (the player starts the run already aware of common fauna) and for
// scrolls / NPC dialogue that name a creature the player hasn't seen yet.
// Routes through SimState.DiscoverSpecies so the appearsInBestiary
// filter and the announcement bus pick it up the same way an in-world
// sighting would.
[GlobalClass]
public partial class MobTeachable : TeachableConcept
{
    [Export] public SpeciesData species;

    public override string GetDisplayName()
    {
        if (species == null) { return string.Empty; }
        string name = species.displayName?.ToString();
        return string.IsNullOrEmpty(name) ? species.mob?.displayName.ToString() ?? string.Empty : name;
    }

    public override bool Teach(Player player)
    {
        if (player == null || species == null)
        {
            return false;
        }
        SimState sim = player.Sim?.WorldState?.SimState;
        return sim != null && sim.DiscoverSpecies(species);
    }

    public override bool IsKnown(Player player)
    {
        if (species == null)
        {
            return false;
        }
        return player?.Sim?.WorldState?.SimState?.IsSpeciesDiscovered(species) ?? false;
    }
}
