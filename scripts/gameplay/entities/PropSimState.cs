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

    // Every prop scene roots on a PropInstance, whatever its PropType — the
    // type is a scatter slot (canopy vs ground cover) and a wire byte, not a
    // behavior. A scene wanting more than a prop's own behavior says so with
    // its root script (see Foliage, for foliage you walk through) and adds an
    // arm here.
    public override Node3D CreateEntity(Sim sim)
    {
        return PropInstance.Create(sim, this);
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
        return new VoxelStamp(cell, height, Blocks.OpeningId, carves: true);
    }

    public override void GetPathBlockerCells(Node3D entity, List<Vector3I> outCells)
    {
        // A prop blocks the cells its solid colliders physically cover — a
        // tree's CylinderShape3D has radius ~1.0–1.5m, so its footprint is a
        // 3×3 (or 5×5) cell disc, not the single cell at the trunk's origin.
        // Not gated on PropType: the rasterizer only collects Solid-layer
        // bodies, so a prop authored without one still emits nothing, and the
        // answer stays "whatever this scene actually stands in the way of".
        PathBlockerRasterizer.Rasterize(entity, Mathf.FloorToInt(WorldPosition.Y), outCells);
    }
}
