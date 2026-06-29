using Godot;
using System.Collections.Generic;

public class PropSimState : EntitySimState
{
    public readonly PropType Type;

    // Y-axis spawn rotation (radians). Trees and tall grass randomize this in
    // WorldGen so a meadow or forest doesn't read as a grid of identical
    // sprites/models all facing the same way.
    public float RotationY;

    public PropSimState(PropType type, Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
        Type = type;
    }

    // Trees and tall grass are surface scenery roads route around and clear.
    public override bool IsRoadObstacle => true;

    public override Node3D CreateEntity(World world)
    {
        return Type switch
        {
            PropType.Foliage => Foliage.Create(world, this),
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
        // physically covering 3×3 (or 5×5) voxel cells around the trunk. The
        // rasterizer walks every Environment-layer collider on the entity and
        // emits the union of their footprints so A* doesn't route mobs through
        // cells the cylinder blocks.
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}
