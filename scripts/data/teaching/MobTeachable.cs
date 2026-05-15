using Godot;

// Marks a mob species as bestiary-discovered. Used for initial knowledge
// (the player starts the run already aware of common fauna) and for
// scrolls / NPC dialogue that name a creature the player hasn't seen yet.
// Routes through WorldSimState.DiscoverMob so the appearsInBestiary
// filter and the announcement bus pick it up the same way an in-world
// sighting would.
[GlobalClass]
public partial class MobTeachable : TeachableConcept
{
    [Export] public MobData mob;

    public override string GetDisplayName()
    {
        return mob != null ? mob.displayName.ToString() : string.Empty;
    }

    public override bool Teach(Player player)
    {
        if (player == null || mob == null)
        {
            return false;
        }
        WorldSimState sim = player.World?.WorldState?.SimState;
        return sim != null && sim.DiscoverMob(mob);
    }
}
