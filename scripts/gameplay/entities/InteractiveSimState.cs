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
    // When true, Torch.Create overrides Active based on world time-of-day at
    // chunk activation: lit at night, unlit during the day. Authored on
    // worldgen-spawned campfires so they "come alive" after dark without the
    // player having to light each one. Player toggles still apply for the
    // duration the chunk is loaded; the next chunk activation re-evaluates.
    public bool AutoLightAtNight;

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

    public override Vector3I? PathBlockerCell => new Vector3I(
        Mathf.FloorToInt(WorldPosition.X),
        Mathf.FloorToInt(WorldPosition.Y),
        Mathf.FloorToInt(WorldPosition.Z));
}

public class TrapSimState : EntitySimState
{
    public bool Disarmed;

    public TrapSimState(Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
    }

    public override Node3D CreateEntity(World world)
    {
        return Trap.Create(world, this);
    }
}

public class SignpostSimState : EntitySimState
{
    // Text shown in the HUD panel when the player interacts. Stored on the
    // sim state so each placed signpost in a world file can carry its own
    // message — the .tscn is shared.
    public string Text;

    public SignpostSimState(Vector3 worldPosition, PackedScene scene, string text)
        : base(worldPosition, scene)
    {
        Text = text ?? string.Empty;
    }

    public override Node3D CreateEntity(World world)
    {
        return Signpost.Create(world, this);
    }
}
