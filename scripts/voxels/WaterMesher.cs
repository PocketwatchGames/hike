using System;
using Godot;

// Water stays on the old cubic face-culling mesher. Water only exists at
// wy <= 0 and presents a planar-ish surface; no reason to deform it with DC.
//
// Two sub-voxel offsets handle land ↔ water transitions:
//   - TOP_EPSILON pulls the exposed top face down a hair so it's never
//     coplanar with whatever sits above it. Kills remaining z-fight at
//     distance where the backface's clip-space bias runs out of precision.
//   - SHORE_INSET pulls side faces inward when the neighbor is solid
//     (land). DC land has fractional-height vertices; insetting water
//     keeps its hard edge from visibly clashing with those curves. The
//     tiny gap fills naturally with the land surface behind it.
public static class WaterMesher
{
    private const int N = ChunkState.SIZE;
    private const float TOP_EPSILON = 0.02f;
    private const float SHORE_INSET = 0.05f;

    private static readonly Vector3[][] Faces =
    {
        new Vector3[] { new(0, 1, 0), new(0, 1, 1), new(1, 1, 1), new(1, 1, 0) },       // Top
        new Vector3[] { new(0, 0, 0), new(1, 0, 0), new(1, 0, 1), new(0, 0, 1) },       // Bottom
        new Vector3[] { new(0, 0, 0), new(0, 1, 0), new(1, 1, 0), new(1, 0, 0) },       // -Z
        new Vector3[] { new(0, 0, 1), new(1, 0, 1), new(1, 1, 1), new(0, 1, 1) },       // +Z
        new Vector3[] { new(0, 0, 0), new(0, 0, 1), new(0, 1, 1), new(0, 1, 0) },       // -X
        new Vector3[] { new(1, 0, 0), new(1, 1, 0), new(1, 1, 1), new(1, 0, 1) },       // +X
    };

    private static readonly Vector3[] Normals =
    {
        Vector3.Up, Vector3.Down,
        new Vector3(0, 0, -1), new Vector3(0, 0, 1),
        Vector3.Left, Vector3.Right,
    };

    private static readonly Vector3I[] NeighborOffsets =
    {
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, -1), new(0, 0, 1),
        new(-1, 0, 0), new(1, 0, 0),
    };

    public static void Build(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        SurfaceTool st,
        int chunkWorldX, int chunkWorldY, int chunkWorldZ,
        out bool hasAnyFace)
    {
        hasAnyFace = false;
        Color color = VoxelTypeInfo.Colors[VoxelType.Water];
        int tile = VoxelTypeInfo.GetTileForFace(VoxelType.Water, 0);

        for (int x = 0; x < N; x++)
        {
            for (int y = 0; y < N; y++)
            {
                for (int z = 0; z < N; z++)
                {
                    if (data.Voxels[x, y, z] != VoxelType.Water)
                    {
                        continue;
                    }

                    Vector3 offset = new(x, y, z);
                    int wx = chunkWorldX + x;
                    int wy = chunkWorldY + y;
                    int wz = chunkWorldZ + z;

                    for (int f = 0; f < 6; f++)
                    {
                        Vector3I no = NeighborOffsets[f];
                        VoxelType neighbor = getVoxel(wx + no.X, wy + no.Y, wz + no.Z);
                        if (neighbor == VoxelType.Water)
                        {
                            continue;
                        }

                        Vector3[] verts = Faces[f];
                        Vector3 normal = Normals[f];

                        // Sub-voxel offsets to avoid visible clashes with
                        // DC-meshed land. See file header for rationale.
                        Vector3 faceOffset = Vector3.Zero;
                        if (f == 0)
                        {
                            // Top face — pull the exposed water surface down
                            // by TOP_EPSILON so it can't z-fight with anything
                            // at integer Y above it.
                            faceOffset.Y = -TOP_EPSILON;
                        }
                        else if (f >= 2 && VoxelTypeInfo.IsSolid(neighbor))
                        {
                            // Horizontal side faces with a solid (land) neighbor —
                            // inset toward the water cell's interior so the
                            // cubic face doesn't sit exactly where the land's
                            // DC-deformed surface wants to be. Creates a thin
                            // channel that fills visually with the land behind.
                            faceOffset = -(Vector3)no * SHORE_INSET;
                        }

                        // Water samples lighting from itself (light doesn't
                        // propagate through but diffuses into the cell).
                        Color custom = new Color(wx + 0.5f, wy + 0.5f, wz + 0.5f, tile);

                        Vector3 v0 = verts[0] + offset + faceOffset;
                        Vector3 v1 = verts[1] + offset + faceOffset;
                        Vector3 v2 = verts[2] + offset + faceOffset;
                        Vector3 v3 = verts[3] + offset + faceOffset;

                        EmitTri(st, v0, v2, v1, normal, color, custom);
                        EmitTri(st, v0, v3, v2, normal, color, custom);
                        hasAnyFace = true;
                    }
                }
            }
        }
    }

    private static void EmitTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 n, Color col, Color custom)
    {
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(a);
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(b);
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(c);
    }
}
