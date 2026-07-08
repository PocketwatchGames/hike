using Godot;

// Reveals a named map region — adds it to WorldSimState.DiscoveredRegions so
// its label shows on the world map before the player has physically entered
// the region. The on-entry banner / loot table hooks are still owned by
// GameClient.UpdateRegion; this concept only seeds the "I know this place
// exists" bit. Useful for treasure-map scrolls and NPC quest-giver hints.
[GlobalClass]
public partial class RegionTeachable : TeachableConcept
{
    [Export] public RegionData region;

    public override string GetDisplayName()
    {
        return region != null ? region.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || region == null)
        {
            return false;
        }
        WorldSimState sim = player.World?.WorldState?.SimState;
        if (sim == null)
        {
            return false;
        }
        return sim.DiscoverRegion(region);
    }
}
