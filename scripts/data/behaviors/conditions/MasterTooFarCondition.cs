using Godot;

// Companion leash predicate: true when the mob's master (the player) is farther
// than `maxDistance`, or there is no player. Used to pull a wary guard dog off a
// threat and back into Follow when the player walks away — a companion stays
// with its master rather than committing to a standoff it was only watching.
[GlobalClass]
public partial class MasterTooFarCondition : BehaviorTransitionData
{
    [Export] public float maxDistance = 12f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        Player master = me.Sim?.player;
        if (master == null)
        {
            return true;
        }
        return me.GlobalPosition.DistanceSquaredTo(master.GlobalPosition) > maxDistance * maxDistance;
    }
}
