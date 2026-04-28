using Godot;

public enum EPlayerPerceptionState
{
    Hidden,
    Detected,
    Discovered
}

public class MobSimState : EntitySimState
{
    public readonly MobData MobData;

    // Mutable runtime sim state — this is the canonical source of truth for the
    // mob; the Mob node is just a view that reads from and writes back to it.
    // RotationY (and inherited WorldPosition) are kept current by Mob.SyncToSimState
    // before the node is freed on chunk unload.
    public float RotationY;
    // Authored spawn transform, used by Idle to send the mob home after it has
    // been pulled away (combat, wander) and to restore its original facing.
    // Captured at construction from the initial WorldPosition/RotationY, so it
    // reflects where the mob first appeared rather than where it is currently.
    public readonly Vector3 SpawnPosition;
    public readonly float SpawnRotationY;
    // Optional per-mob override for the behavior the mob starts in (and returns to
    // when a behavior returns Complete). Null means use the brain's idleBehavior.
    public StringName InitialBehavior;
    public bool Alive;
    // Burrow is a two-phase state machine: Burrowing is the descent window
    // after aiOutput.burrow first goes true, BurrowTimeMs is the absolute
    // GameTimeMs at which the descent completes, and Burrowed is the fully-
    // hidden state once the countdown elapses. All three clear the moment
    // aiOutput.burrow stops being set.
    public bool Burrowing;
    public ulong BurrowTimeMs;
    public bool Burrowed;
    public float MaxHealth;
    public float Health;
    public float PlayerPerception;
    public ulong MemoryTimeMs;
    public ulong VisibleTimeMs;
    public EPlayerPerceptionState DiscoveryState;
    public InvestigateState? Investigation;
    public bool Yelled;
    public ulong SuspendAITimeMs;
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
        : this(worldPosition, rotationY, worldPosition, rotationY, scene, mobData)
    {
    }

    // Full constructor used by the deserializer so a mob restored from disk
    // keeps its authored spawn transform even if its current position has drifted.
    public MobSimState(Vector3 worldPosition, float rotationY, Vector3 spawnPosition, float spawnRotationY, PackedScene scene, MobData mobData)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        SpawnPosition = spawnPosition;
        SpawnRotationY = spawnRotationY;
        MobData = mobData;
        Alive = true;
        MaxHealth = 1f;
        Health = 1f;
        PlayerPerception = 0f;
        DiscoveryState = EPlayerPerceptionState.Hidden;
        MemoryTimeMs = 0;
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
