using Godot;

public class DoorSimState : EntitySimState
{
    public bool Active = true;
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

public class TorchSimState : EntitySimState
{
    public bool Active = true;

    public TorchSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Torch.Create(world, this);
    }
}

public class ChestSimState : EntitySimState
{
    public bool Active = true;
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
