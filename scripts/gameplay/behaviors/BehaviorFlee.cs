using Godot;

public partial class BehaviorFlee : BehaviorBase
{
    private const float PathSuccessDistance = 1f;
    private const float FleeSpeed = 1f;

    private readonly FleeBehaviorData _data;
    private ulong _pauseUntilMs;
    private ulong _pathTimeoutMs;
    private Vector3? _fleePoint;
    // Where the threat last was. TickAI zeroes the perception slot a behavior
    // sees the moment nothing is `triggered`, so a mob that loses track
    // mid-flight would otherwise have nothing to run from and stall in the
    // open. Keep running from the last known position; a brain that wants the
    // mob to calm down instead leaves via its own aggro-lost transition.
    private Vector3? _threatPosition;

    public BehaviorFlee(FleeBehaviorData data)
    {
        _data = data;
    }

    // Reset cross-tick state on re-entry. A stale flee point can be aimed
    // at a direction relative to a previous threat that's no longer the
    // current one (different perception target, different angle); same
    // for the per-leg pause so a mob doesn't carry over a half-finished
    // breather between separate flee sessions.
    public override void OnEnter(Mob me, ulong time)
    {
        _fleePoint = null;
        _pauseUntilMs = 0;
        _pathTimeoutMs = 0;
        _threatPosition = null;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        Player target = targetPerception.pawnTarget;
        if (target != null)
        {
            _threatPosition = target.GlobalPosition;
        }
        if (!_threatPosition.HasValue)
        {
            // Entered without ever having a threat. Stand still and let a
            // transition pull us out next tick.
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Yell once on first sighting so nearby mobs also investigate. Mob's
        // AIOutput processing flips _simState.Yelled when the yell actually
        // fires; MobAI clears it again when perception drops so the next
        // engagement yells again.
        if (target != null && !me.yelled && targetPerception.canSee)
        {
            output.vocalization = EVocalization.Yell;
            output.targetPos = target.GlobalPosition;
        }

        Vector3 diff = _threatPosition.Value - me.weaponPosition;

        if (!_fleePoint.HasValue && time >= _pauseUntilMs)
        {
            // Angle facing away from the threat, jittered by ±90° so a pack of
            // mobs doesn't all flee into the same corridor.
            float angleFromTarget = Mathf.Atan2(diff.X, diff.Z) + Mathf.Pi;
            float jitter = ((float)GD.Randf() - 0.5f) * Mathf.Pi;
            float fleeAngle = angleFromTarget + jitter;

            Vector3 candidate = me.GlobalPosition
                + new Vector3(Mathf.Sin(fleeAngle), 0f, Mathf.Cos(fleeAngle)) * _data.fleeRange;

            _fleePoint = candidate;
            _pathTimeoutMs = time + (ulong)(_data.pathTimeoutSeconds * 1000f);
        }

        if (_fleePoint.HasValue)
        {
            Vector3 toPoint = _fleePoint.Value - me.GlobalPosition;
            toPoint.Y = 0f;
            if (toPoint.Length() > PathSuccessDistance && time < _pathTimeoutMs)
            {
                output.pathTarget = _fleePoint.Value;
                output.speed = FleeSpeed;
                output.pathSuccessDistance = 0.5f;
            }
            else
            {
                double pauseSeconds = GD.RandRange((double)_data.pauseTimeRange.X, (double)_data.pauseTimeRange.Y);
                _pauseUntilMs = time + (ulong)(pauseSeconds * 1000.0);
                _fleePoint = null;
            }
        }

        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
