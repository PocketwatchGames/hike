using Godot;
using System.Collections.Generic;

public class PropSimState : EntitySimState, IVoxelStamper
{
    public readonly PropType Type;

    // RotationY lives on EntitySimState. WorldGen randomizes it for trees and
    // tall grass so a meadow doesn't read as a grid of identical sprites all
    // facing the same way.

    public PropSimState(PropType type, Vector3 worldPosition, PackedScene scene)
        : base(worldPosition, scene)
    {
        Type = type;
    }

    // Trees and tall grass are surface scenery roads route around and clear.
    public override bool IsRoadObstacle => true;

    public override Node3D CreateEntity(Sim sim)
    {
        return Type switch
        {
            PropType.Foliage => Foliage.Create(sim, this),
            _ => PropInstance.Create(sim, this),
        };
    }

    // Aperture props (window frames) carve the wall they stand in; every other
    // prop resolves to nothing. The carved column starts at the frame's own
    // cell, so where the frame is IS where the hole is — moving one in the
    // editor moves the hole it stamps on the next load.
    public VoxelStamp ResolveStamp(WorldState world)
    {
        if (Type == PropType.Foliage)
        {
            return VoxelStamp.None;
        }
        int height = PropInstance.GetApertureHeight(Scene);
        if (height <= 0)
        {
            return VoxelStamp.None;
        }
        var cell = new Vector3I(
            Mathf.FloorToInt(WorldPosition.X),
            Mathf.FloorToInt(WorldPosition.Y),
            Mathf.FloorToInt(WorldPosition.Z));
        return new VoxelStamp(cell, height, VoxelType.Opening, carves: true);
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
