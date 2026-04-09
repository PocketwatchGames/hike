using Godot;

public enum EPlayerPerceptionState
{
    Hidden,
    Detected,
    Seen
}

public class MobSimState : EntitySimState
{
    public readonly MobData MobData;

    // Mutable runtime sim state — this is the canonical source of truth for the
    // mob; the Mob node is just a view that reads from and writes back to it.
    // RotationY (and inherited WorldPosition) are kept current by Mob.SyncToSimState
    // before the node is freed on chunk unload.
    public float RotationY;
    public bool Alive;
    public float MaxHealth;
    public float Health;
    public float PlayerPerception;
    public ulong PlayerPerceptionRelaxationTimeMs;
    public EPlayerPerceptionState PlayerPerceptionState;
    public InvestigateState? Investigation;
    public bool Yelled;
    // One perception slot per potential target. Currently sized to 1 (the player);
    // the array shape is kept so multiplayer can add slots without reshuffling.
    public PerceptionState[] PerceptionTargets = new PerceptionState[1];

    // UpdatePerception is throttled to PerceptionTickInterval seconds. Each frame
    // accumulates delta into PerceptionTickAccumulator; when it overflows the
    // interval, UpdatePerception runs with the accumulated delta and the
    // accumulator is reset. The accumulator is seeded with a random offset at
    // construction so different mobs raycast on different frames (jitter).
    public const float PerceptionTickInterval = 0.1f;
    public float PerceptionTickAccumulator;

    public MobSimState(Vector3 worldPosition, float rotationY, PackedScene scene, MobData mobData)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        MobData = mobData;
        Alive = true;
        MaxHealth = 1f;
        Health = 1f;
        PlayerPerception = 0f;
        PlayerPerceptionState = EPlayerPerceptionState.Hidden;
        PlayerPerceptionRelaxationTimeMs = 0;
        PerceptionTickAccumulator = (float)GD.RandRange(0.0, PerceptionTickInterval);
    }

    public override Node3D CreateEntity(World world)
    {
        if (!Alive)
        {
            return null;
        }
        return Mob.Create(world, this);
    }
}
