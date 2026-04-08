using Godot;

public abstract class EntitySpawnState
{
    public readonly Vector3 WorldPosition;
    public readonly PackedScene Scene;

    protected EntitySpawnState(Vector3 worldPosition, PackedScene scene)
    {
        WorldPosition = worldPosition;
        Scene = scene;
    }

    // Returns null if this spawn state should not materialize an entity right now
    // (e.g. picked up loot, dead mob).
    public abstract Node3D CreateEntity(World world);
}
