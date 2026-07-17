using Godot;

// True when the mob was damaged recently AND its perceived player is in a
// safety zone. Wired on Idle/Wander → Retreat so a mob the player snipes from
// safety bolts instead of just standing there: the safe-gated
// AggroAcquiredCondition won't let it enter Attack, so this routes it to flee.
// (When the player is NOT safe, being hit funnels back through the normal
// aggro/attack path instead.)
[GlobalClass]
public partial class HurtWhileTargetSafeCondition : BehaviorTransitionData
{
    // A hit within this many seconds counts as "just attacked".
    [Export] public float recentDamageSeconds = 1.5f;

    public override bool Evaluate(Mob me, ref PerceptionState targetPerception)
    {
        Player target = targetPerception.pawnTarget;
        if (target == null || !target.IsSafe)
        {
            return false;
        }
        return me.GameTimeMs - me.LastDamagedMs < (ulong)(recentDamageSeconds * 1000f);
    }
}
