using Godot;

public partial class BehaviorInvestigate : BehaviorBase
{
    // Eye height for the line-of-sight raycast. Matches UpdatePerception so
    // "can I see the investigation point" uses the same sightline as "can I
    // see the player".
    private const float EyeHeight = 1.5f;
    // Extra slop on top of the investigation range when deciding we've
    // "arrived" for the purposes of starting the pause timer. The path
    // controller stops at `range`; this tolerance keeps us from missing the
    // arrival check by a few centimeters.
    private const float ArrivalSlack = 1f;
    private const float InvestigateSpeed = 0.25f;
    private const float RepathSuccessDistance = 0.5f;

    private readonly InvestigateBehaviorData _data;

    public BehaviorInvestigate(InvestigateBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.useTorch = me.ShouldUseTorch;

        // No investigation point means nothing to do — fall back to the default
        // behavior rather than asserting, since the data can be cleared at any
        // time by aiOutput.resetInvestigation.
        if (!me.investigation.HasValue)
        {
            return new BehaviorOutput(EBehaviorResult.Complete);
        }

        InvestigateState investigation = me.investigation.Value;
        output.pathTarget = investigation.position;
        output.pathSuccessDistance = investigation.range;
        output.speed = InvestigateSpeed;

        Vector3 diff = investigation.position - me.GlobalPosition;
        float distSq = diff.LengthSquared();
        float arriveRange = investigation.range + ArrivalSlack;

        if (distSq < arriveRange * arriveRange && HasLineOfSight(me, investigation.position))
        {
            // Arrived and can see the point — face it and start the pause
            // countdown. Clamp the existing cancelTime down so a very long
            // investigation doesn't keep us parked here past pauseTime.
            Vector2 flat = new Vector2(diff.X, diff.Z);
            if (flat.LengthSquared() > 0.0001f)
            {
                output.yaw = Mathf.Atan2(flat.X, flat.Y);
            }
            ulong pauseUntil = time + investigation.pauseTime;
            if (pauseUntil < investigation.cancelTime)
            {
                investigation.cancelTime = pauseUntil;
            }
            output.investigation = investigation;
        }

        if (time >= investigation.cancelTime)
        {
            output.resetInvestigation = true;
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }

    // Environment-only raycast from the mob's eye to the investigation point.
    // Mirrors the LOS check in UpdatePerception so both systems agree on what
    // counts as visible.
    private static bool HasLineOfSight(Mob me, Vector3 target)
    {
        Vector3 rayStart = me.GlobalPosition + new Vector3(0f, EyeHeight, 0f);
        Vector3 rayEnd = target + new Vector3(0f, EyeHeight, 0f);
        using var query = PhysicsRayQueryParameters3D.Create(rayStart, rayEnd, (uint)ECollisionLayer.Environment);
        query.CollideWithAreas = false;
        query.CollideWithBodies = true;
        var result = me.GetWorld3D().DirectSpaceState.IntersectRay(query);
        return result.Count == 0;
    }
}
