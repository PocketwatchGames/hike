using Godot;

// Reveals an item's real name via WorldSimState.IdentifyItem. Used as the
// "I know this item" entry in PlayerSpawnData.initialKnowledge (so the
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
        WorldSimState sim = player.World?.WorldState?.SimState;
        return sim != null && sim.IdentifyItem(item);
    }
}
