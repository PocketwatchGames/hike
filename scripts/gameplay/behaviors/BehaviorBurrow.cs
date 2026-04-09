using Godot;

// Terminal behavior for mobs with canBurrow = true. Raises the burrow flag on
// the AI output; Mob consumes the flag to mark the sim state as burrowed and
// despawn the node. The sim state's Burrowed flag keeps the mob from
// respawning when its chunk is re-loaded.
public partial class BehaviorBurrow : BehaviorBase
{
    private readonly BurrowBehaviorData _data;

    public BehaviorBurrow(BurrowBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }
        output.speed = 0f;
        output.burrow = true;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
