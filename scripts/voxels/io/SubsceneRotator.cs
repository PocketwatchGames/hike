using System;
using Godot;

// Turns a SubsceneState by whole quarter-turns about +Y. Every channel turns
// together — voxels, the presence mask, the per-voxel sharp-axis shape (X and
// Z trade places), the env subgrid, the anchor and every entity — so a rotated
// scene is the same building facing a different way rather than a sheared one.
//
// Rotating up front (rather than inside the stamper) is what keeps the rest of
// the pipeline rotation-unaware: the result is an ordinary SubsceneState whose
// Size and Anchor already describe the turned footprint, so footprint
// reservation, plateau sampling and entity eviction all measure the true shape
// with no special casing.
//
// The scene spins about its ANCHOR — the anchored point stays where it is and
// the bbox swings around it — which is why a scene anchored at a corner covers
// different ground once turned.
//
// The source state is CONSUMED: its entities are rotated in place and moved
// onto the result. Read a fresh state from disk to rotate the same file again.
public static class SubsceneRotator
{
    // quarterTurns counts 90° steps about +Y, the same sense as an entity's
    // RotationY: a wall authored facing +Z faces +X after one turn. Any
    // integer is accepted; 0 (or a multiple of 4) returns the source untouched.
    public static SubsceneState Rotate(SubsceneState src, int quarterTurns)
    {
        int turns = Wrap4(quarterTurns);
        if (turns == 0)
        {
            return src;
        }

        Vector3I size = src.Size;
        Vector3I dstSize = turns % 2 == 0 ? size : new Vector3I(size.Z, size.Y, size.X);
        var dst = new SubsceneState(dstSize);

        for (int lx = 0; lx < size.X; lx++)
        {
            for (int lz = 0; lz < size.Z; lz++)
            {
                RotateCell(lx, lz, turns, size, out int nx, out int nz);
                for (int ly = 0; ly < size.Y; ly++)
                {
                    dst.Voxels[nx, ly, nz] = src.Voxels[lx, ly, lz];
                    dst.Shape[nx, ly, nz] = RotateShape(src.Shape[lx, ly, lz], turns);
                    dst.TerrainId[nx, ly, nz] = src.TerrainId[lx, ly, lz];
                    dst.OverlayId[nx, ly, nz] = src.OverlayId[lx, ly, lz];
                    dst.DetailGroup[nx, ly, nz] = src.DetailGroup[lx, ly, lz];
                    dst.DetailStrength[nx, ly, nz] = src.DetailStrength[lx, ly, lz];
                    dst.PresenceMask[nx, ly, nz] = src.PresenceMask[lx, ly, lz];
                }
            }
        }

        if (src.EnvTag != null)
        {
            dst.EnsureEnvTag();
            RotateEnvGrid(src.EnvTag, dst.EnvTag, dst.EnvSize, dstSize, turns);
        }
        if (src.Interiorness != null)
        {
            dst.EnsureInteriorness();
            RotateEnvGrid(src.Interiorness, dst.Interiorness, dst.EnvSize, dstSize, turns);
        }

        dst.Anchor = RotatePoint(src.Anchor, turns, size);

        if (src.Entities != null)
        {
            Func<Vector3, Vector3> map = p => RotatePoint(p, turns, size);
            foreach (EntitySimState e in src.Entities)
            {
                e.RotateQuarterTurns(turns, map);
                dst.Entities.Add(e);
            }
            src.Entities.Clear();
        }

        return dst;
    }

    // Maps a point out of a box of `size` into that box turned `turns` quarter
    // turns about +Y, re-origined so the result still starts at (0,0,0). Its own
    // inverse when applied with (4 - turns) against the turned size.
    public static Vector3 RotatePoint(Vector3 p, int turns, Vector3I size)
    {
        switch (Wrap4(turns))
        {
            case 1: return new Vector3(p.Z, p.Y, size.X - p.X);
            case 2: return new Vector3(size.X - p.X, p.Y, size.Z - p.Z);
            case 3: return new Vector3(size.Z - p.Z, p.Y, p.X);
            default: return p;
        }
    }

    private static void RotateCell(int lx, int lz, int turns, Vector3I size, out int nx, out int nz)
    {
        switch (turns)
        {
            case 1: nx = lz; nz = size.X - 1 - lx; break;
            case 2: nx = size.X - 1 - lx; nz = size.Z - 1 - lz; break;
            case 3: nx = size.Z - 1 - lz; nz = lx; break;
            default: nx = lx; nz = lz; break;
        }
    }

    // The sharp-axis mask names axes, so X and Z swap on an odd turn. Y is the
    // rotation axis and never moves.
    private static byte RotateShape(byte shape, int turns)
    {
        if (turns % 2 == 0)
        {
            return shape;
        }
        var src = (VoxelTypeInfo.SharpAxes)shape;
        VoxelTypeInfo.SharpAxes rotated = src & VoxelTypeInfo.SharpAxes.Y;
        if ((src & VoxelTypeInfo.SharpAxes.X) != 0) { rotated |= VoxelTypeInfo.SharpAxes.Z; }
        if ((src & VoxelTypeInfo.SharpAxes.Z) != 0) { rotated |= VoxelTypeInfo.SharpAxes.X; }
        return (byte)rotated;
    }

    // Pulled per destination cell rather than pushed per source cell: an env
    // cell covers 4³ voxels, so unless the scene's size is a multiple of 4 the
    // two grids don't line up after a turn. Sampling by cell centre (and
    // clamping) gives every destination cell a defined source instead of
    // leaving a seam of untouched cells along the ragged edge.
    private static void RotateEnvGrid(byte[,,] src, byte[,,] dst, Vector3I dstCells, Vector3I dstSize, int turns)
    {
        const int S = ChunkState.ENV_VOXELS_PER_CELL;
        int srcCellsX = src.GetLength(0);
        int srcCellsY = src.GetLength(1);
        int srcCellsZ = src.GetLength(2);
        for (int cx = 0; cx < dstCells.X; cx++)
        {
            for (int cy = 0; cy < dstCells.Y; cy++)
            {
                for (int cz = 0; cz < dstCells.Z; cz++)
                {
                    var centre = new Vector3(cx * S + S * 0.5f, cy * S + S * 0.5f, cz * S + S * 0.5f);
                    Vector3 srcVoxel = RotatePoint(centre, 4 - turns, dstSize);
                    int sx = Mathf.Clamp(Mathf.FloorToInt(srcVoxel.X / S), 0, srcCellsX - 1);
                    int sy = Mathf.Clamp(Mathf.FloorToInt(srcVoxel.Y / S), 0, srcCellsY - 1);
                    int sz = Mathf.Clamp(Mathf.FloorToInt(srcVoxel.Z / S), 0, srcCellsZ - 1);
                    dst[cx, cy, cz] = src[sx, sy, sz];
                }
            }
        }
    }

    private static int Wrap4(int turns)
    {
        return ((turns % 4) + 4) % 4;
    }
}
