using Godot;

public partial class BehaviorInvestigate : BehaviorBase
{
    // Extra slop on top of the investigation range when deciding we've
    // "arrived" for the purposes of starting the pause timer. The path
    // controller stops at `range`; this tolerance keeps us from missing the
    // arrival check by a few centimeters.
    private const float ArrivalSlack = 1f;
    private const float InvestigateSpeed = 0.25f;
    // Metres closer to the point that count as progress for the stall timer —
    // above the jitter of a mob shuffling against a wall.
    private const float ProgressEpsilon = 0.5f;

    private readonly InvestigateBehaviorData _data;
    private float _bestDistance;
    private ulong _lastProgressTime;

    public BehaviorInvestigate(InvestigateBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        _bestDistance = float.MaxValue;
        _lastProgressTime = time;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }


        // No investigation point means nothing to do — fall back to the default
        // behavior rather than asserting, since the data can be cleared at any
        // time by aiOutput.resetInvestigation.
        if (!me.investigation.HasValue)
        {
            return new BehaviorOutput(EBehaviorResult.Complete);
        }

        InvestigateState investigation = me.investigation.Value;
        // Route through the navigator so the mob A*-paths around obstacles
        // instead of walking into walls between us and the noise. Speed is
        // set explicitly so the navigator's defaults-fallback (1f) doesn't
        // pull us up to full sprint. Use the navigator's default arrival
        // distance — investigation.range is the behavior's "I've inspected
        // here" tolerance (checked below with LOS), not the navigator's
        // stopping distance; passing it through would make the mob halt as
        // soon as it's within range of the point and never close the gap.
        me.Navigator.Goto(investigation.position, allowFalling: true);
        output.speed = InvestigateSpeed;

        Vector3 diff = investigation.position - me.GlobalPosition;
        float distSq = diff.LengthSquared();
        float arriveRange = investigation.range + ArrivalSlack;

        // The point tracks a moving target, so progress is measured against
        // wherever it is now: any tick that ends meaningfully closer than the
        // best so far resets the stall clock.
        float dist = Mathf.Sqrt(distSq);
        if (dist < _bestDistance - ProgressEpsilon)
        {
            _bestDistance = dist;
            _lastProgressTime = time;
        }

        // Face the investigation point every tick — a yell that drops us into
        // Investigate should snap our head toward the source immediately, not
        // wait for the path-direction auto-yaw to kick in (and not depend on
        // LOS, since a yell from behind cover still draws attention).
        Vector2 flat = new Vector2(diff.X, diff.Z);
        if (flat.LengthSquared() > 0.0001f)
        {
            output.yaw = Mathf.Atan2(flat.X, flat.Y);
        }

        bool arrived = distSq < arriveRange * arriveRange && Sightline.IsClear(me, investigation.position);
        if (arrived)
        {
            // Arrived and can see the point — start the pause countdown.
            // Clamp the existing cancelTime down so a very long investigation
            // doesn't keep us parked here past pauseTime.
            ulong pauseUntil = time + investigation.pauseTime;
            if (pauseUntil < investigation.cancelTime)
            {
                investigation.cancelTime = pauseUntil;
            }
            output.investigation = investigation;
        }

        bool stalled = !arrived && time - _lastProgressTime >= (ulong)(me.mobData.investigateStallTime * 1000f);
        if (time >= investigation.cancelTime || stalled)
        {
            output.abandonInvestigation = true;
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }

    public override string DebugStatus(ulong time)
    {
        return $"best={_bestDistance:F1}m noProgress={(time - _lastProgressTime) / 1000f:F1}s";
    }
}
