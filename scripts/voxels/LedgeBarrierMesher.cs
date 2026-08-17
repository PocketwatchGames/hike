using System.Collections.Generic;
using Godot;

// Builds the invisible barriers that stand at the top edge of every drop taller
// than a legal step, so a body simply cannot walk off one.
//
// This is the geometric alternative to constraining velocity per tick. The
// appeal is not that it is cheaper — it is that "slide along the edge instead
// of falling" stops being something we compute at all. The barrier IS a wall,
// so MoveAndSlide produces the slide, with true geometric normals, correct
// multi-contact behaviour at corners, and no lookahead probe to snag on jagged
// terrain. Every failure mode of the per-tick approach (estimated normals,
// corner cutting, iteration that may not converge, choosing a reference height,
// sizing a probe against the capsule radius) is structurally absent here.
//
// Barriers are emitted for every chunk regardless of whether anything currently
// collides with them; opting in is a collision-mask decision on the body. That
// keeps the A/B against the per-tick guard instant instead of requiring a world
// rebuild.
public static class LedgeBarrierMesher
{
    private const int N = ChunkState.SIZE;

    // Drop, in voxels, a body may walk off unaided: one voxel down is a step,
    // two is a ledge. THE authority for that split — nothing else decides it
    // now, so raising it means terrain that used to stop the player no longer
    // does. Pairs with PlayerData.mantleMinRise, which is the smallest rise the
    // interact-to-climb affordance will offer: leave a gap between them and
    // there is terrain that can be neither walked down nor climbed.
    private const int MaxLegalDropVoxels = 1;

    // How far a barrier rises above the surface it guards, in metres. Must
    // exceed the movement capsule's height (1.5) so it cannot be climbed by the
    // step-up lift, which raises the body by stepHeight before moving.
    private const float BarrierHeight = 2f;

    // How far below the surface to look for the neighbour's ground before
    // calling it a drop. Only has to cover the legal step plus a voxel of slack;
    // anything deeper is a ledge regardless of how much deeper.
    private const int NeighbourProbeDepth = MaxLegalDropVoxels + 1;

    private static readonly Vector3I[] Horizontal =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
    };

    // Emits two triangles for the vertical face shared with the neighbour in
    // direction `dir`, spanning the full cell width and BarrierHeight upward
    // from the surface.
    //
    // Coordinates are CHUNK-LOCAL, matching the terrain mesh: ChunkMesh sets its
    // node Position to the chunk origin and the mesher emits local vertices, so
    // anything emitted in world space lands a whole chunk origin away. Note this
    // is the opposite convention to getVoxel, which is world-indexed.
    private static void AddFace(List<Vector3> tris, Vector3I dir, float x, float y, float z)
    {
        float top = y + BarrierHeight;
        Vector3 a, b;
        if (dir.X != 0)
        {
            float fx = dir.X > 0 ? x + 1f : x;
            a = new Vector3(fx, y, z);
            b = new Vector3(fx, y, z + 1f);
        }
        else
        {
            float fz = dir.Z > 0 ? z + 1f : z;
            a = new Vector3(x, y, fz);
            b = new Vector3(x + 1f, y, fz);
        }
        Vector3 aTop = new(a.X, top, a.Z);
        Vector3 bTop = new(b.X, top, b.Z);

        // Winding is irrelevant here — the shape is built with backface
        // collision on, since a barrier must be felt from whichever side a body
        // reaches it.
        tris.Add(a); tris.Add(b); tris.Add(bTop);
        tris.Add(a); tris.Add(bTop); tris.Add(aTop);
    }

    // True when (x,y,z) is a surface a body stands ON: air here, solid beneath.
    private static bool IsSurface(System.Func<int, int, int, int> getVoxel, int x, int y, int z)
    {
        return !Blocks.IsSolid(getVoxel(x, y, z)) && Blocks.IsSolid(getVoxel(x, y - 1, z));
    }

    // Does the neighbouring column offer footing within a legal step of `y`?
    // Water counts: walking into water is not falling off a ledge, and the
    // player swims. A solid neighbour at `y` is a wall, which needs no barrier
    // because terrain collision already stops the body.
    private static bool NeighbourIsReachable(System.Func<int, int, int, int> getVoxel, int nx, int y, int nz)
    {
        if (Blocks.IsSolid(getVoxel(nx, y, nz)))
        {
            return true;
        }
        for (int d = 0; d <= NeighbourProbeDepth; d++)
        {
            int ny = y - d;
            if (getVoxel(nx, ny, nz) == Blocks.WaterId)
            {
                return true;
            }
            if (IsSurface(getVoxel, nx, ny, nz))
            {
                return d <= MaxLegalDropVoxels;
            }
        }
        return false;
    }

    // Collects barrier triangles for one chunk, in CHUNK-LOCAL space. Returns
    // null when the chunk has no ledges, which is the common case for flat
    // terrain and lets the caller skip creating a body at all.
    //
    // The two coordinate spaces in play are easy to conflate: getVoxel is
    // world-indexed (it resolves across chunk boundaries) while emitted vertices
    // must be chunk-local (the node transform places them). So the origin is
    // added for lookups and NOT for output.
    public static List<Vector3> Build(System.Func<int, int, int, int> getVoxel,
        int chunkWorldX, int chunkWorldY, int chunkWorldZ)
    {
        List<Vector3> tris = null;
        for (int y = 0; y < N; y++)
        {
            int wy = chunkWorldY + y;
            for (int z = 0; z < N; z++)
            {
                int wz = chunkWorldZ + z;
                for (int x = 0; x < N; x++)
                {
                    int wx = chunkWorldX + x;
                    if (!IsSurface(getVoxel, wx, wy, wz))
                    {
                        continue;
                    }
                    for (int i = 0; i < Horizontal.Length; i++)
                    {
                        Vector3I dir = Horizontal[i];
                        if (NeighbourIsReachable(getVoxel, wx + dir.X, wy, wz + dir.Z))
                        {
                            continue;
                        }
                        tris ??= new List<Vector3>();
                        AddFace(tris, dir, x, y, z);
                    }
                }
            }
        }
        return tris;
    }
}
