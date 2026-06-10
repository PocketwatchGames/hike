using Godot;

// Companion behavior: stand still where commanded. Transitions (back to
// Follow) are evaluated first so releasing the stay command resumes following
// immediately. Latches a short AI-suspend window while idle, mirroring
// BehaviorIdle, so a parked companion can be physics-frozen.
public partial class BehaviorStay : BehaviorBase
{
    private const ulong SuspendWindowMs = 100;

    private readonly StayBehaviorData _data;

    public BehaviorStay(StayBehaviorData data)
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
        output.suspendTimeMs = time + SuspendWindowMs;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
