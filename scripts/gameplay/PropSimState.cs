using Godot;

public class PropSimState : EntitySimState
{
    public readonly PropType Type;
    public bool PickedUp;

    public PropSimState(PropType type, Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
        Type = type;
    }

    public override Node3D CreateEntity(World world)
    {
        if (PickedUp)
        {
            return null;
        }

        return Type switch
        {
            PropType.TallGrass => TallGrass.Create(world, this),
            PropType.Loot => Loot.Create(world, this),
            _ => PropInstance.Create(world, this),
        };
    }
}
