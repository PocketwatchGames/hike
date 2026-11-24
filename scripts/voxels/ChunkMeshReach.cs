using System;
using System.Collections.Generic;
using Godot;

// Which chunk meshes read a given voxel — the set an edit must re-mesh. A chunk
// build reads past its own 16³ (seam faces, AO, the sun/openness march, ledge
// probes, detail-scatter edge walks), each mesher declares how far next to the
// constants that decide it, and this unions them. `debug_mesh_reach` checks the
// declarations against what a build actually reads.
//
// Sunlight is not an input here: in-window terrain shades with the live light
// volume times a static openness term, so a relight needs no re-mesh. The baked
// sun only feeds visual-only backdrop chunks, which are built fresh on load.
public static class ChunkMeshReach
{
    private const int N = ChunkState.SIZE;

    public static void Resolve(out Vector3I below, out Vector3I above)
    {
        below = Vector3I.One * Math.Max(ChunkMesherDC.READ_BELOW, Math.Max(WaterMesher.READ_REACH, WaterWallMesher.READ_REACH));
        above = Vector3I.One * Math.Max(ChunkMesherDC.READ_ABOVE, Math.Max(WaterMesher.READ_REACH, WaterWallMesher.READ_REACH));
        foreach (LedgeBarrierClasses.Entry ledgeClass in LedgeBarrierClasses.All)
        {
            LedgeBarrierMesher.ReadReach(ledgeClass.MaxFallVoxels, out Vector3I ledgeBelow, out Vector3I ledgeAbove);
            below = below.Max(ledgeBelow);
            above = above.Max(ledgeAbove);
        }
        ChunkDetailScatter.ReadReach(ChunkMesh.ActiveDetailGroups, out Vector3I scatterBelow, out Vector3I scatterAbove);
        below = below.Max(scatterBelow);
        above = above.Max(scatterAbove);
    }

    // Every chunk whose mesh reads one of `cells`, with the voxels already
    // written. Besides the declared reach, a cell is read by the water under it:
    // WaterMesher decides whether a column is roofed by climbing its water run
    // to the first non-water voxel, which may be this one, any distance above.
    public static HashSet<Vector3I> ChunksReading(WorldState world, IEnumerable<Vector3I> cells)
    {
        Resolve(out Vector3I below, out Vector3I above);
        var chunks = new HashSet<Vector3I>();
        foreach (Vector3I cell in cells)
        {
            AddChunksReading(cell, cell, below, above, chunks);
            int bottom = cell.Y;
            while (Blocks.IsWater(world.GetBlockWorld(cell.X, bottom - 1, cell.Z)))
            {
                bottom--;
            }
            if (bottom < cell.Y)
            {
                AddChunksReading(new Vector3I(cell.X, bottom, cell.Z), new Vector3I(cell.X, cell.Y - 1, cell.Z), below, above, chunks);
            }
        }
        return chunks;
    }

    // Every chunk whose mesh reads anything in the box — for an edit that moves
    // a non-voxel input over a region (a roof's SunOpaque cover).
    public static HashSet<Vector3I> ChunksReading(VoxelBox box)
    {
        Resolve(out Vector3I below, out Vector3I above);
        var chunks = new HashSet<Vector3I>();
        AddChunksReading(box.Min, box.Max, below, above, chunks);
        return chunks;
    }

    // Chunk k reads [k*N - below, k*N + N-1 + above] on each axis, so it reads
    // something in [lo, hi] when k*N - below <= hi and k*N + N-1 + above >= lo.
    private static void AddChunksReading(Vector3I lo, Vector3I hi, Vector3I below, Vector3I above, HashSet<Vector3I> into)
    {
        Vector3I first = new Vector3I(
            FloorDiv(lo.X - above.X, N),
            FloorDiv(lo.Y - above.Y, N),
            FloorDiv(lo.Z - above.Z, N));
        Vector3I last = new Vector3I(
            FloorDiv(hi.X + below.X, N),
            FloorDiv(hi.Y + below.Y, N),
            FloorDiv(hi.Z + below.Z, N));
        for (int x = first.X; x <= last.X; x++)
        {
            for (int y = first.Y; y <= last.Y; y++)
            {
                for (int z = first.Z; z <= last.Z; z++)
                {
                    into.Add(new Vector3I(x, y, z));
                }
            }
        }
    }

    private static int FloorDiv(int a, int b)
    {
        return (int)Math.Floor(a / (double)b);
    }
}
