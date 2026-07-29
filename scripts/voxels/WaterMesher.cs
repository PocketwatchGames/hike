using System;
using Godot;

// Water stays on the old cubic face-culling mesher. Water only exists at
// wy <= 0 and presents a planar-ish surface; no reason to deform it with DC.
//
// The water is meshed as ONE contiguous volume, dilated a voxel laterally into
// the shore, and skinned with unit quads on the voxel grid. The dilation is what
// covers the land ↔ water seam: land is a DC surface whose vertex can sit
// anywhere inside its own voxel, so where it dips below the waterline a water
// body that stopped at the voxel boundary left the dip dry. The shell cell
// covers it, and where the land instead sits flush or higher, land is opaque and
// in front, so the shell is simply hidden.
//
// Dilating the SET rather than growing each voxel's box is the whole point.
// Per-voxel growth made neighbouring quads share an edge without sharing
// vertices — a T-junction — and coplanar triangles interpolated from different
// endpoints do not rasterize to identical coverage, so the seam cracked into
// single flickering pixels. It also let two cells grow over the same land and
// double-composite. Working on the occupancy set keeps every face a unit quad on
// the grid, so adjacent cells share vertices exactly and both failures are
// structurally impossible.
//
// TOP_EPSILON pulls the exposed top face down a hair so it is never coplanar
// with whatever sits above it. It kills z-fighting at distance where the
// backface's clip-space bias runs out of precision, and it is also what hides
// the shell against land that sits flush with the waterline. Uniform across
// every cell, so it does not reintroduce a mismatch.
public static class WaterMesher
{
    private const int N = ChunkState.SIZE;
    private const float TOP_EPSILON = 0.02f;

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
                    int wx = chunkWorldX + x;
                    int wy = chunkWorldY + y;
                    int wz = chunkWorldZ + z;

                    if (!InWaterVolume(getVoxel, wx, wy, wz))
                    {
                        continue;
                    }

                    Vector3 offset = new(x, y, z);

                    // Whether this cell is "roofed" — has anything other than
                    // air directly above. A roofed cell is in a sealed pocket
                    // (water under rock with no air gap) and none of its faces
                    // represent a real water boundary the player can see;
                    // emitting any of them leaks through the ceiling cutaway
                    // because the clipped solid above no longer writes depth,
                    // leaving the water's deep seabed as the only depth in the
                    // buffer — water front faces happily pass their depth test
                    // against that and paint over the cap.
                    //
                    // This also gates the dilated shell for free: a shell cell
                    // buried under tall land has solid above it and drops out,
                    // so only the cells beside a low shore — the ones that
                    // actually cover a dip — survive.
                    VoxelType above = getVoxel(wx, wy + 1, wz);
                    bool roofed = above != VoxelType.Air;
                    if (roofed)
                    {
                        continue;
                    }

                    for (int f = 0; f < 6; f++)
                    {
                        Vector3I no = NeighborOffsets[f];
                        // Cull against the VOLUME, not against VoxelType.Water,
                        // so the dilated shell is interior and only the outside
                        // of the whole body is skinned.
                        if (InWaterVolume(getVoxel, wx + no.X, wy + no.Y, wz + no.Z))
                        {
                            continue;
                        }

                        Vector3[] verts = Faces[f];
                        Vector3 normal = Normals[f];

                        // Water samples lighting from itself (light doesn't
                        // propagate through but diffuses into the cell).
                        Color custom = new Color(wx + 0.5f, wy + 0.5f, wz + 0.5f, tile);

                        // Unit quads on the voxel grid. The TOP_EPSILON drop is
                        // the only deviation, and it is uniform, so neighbouring
                        // cells still share vertex positions exactly.
                        Vector3 drop = new Vector3(0f, TOP_EPSILON, 0f);
                        Vector3 v0 = verts[0] + offset - (verts[0].Y > 0.5f ? drop : Vector3.Zero);
                        Vector3 v1 = verts[1] + offset - (verts[1].Y > 0.5f ? drop : Vector3.Zero);
                        Vector3 v2 = verts[2] + offset - (verts[2].Y > 0.5f ? drop : Vector3.Zero);
                        Vector3 v3 = verts[3] + offset - (verts[3].Y > 0.5f ? drop : Vector3.Zero);

                        EmitTri(st, v0, v2, v1, normal, color, custom);
                        EmitTri(st, v0, v3, v2, normal, color, custom);
                        hasAnyFace = true;
                    }
                }
            }
        }
    }

    // The water VOLUME: real water, plus a one-voxel lateral shell into the
    // solid it touches. Lateral only — water must not climb over land or seep
    // down into bedrock. A pure function of world voxels, so two chunks sharing
    // a boundary cell always agree and no seam can appear between them.
    private static bool InWaterVolume(Func<int, int, int, VoxelType> getVoxel, int wx, int wy, int wz)
    {
        VoxelType v = getVoxel(wx, wy, wz);
        if (v == VoxelType.Water)
        {
            return true;
        }
        if (!VoxelTypeInfo.IsSolid(v))
        {
            return false;
        }
        return getVoxel(wx - 1, wy, wz) == VoxelType.Water
            || getVoxel(wx + 1, wy, wz) == VoxelType.Water
            || getVoxel(wx, wy, wz - 1) == VoxelType.Water
            || getVoxel(wx, wy, wz + 1) == VoxelType.Water;
    }

    private static void EmitTri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 n, Color col, Color custom)
    {
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(a);
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(b);
        st.SetNormal(n); st.SetColor(col); st.SetCustom(0, custom); st.AddVertex(c);
    }
}
