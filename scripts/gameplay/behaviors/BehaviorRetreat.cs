using Godot;

// Disengage response to a player who has reached safety (a safety zone). The
// mob breaks off, holds a brief stare, then walks directly away until it's far
// enough to lose interest and revert to its default behavior. A mob that was
// just attacked skips the stare and bolts. Re-engages (transition back to
// Attack via AggroAcquiredCondition) the instant the player leaves safety.
//
// Straight-line away-points (like BehaviorFlee) rather than A* home-pathing:
// the whole point is to move away from the player, not to reach a spawn post
// that may be unreachable across water/terrain (the failure mode the old
// idle-return had in the swamp).
public partial class BehaviorRetreat : BehaviorBase
{
    private const float PathSuccessDistance = 1f;

    private readonly RetreatBehaviorData _data;
    private ulong _stareUntilMs;
    private ulong _legTimeoutMs;
    private Vector3? _awayPoint;

    public BehaviorRetreat(RetreatBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        _awayPoint = null;
        _legTimeoutMs = 0;
        // Skip the stare when we're here because we were just hit — a shot from
        // safety means run, not gawk.
        bool justHit = time - me.LastDamagedMs < (ulong)(_data.recentDamageSeconds * 1000f);
        _stareUntilMs = justHit ? 0 : time + (ulong)(_data.stareSeconds * 1000f);
        me.Navigator?.Stop();
        // A flier that was resting must take off before it can move away.
        if (me.mobData?.CanFly == true)
        {
            me.LeavePerch();
        }
        if (CVars.safetyDebug.Value)
        {
            GD.Print($"[safety] Retreat.OnEnter mob={me.mobData?.displayName} justHit={justHit}");
        }
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        // Re-engage edge: AggroAcquiredCondition fires here only once the player
        // has left safety (it's gated on !IsSafe), pulling us back into Attack.
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        Player target = targetPerception.pawnTarget;
        if (target == null)
        {
            // Lost track of who we're fleeing — nothing to retreat from.
            return new BehaviorOutput(EBehaviorResult.Complete);
        }

        // Flying mobs retreat airborne — hover in place for the stare, then
        // cruise away holding altitude — rather than dropping to ground pathing.
        bool flying = me.mobData?.CanFly == true;

        Vector3 toPlayer = target.GlobalPosition - me.GlobalPosition;
        toPlayer.Y = 0f;
        float distToPlayer = toPlayer.Length();

        // Far enough away — lose interest and hand back to the default behavior.
        if (distToPlayer >= _data.disengageDistance)
        {
            return new BehaviorOutput(EBehaviorResult.Complete);
        }

        // A hit at any point during the stare cancels it — bolt now.
        bool justHit = time - me.LastDamagedMs < (ulong)(_data.recentDamageSeconds * 1000f);
        if (justHit)
        {
            _stareUntilMs = 0;
        }

        // Stare phase: face the player, hold still.
        if (time < _stareUntilMs)
        {
            if (toPlayer.LengthSquared() > 0.0001f)
            {
                output.yaw = Mathf.Atan2(toPlayer.X, toPlayer.Z);
            }
            output.speed = 0f;
            output.airborne = flying;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Walk-away phase: pick an away-point roughly opposite the player
        // (±90° jitter so a pack doesn't funnel into one line) and head for it.
        if (!_awayPoint.HasValue || time >= _legTimeoutMs)
        {
            float awayAngle = Mathf.Atan2(-toPlayer.X, -toPlayer.Z);
            float jitter = ((float)GD.Randf() - 0.5f) * Mathf.Pi;
            float angle = awayAngle + jitter;
            _awayPoint = me.GlobalPosition
                + new Vector3(Mathf.Sin(angle), 0f, Mathf.Cos(angle)) * _data.legRange;
            _legTimeoutMs = time + (ulong)(_data.legTimeoutSeconds * 1000f);
        }

        Vector3 toPoint = _awayPoint.Value - me.GlobalPosition;
        toPoint.Y = 0f;
        if (toPoint.Length() <= PathSuccessDistance)
        {
            // Reached this leg — re-pick next tick (keeps moving away until we
            // hit disengageDistance).
            _awayPoint = null;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        output.pathTarget = _awayPoint.Value;
        output.speed = _data.retreatSpeed;
        output.pathSuccessDistance = PathSuccessDistance;
        output.airborne = flying;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
