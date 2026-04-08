using Godot;

public abstract class InteractiveSimState : EntitySimState
{
    public bool Active = true;

    protected InteractiveSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }
}

public class DoorSimState : InteractiveSimState
{
    public readonly float RotationY;

    public DoorSimState(Vector3 worldPosition, float rotationY, PackedScene scene)
        : base(worldPosition, scene)
    {
        RotationY = rotationY;
    }

    public override Node3D CreateEntity(World world)
    {
        return Door.Create(world, this);
    }
}

public class TorchSimState : InteractiveSimState
{
    public TorchSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Torch.Create(world, this);
    }
}

public class ChestSimState : InteractiveSimState
{
    public readonly int LootCount;
    public readonly PackedScene LootScene;

    public ChestSimState(Vector3 worldPosition, PackedScene scene, int lootCount, PackedScene lootScene)
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
