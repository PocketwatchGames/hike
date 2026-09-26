using Godot;

public partial class BehaviorIdle : BehaviorBase
{
    // Distance from spawn beyond which the mob walks home instead of standing
    // still. Small enough that normal idle jostling doesn't trigger a return,
    // large enough to cover the patrol radius used by BehaviorWander.
    private const float ReturnToSpawnDistance = 1.0f;
    private const float ReturnSpeed = 0.25f;
    private const float PathSuccessDistance = 0.5f;

    private readonly IdleBehaviorData _data;
    private ulong _retryHomeAtMs;

    public BehaviorIdle(IdleBehaviorData data)
    {
        _data = data;
    }

    public override void OnEnter(Mob me, ulong time)
    {
        _retryHomeAtMs = 0;
    }

    public override string DebugStatus(ulong time)
    {
        return time < _retryHomeAtMs ? $"home unreachable, retry {(_retryHomeAtMs - time) / 1000f:F1}s" : null;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }


        MobNavigator nav = me.Navigator;
        Vector3 toSpawn = me.spawnPosition - me.GlobalPosition;
        toSpawn.Y = 0f;
        if (toSpawn.LengthSquared() > ReturnToSpawnDistance * ReturnToSpawnDistance && nav != null)
        {
            // A failed plan leaves the navigator steering straight at the goal
            // with no walkability check — into the tree between here and home.
            // Stand instead, and plan again after a pause.
            if (nav.IsBlocked)
            {
                nav.Stop();
                _retryHomeAtMs = time + (ulong)(_data.unreachableHomeRetrySeconds * 1000f);
            }
            if (time >= _retryHomeAtMs)
            {
                nav.Goto(me.spawnPosition, PathSuccessDistance);
                output.speed = ReturnSpeed;
                return new BehaviorOutput(EBehaviorResult.Running);
            }
        }
        nav?.Stop();
        output.speed = 0f;
        output.yaw = me.spawnRotationY;
        output.suspendTimeMs = time + 100;
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
