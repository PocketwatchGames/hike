using Godot;

public abstract class InteractiveSpawnState
{
    public readonly Vector3 WorldPosition;
    public readonly PackedScene Scene;
    public bool Active = true;

    protected InteractiveSpawnState(Vector3 worldPosition, PackedScene scene)
    {
        WorldPosition = worldPosition;
        Scene = scene;
    }
}

public class DoorSpawnState : InteractiveSpawnState
{
    public readonly float RotationY;

    public DoorSpawnState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }
}

public class TorchSpawnState : InteractiveSpawnState
{
    public TorchSpawnState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }
}

public class ChestSpawnState : InteractiveSpawnState
{
    public readonly int LootCount;
    public readonly PackedScene LootScene;

    public ChestSpawnState(Vector3 worldPosition, PackedScene scene, int lootCount, PackedScene lootScene)
        : base(worldPosition, scene)
    {
        LootCount = lootCount;
        LootScene = lootScene;
    }
}
