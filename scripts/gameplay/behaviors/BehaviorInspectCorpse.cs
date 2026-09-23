using Godot;

// Reacting to a dead body. One phase machine covers every case, because the
// three the game authors are the same sequence with legs dropped:
//
//   saw an attacker kill it  glance → walk over → study → face where it came from
//   saw a trap kill it       stare (the trap is a place to avoid, not a scene)
//   found the body later     glance → walk over → study
//   species that never walks  stare
//
// Which legs run is decided by the stimulus (CorpseSighting.approach /
// .damageOrigin) and the species (CorpseInspectBehaviorData.approach); the
// durations are all authored. Any phase is interruptible by the node's own
// transitions, which is how aggro cuts the whole thing short.
public partial class BehaviorInspectCorpse : BehaviorBase
{
    private enum EPhase
    {
        Glance,
        Approach,
        Study,
        LookForKiller,
    }

    private readonly CorpseInspectBehaviorData _data;

    // Read by Mob.Corpses: the sighting is posted (and its suspicion spike
    // applied) before this behavior ever runs, so the tuning has to be reachable
    // from the mob that noticed, not just from the running node.
    public CorpseInspectBehaviorData Data => _data;
    private EPhase _phase;
    private ulong _phaseUntilMs;
    private ulong _approachUntilMs;

    public BehaviorInspectCorpse(CorpseInspectBehaviorData data)
    {
        _data = data;
    }

    // Stop whatever wander leg was in progress — the mob has noticed a body and
    // holds still to look at it.
    public override void OnEnter(Mob me, ulong time)
    {
        _phase = EPhase.Glance;
        bool approaching = me.CorpseSighting.HasValue && WillApproach(me, me.CorpseSighting.Value);
        _phaseUntilMs = time + RollSeconds(approaching ? _data.glanceTimeRange : _data.glanceOnlyTimeRange);
        _approachUntilMs = 0;
        me.Navigator?.Stop();
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        // Cleared out from under us (the mob engaged, or something else reset
        // it) — nothing left to react to.
        if (!me.CorpseSighting.HasValue)
        {
            return new BehaviorOutput(EBehaviorResult.Complete);
        }
        CorpseSighting sighting = me.CorpseSighting.Value;

        // Hold the suspicion the sighting raised for as long as the mob is still
        // dealing with the body — re-raising each tick restamps it at the same
        // level, so the bleed-off only starts once the reaction is over rather
        // than while the mob is still walking to the corpse.
        me.RaiseSuspicion(sighting.witnessed ? _data.witnessedDeathSuspicion : _data.foundCorpseSuspicion);

        // A flier holds whatever altitude mode it was already in: staring at
        // something neither launches a perched bird nor drops a cruising drake.
        output.airborne = me.mobData?.CanFly == true && me.IsAirborne;

        switch (_phase)
        {
            case EPhase.Glance:
                FaceFlat(me, sighting.position, ref output);
                output.speed = 0f;
                if (time >= _phaseUntilMs)
                {
                    if (!WillApproach(me, sighting))
                    {
                        return Finish(ref output);
                    }
                    _phase = EPhase.Approach;
                    _approachUntilMs = time + (ulong)(Mathf.Max(0f, _data.approachTimeoutSeconds) * 1000f);
                }
                return new BehaviorOutput(EBehaviorResult.Running);

            case EPhase.Approach:
                // Route through the navigator so the mob paths around what is
                // between it and the body rather than walking into it. Facing is
                // left to the movement auto-yaw for this leg.
                me.Navigator.Goto(sighting.position, allowFalling: true);
                output.speed = _data.approachSpeed;
                if (me.GlobalPosition.DistanceSquaredTo(sighting.position) <= _data.arriveRange * _data.arriveRange)
                {
                    me.Navigator.Stop();
                    _phase = EPhase.Study;
                    _phaseUntilMs = time + RollSeconds(_data.studyTimeRange);
                }
                else if (time >= _approachUntilMs)
                {
                    // Unreachable — the body is remembered either way, so the
                    // mob moves on instead of grinding against the geometry.
                    me.Navigator.Stop();
                    return Finish(ref output);
                }
                return new BehaviorOutput(EBehaviorResult.Running);

            case EPhase.Study:
                FaceFlat(me, sighting.position, ref output);
                output.speed = 0f;
                if (time >= _phaseUntilMs)
                {
                    if (!sighting.damageOrigin.HasValue)
                    {
                        return Finish(ref output);
                    }
                    _phase = EPhase.LookForKiller;
                    _phaseUntilMs = time + RollSeconds(_data.damageLookTimeRange);
                }
                return new BehaviorOutput(EBehaviorResult.Running);

            default:
                FaceFlat(me, sighting.damageOrigin ?? sighting.position, ref output);
                output.speed = 0f;
                if (time >= _phaseUntilMs)
                {
                    return Finish(ref output);
                }
                return new BehaviorOutput(EBehaviorResult.Running);
        }
    }

    // Clear the sighting on the way out so the default behavior's
    // HasCorpseSighting transition cannot bounce us straight back in. The body
    // was remembered when the sighting was posted, so it stays reacted-to.
    private static BehaviorOutput Finish(ref AIOutput output)
    {
        output.resetCorpseSighting = true;
        return new BehaviorOutput(EBehaviorResult.Complete);
    }

    // Three ways to end up staring instead of walking over: the species never
    // approaches, the sighting says to keep away (a trap kill), or the species
    // flies — the walk over is ground pathing.
    private bool WillApproach(Mob me, in CorpseSighting sighting)
    {
        return _data.approach && sighting.approach && me.mobData?.CanFly != true;
    }

    private static void FaceFlat(Mob me, Vector3 target, ref AIOutput output)
    {
        Vector3 diff = target - me.GlobalPosition;
        Vector2 flat = new Vector2(diff.X, diff.Z);
        if (flat.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(flat.X, flat.Y);
        }
    }

    // A leg's length in ms, rolled from its authored (min, max) second range.
    private static ulong RollSeconds(Vector2 range)
    {
        double seconds = GD.RandRange((double)range.X, (double)range.Y);
        return (ulong)(Mathf.Max(0f, (float)seconds) * 1000f);
    }
}
