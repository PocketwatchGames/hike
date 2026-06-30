using Godot;

// Resting state for a flying mob: perched on a claimed perch (body frozen
// there by SettleOnPerch) or simply standing on the ground. Never airborne —
// flight is travel-only, so the bird only leaves this state via a transition
// into a flight behavior. Mirrors the suspend-when-still pattern of
// BehaviorIdle so a calm bird is cheap to tick.
public partial class BehaviorPerch : BehaviorBase
{
    private readonly PerchBehaviorData _data;
    private ulong _nextAlarmMs;

    public BehaviorPerch(PerchBehaviorData data)
    {
        _data = data;
    }

    // Fresh look each time we settle — sound the first alarm promptly if a
    // threat is already in range, rather than waiting out a stale interval.
    public override void OnEnter(Mob me, ulong time)
    {
        _nextAlarmMs = 0;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.airborne = false;
        output.speed = 0f;
        if (me.ClaimedPerch != null)
        {
            output.yaw = me.ClaimedPerch.facingYaw;
        }

        // Aware of a threat: sound periodic alarm calls at it while it's within
        // the (horizontal) yell distance — the bird raises the alarm before the
        // proximity-gated Perch->FlyFlee transition makes it bolt. Don't suspend
        // the AI while aware so the alarm interval keeps ticking.
        Player threat = targetPerception.pawnTarget;
        if (threat != null)
        {
            Vector3 to = threat.GlobalPosition - me.GlobalPosition;
            Vector2 toXZ = new Vector2(to.X, to.Z);
            float distXZ = toXZ.Length();

            // Turn to face the threat rather than keeping the perch's fixed
            // facing — an alarmed bird looks at what it's yelling at. Overrides
            // the ClaimedPerch.FacingYaw set above. (atan2(x, z) matches the
            // yaw convention in Mob._PhysicsProcess.)
            if (distXZ > 0.0001f)
            {
                output.yaw = Mathf.Atan2(toXZ.X, toXZ.Y);
            }

            if (distXZ < _data.yellDistance && time >= _nextAlarmMs)
            {
                output.vocalization = EVocalization.Yell;
                output.targetPos = threat.GlobalPosition;
                _nextAlarmMs = time + (ulong)(_data.alarmIntervalSeconds * 1000f);
            }
        }
        else
        {
            // Calm — idle-LOD suspend like the other resting behaviors.
            output.suspendTimeMs = time + 100;
        }
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
