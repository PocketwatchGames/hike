using System.Collections.Generic;
using Godot;

// The voxels an entity owns and keeps in step with itself: a door's doorway
// (Barrier while shut, Opening while open), a window frame's aperture (Opening,
// carved out of the wall the frame stands in), a bomb wall's rock (dressed in its
// crack overlay while standing, Air once broken).
public readonly struct VoxelStamp
{
    // `Type` value that leaves each cell's block as it is — a stamp that only
    // dresses what the world already has there.
    public const int KeepBlock = -1;

    public readonly Vector3I[] Cells;
    public readonly int Type;
    // Whether this stamp may replace authored geometry. A door must not: a seat
    // position that resolved onto a floor or wall block would erase it and leave
    // a hole to fall through. An aperture is nothing BUT a hole in a wall, so it
    // carves what it covers.
    public readonly bool Carves;
    // Overlay (an atlas layer) worn on each cell's faces that are open to an
    // empty neighbour; ChunkState.OVERLAY_NONE leaves the overlay channel alone.
    public readonly int OverlayId;

    public VoxelStamp(Vector3I[] cells, int type, bool carves, int overlayId = ChunkState.OVERLAY_NONE)
    {
        Cells = cells;
        Type = type;
        Carves = carves;
        OverlayId = overlayId;
    }

    // A vertical run of `height` cells upward from `baseCell` — a doorway or an
    // aperture.
    public static VoxelStamp Column(Vector3I baseCell, int height, int type, bool carves)
    {
        var cells = new Vector3I[Mathf.Max(0, height)];
        for (int i = 0; i < cells.Length; i++)
        {
            cells[i] = new Vector3I(baseCell.X, baseCell.Y + i, baseCell.Z);
        }
        return new VoxelStamp(cells, type, carves);
    }

    public static readonly VoxelStamp None = new VoxelStamp(System.Array.Empty<Vector3I>(), Blocks.AirId, false);

    public bool Any => Cells != null && Cells.Length > 0;
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
    private static readonly Vector3I[] FaceOffsets =
    {
        Vector3I.Right, Vector3I.Left, Vector3I.Up, Vector3I.Down, Vector3I.Back, Vector3I.Forward,
    };

    private static readonly EVoxelFace[] FaceBits =
    {
        EVoxelFace.PosX, EVoxelFace.NegX, EVoxelFace.PosY, EVoxelFace.NegY, EVoxelFace.PosZ, EVoxelFace.NegZ,
    };

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
        if (stamp.Any)
        {
            outCells.AddRange(stamp.Cells);
        }
    }

    // Writes one stamp, appending the cells it actually changed — block or
    // overlay — to `changed` for the caller's relight and re-mesh. False when the
    // stamp is empty, so the load pass can count the entities that own voxels.
    public static bool Apply(WorldState world, VoxelStamp stamp, List<Vector3I> changed)
    {
        if (!stamp.Any)
        {
            return false;
        }
        foreach (Vector3I cell in stamp.Cells)
        {
            if (!world.IsInBounds(cell.X, cell.Y, cell.Z))
            {
                continue;
            }
            bool moved = false;
            int existing = world.GetBlockWorld(cell.X, cell.Y, cell.Z);
            if (stamp.Type != VoxelStamp.KeepBlock && existing != stamp.Type && CanOverwrite(existing, stamp.Carves))
            {
                world.SetBlockWorld(cell.X, cell.Y, cell.Z, stamp.Type);
                moved = true;
                // An emptied cell keeps no dressing: the mesher votes overlay
                // over a neighbourhood, so a stale id would bleed onto the faces
                // the carve just exposed.
                if (!Blocks.HasGeometry(stamp.Type))
                {
                    world.SetOverlayIdWorld(cell.X, cell.Y, cell.Z, ChunkState.OVERLAY_NONE);
                    world.SetOverlayFacesWorld(cell.X, cell.Y, cell.Z, (int)EVoxelFace.None);
                }
            }
            if (stamp.OverlayId != ChunkState.OVERLAY_NONE && DressCell(world, cell, stamp.OverlayId))
            {
                moved = true;
            }
            if (moved)
            {
                changed?.Add(cell);
            }
        }
        return true;
    }

    // Puts `overlayId` on the faces of `cell` that are open to an empty
    // neighbour. A buried cell has none and is left alone — a 0 face mask means
    // ALL faces (EVoxelFace), so it must never be written as "none".
    private static bool DressCell(WorldState world, Vector3I cell, int overlayId)
    {
        if (!Blocks.HasGeometry(world.GetBlockWorld(cell.X, cell.Y, cell.Z)))
        {
            return false;
        }
        int faces = 0;
        for (int i = 0; i < FaceOffsets.Length; i++)
        {
            Vector3I n = cell + FaceOffsets[i];
            if (world.IsInBounds(n.X, n.Y, n.Z) && !Blocks.HasGeometry(world.GetBlockWorld(n.X, n.Y, n.Z)))
            {
                faces |= (int)FaceBits[i];
            }
        }
        if (faces == 0)
        {
            return false;
        }
        if (world.GetOverlayIdWorld(cell.X, cell.Y, cell.Z) == overlayId
            && world.GetOverlayFacesWorld(cell.X, cell.Y, cell.Z) == faces)
        {
            return false;
        }
        world.SetOverlayIdWorld(cell.X, cell.Y, cell.Z, overlayId);
        world.SetOverlayFacesWorld(cell.X, cell.Y, cell.Z, faces);
        return true;
    }

    // Empty cells and the markers stamps themselves write (Opening, Barrier,
    // ClearBarrier — everything geometry-less but water) are always fair game — Opening
    // has to be, or a door that stamped one on opening could never close back. Solid geometry only gives way to a carving stamp, and
    // water never does: an aperture punched through a water volume would drain a
    // hole in it rather than make a window.
    private static bool CanOverwrite(int existing, bool carves)
    {
        if (!Blocks.HasGeometry(existing) && !Blocks.IsWater(existing))
        {
            return true;
        }
        return carves && !Blocks.IsWater(existing);
    }
}
