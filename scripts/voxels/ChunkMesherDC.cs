using System;
using Godot;

// Naive Dual Contouring / Surface Nets mesher. Per chunk:
//   1. Sample corner densities (with a 1-voxel apron) from the world.
//   2. For every cell in [-1, N] with a corner-sign change, place one vertex
//      at the average of the cell's sign-change edge midpoints.
//   3. For each grid edge in this chunk's corner range [0, N] with a sign
//      change, emit a quad using the 4 adjacent cell vertices.
// The apron means cells on the neighbour side of a chunk boundary are also
// computed here, so boundary quads connect without seams. Density is a
// deterministic function of VoxelType + min-rule, so neighbouring chunks
// compute the same vertex for a shared boundary cell.
public static class ChunkMesherDC
{
    private const int N = ChunkState.SIZE;

    // Cells at coord [-1, N] inclusive  →  N+2 slots, indexed (coord + 1).
    // Corners at coord [-1, N+1] inclusive  →  N+3 slots, indexed (coord + 1).
    private const int CELL_LO = -1;
    private const int CELL_HI = N;
    private const int CELL_DIM = N + 2;
    private const int CORNER_LO = -1;
    private const int CORNER_HI = N + 1;
    private const int CORNER_DIM = N + 3;

    private static int CellIdx(int c) => c - CELL_LO;
    private static int CornerIdx(int c) => c - CORNER_LO;

    private static readonly (int dx, int dy, int dz)[] CornerOffsets =
    {
        (0,0,0), (1,0,0), (0,1,0), (1,1,0),
        (0,0,1), (1,0,1), (0,1,1), (1,1,1),
    };

    private static readonly (int a, int b)[] CellEdges =
    {
        (0,1), (2,3), (4,5), (6,7), // X
        (0,2), (1,3), (4,6), (5,7), // Y
        (0,4), (1,5), (2,6), (3,7), // Z
    };

    public static bool DebugLog = false;

    // Per-axis emission gates for debugging winding. Disable an axis to see
    // whether the remaining geometry still contains a given artifact.
    public static bool EmitX = true;
    public static bool EmitY = true;
    public static bool EmitZ = true;

