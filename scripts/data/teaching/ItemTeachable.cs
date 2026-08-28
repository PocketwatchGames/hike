using Godot;

// Reveals an item's real name via SimState.IdentifyItem. Used as the
// "I know this item" entry in WorldStartData.initialKnowledge (so the
// starting health potion reads as itself, not "Unknown Potion"), and as
// the concept payload on identification scrolls / NPC rewards that just
// tell the player what something is without granting it.
[GlobalClass]
public partial class ItemTeachable : TeachableConcept
{
    [Export] public ItemData item;

    public override string GetDisplayName()
    {
        return item != null ? item.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || item == null)
        {
            return false;
        }
        SimState sim = player.Sim?.WorldState?.SimState;
        return sim != null && sim.IdentifyItem(item);
    }

    public override bool IsKnown(Player player)
    {
        if (item == null)
        {
            return false;
        }
        // IsItemIdentified returns true for items with no placeholder name; guard
        // so a no-op teach doesn't read as "known" and dim a marker prematurely.
        SimState sim = player?.Sim?.WorldState?.SimState;
        return sim != null && sim.IsItemIdentified(item);
    }
}
