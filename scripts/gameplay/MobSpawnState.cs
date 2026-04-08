using Godot;

public class MobSpawnState : EntitySpawnState
{
    public readonly float RotationY;
    public bool Alive;
    public float MaxHealth;
    public float Health;
    public float Aggro;
    public MobData MobData;

    public MobSpawnState(Vector3 worldPosition, float rotationY, PackedScene scene, MobData mobData)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
        MobData = mobData;
        Alive = true;
        MaxHealth = 1f;
        Health = 1f;
        Aggro = 0f;
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
