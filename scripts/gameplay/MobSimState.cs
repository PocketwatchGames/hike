using Godot;

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
    public float Aggro;
    public EAggroState AggroState;
    // Game-time absolute (WorldState.GameTimeMs based) at which alert relaxes.
    // Persistent because game time is persistent.
    public ulong AlertRelaxationTimeMs;

    public MobSimState(Vector3 worldPosition, float rotationY, PackedScene scene, MobData mobData)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        MobData = mobData;
        Alive = true;
        MaxHealth = 1f;
        Health = 1f;
        Aggro = 0f;
        AggroState = EAggroState.Idle;
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
