using Godot;

public abstract class InteractiveSpawnState : EntitySpawnState
{
    public bool Active = true;

    protected InteractiveSpawnState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
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

    public override Node3D CreateEntity(World world)
    {
        return Door.Create(world, this);
    }
}

public class TorchSpawnState : InteractiveSpawnState
{
    public TorchSpawnState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Torch.Create(world, this);
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

    public override Node3D CreateEntity(World world)
    {
        return Chest.Create(world, this);
    }
}
