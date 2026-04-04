using Godot;

public class InteractiveSpawnState
{
    public readonly InteractiveType Type;
    public readonly Vector3 WorldPosition;
    public readonly float RotationY;
    public readonly PackedScene Scene;
    public bool Active = true;

    // Chest-specific
    public readonly int LootCount;
    public readonly PackedScene LootScene;

    public InteractiveSpawnState(InteractiveType type, Vector3 worldPosition, float rotationY, PackedScene scene)
    {
        Type = type;
        WorldPosition = worldPosition;
        RotationY = rotationY;
        Scene = scene;
    }

    public InteractiveSpawnState(InteractiveType type, Vector3 worldPosition, float rotationY, PackedScene scene,
        int lootCount, PackedScene lootScene)
        : this(type, worldPosition, rotationY, scene)
    {
        LootCount = lootCount;
        LootScene = lootScene;
    }
}
