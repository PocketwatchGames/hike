using Godot;

// True when the mob's perceived player target is standing in a safety zone
// (Player.IsSafe). Wired as an Attack → LookAt edge on aggressive brains: a mob
// mid-attack breaks off to stare (BehaviorLookAt) the moment the player reaches
// safety, then LookAt times out back to the mob's default idle/wander. The
// safe-gated AggroAcquiredCondition then keeps it from re-engaging until the
// player steps back out.
[GlobalClass]
public partial class TargetSafeCondition : BehaviorTransitionData
{
    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        Player target = targetPerception.pawnTarget;
        return target != null && target.IsSafe;
    }
}
