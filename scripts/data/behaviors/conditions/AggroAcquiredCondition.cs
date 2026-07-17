using Godot;

[GlobalClass]
public partial class AggroAcquiredCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        // A player standing in a safety zone is off-limits: aggressive mobs
        // don't start (or re-start) an attack against them. An already-engaged
        // mob breaks off via TargetSafeCondition into a stare, then this gate
        // keeps it from re-acquiring so it wanders away instead of ping-ponging.
        Player target = targetPerception.pawnTarget;
        return target != null && !target.IsSafe;
    }
}
