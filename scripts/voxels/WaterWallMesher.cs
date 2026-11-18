using System.Collections.Generic;
using Godot;

// One-sided walls on every vertical water face that borders open air — the
// standing face of a waterfall's upper pool, wherever a voxel world puts one.
//
// A body's waterline is the top of whatever water column its feet are in, so
// a body that crosses such a face from the air side adopts the higher pool's
// surface and buoyancy floats it up the fall. These walls face OUT of the water:
// a body coming from the air side is stopped, one leaving the water passes
// through the back, so going over a fall stays possible and climbing one never
// is. Getting up a fall is a mantle.
//
// Per voxel face rather than a contour (cf. LedgeBarrierMesher): water faces are
// exact lattice faces, so a diagonal lip is a staircase of faces that meet at
// their corners with no gap to slip through.
public static class WaterWallMesher
{
    private const int N = ChunkState.SIZE;

    // How far a wall reaches below the lowest exposed face of its column, in
    // metres. Below the exposed run the neighbour is water or ground, and where
    // it is water (a pool standing beside a deeper one, no rock between) a
    // swimmer's feet sit under the lower surface — the wall must reach down past
    // the whole movement capsule (1.5) or the body slips under it into the
    // higher column.
    private const float WallSink = 1.5f;

    private static readonly Vector3I[] Horizontal =
    {
        new(1, 0, 0), new(-1, 0, 0), new(0, 0, 1), new(0, 0, -1),
    };

    // Open air beside water: nothing collides there and no water is there.
    // HasGeometry, not IsSolid, for the reason LedgeBarrierMesher gives — a
    // Barrier block is solid but meshes nothing, so it stops no body.
    private static bool IsOpen(int id)
    {
        return !Blocks.IsWater(id) && !Blocks.HasGeometry(id);
    }

    // Wall triangles for one chunk in CHUNK-LOCAL space, or null when the chunk
    // has no exposed water face. getVoxel is world-indexed.
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
                    if (!Blocks.IsWater(getVoxel(wx, wy, wz)))
                    {
                        continue;
                    }
                    for (int d = 0; d < Horizontal.Length; d++)
                    {
                        Vector3I n = Horizontal[d];
                        if (!IsOpen(getVoxel(wx + n.X, wy, wz + n.Z)))
                        {
                            continue;
                        }
                        // The bottom of an exposed run reaches down past it;
                        // the faces above it only meet the one below.
                        bool runBottom = !(Blocks.IsWater(getVoxel(wx, wy - 1, wz))
                            && IsOpen(getVoxel(wx + n.X, wy - 1, wz + n.Z)));
                        float bottom = runBottom ? y - WallSink : y;
                        tris ??= new List<Vector3>();
                        AddFace(tris, x, z, n, bottom, y + 1f);
                    }
                }
            }
        }
        return tris;
    }

    // A quad on the face of cell (x, z) toward `outward`, front side facing out
    // of the water. Godot treats CLOCKWISE as front, so seen from outside the
    // corners run top-left, top-right, bottom-right, bottom-left.
    private static void AddFace(List<Vector3> tris, int x, int z, Vector3I outward, float bottom, float top)
    {
        Vector3 normal = new(outward.X, 0f, outward.Z);
        Vector3 right = Vector3.Up.Cross(normal) * 0.5f;
        Vector3 center = new(x + 0.5f + normal.X * 0.5f, 0f, z + 0.5f + normal.Z * 0.5f);

        Vector3 tl = center - right + new Vector3(0f, top, 0f);
        Vector3 tr = center + right + new Vector3(0f, top, 0f);
        Vector3 br = center + right + new Vector3(0f, bottom, 0f);
        Vector3 bl = center - right + new Vector3(0f, bottom, 0f);

        tris.Add(tl); tris.Add(tr); tris.Add(br);
        tris.Add(tl); tris.Add(br); tris.Add(bl);
    }
}
