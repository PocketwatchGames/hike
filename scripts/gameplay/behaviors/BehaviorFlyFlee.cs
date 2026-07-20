using Godot;

// Flight-flee for a flying mob: on entry, take off (unfreeze / release the
// current perch), pick a destination once — a free perch in the away-from-
// threat direction, or a point out along that direction as a ground-landing
// fallback — then fly there and land. Completing returns control to the
// brain's default behavior (typically Perch). Flight is travel-only: this
// behavior always resolves to a landing rather than parking in the air.
public partial class BehaviorFlyFlee : BehaviorBase
{
    // 3D arrival radius at which the bird commits to landing.
    private const float LandDistance = 1.5f;
    private const float FleeSpeed = 1f;

    private readonly FlyFleeBehaviorData _data;
    private Vector3? _destination;
    private Perch _targetPerch;
    private ulong _pathTimeoutMs;

    public BehaviorFlyFlee(FlyFleeBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        _destination = null;
        _targetPerch = null;
        _pathTimeoutMs = 0;
        // Arm the shared reaction clock IncomingProjectileCondition checks (same
        // channel the grounded dodge uses) so a volley can't re-flee this bird
        // every tick. Timed from takeoff, like the dodge — no wait for landing.
        me.ReactionReadyMs = time + (ulong)(_data.reactionCooldownSeconds * 1000f);
        // Take off from any perch we were resting on so the body can move.
        me.LeavePerch();
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        // Alarm once when the player first closes into flee distance (this
        // behavior only runs once Perch -> FlyFlee fired on proximity). The
        // yelled latch (cleared by MobAI when perception drops) keeps it to one
        // alarm per encounter, so the bird doesn't chirp on every perch hop.
        Player threat = targetPerception.pawnTarget;
        if (threat != null && !me.yelled && targetPerception.canSee)
        {
            output.vocalization = EVocalization.Yell;
            output.targetPos = threat.GlobalPosition;
        }

        if (!_destination.HasValue)
        {
            Vector3 fleeDir = ComputeFleeDir(me, ref targetPerception);
            Perch perch = me.Sim?.Perches.FindFleePerch(
                me.GlobalPosition, fleeDir, _data.minPerchRange, _data.maxPerchRange, _data.perchConeDot);
            if (perch != null && perch.TryClaim(me))
            {
                _targetPerch = perch;
                _destination = perch.WorldPosition;
            }
            else
            {
                _destination = me.GlobalPosition + fleeDir * _data.fleeRange;
            }
            _pathTimeoutMs = time + (ulong)(_data.pathTimeoutSeconds * 1000f);
        }

        // Arrival is horizontal: the bird cruises at hover altitude, so the
        // vertical gap to an elevated perch (or to the ground) would otherwise
        // keep 3D distance above the threshold forever. Once it's over the
        // destination it commits to landing (snapping onto the perch, or
        // dropping to the ground for the fallback).
        Vector3 toDest = _destination.Value - me.GlobalPosition;
        float distXZ = new Vector2(toDest.X, toDest.Z).Length();
        bool arrived = distXZ <= LandDistance;
        if (!arrived && time < _pathTimeoutMs)
        {
            output.airborne = true;
            output.pathTarget = _destination.Value;
            output.speed = FleeSpeed;
            output.pathSuccessDistance = LandDistance;
            return new BehaviorOutput(EBehaviorResult.Running);
        }

        // Arrived or timed out — land. Only snap onto the perch if we actually
        // reached it; on a timeout we may be far away, so just settle to the
        // ground in place (and release the perch we never reached).
        if (arrived && _targetPerch != null)
        {
            me.SettleOnPerch(_targetPerch);
        }
        else
        {
            _targetPerch?.Release(me);
        }
        output.airborne = false;
        output.speed = 0f;
        return new BehaviorOutput(EBehaviorResult.Complete);
    }

    private static Vector3 ComputeFleeDir(Mob me, ref PerceptionState targetPerception)
    {
        Player threat = targetPerception.pawnTarget;
        if (threat != null)
        {
            Vector3 away = me.GlobalPosition - threat.GlobalPosition;
            away.Y = 0f;
            if (away.LengthSquared() > 1e-4f)
            {
                return away.Normalized();
            }
        }
        float a = (float)GD.RandRange(0.0, Mathf.Tau);
        return new Vector3(Mathf.Sin(a), 0f, Mathf.Cos(a));
    }
}
