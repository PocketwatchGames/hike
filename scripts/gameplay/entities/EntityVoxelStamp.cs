using System.Collections.Generic;
using Godot;

// A column of voxels an entity owns and keeps in step with itself: a door's
// doorway (Barrier while shut, Opening while open), a window frame's aperture
// (Opening, carved out of the wall the frame stands in).
public readonly struct VoxelStamp
{
    public readonly Vector3I BaseCell;
    public readonly int Height;
    public readonly int Type;
    // Whether this stamp may replace authored geometry. A door must not: a seat
    // position that resolved onto a floor or wall block would erase it and leave
    // a hole to fall through. An aperture is nothing BUT a hole in a wall, so it
    // carves what it covers.
    public readonly bool Carves;

    public VoxelStamp(Vector3I baseCell, int height, int type, bool carves)
    {
        BaseCell = baseCell;
        Height = height;
        Type = type;
        Carves = carves;
    }

    public static readonly VoxelStamp None = new VoxelStamp(Vector3I.Zero, 0, Blocks.AirId, false);

    public bool Any => Height > 0;
}

// A sim state that owns voxels. Resolving is separate from applying so a caller
// can see which cells are about to move — the editor touches them first, which
// is how the stamp lands inside its undo step.
public interface IVoxelStamper
{
    VoxelStamp ResolveStamp(WorldState world);
}

// Reconciles entity-owned voxels with the entities that own them.
//
// Nothing else in the pipeline writes those voxels: worldgen and the editor
// place the entity, and the voxels follow from it. Without the load pass a shut
// door occludes nothing until the player happens to close it once and a window
// frame is a picture hung on a solid wall — and the baked sun field (with every
// vertex bake on top of it) disagrees with what the world visibly is.
//
// Runs alongside FoliageStamper and before LightEngine.Relight, for the same
// reason: the sun pass has to see the occluders to bake them. Separate from that
// walk because this writes VOXELS rather than the non-voxel occlusion fields
// FoliageStamper clears, so it has no ordering relationship with it.
public static class EntityVoxelStamper
{
    public static void Stamp(WorldState world)
    {
        if (world == null)
        {
            return;
        }
        int stamped = 0;
        foreach (List<EntitySimState> bucket in world._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is not IVoxelStamper stamper)
                {
                    continue;
                }
                // Null `changed` — nothing to relight incrementally, the full
                // sun pass runs straight after this.
                if (Apply(world, stamper.ResolveStamp(world), null))
                {
                    stamped++;
                }
            }
        }
        if (stamped > 0)
        {
            GD.Print($"[EntityVoxelStamper] entities={stamped}");
        }
    }

    // The cells a stamp covers, whether or not writing them would change
    // anything — what a caller has to declare before the write (editor undo).
    public static void Cells(VoxelStamp stamp, List<Vector3I> outCells)
    {
        for (int i = 0; i < stamp.Height; i++)
        {
            outCells.Add(new Vector3I(stamp.BaseCell.X, stamp.BaseCell.Y + i, stamp.BaseCell.Z));
        }
    }

    // Writes one stamp, appending the cells it actually changed to `changed` for
    // the caller's relight. False when the stamp is empty, so the load pass can
    // count the entities that own voxels.
    public static bool Apply(WorldState world, VoxelStamp stamp, List<Vector3I> changed)
    {
        if (!stamp.Any)
        {
            return false;
        }
        for (int i = 0; i < stamp.Height; i++)
        {
            var cell = new Vector3I(stamp.BaseCell.X, stamp.BaseCell.Y + i, stamp.BaseCell.Z);
            int existing = world.GetBlockWorld(cell.X, cell.Y, cell.Z);
            if (existing == stamp.Type || !CanOverwrite(existing, stamp.Carves))
            {
                continue;
            }
            world.SetBlockWorld(cell.X, cell.Y, cell.Z, stamp.Type);
            changed?.Add(cell);
        }
        return true;
    }

    // Empty cells and the markers stamps themselves write are always fair game —
    // Opening has to be, or a door that stamped one on opening could never close
    // back to Barrier. Solid geometry only gives way to a carving stamp, and
    // water never does: an aperture punched through a water volume would drain a
    // hole in it rather than make a window.
    private static bool CanOverwrite(int existing, bool carves)
    {
        if (existing == Blocks.AirId || existing == Blocks.BarrierId || existing == Blocks.OpeningId)
        {
            return true;
        }
        return carves && !Blocks.IsWater(existing);
    }
}
