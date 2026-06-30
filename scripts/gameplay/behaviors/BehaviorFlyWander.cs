using Godot;

// Airborne wander: the inherited ground-wander cadence (random legs around the
// spawn anchor, pauses between them) flown at cruise altitude. The mob holds the
// airborne flag on every running tick — including the pauses between legs, since
// a flier hovers in place rather than landing — at MobData.hoverHeight (a null
// flyAltitude). Takes off into BehaviorFlyAttack on aggro via the brain's
// transitions; nothing here lands.
public partial class BehaviorFlyWander : BehaviorWander
{
    public BehaviorFlyWander(WanderBehaviorData data) : base(data)
    {
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        BehaviorOutput result = base.Run(me, time, ref targetPerception, ref output);
        if (result.result == EBehaviorResult.Running)
        {
            output.airborne = true;
        }
        return result;
    }
}
