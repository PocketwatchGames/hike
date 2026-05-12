using Godot;
using System.Collections.Generic;

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

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        if (Type != PropType.Tree)
        {
            return;
        }
        // Trees have a CylinderShape3D collider with radius ~1.0–1.5m,
        // physically covering 3×3 (or 5×5) voxel cells around the trunk.
        // The older single-cell registration left A* routing mobs through
        // the adjacent cells the cylinder still blocks; the rasterizer
        // walks every Environment-layer collider on the entity and emits
        // the union of their footprints.
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}
