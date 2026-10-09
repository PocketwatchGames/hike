using Godot;
using System.Collections.Generic;

public class PropSimState : EntitySimState, IVoxelStamper, IBreakableSimState
{
    public readonly PropType Type;

    // Every prop carries one: whether a prop CAN break is its scene's say (a
    // Destructible), and every path that places props — the prop library, the
    // painter's prop lists, foliage scatter, stamped scenes — then gets it free.
    public BreakState Break { get; } = new();

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
}