    public static void Build(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, bool> chunkExists,
        SurfaceTool st,
        int chunkWorldX, int chunkWorldY, int chunkWorldZ,
        out bool hasAnyFace)
    {
        hasAnyFace = false;
        int activeCells = 0;
        int quadsEmitted = 0;
        int quadsSkipped = 0;

        var density = new sbyte[CORNER_DIM, CORNER_DIM, CORNER_DIM];
        for (int cx = CORNER_LO; cx <= CORNER_HI; cx++)
        {
            for (int cy = CORNER_LO; cy <= CORNER_HI; cy++)
            {
                for (int cz = CORNER_LO; cz <= CORNER_HI; cz++)
                {
                    density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)] = Density.CornerDensity(
                        chunkWorldX + cx, chunkWorldY + cy, chunkWorldZ + cz, getVoxel);
                }
            }
        }

        var cellVert = new Vector3[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellTile = new int[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellAmp = new float[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellHas = new bool[CELL_DIM, CELL_DIM, CELL_DIM];

        for (int x = CELL_LO; x <= CELL_HI; x++)
        {
            for (int y = CELL_LO; y <= CELL_HI; y++)
            {
                for (int z = CELL_LO; z <= CELL_HI; z++)
                {
                    sbyte d0 = density[CornerIdx(x),   CornerIdx(y),   CornerIdx(z)  ];
                    sbyte d1 = density[CornerIdx(x+1), CornerIdx(y),   CornerIdx(z)  ];
                    sbyte d2 = density[CornerIdx(x),   CornerIdx(y+1), CornerIdx(z)  ];
                    sbyte d3 = density[CornerIdx(x+1), CornerIdx(y+1), CornerIdx(z)  ];
                    sbyte d4 = density[CornerIdx(x),   CornerIdx(y),   CornerIdx(z+1)];
                    sbyte d5 = density[CornerIdx(x+1), CornerIdx(y),   CornerIdx(z+1)];
                    sbyte d6 = density[CornerIdx(x),   CornerIdx(y+1), CornerIdx(z+1)];
                    sbyte d7 = density[CornerIdx(x+1), CornerIdx(y+1), CornerIdx(z+1)];

                    int insideMask = 0;
                    if (d0 < 0) { insideMask |= 1; }
                    if (d1 < 0) { insideMask |= 2; }
                    if (d2 < 0) { insideMask |= 4; }
                    if (d3 < 0) { insideMask |= 8; }
                    if (d4 < 0) { insideMask |= 16; }
                    if (d5 < 0) { insideMask |= 32; }
                    if (d6 < 0) { insideMask |= 64; }
                    if (d7 < 0) { insideMask |= 128; }

                    if (insideMask == 0 || insideMask == 255)
                    {
                        continue;
                    }

                    sbyte[] dArr = { d0, d1, d2, d3, d4, d5, d6, d7 };

                    Vector3 accum = Vector3.Zero;
                    int count = 0;
                    foreach (var (ca, cb) in CellEdges)
                    {
                        bool aIn = dArr[ca] < 0;
                        bool bIn = dArr[cb] < 0;
                        if (aIn == bIn)
                        {
                            continue;
                        }
                        var (ax, ay, az) = CornerOffsets[ca];
                        var (bx, by, bz) = CornerOffsets[cb];
                        float da = dArr[ca];
                        float db = dArr[cb];
                        float t = da / (da - db);
                        accum.X += ax + (bx - ax) * t;
                        accum.Y += ay + (by - ay) * t;
                        accum.Z += az + (bz - az) * t;
                        count++;
                    }
                    Vector3 vertLocal = accum / count;

                    cellVert[CellIdx(x), CellIdx(y), CellIdx(z)] = vertLocal;
                    cellHas[CellIdx(x), CellIdx(y), CellIdx(z)] = true;
                    PickTileAndAmpForCell(data, x, y, z, getVoxel, chunkWorldX, chunkWorldY, chunkWorldZ, out int tile, out float amp);
                    cellTile[CellIdx(x), CellIdx(y), CellIdx(z)] = tile;
                    cellAmp[CellIdx(x), CellIdx(y), CellIdx(z)] = amp;
                    activeCells++;
                }
            }
        }

        // Emit quads for edges owned by this chunk: all three corner indices of
        // the edge's lower endpoint must lie in [0, N-1]. Edges on a +X/+Y/+Z
        // chunk face are owned by the neighbour (they appear there at index 0
        // along that axis), so each shared edge is emitted exactly once.
        for (int cx = 0; cx <= N; cx++)
        {
            for (int cy = 0; cy <= N; cy++)
            {
                for (int cz = 0; cz <= N; cz++)
                {
                    if (cx < N && cy < N && cz < N && EmitX)
                    {
                        sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                        sbyte b = density[CornerIdx(cx + 1), CornerIdx(cy), CornerIdx(cz)];
                        if ((a < 0) != (b < 0))
                        {
                            s_axisTag = 'X'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                            EmitQuad(
                                st,
                                cellHas, cellVert, cellTile, cellAmp,
                                chunkWorldX, chunkWorldY, chunkWorldZ,
                                cx, cy - 1, cz - 1,
                                cx, cy,     cz - 1,
                                cx, cy,     cz,
                                cx, cy - 1, cz,
                                flip: a < 0,
                                ref hasAnyFace,
                                ref quadsEmitted,
                                ref quadsSkipped);
                        }
                    }
                    if (cy < N && cx < N && cz < N && EmitY)
                    {
                        sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                        sbyte b = density[CornerIdx(cx), CornerIdx(cy + 1), CornerIdx(cz)];
                        if ((a < 0) != (b < 0))
                        {
                            // Cells wound CCW around the +Y axis (viewed from +Y),
                            // so the unflipped cross product points +Y — matching
                            // +X and +Z axes.
                            s_axisTag = 'Y'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                            EmitQuad(
                                st,
                                cellHas, cellVert, cellTile, cellAmp,
                                chunkWorldX, chunkWorldY, chunkWorldZ,
                                cx - 1, cy, cz - 1,
                                cx - 1, cy, cz,
                                cx,     cy, cz,
                                cx,     cy, cz - 1,
                                flip: a < 0,
                                ref hasAnyFace,
                                ref quadsEmitted,
                                ref quadsSkipped);
                        }
                    }
                    if (cz < N && cx < N && cy < N && EmitZ)
                    {
                        sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                        sbyte b = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz + 1)];
                        if ((a < 0) != (b < 0))
                        {
                            s_axisTag = 'Z'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                            EmitQuad(
                                st,
                                cellHas, cellVert, cellTile, cellAmp,
                                chunkWorldX, chunkWorldY, chunkWorldZ,
                                cx - 1, cy - 1, cz,
                                cx,     cy - 1, cz,
                                cx,     cy,     cz,
                                cx - 1, cy,     cz,
                                flip: a < 0,
                                ref hasAnyFace,
                                ref quadsEmitted,
                                ref quadsSkipped);
                        }
                    }
                }
            }
        }

        // World-boundary apron emission: at the -X / -Y / -Z faces of a chunk
        // that has no neighbour on that side, the sign-change edge between
        // corner -1 and 0 would normally be owned by the (nonexistent) chunk
        // below/behind/left of us. Emit it here so the world is closed and
        // backfaces exist for the ceiling-clip shader to terminate against.
        bool noNegX = !chunkExists(chunkWorldX - 1, chunkWorldY, chunkWorldZ);
        bool noNegY = !chunkExists(chunkWorldX, chunkWorldY - 1, chunkWorldZ);
        bool noNegZ = !chunkExists(chunkWorldX, chunkWorldY, chunkWorldZ - 1);

        if (noNegX && EmitX)
        {
            int cx = -1;
            for (int cy = 0; cy < N; cy++)
            {
                for (int cz = 0; cz < N; cz++)
                {
                    sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                    sbyte b = density[CornerIdx(cx + 1), CornerIdx(cy), CornerIdx(cz)];
                    if ((a < 0) != (b < 0))
                    {
                        s_axisTag = 'X'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                        EmitQuad(
                            st,
                            cellHas, cellVert, cellTile, cellAmp,
                            chunkWorldX, chunkWorldY, chunkWorldZ,
                            cx, cy - 1, cz - 1,
                            cx, cy,     cz - 1,
                            cx, cy,     cz,
                            cx, cy - 1, cz,
                            flip: a < 0,
                            ref hasAnyFace,
                            ref quadsEmitted,
                            ref quadsSkipped);
                    }
                }
            }
        }

        if (noNegY && EmitY)
        {
            int cy = -1;
            for (int cx = 0; cx < N; cx++)
            {
                for (int cz = 0; cz < N; cz++)
                {
                    sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                    sbyte b = density[CornerIdx(cx), CornerIdx(cy + 1), CornerIdx(cz)];
                    if ((a < 0) != (b < 0))
                    {
                        s_axisTag = 'Y'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                        EmitQuad(
                            st,
                            cellHas, cellVert, cellTile, cellAmp,
                            chunkWorldX, chunkWorldY, chunkWorldZ,
                            cx - 1, cy, cz - 1,
                            cx - 1, cy, cz,
                            cx,     cy, cz,
                            cx,     cy, cz - 1,
                            flip: a < 0,
                            ref hasAnyFace,
                            ref quadsEmitted,
                            ref quadsSkipped);
                    }
                }
            }
        }

        if (noNegZ && EmitZ)
        {
            int cz = -1;
            for (int cx = 0; cx < N; cx++)
            {
                for (int cy = 0; cy < N; cy++)
                {
                    sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];
                    sbyte b = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz + 1)];
                    if ((a < 0) != (b < 0))
                    {
                        s_axisTag = 'Z'; s_edgeCx = cx; s_edgeCy = cy; s_edgeCz = cz; s_edgeA = a; s_edgeB = b;
                        EmitQuad(
                            st,
                            cellHas, cellVert, cellTile, cellAmp,
                            chunkWorldX, chunkWorldY, chunkWorldZ,
                            cx - 1, cy - 1, cz,
                            cx,     cy - 1, cz,
                            cx,     cy,     cz,
                            cx - 1, cy,     cz,
                            flip: a < 0,
                            ref hasAnyFace,
                            ref quadsEmitted,
                            ref quadsSkipped);
                    }
                }
            }
        }

        if (DebugLog)
        {
            GD.Print($"[DC] chunk ({chunkWorldX / N},{chunkWorldY / N},{chunkWorldZ / N}) active={activeCells} quads={quadsEmitted} dropped={quadsSkipped}");
        }
    }

    // Set by Build before each axis pass so EmitQuad can log per-quad context.
    private static char s_axisTag;
    private static int s_edgeCx, s_edgeCy, s_edgeCz;
    private static sbyte s_edgeA, s_edgeB;

    private static void EmitQuad(
        SurfaceTool st,
        bool[,,] cellHas, Vector3[,,] cellVert, int[,,] cellTile, float[,,] cellAmp,
        int cwX, int cwY, int cwZ,
        int x0, int y0, int z0,
        int x1, int y1, int z1,
        int x2, int y2, int z2,
        int x3, int y3, int z3,
        bool flip,
        ref bool hasAnyFace,
        ref int quadsEmitted,
        ref int quadsSkipped)
    {
        int i0x = CellIdx(x0), i0y = CellIdx(y0), i0z = CellIdx(z0);
        int i1x = CellIdx(x1), i1y = CellIdx(y1), i1z = CellIdx(z1);
        int i2x = CellIdx(x2), i2y = CellIdx(y2), i2z = CellIdx(z2);
        int i3x = CellIdx(x3), i3y = CellIdx(y3), i3z = CellIdx(z3);

        if (!cellHas[i0x, i0y, i0z] || !cellHas[i1x, i1y, i1z]
            || !cellHas[i2x, i2y, i2z] || !cellHas[i3x, i3y, i3z])
        {
            quadsSkipped++;
            return;
        }
        quadsEmitted++;

        Vector3 v0 = cellVert[i0x, i0y, i0z] + new Vector3(x0, y0, z0);
        Vector3 v1 = cellVert[i1x, i1y, i1z] + new Vector3(x1, y1, z1);
        Vector3 v2 = cellVert[i2x, i2y, i2z] + new Vector3(x2, y2, z2);
        Vector3 v3 = cellVert[i3x, i3y, i3z] + new Vector3(x3, y3, z3);

        int t0 = cellTile[i0x, i0y, i0z];
        int t1 = cellTile[i1x, i1y, i1z];
        int t2 = cellTile[i2x, i2y, i2z];
        int t3 = cellTile[i3x, i3y, i3z];

        float a0 = cellAmp[i0x, i0y, i0z];
        float a1 = cellAmp[i1x, i1y, i1z];
        float a2 = cellAmp[i2x, i2y, i2z];
        float a3 = cellAmp[i3x, i3y, i3z];

        if (flip)
        {
            AddTri(st, v0, v2, v1, t0, t2, t1, a0, a2, a1);
            AddTri(st, v0, v3, v2, t0, t3, t2, a0, a3, a2);
        }
        else
        {
            AddTri(st, v0, v1, v2, t0, t1, t2, a0, a1, a2);
            AddTri(st, v0, v2, v3, t0, t2, t3, a0, a2, a3);
        }

        if (DebugLog)
        {
            // Geometric normal of the as-emitted triangle (v0, v1, v2) for the
            // unflipped path, or (v0, v2, v1) for the flipped path. Printed so
            // we can compare the sign-rule's intent to the quad's real normal.
            Vector3 na, nb;
            if (flip)
            {
                na = v2 - v0;
                nb = v1 - v0;
            }
            else
            {
                na = v1 - v0;
                nb = v2 - v0;
            }
            Vector3 geomN = na.Cross(nb);
            GD.Print($"[DC] {s_axisTag} edge({s_edgeCx},{s_edgeCy},{s_edgeCz}) a={s_edgeA} b={s_edgeB} flip={flip} geomN=({geomN.X:F2},{geomN.Y:F2},{geomN.Z:F2})");
        }

        hasAnyFace = true;
    }

    // Encodes per-triangle texture-blend data:
    //  - CUSTOM0 = (tile_a, tile_b, tile_c, amp_self): first three are constant
    //    across the triangle so any fragment can index all three corners' tiles;
    //    .w is per-vertex blend-noise amplitude (interpolated to fragment).
    //  - COLOR.rgb = bary indicator (1,0,0)/(0,1,0)/(0,0,1). Linearly interpolated
    //    by the rasterizer so fragment.COLOR.rgb is the barycentric weight vector.
    private static void AddTri(SurfaceTool st,
        Vector3 a, Vector3 b, Vector3 c,
        int ta, int tb, int tc,
        float ampA, float ampB, float ampC)
    {
        Color custA = new Color(ta, tb, tc, ampA);
        Color custB = new Color(ta, tb, tc, ampB);
        Color custC = new Color(ta, tb, tc, ampC);
        st.SetColor(new Color(1f, 0f, 0f, 1f)); st.SetCustom(0, custA); st.AddVertex(a);
        st.SetColor(new Color(0f, 1f, 0f, 1f)); st.SetCustom(0, custB); st.AddVertex(b);
        st.SetColor(new Color(0f, 0f, 1f, 1f)); st.SetCustom(0, custC); st.AddVertex(c);
    }

    // Pick a tile + blend-noise amplitude for the cell. Extended cells (x, y,
    // or z outside [0, N-1]) fall back to a neighbour lookup via getVoxel.
    private static void PickTileAndAmpForCell(
        ChunkState data, int x, int y, int z,
        Func<int, int, int, VoxelType> getVoxel,
        int cwX, int cwY, int cwZ,
        out int tile, out float amp)
    {
        VoxelType self;
        if (x >= 0 && x < N && y >= 0 && y < N && z >= 0 && z < N)
        {
            self = data.GetVoxel(x, y, z);
        }
        else
        {
            self = getVoxel(cwX + x, cwY + y, cwZ + z);
        }
        VoxelType dominant = VoxelType.Air;
        if (VoxelTypeInfo.IsSolid(self) && self != VoxelType.Barrier)
        {
            dominant = self;
        }
        else
        {
            for (int dx = -1; dx <= 1 && dominant == VoxelType.Air; dx++)
            {
                for (int dy = -1; dy <= 1 && dominant == VoxelType.Air; dy++)
                {
                    for (int dz = -1; dz <= 1 && dominant == VoxelType.Air; dz++)
                    {
                        if (dx == 0 && dy == 0 && dz == 0) { continue; }
                        VoxelType v = getVoxel(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                        if (VoxelTypeInfo.IsSolid(v) && v != VoxelType.Barrier)
                        {
                            dominant = v;
                        }
                    }
                }
            }
        }

        if (dominant == VoxelType.Air)
        {
            tile = 0;
            amp = 0f;
            return;
        }

        tile = VoxelTypeInfo.GetTileForFace(dominant, 0);
        amp = VoxelTypeInfo.GetBlendNoise(dominant);
    }
}
