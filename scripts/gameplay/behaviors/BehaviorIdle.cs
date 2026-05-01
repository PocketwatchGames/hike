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

    public BehaviorIdle(IdleBehaviorData data)
    {
        _data = data;
    }

    public override BehaviorOutput Run(Mob me, ulong time, ref PerceptionState targetPerception, ref AIOutput output)
    {
        if (TryTransitions(me, time, ref targetPerception, out StringName destination))
        {
            return new BehaviorOutput(EBehaviorResult.RunNewBehavior, destination);
        }

        output.useTorch = me.ambientLight < MobSimState.TorchAmbientThreshold;

        Vector3 toSpawn = me.spawnPosition - me.GlobalPosition;
        toSpawn.Y = 0f;
        if (toSpawn.LengthSquared() > ReturnToSpawnDistance * ReturnToSpawnDistance)
        {
            output.pathTarget = me.spawnPosition;
            output.speed = ReturnSpeed;
            output.pathSuccessDistance = PathSuccessDistance;
        }
        else
        {
            output.speed = 0f;
            output.yaw = me.spawnRotationY;
            output.suspendTimeMs = time + 100;
        }
        return new BehaviorOutput(EBehaviorResult.Running);
    }
}
