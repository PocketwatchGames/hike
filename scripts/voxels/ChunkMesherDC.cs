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
    // Corners at coord [-2, N+2] inclusive  →  N+5 slots, indexed (coord + 2).
    // The extra corner layer (vs a min [-1, N+1] apron for meshing alone) gives
    // every cell in [-1, N] a full 3×3×3 box-smoothing neighbourhood around each
    // of its 8 corners, so the normal gradient reads off a continuous density
    // field. Both neighbouring chunks sample the same densities at the same
    // world corners, so shared boundary cells get identical smoothed normals
    // → no slope-pick seam across chunk boundaries.
    private const int CELL_LO = -1;
    private const int CELL_HI = N;
    private const int CELL_DIM = N + 2;
    private const int CORNER_LO = -2;
    private const int CORNER_HI = N + 2;
    private const int CORNER_DIM = N + 5;

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

    // True iff the cell's 4 corner columns have topY values differing by at
    // most 1 — a ≤1-voxel Y step the mesher should smooth into a ramp instead
    // of snapping to a vertical wall. Deterministic across chunks because the
    // scan window [cellY-1, cellY+2] fits within both neighbours' apron Y
    // ranges at every shared boundary cell; tall cliffs naturally cap at
    // cellY+2 and fall through to the max-min≥2 cliff branch.
    private static bool IsShallowYTransition(sbyte[,,] density, int cx, int cellY, int cz)
    {
        int minTop = int.MaxValue;
        int maxTop = int.MinValue;
        for (int dx = 0; dx < 2; dx++)
        {
            for (int dz = 0; dz < 2; dz++)
            {
                int topY = int.MinValue;
                for (int y = cellY + 2; y >= cellY - 1; y--)
                {
                    if (density[CornerIdx(cx + dx), CornerIdx(y), CornerIdx(cz + dz)] < 0)
                    {
                        topY = y;
                        break;
                    }
                }
                if (topY == int.MinValue)
                {
                    return false;
                }
                if (topY < minTop)
                {
                    minTop = topY;
                }
                if (topY > maxTop)
                {
                    maxTop = topY;
                }
            }
        }
        return (maxTop - minTop) <= 1;
    }

    public static void Build(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getKitId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, bool> chunkExists,
        SurfaceTool st,
        int chunkWorldX, int chunkWorldY, int chunkWorldZ,
        out bool hasAnyFace)
    {
        hasAnyFace = false;
        int activeCells = 0;
        int quadsEmitted = 0;
        int quadsSkipped = 0;

        // World-boundary neighbors: when a neighbour chunk is absent on an
        // axis, the apron cells at local -1 or N on that axis snap their
        // coord to the boundary plane so the apron emit produces a coplanar,
        // axis-aligned face. Without this the apron quad sits ~0.5 voxels
        // off the boundary with per-cell jitter, letting the camera-clip
        // shader see past the mesh at the world edge.
        bool noNegX = !chunkExists(chunkWorldX - 1, chunkWorldY, chunkWorldZ);
        bool noNegY = !chunkExists(chunkWorldX, chunkWorldY - 1, chunkWorldZ);
        bool noNegZ = !chunkExists(chunkWorldX, chunkWorldY, chunkWorldZ - 1);
        bool noPosX = !chunkExists(chunkWorldX + N, chunkWorldY, chunkWorldZ);
        bool noPosY = !chunkExists(chunkWorldX, chunkWorldY + N, chunkWorldZ);
        bool noPosZ = !chunkExists(chunkWorldX, chunkWorldY, chunkWorldZ + N);

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

        // Box-smoothed density at corners in [-1, N+1], 3×3×3 kernel over the
        // raw binary density. The outer corner layer at ±2 exists solely to
        // give this pass a full kernel at the edges. Meshing still reads the
        // raw binary density for topology, so surface placement is unchanged;
        // only the gradient used for per-cell normals sees the smoothed field.
        const int SMOOTH_LO = -1;
        const int SMOOTH_HI = N + 1;
        const int SMOOTH_DIM = N + 3;
        var smoothDensity = new float[SMOOTH_DIM, SMOOTH_DIM, SMOOTH_DIM];
        for (int cx = SMOOTH_LO; cx <= SMOOTH_HI; cx++)
        {
            for (int cy = SMOOTH_LO; cy <= SMOOTH_HI; cy++)
            {
                for (int cz = SMOOTH_LO; cz <= SMOOTH_HI; cz++)
                {
                    int sum = 0;
                    for (int ox = -1; ox <= 1; ox++)
                    {
                        for (int oy = -1; oy <= 1; oy++)
                        {
                            for (int oz = -1; oz <= 1; oz++)
                            {
                                sum += density[CornerIdx(cx + ox), CornerIdx(cy + oy), CornerIdx(cz + oz)];
                            }
                        }
                    }
                    smoothDensity[cx - SMOOTH_LO, cy - SMOOTH_LO, cz - SMOOTH_LO] = sum;
                }
            }
        }

        var cellVert = new Vector3[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellTile = new int[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-cell dominant kit id. Same 27-voxel majority vote as the tile
        // pick, but counting KitId instead of VoxelType. Triangles carry three
        // corner kits via CUSTOM1.yzw so the shader can barycentric-blend at
        // kit boundaries the same way it does for tile boundaries.
        var cellKit = new int[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-cell dominant overlay id. Unlike kit/tile, the vote ignores
        // OverlayId=0 — most buried voxels carry zero, so a naive majority
        // would drown out a single surface voxel stamped with an overlay. The
        // rule is "any non-zero wins, tie-broken by count."
        var cellOverlay = new int[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellAmp = new float[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellHas = new bool[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-vertex sharpness in [0,1]: 0 = fully smooth (rely on interpolated
        // NORMAL), 1 = fully flat (shader substitutes dFdx/dFdy face normal).
        // Interpolates across the quad, so mixed cells get a soft crease.
        var cellSharpness = new float[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-cell smooth normal, derived deterministically from the cell's 8
        // corner densities. Written to SurfaceTool.SetNormal so we can skip
        // GenerateNormals — which, run per-chunk, would average only the owner
        // chunk's triangles at boundary vertices and disagree with the neighbour
        // chunk's average, producing a visible slope-pick seam at chunk edges.
        var cellNormal = new Vector3[CELL_DIM, CELL_DIM, CELL_DIM];

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

                    PickTileAndAmpForCell(data, x, y, z, getVoxel, getShape, getKitId, getOverlayId, chunkWorldX, chunkWorldY, chunkWorldZ, out int tile, out int kitId, out int overlayId, out float amp, out VoxelTypeInfo.SharpAxes sharpMask, out float sharpness, out VoxelType dominant);

                    // Per-axis majority counts (for snapped coords) and the
                    // edge-midpoint accumulator (for smooth coords). Computed
                    // in one pass so axes can be mixed: snap Y + smooth X,Z
                    // gives flat ceilings with organic walls, for example.
                    int lowX = 0, highX = 0, lowY = 0, highY = 0, lowZ = 0, highZ = 0;
                    for (int ci = 0; ci < 8; ci++)
                    {
                        if (dArr[ci] >= 0) { continue; }
                        var (ox, oy, oz) = CornerOffsets[ci];
                        if (ox == 0) { lowX++; } else { highX++; }
                        if (oy == 0) { lowY++; } else { highY++; }
                        if (oz == 0) { lowZ++; } else { highZ++; }
                    }

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

                    float vx = (sharpMask & VoxelTypeInfo.SharpAxes.X) != 0
                        ? (lowX > highX ? 0f : (highX > lowX ? 1f : 0.5f))
                        : accum.X / count;
                    // Shallow-Y smoothing: disable the Y snap for cells whose
                    // 4 corner columns have topY values within 1 voxel of each
                    // other, so 1-voxel ground steps mesh into navigable ramps
                    // via surface-nets averaging. YHard in the OR'd sharpMask
                    // suppresses the smoothing everywhere — architectural
                    // materials keep crisp single-voxel steps. YHardCeiling
                    // suppresses it only when the cell is a ceiling (more
                    // solid corners on top than bottom); natural terrain uses
                    // this so outdoor floors still ramp but cave ceilings and
                    // overhang undersides stay flat.
                    bool yIsSharp = (sharpMask & VoxelTypeInfo.SharpAxes.Y) != 0;
                    bool yIsHard = (sharpMask & VoxelTypeInfo.SharpAxes.YHard) != 0;
                    bool yCeilingHard = (sharpMask & VoxelTypeInfo.SharpAxes.YHardCeiling) != 0 && highY > lowY;
                    if (yIsSharp && !yIsHard && !yCeilingHard && IsShallowYTransition(density, x, y, z))
                    {
                        yIsSharp = false;
                    }
                    float vy = yIsSharp
                        ? (lowY > highY ? 0f : (highY > lowY ? 1f : 0.5f))
                        : accum.Y / count;
                    float vz = (sharpMask & VoxelTypeInfo.SharpAxes.Z) != 0
                        ? (lowZ > highZ ? 0f : (highZ > lowZ ? 1f : 0.5f))
                        : accum.Z / count;

                    // Snap apron-axis coord to the world boundary plane for
                    // cells on a world-edge apron row. -1 apron snaps to 1
                    // (cell-local +X of the cell, which lines up at world
                    // chunkOrigin). N apron snaps to 0 (cell-local -X, at
                    // world chunkOrigin + N). Each axis is independent so
                    // corner cells get multiple snaps.
                    if (x == -1 && noNegX) { vx = 1f; }
                    if (y == -1 && noNegY) { vy = 1f; }
                    if (z == -1 && noNegZ) { vz = 1f; }
                    if (x == N && noPosX) { vx = 0f; }
                    if (y == N && noPosY) { vy = 0f; }
                    if (z == N && noPosZ) { vz = 0f; }

                    cellVert[CellIdx(x), CellIdx(y), CellIdx(z)] = new Vector3(vx, vy, vz);
                    cellHas[CellIdx(x), CellIdx(y), CellIdx(z)] = true;
                    cellTile[CellIdx(x), CellIdx(y), CellIdx(z)] = tile;
                    cellKit[CellIdx(x), CellIdx(y), CellIdx(z)] = kitId;
                    cellOverlay[CellIdx(x), CellIdx(y), CellIdx(z)] = overlayId;
                    cellAmp[CellIdx(x), CellIdx(y), CellIdx(z)] = amp;
                    cellSharpness[CellIdx(x), CellIdx(y), CellIdx(z)] = sharpness;

                    // Gradient across the cell's 8 corners of the box-smoothed
                    // density. smoothDensity<0 inside, >0 outside, so the raw
                    // gradient already points from solid toward air — that's
                    // the outward surface normal. Using the smoothed field
                    // avoids the per-cell direction quantization that binary
                    // density produces (which manifests as star-shaped lighting
                    // patches and slope-pick fracturing). Deterministic across
                    // chunks because the 3×3×3 kernel reads only densities at
                    // world corners that both neighbours agree on.
                    int sx = x - SMOOTH_LO, sy = y - SMOOTH_LO, sz = z - SMOOTH_LO;
                    float s0 = smoothDensity[sx,   sy,   sz  ];
                    float s1 = smoothDensity[sx+1, sy,   sz  ];
                    float s2 = smoothDensity[sx,   sy+1, sz  ];
                    float s3 = smoothDensity[sx+1, sy+1, sz  ];
                    float s4 = smoothDensity[sx,   sy,   sz+1];
                    float s5 = smoothDensity[sx+1, sy,   sz+1];
                    float s6 = smoothDensity[sx,   sy+1, sz+1];
                    float s7 = smoothDensity[sx+1, sy+1, sz+1];
                    float gx = (s1 + s3 + s5 + s7) - (s0 + s2 + s4 + s6);
                    float gy = (s2 + s3 + s6 + s7) - (s0 + s1 + s4 + s5);
                    float gz = (s4 + s5 + s6 + s7) - (s0 + s1 + s2 + s3);
                    Vector3 normal = new Vector3(gx, gy, gz);
                    float nLen = normal.Length();
                    cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)] = nLen > 1e-5f ? normal / nLen : Vector3.Up;
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
                                cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
                                cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
                                cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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

        // +X / +Y / +Z aprons: the boundary edge at local coord N on the
        // axis (between corner N, which may be solid, and corner N+1, which
        // is always outside the world). Would normally be owned by the
        // absent neighbour chunk at that side.
        if (noPosX && EmitX)
        {
            int cx = N;
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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

        if (noPosY && EmitY)
        {
            int cy = N;
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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

        if (noPosZ && EmitZ)
        {
            int cz = N;
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
                            cellHas, cellVert, cellNormal, cellTile, cellKit, cellOverlay, cellAmp, cellSharpness,
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
        bool[,,] cellHas, Vector3[,,] cellVert, Vector3[,,] cellNormal, int[,,] cellTile, int[,,] cellKit, int[,,] cellOverlay, float[,,] cellAmp, float[,,] cellSharpness,
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

        int k0 = cellKit[i0x, i0y, i0z];
        int k1 = cellKit[i1x, i1y, i1z];
        int k2 = cellKit[i2x, i2y, i2z];
        int k3 = cellKit[i3x, i3y, i3z];

        int o0 = cellOverlay[i0x, i0y, i0z];
        int o1 = cellOverlay[i1x, i1y, i1z];
        int o2 = cellOverlay[i2x, i2y, i2z];
        int o3 = cellOverlay[i3x, i3y, i3z];

        float a0 = cellAmp[i0x, i0y, i0z];
        float a1 = cellAmp[i1x, i1y, i1z];
        float a2 = cellAmp[i2x, i2y, i2z];
        float a3 = cellAmp[i3x, i3y, i3z];

        float s0 = cellSharpness[i0x, i0y, i0z];
        float s1 = cellSharpness[i1x, i1y, i1z];
        float s2 = cellSharpness[i2x, i2y, i2z];
        float s3 = cellSharpness[i3x, i3y, i3z];

        Vector3 n0 = cellNormal[i0x, i0y, i0z];
        Vector3 n1 = cellNormal[i1x, i1y, i1z];
        Vector3 n2 = cellNormal[i2x, i2y, i2z];
        Vector3 n3 = cellNormal[i3x, i3y, i3z];

        if (flip)
        {
            AddTri(st, v0, v2, v1, n0, n2, n1, t0, t2, t1, k0, k2, k1, o0, o2, o1, a0, a2, a1, s0, s2, s1);
            AddTri(st, v0, v3, v2, n0, n3, n2, t0, t3, t2, k0, k3, k2, o0, o3, o2, a0, a3, a2, s0, s3, s2);
        }
        else
        {
            AddTri(st, v0, v1, v2, n0, n1, n2, t0, t1, t2, k0, k1, k2, o0, o1, o2, a0, a1, a2, s0, s1, s2);
            AddTri(st, v0, v2, v3, n0, n2, n3, t0, t2, t3, k0, k2, k3, o0, o2, o3, a0, a2, a3, s0, s2, s3);
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
    //  - CUSTOM1 = (sharpness, kit_a, kit_b, kit_c). .x is per-vertex sharpness
    //    in [0,1]; shader lerps between the interpolated smooth NORMAL and the
    //    dFdx/dFdy face normal by this value, so hard-material cells read as
    //    flat-shaded and soft terrain stays smooth. .yzw are the triangle's
    //    three corner kit ids — constant across the tri, same pattern as tiles.
    //  - CUSTOM2 = (overlay_a, overlay_b, overlay_c, _unused). Per-corner
    //    authored overlay ids; the shader picks the same corner the tile/kit
    //    pick chose so overlay boundaries inherit the organic edge jitter.
    //  - COLOR.rgb = bary indicator (1,0,0)/(0,1,0)/(0,0,1). Linearly interpolated
    //    by the rasterizer so fragment.COLOR.rgb is the barycentric weight vector.
    private static void AddTri(SurfaceTool st,
        Vector3 a, Vector3 b, Vector3 c,
        Vector3 na, Vector3 nb, Vector3 nc,
        int ta, int tb, int tc,
        int ka, int kb, int kc,
        int oa, int ob, int oc,
        float ampA, float ampB, float ampC,
        float sharpA, float sharpB, float sharpC)
    {
        Color custA = new Color(ta, tb, tc, ampA);
        Color custB = new Color(ta, tb, tc, ampB);
        Color custC = new Color(ta, tb, tc, ampC);
        Color sharpCustA = new Color(sharpA, ka, kb, kc);
        Color sharpCustB = new Color(sharpB, ka, kb, kc);
        Color sharpCustC = new Color(sharpC, ka, kb, kc);
        Color overlayCust = new Color(oa, ob, oc, 0f);
        st.SetNormal(na); st.SetColor(new Color(1f, 0f, 0f, 1f)); st.SetCustom(0, custA); st.SetCustom(1, sharpCustA); st.SetCustom(2, overlayCust); st.AddVertex(a);
        st.SetNormal(nb); st.SetColor(new Color(0f, 1f, 0f, 1f)); st.SetCustom(0, custB); st.SetCustom(1, sharpCustB); st.SetCustom(2, overlayCust); st.AddVertex(b);
        st.SetNormal(nc); st.SetColor(new Color(0f, 0f, 1f, 1f)); st.SetCustom(0, custC); st.SetCustom(1, sharpCustC); st.SetCustom(2, overlayCust); st.AddVertex(c);
    }

    // Pick a tile + blend-noise amplitude for the cell. Extended cells (x, y,
    // or z outside [0, N-1]) fall back to a neighbour lookup via getVoxel.
    // A cell's sharp mask is the OR of the per-voxel Shape channel over every
    // solid voxel in its 27-neighbourhood. Worldgen is authoritative for shape
    // — this function just reads the channel. Intent (architectural vs natural
    // vs ramp) lives in the data, not in heuristics here.
    private static void PickTileAndAmpForCell(
        ChunkState data, int x, int y, int z,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getKitId,
        Func<int, int, int, int> getOverlayId,
        int cwX, int cwY, int cwZ,
        out int tile, out int kitId, out int overlayId, out float amp, out VoxelTypeInfo.SharpAxes sharpMask, out float sharpness, out VoxelType dominant)
    {
        // Dominant material: majority vote over the cell's 3x3x3 contributing
        // neighbourhood. DC cells don't "own" the voxels at their 8 corner
        // positions — corners are grid points, and the min-rule in
        // Density.CornerDensity means a corner reads INSIDE if ANY of the 8
        // voxels touching it is solid. So a top-face cell sits one row above
        // the surface and has ALL corner-position voxels in air; the solid
        // voxels that actually produced the sign change live one row below.
        // Voting across the 3x3x3 covers every voxel whose density contributes
        // to any of this cell's 8 corners.
        //
        // The previous "first solid seen" rule scanned the same 27 voxels but
        // accepted whichever lex-order match came first, so a wall-edge cell
        // with self=Air picked up grass from a far non-cliff column. Majority
        // weights each type by how much of the neighbourhood it occupies, so
        // a cliff-face cell with dirt-heavy surroundings reads as dirt.
        Span<int> counts = stackalloc int[16];
        Span<int> kitCounts = stackalloc int[256];
        Span<int> overlayCounts = stackalloc int[256];
        sharpMask = VoxelTypeInfo.SharpAxes.None;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    VoxelType v = getVoxel(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier) { continue; }
                    counts[(int)v]++;
                    sharpMask |= getShape(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    kitCounts[getKitId(cwX + x + dx, cwY + y + dy, cwZ + z + dz)]++;
                    int o = getOverlayId(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    if (o != 0) { overlayCounts[o]++; }
                }
            }
        }
        dominant = VoxelType.Air;
        int bestCount = 0;
        for (int i = 0; i < counts.Length; i++)
        {
            if (counts[i] > bestCount)
            {
                bestCount = counts[i];
                dominant = (VoxelType)i;
            }
        }
        kitId = 0;
        int bestKitCount = 0;
        for (int i = 0; i < kitCounts.Length; i++)
        {
            if (kitCounts[i] > bestKitCount)
            {
                bestKitCount = kitCounts[i];
                kitId = i;
            }
        }
        overlayId = 0;
        int bestOverlayCount = 0;
        for (int i = 1; i < overlayCounts.Length; i++)
        {
            if (overlayCounts[i] > bestOverlayCount)
            {
                bestOverlayCount = overlayCounts[i];
                overlayId = i;
            }
        }

        // Flat-shading is reserved for architectural material (shape=All).
        // Partial snaps (Y-only, for cave/overworld ground) snap the *coord*
        // for a clean plateau but must not drive flat-shading — the shader's
        // slope-based AUTO material pick uses the fragment normal and
        // fractures when flat-shaded quads give two differently-facing
        // triangles straddling a slope threshold.
        sharpness = (sharpMask & VoxelTypeInfo.SharpAxes.All) == VoxelTypeInfo.SharpAxes.All ? 1f : 0f;

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
