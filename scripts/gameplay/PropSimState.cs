using Godot;

public class PropSimState : EntitySimState
{
    public readonly PropType Type;
    public bool PickedUp;

    // For PropType.Loot: the item carried by this drop. Null on world-spawned
    // generic loot (legacy worldgen drops). Player-dropped loot has it set so
    // pickup can deposit the item back into the player's inventory. Not yet
    // serialized — phase 1 doesn't persist inventory across save/load.
    public ItemState Item;

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
            PropType.AutoLoot => AutoLoot.Create(world, this),
            PropType.Loot => Loot.Create(world, this),
            _ => PropInstance.Create(world, this),
        };
    }
}
