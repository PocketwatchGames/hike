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
// deterministic function of VoxelType, so neighbouring chunks compute the
// same vertex for a shared boundary cell.
//
// The sampling lattice is chosen by CVars.voxelCenterSampling. The `density`
// array and all the cx/cy/cz "corner" indexing below address whichever
// lattice is active: voxel corners (min-rule) or voxel centres (one sign per
// voxel). Under centre sampling the lattice sits half a voxel further along
// every axis, so cell vertices carry a +0.5 `latticeOffset` and a sharp cell
// places its vertex at the cell centre — which lands exactly on a voxel-grid
// corner, making SharpAxes.All regions a true cubic mesh.
public static class ChunkMesherDC
{
    private const int N = ChunkState.SIZE;

    // Corners at coord [-2, N+2] inclusive  →  N+5 slots, indexed (coord + 2).
    // The extra corner layer (vs a min [-1, N+1] apron for meshing alone) gives
    // every cell a full 3×3×3 box-smoothing neighbourhood around each of its 8
    // corners, so corner sampling's normal gradient reads off a continuous
    // density field. Both neighbouring chunks sample the same densities at the
    // same world corners, so shared boundary cells agree → no slope-pick seam
    // across chunk boundaries.
    //
    // Cells are ALLOCATED over [-3, N+2] (N+6 slots, indexed coord + 3) but the
    // active range is per-lattice — see cellLo/cellHi in Build. Only [-1, N] is
    // ever emitted (USED_LO/USED_HI); corner sampling computes exactly that.
    //
    // Centre sampling needs two extra rings. Its normals come from accumulating
    // the face normals of every quad touching a cell, then smoothing each
    // emitted cell against its face-neighbours. So an emitted cell at -1 needs
    // accurate accumulation at -2, which needs quads formed from cells at -3.
    // Skip a ring and a boundary cell would sum fewer quads here than the
    // neighbouring chunk sums for the same world cell, and the two would
    // disagree on a shared vertex normal — a lighting seam at every chunk edge.
    private const int CELL_LO = -3;
    private const int CELL_HI = N + 2;
    private const int CELL_DIM = N + 6;
    private const int USED_LO = -1;
    private const int USED_HI = N;
    private const int CORNER_LO = -3;
    private const int CORNER_HI = N + 3;
    private const int CORNER_DIM = N + 7;

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

    // --- Ambient occlusion bake -------------------------------------------
    // Hemisphere occlusion sampled at mesh-gen time and packed into COLOR.a
    // (0 = open/unoccluded, 1 = fully sheltered). Directions span the full
    // sphere; per vertex we use only those in the outward-normal hemisphere
    // (cosine-weighted), so a flat open surface reads ~0 occlusion while inside
    // corners / crevices read high. Sampling the local binary `density` corner
    // field (not getVoxel) keeps the bake cheap and local; clamping at the ±2
    // apron can only nudge the outermost apron row, and AO is low-frequency, so
    // any boundary difference is sub-perceptual. Distinct from concavity — AO is
    // OCCLUSION (how much geometry blocks the hemisphere), not local shape.
    private const int AO_STEPS = 2;            // sample distances: 1, 2 voxels
    private const float AO_MIN_FACING = 0.1f;  // skip near-tangent directions
    private static readonly Vector3[] AoDirs = BuildAoDirs();

    private static Vector3[] BuildAoDirs()
    {
        const float s = 0.57735026f; // 1/sqrt(3)
        return new Vector3[]
        {
            new Vector3( 1, 0, 0), new Vector3(-1, 0, 0),
            new Vector3( 0, 1, 0), new Vector3( 0,-1, 0),
            new Vector3( 0, 0, 1), new Vector3( 0, 0,-1),
            new Vector3( s, s, s), new Vector3(-s, s, s),
            new Vector3( s,-s, s), new Vector3(-s,-s, s),
            new Vector3( s, s,-s), new Vector3(-s, s,-s),
            new Vector3( s,-s,-s), new Vector3(-s,-s,-s),
        };
    }

    // Fraction of the outward hemisphere blocked by nearby solid, in [0,1].
    // Each qualifying direction marches out up to AO_STEPS voxels and stops at
    // the first solid corner; nearer hits occlude more. Cosine-weighted by
    // facing so grazing directions contribute less.
    private static float ComputeAo(sbyte[,,] density, Vector3 p, Vector3 n)
    {
        float occ = 0f;
        float totalW = 0f;
        for (int i = 0; i < AoDirs.Length; i++)
        {
            Vector3 d = AoDirs[i];
            float nd = d.Dot(n);
            if (nd < AO_MIN_FACING)
            {
                continue;
            }
            totalW += nd;
            for (int step = 1; step <= AO_STEPS; step++)
            {
                if (SampleSolid(density, p + d * step))
                {
                    occ += nd * (1f - (float)(step - 1) / AO_STEPS);
                    break;
                }
            }
        }
        return totalW > 1e-5f ? Mathf.Clamp(occ / totalW, 0f, 1f) : 0f;
    }

    // Per-vertex SKY VISIBILITY: the cosine-weighted fraction of this surface's
    // outward hemisphere that reaches open sky.
    //
    // Three earlier models each failed on a case this one has to get right:
    //   - Sampling the light volume near the surface mixed in the ground's own
    //     unlit texels (sunlight is baked into AIR only), and the share of solid
    //     inside the trilinear footprint cycles with the surface's sub-voxel
    //     position — that banded every slope.
    //   - Pushing that sample further out cured the bands but walked it
    //     horizontally out of cliff faces into open sky, lighting a band along
    //     every clifftop as wide as the offset.
    //   - One sample along the normal is orientation-blind: air beside an open
    //     cliff is sky-exposed at every height, so a vertical face read a flat
    //     1.0 like level ground, which washed the lighting out. At a convex lip
    //     the tilted normal also stepped from the enclosed side to the open one,
    //     putting a one-cell bright band along the top of every wall.
    //
    // Averaging over the hemisphere fixes all three: it is orientation-aware
    // (level ground sees the whole hemisphere, a wall roughly half, a ceiling
    // none), and no single direction can flip the result, so a lip blends over
    // its neighbours instead of stepping.
    //
    // `pos` is lattice space (as ComputeAo's), so RoundToInt gives the voxel
    // index the way SampleSolid does. Static per bake — sunlight is recomputed
    // only on load/edit, and time of day is a shader uniform, not part of this.
    private const int SUN_STEPS = 4;

    private static float BakeVertexSun(
        Func<int, int, int, VoxelType> getVoxel, Func<int, int, int, int> getSunlight,
        Vector3 pos, Vector3 n, int cwX, int cwY, int cwZ)
    {
        float lit = 0f;
        float totalW = 0f;
        for (int i = 0; i < AoDirs.Length; i++)
        {
            Vector3 d = AoDirs[i];
            float nd = d.Dot(n);
            if (nd < AO_MIN_FACING)
            {
                continue;
            }
            totalW += nd;

            // March outward, taking the sunlight where the ray ended up so a
            // direction that escapes into shadowed air contributes only that
            // air's light.
            //
            // A blocker's occlusion is GRADED by how near it is, the same way
            // ComputeAo does it, rather than being all-or-nothing. Two reasons,
            // and they pull the same way:
            //   - Ungraded, a ray flipping between clear and blocked swings the
            //     result by 1/14 of the hemisphere. A single vertex swinging that
            //     far shades as a diamond, which is the residual faceting.
            //   - Grading is what puts local variation back. All-or-nothing only
            //     registers geometry close enough to block outright, so broad
            //     open ground came out uniformly lit and read flat.
            float reach = 0f;
            for (int step = 1; step <= SUN_STEPS; step++)
            {
                Vector3 sp = pos + d * step;
                int wx = cwX + Mathf.RoundToInt(sp.X);
                int wy = cwY + Mathf.RoundToInt(sp.Y);
                int wz = cwZ + Mathf.RoundToInt(sp.Z);
                if (VoxelTypeInfo.IsSolid(getVoxel(wx, wy, wz)))
                {
                    // Adjacent blocker (step 1) closes the direction entirely; one
                    // at the end of the march barely dims it.
                    reach *= (step - 1) / (float)SUN_STEPS;
                    break;
                }
                reach = getSunlight(wx, wy, wz) / (float)LightEngine.MAX_LIGHT;
            }
            lit += nd * reach;
        }
        if (totalW <= 1e-5f)
        {
            return 0f;
        }
        float openness = Mathf.Clamp(lit / totalW, 0f, 1f);

        // Local occlusion alone can't darken a cliff: the hemisphere pointing
        // away from an open face genuinely is unobstructed, so the march returns
        // 1.0 for a vertical wall exactly as for level ground. What it misses is
        // that the sky is ABOVE — an unoccluded plane sees (1 + n.y)/2 of the sky
        // hemisphere. That is exact, and gives 1.0 flat, 0.5 vertical, 0 facing
        // down, which is the wall/ground contrast a single sample threw away.
        float skyFacing = (1f + n.Y) * 0.5f;
        return Mathf.Clamp(openness * skyFacing, 0f, 1f);
    }

    // Capped by the spare rings for the same reason as the vertex relaxation:
    // iteration k needs correct values at distance k, and a cell smoothed against
    // a truncated neighbourhood resolves differently in the two chunks sharing
    // it — a lighting seam at every chunk border.
    internal static int SUN_SMOOTH_ITERATIONS = 2;

    // Laplacian smoothing of the baked sun over the surface graph, gated by the
    // same crease rule as the normal smoothing: without it the ground above a
    // thin roof and the cave ceiling under it are face-neighbours and would
    // average together, undoing exactly the separation this bake exists for.
    private static void SmoothSunAcrossSurface(bool[,,] cellHas, Vector3[,,] cellNormal, float[,,] cellSun)
    {
        var src = (float[,,])cellSun.Clone();
        for (int x = CELL_LO; x <= CELL_HI; x++)
        {
            for (int y = CELL_LO; y <= CELL_HI; y++)
            {
                for (int z = CELL_LO; z <= CELL_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)]) { continue; }
                    Vector3 self = cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)];
                    float sum = src[CellIdx(x), CellIdx(y), CellIdx(z)];
                    int n = 1;
                    for (int i = 0; i < FaceNeighbors.Length; i++)
                    {
                        var (dx, dy, dz) = FaceNeighbors[i];
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < CELL_LO || nx > CELL_HI || ny < CELL_LO || ny > CELL_HI
                            || nz < CELL_LO || nz > CELL_HI)
                        {
                            continue;
                        }
                        if (!cellHas[CellIdx(nx), CellIdx(ny), CellIdx(nz)]) { continue; }
                        if (self.Dot(cellNormal[CellIdx(nx), CellIdx(ny), CellIdx(nz)]) < NORMAL_SMOOTH_MIN_DOT)
                        {
                            continue;
                        }
                        sum += src[CellIdx(nx), CellIdx(ny), CellIdx(nz)];
                        n++;
                    }
                    cellSun[CellIdx(x), CellIdx(y), CellIdx(z)] = sum / n;
                }
            }
        }
    }

    private static bool SampleSolid(sbyte[,,] density, Vector3 sp)
    {
        int cx = Math.Clamp(Mathf.RoundToInt(sp.X), CORNER_LO, CORNER_HI);
        int cy = Math.Clamp(Mathf.RoundToInt(sp.Y), CORNER_LO, CORNER_HI);
        int cz = Math.Clamp(Mathf.RoundToInt(sp.Z), CORNER_LO, CORNER_HI);
        return density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)] < 0;
    }

    // --- Concavity bake ----------------------------------------------------
    private static readonly (int dx, int dy, int dz)[] FaceNeighbors =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };

    // Signed local curvature at the cell's vertex: how far the vertex sits below
    // the centroid of its existing face-neighbour vertices, measured along the
    // outward normal. Units are voxels (a dip of ~0.3 voxels reads ~+0.3).
    // Positive = concave dip; negative = convex bump; ~0 = flat. Boundary cells
    // with missing neighbours average over fewer samples — acceptable for a
    // low-frequency wetness term (any tiny seam is imperceptible vs lighting).
    private static float ComputeConcavity(bool[,,] cellHas, Vector3[,,] cellVert, Vector3[,,] cellNormal, int x, int y, int z)
    {
        Vector3 selfPos = cellVert[CellIdx(x), CellIdx(y), CellIdx(z)] + new Vector3(x, y, z);
        Vector3 normal = cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)];
        Vector3 centroid = Vector3.Zero;
        int count = 0;
        for (int i = 0; i < FaceNeighbors.Length; i++)
        {
            var (dx, dy, dz) = FaceNeighbors[i];
            int nx = x + dx, ny = y + dy, nz = z + dz;
            if (nx < CELL_LO || nx > CELL_HI || ny < CELL_LO || ny > CELL_HI || nz < CELL_LO || nz > CELL_HI)
            {
                continue;
            }
            if (!cellHas[CellIdx(nx), CellIdx(ny), CellIdx(nz)])
            {
                continue;
            }
            centroid += cellVert[CellIdx(nx), CellIdx(ny), CellIdx(nz)] + new Vector3(nx, ny, nz);
            count++;
        }
        if (count == 0)
        {
            return 0f;
        }
        centroid /= count;
        return (centroid - selfPos).Dot(normal);
    }

    // Per-axis emission gates for debugging winding. Disable an axis to see
    // whether the remaining geometry still contains a given artifact.
    public static bool EmitX = true;
    public static bool EmitY = true;
    public static bool EmitZ = true;

    public static void Build(
        ChunkState data,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        Func<int, int, int, int> getSunlight,
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

        // Read once per chunk so a mid-build toggle can't split one mesh
        // across both lattices.
        bool centerSampling = CVars.voxelCenterSampling.Value;
        float latticeOffset = centerSampling ? 0.5f : 0f;

        var density = new sbyte[CORNER_DIM, CORNER_DIM, CORNER_DIM];
        for (int cx = CORNER_LO; cx <= CORNER_HI; cx++)
        {
            for (int cy = CORNER_LO; cy <= CORNER_HI; cy++)
            {
                for (int cz = CORNER_LO; cz <= CORNER_HI; cz++)
                {
                    density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)] = centerSampling
                        ? Density.VoxelDensity(chunkWorldX + cx, chunkWorldY + cy, chunkWorldZ + cz, getVoxel)
                        : Density.CornerDensity(chunkWorldX + cx, chunkWorldY + cy, chunkWorldZ + cz, getVoxel);
                }
            }
        }

        // Box-smoothed density at corners in [-1, N+1], 3×3×3 kernel over the
        // raw binary density, used ONLY for corner sampling's normal gradient.
        // Centre sampling derives normals from the emitted geometry instead
        // (see the accumulation pass below), so it skips this entirely — the
        // volumetric kernel is precisely what made buried geometry bleed into
        // surface normals there.
        const int SMOOTH_LO = -1;
        const int SMOOTH_HI = N + 1;
        const int SMOOTH_DIM = N + 3;
        float[,,] smoothDensity = null;
        if (!centerSampling)
        {
            smoothDensity = new float[SMOOTH_DIM, SMOOTH_DIM, SMOOTH_DIM];
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
        }

        var cellVert = new Vector3[CELL_DIM, CELL_DIM, CELL_DIM];
        var cellTile = new int[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-cell dominant kit id. Same 27-voxel majority vote as the tile
        // pick, but counting TerrainId instead of VoxelType. Triangles carry three
        // corner kits via CUSTOM1.yzw so the shader can barycentric-blend at
        // kit boundaries the same way it does for tile boundaries.
        var cellTerrain = new int[CELL_DIM, CELL_DIM, CELL_DIM];
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
        // Per-vertex baked sun, read from the air voxel the surface FACES.
        // See BakeVertexSun for why this can't come from the light volume.
        var cellSun = new float[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-cell smooth normal, derived deterministically from the cell's 8
        // corner densities. Written to SurfaceTool.SetNormal so we can skip
        // GenerateNormals — which, run per-chunk, would average only the owner
        // chunk's triangles at boundary vertices and disagree with the neighbour
        // chunk's average, producing a visible slope-pick seam at chunk edges.
        var cellNormal = new Vector3[CELL_DIM, CELL_DIM, CELL_DIM];
        // Per-vertex ambient occlusion in [0,1]: 0 = open, 1 = fully sheltered.
        // Baked from a low-sample hemisphere check against the local solidity
        // field (ComputeAo); packed into COLOR.a and applied as a multiplicative
        // diffuse darken in voxel_clip.gdshader.
        var cellAo = new float[CELL_DIM, CELL_DIM, CELL_DIM];

        // Active cell range. Centre sampling needs the extra rings so its
        // geometric normal accumulation and smoothing are complete at chunk
        // borders; corner sampling doesn't, and keeping it at [-1, N] leaves
        // that path's output bit-identical to before this lattice work.
        int cellLo = centerSampling ? CELL_LO : USED_LO;
        int cellHi = centerSampling ? CELL_HI : USED_HI;

        for (int x = cellLo; x <= cellHi; x++)
        {
            for (int y = cellLo; y <= cellHi; y++)
            {
                for (int z = cellLo; z <= cellHi; z++)
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

                    // Cells outside the emitted range exist only to complete
                    // centre sampling's normal accumulation. They still need
                    // their vertex placed (so sharpMask/anySoftY), but nothing
                    // reads their tile/kit/overlay — skip that work, which is
                    // what the extra rings would otherwise cost.
                    bool needMaterials = x >= USED_LO && x <= USED_HI && y >= USED_LO && y <= USED_HI && z >= USED_LO && z <= USED_HI;
                    PickTileAndAmpForCell(data, x, y, z, getVoxel, getShape, getTerrainId, getOverlayId, centerSampling, needMaterials, chunkWorldX, chunkWorldY, chunkWorldZ, out int tile, out int TerrainId, out int overlayId, out float amp, out VoxelTypeInfo.SharpAxes sharpMask, out bool anySoftY, out float sharpness, out VoxelType dominant);

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
                        ? SharpCoord(centerSampling, lowX, highX)
                        : accum.X / count;
                    // Y snap is the default for solid ground; any soft voxel
                    // in the 3×3×3 (shape missing the Y bit) overrides it back
                    // to surface-nets averaging. Asymmetric on purpose: a ramp
                    // column's surface voxel authored as SharpAxes.None softens
                    // the mesh cell straddling the ramp base so it blends into
                    // the adjacent plateau column instead of reading as a
                    // crisp 1-voxel step, while a single plateau neighbour
                    // can't re-harden a ramp back into a cliff. X and Z keep
                    // the OR rule so stone walls next to soft terrain still
                    // get crisp vertical creases.
                    bool ySnap = (sharpMask & VoxelTypeInfo.SharpAxes.Y) != 0 && !anySoftY;
                    float vy = ySnap
                        ? SharpCoord(centerSampling, lowY, highY)
                        : accum.Y / count;
                    float vz = (sharpMask & VoxelTypeInfo.SharpAxes.Z) != 0
                        ? SharpCoord(centerSampling, lowZ, highZ)
                        : accum.Z / count;

                    // Shift onto the active lattice before the boundary snap,
                    // so the snap's 1/0 stay absolute cell-local coords.
                    vx += latticeOffset;
                    vy += latticeOffset;
                    vz += latticeOffset;

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
                    cellTerrain[CellIdx(x), CellIdx(y), CellIdx(z)] = TerrainId;
                    cellOverlay[CellIdx(x), CellIdx(y), CellIdx(z)] = overlayId;
                    cellAmp[CellIdx(x), CellIdx(y), CellIdx(z)] = amp;
                    cellSharpness[CellIdx(x), CellIdx(y), CellIdx(z)] = sharpness;

                    if (!centerSampling)
                    {
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
                    }
                    activeCells++;
                }
            }
        }

        // --- Geometric normals (centre sampling) -----------------------------
        // Accumulate the face normal of every quad touching each cell, then
        // normalize. The normal then describes the surface that actually
        // exists, so nothing buried below it can tilt it — a volumetric density
        // gradient reads through the ground and lets a tunnel roof one voxel
        // down rotate a flat surface's normal by up to 45°, which the shader
        // turns into a cliff tile smeared sideways by triplanar projection.
        // Accumulation is unnormalized so larger quads weigh more, and the
        // orientation comes from the density signs (solid → air) rather than
        // winding, so it can't disagree with the emitted triangles.
        if (centerSampling)
        {
            // Read the normals off a RELAXED copy of the surface, not the
            // emitted one. A terraced slope (any ramp shallower than 45°) is
            // genuinely a staircase, so faithful face normals cycle with the
            // tread — measured 0.936/0.889/0.979 repeating on a 1-in-3, which is
            // the lighting banding. Relaxing only this copy leaves the emitted
            // geometry, silhouette and collision untouched; it changes shading
            // alone. Like the normal smoothing it walks the surface graph, so
            // buried geometry still can't reach the surface.
            AccumulateGeometricNormals(density, cellHas, cellVert, cellNormal, cellLo, cellHi);
            Vector3[,,] shadeVert = cellVert;
            if (VERT_RELAX_ITERATIONS > 0)
            {
                // Orientation from the RAW geometry, used only to gate which
                // neighbours may pull on each other (same crease rule as the
                // normal smoothing — see NORMAL_SMOOTH_MIN_DOT). Without it the
                // ground above a tunnel relaxes toward the tunnel ceiling one
                // voxel below, which is precisely the bug this all exists to
                // prevent: measured 0.943 ungated vs 0.992 required.
                var rawNormal = (Vector3[,,])cellNormal.Clone();
                shadeVert = (Vector3[,,])cellVert.Clone();
                for (int i = 0; i < VERT_RELAX_ITERATIONS; i++)
                {
                    RelaxVertsAcrossSurface(cellHas, cellSharpness, rawNormal, shadeVert);
                }
                AccumulateGeometricNormals(density, cellHas, shadeVert, cellNormal, cellLo, cellHi);
            }
            for (int i = 0; i < NORMAL_SMOOTH_ITERATIONS; i++)
            {
                SmoothNormalsAcrossSurface(cellHas, cellNormal);
            }
        }

        // AO bake: hemisphere occlusion at each vertex, oriented by the cell
        // normal. Separate pass because geometric normals aren't known until
        // every cell vertex exists. The vertex sits at the cell origin plus its
        // offset, in the same lattice space `density` is indexed in (cellVert
        // already carries latticeOffset, so this lands correctly in both modes).
        for (int x = USED_LO; x <= USED_HI; x++)
        {
            for (int y = USED_LO; y <= USED_HI; y++)
            {
                for (int z = USED_LO; z <= USED_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)])
                    {
                        continue;
                    }
                    Vector3 aoPos = new Vector3(x, y, z) + cellVert[CellIdx(x), CellIdx(y), CellIdx(z)];
                    Vector3 vn = cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)];
                    cellAo[CellIdx(x), CellIdx(y), CellIdx(z)] = ComputeAo(density, aoPos, vn);
                }
            }
        }

        // Sun bake over the FULL cell range, spare rings included — NOT just the
        // emitted cells like AO. The smoothing below reads neighbours up to
        // SUN_SMOOTH_ITERATIONS cells out, and an unbaked ring reads 0, which
        // dragged every chunk border dark and put a lighting seam at each chunk
        // edge. Baking the rings is also what makes the two chunks sharing a
        // boundary cell arrive at the same smoothed value. AO needs no such
        // treatment because it is never smoothed.
        for (int x = CELL_LO; x <= CELL_HI; x++)
        {
            for (int y = CELL_LO; y <= CELL_HI; y++)
            {
                for (int z = CELL_LO; z <= CELL_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)])
                    {
                        continue;
                    }
                    Vector3 sunPos = new Vector3(x, y, z) + cellVert[CellIdx(x), CellIdx(y), CellIdx(z)];
                    cellSun[CellIdx(x), CellIdx(y), CellIdx(z)] = BakeVertexSun(
                        getVoxel, getSunlight, sunPos, cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)],
                        chunkWorldX, chunkWorldY, chunkWorldZ);
                }
            }
        }

        // The hemisphere march resolves each direction against a ROUNDED voxel
        // index, so how many of the 14 rays land in solid flips with a vertex's
        // sub-voxel position. That quantisation is per-vertex noise, and a lone
        // vertex differing from its neighbours shades as a diamond — the union of
        // the triangles fanning off it. Smoothing over the surface graph removes
        // the outliers while leaving the large-scale structure (ground 1.0, wall
        // 0.5, ceiling 0.0) intact, since that varies over many cells.
        for (int i = 0; i < SUN_SMOOTH_ITERATIONS; i++)
        {
            SmoothSunAcrossSurface(cellHas, cellNormal, cellSun);
        }

        // --- Concavity bake ------------------------------------------------
        // Local SHAPE/curvature per vertex: compare each cell vertex to the
        // centroid of its face-neighbour vertices, projected onto the outward
        // normal. Positive = the vertex sits below its neighbours along the
        // normal = a dip/bowl (water pools); negative = a bump/ridge. This is a
        // DISTINCT signal from AO — a vertex can be unoccluded yet concave (a
        // shallow open bowl) or occluded yet flat (flush against a wall). Stored
        // in CUSTOM2.w and consumed by the wetness term in voxel_clip.gdshader.
        // Second pass because it reads neighbour cells' finished vertices; reuses
        // already-computed cellVert/cellNormal, so no extra field sampling.
        var cellConcavity = new float[CELL_DIM, CELL_DIM, CELL_DIM];
        for (int x = USED_LO; x <= USED_HI; x++)
        {
            for (int y = USED_LO; y <= USED_HI; y++)
            {
                for (int z = USED_LO; z <= USED_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)])
                    {
                        continue;
                    }
                    cellConcavity[CellIdx(x), CellIdx(y), CellIdx(z)] =
                        ComputeConcavity(cellHas, cellVert, cellNormal, x, y, z);
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
                                cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                                cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                                cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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
                            cellHas, cellVert, cellNormal, cellTile, cellTerrain, cellOverlay, cellAmp, cellSharpness, cellAo, cellSun, cellConcavity,
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

    // Sum the face normal of every sign-change quad into each of the four cells
    // whose vertices form it, then normalize — an area-weighted vertex normal
    // taken from the real geometry.
    //
    // Iterates the FULL allocated cell range rather than the emitted range, so
    // a cell on a chunk border sums exactly the same quads the neighbouring
    // chunk sums for that same world cell. Both chunks compute identical vertex
    // positions for shared cells (density is a deterministic function of
    // VoxelType), so both arrive at the same normal and shared vertices don't
    // crease. Skipping this and only summing owned quads is what would produce
    // a lighting seam at every chunk edge.
    private static void AccumulateGeometricNormals(
        sbyte[,,] density, bool[,,] cellHas, Vector3[,,] cellVert, Vector3[,,] cellNormal,
        int cellLo, int cellHi)
    {
        Array.Clear(cellNormal);

        for (int cx = cellLo; cx <= cellHi + 1; cx++)
        {
            for (int cy = cellLo; cy <= cellHi + 1; cy++)
            {
                for (int cz = cellLo; cz <= cellHi + 1; cz++)
                {
                    sbyte a = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz)];

                    if (cx <= cellHi)
                    {
                        sbyte b = density[CornerIdx(cx + 1), CornerIdx(cy), CornerIdx(cz)];
                        if ((a < 0) != (b < 0))
                        {
                            AddQuadNormal(cellHas, cellVert, cellNormal, cellLo, cellHi,
                                a < 0 ? Vector3.Right : Vector3.Left,
                                cx, cy - 1, cz - 1, cx, cy, cz - 1, cx, cy, cz, cx, cy - 1, cz);
                        }
                    }
                    if (cy <= cellHi)
                    {
                        sbyte b = density[CornerIdx(cx), CornerIdx(cy + 1), CornerIdx(cz)];
                        if ((a < 0) != (b < 0))
                        {
                            AddQuadNormal(cellHas, cellVert, cellNormal, cellLo, cellHi,
                                a < 0 ? Vector3.Up : Vector3.Down,
                                cx - 1, cy, cz - 1, cx - 1, cy, cz, cx, cy, cz, cx, cy, cz - 1);
                        }
                    }
                    if (cz <= cellHi)
                    {
                        sbyte b = density[CornerIdx(cx), CornerIdx(cy), CornerIdx(cz + 1)];
                        if ((a < 0) != (b < 0))
                        {
                            AddQuadNormal(cellHas, cellVert, cellNormal, cellLo, cellHi,
                                a < 0 ? Vector3.Back : Vector3.Forward,
                                cx - 1, cy - 1, cz, cx, cy - 1, cz, cx, cy, cz, cx - 1, cy, cz);
                        }
                    }
                }
            }
        }

        for (int x = cellLo; x <= cellHi; x++)
        {
            for (int y = cellLo; y <= cellHi; y++)
            {
                for (int z = cellLo; z <= cellHi; z++)
                {
                    Vector3 n = cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)];
                    float len = n.Length();
                    cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)] = len > 1e-5f ? n / len : Vector3.Up;
                }
            }
        }
    }

    // How strongly a cell keeps its own accumulated normal versus its
    // face-neighbours'. 1 = pure average (smoothest, softest creases); higher
    // preserves more local shape. 2 restores the volumetric gradient's
    // smoothness on staircase ramps while keeping cliff lips readable.
    internal static float NORMAL_SMOOTH_SELF_WEIGHT = 2f;

    // Minimum alignment (dot) for a neighbour to be smoothed against. Cell
    // adjacency is only a PROXY for surface adjacency — two unrelated surfaces
    // can occupy adjacent cells. The case that matters: ground with a tunnel one
    // voxel beneath, where the floor cell (normal +Y) and the tunnel-ceiling
    // cell (normal −Y) are face-neighbours; averaging them would smuggle buried
    // geometry back into the surface normal, the exact bug geometric normals
    // exist to kill. Rejecting misaligned neighbours also preserves cliff lips
    // (floor vs wall dot ≈ 0) while keeping ramps (neighbours within a few
    // degrees) fully smoothed.
    internal static float NORMAL_SMOOTH_MIN_DOT = 0.5f;

    internal static int NORMAL_SMOOTH_ITERATIONS = 1;

    // Iterations of vertex relaxation applied to the shading copy of the
    // surface. 0 disables (normals read the emitted geometry verbatim).
    //
    // Capped by the spare rings, NOT by looks: each iteration pulls truncation
    // one cell further in, and a cell relaxed against a truncated neighbourhood
    // resolves differently in the two chunks that share it — a lighting seam at
    // every chunk border. Iteration k needs correct values at distance k, so
    // with USED_LO = -1 and CELL_LO = -3 exactly 2 are exact. Raising this
    // requires widening CELL_LO/CELL_HI to match.
    internal static int VERT_RELAX_ITERATIONS = 2;

    // Laplacian relaxation of the cell vertices, over the same surface graph the
    // normal smoothing uses. Positions are averaged in lattice space (cell
    // origin + offset), so a staircase's treads and risers pull each other
    // toward the plane they approximate.
    //
    // Architectural cells (sharpness 1) are pinned: a wall corner must keep its
    // exact edge, and it is the one place the staircase is the intended shape.
    private static void RelaxVertsAcrossSurface(bool[,,] cellHas, float[,,] cellSharpness,
        Vector3[,,] rawNormal, Vector3[,,] cellVert)
    {
        var src = (Vector3[,,])cellVert.Clone();
        for (int x = CELL_LO; x <= CELL_HI; x++)
        {
            for (int y = CELL_LO; y <= CELL_HI; y++)
            {
                for (int z = CELL_LO; z <= CELL_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)]) { continue; }
                    if (cellSharpness[CellIdx(x), CellIdx(y), CellIdx(z)] > 0.5f) { continue; }

                    Vector3 sum = src[CellIdx(x), CellIdx(y), CellIdx(z)] + new Vector3(x, y, z);
                    int n = 1;
                    for (int i = 0; i < FaceNeighbors.Length; i++)
                    {
                        var (dx, dy, dz) = FaceNeighbors[i];
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < CELL_LO || nx > CELL_HI || ny < CELL_LO || ny > CELL_HI
                            || nz < CELL_LO || nz > CELL_HI)
                        {
                            continue;
                        }
                        if (!cellHas[CellIdx(nx), CellIdx(ny), CellIdx(nz)]) { continue; }
                        if (rawNormal[CellIdx(x), CellIdx(y), CellIdx(z)]
                                .Dot(rawNormal[CellIdx(nx), CellIdx(ny), CellIdx(nz)]) < NORMAL_SMOOTH_MIN_DOT)
                        {
                            continue;
                        }
                        sum += src[CellIdx(nx), CellIdx(ny), CellIdx(nz)] + new Vector3(nx, ny, nz);
                        n++;
                    }
                    cellVert[CellIdx(x), CellIdx(y), CellIdx(z)] = sum / n - new Vector3(x, y, z);
                }
            }
        }
    }

    // Laplacian smoothing over the SURFACE graph — each cell averaged against
    // the face-adjacent cells that also carry geometry. This is the crucial
    // distinction from the box filter it replaces: that one smoothed the
    // volumetric density field, so it reached DOWN through the ground and let a
    // buried tunnel tilt the surface above it. Walking cell-to-cell can only
    // ever touch the same surface, so ramps get their smooth shading back with
    // no path for buried geometry to influence anything.
    //
    // Only emitted cells are written, but neighbours are read from the extra
    // rings — which is why those rings exist and must be accumulated exactly.
    // Reads from a snapshot so the pass is order-independent and both chunks
    // sharing a boundary cell compute the same result.
    private static void SmoothNormalsAcrossSurface(bool[,,] cellHas, Vector3[,,] cellNormal)
    {
        var src = (Vector3[,,])cellNormal.Clone();
        for (int x = USED_LO; x <= USED_HI; x++)
        {
            for (int y = USED_LO; y <= USED_HI; y++)
            {
                for (int z = USED_LO; z <= USED_HI; z++)
                {
                    if (!cellHas[CellIdx(x), CellIdx(y), CellIdx(z)])
                    {
                        continue;
                    }
                    Vector3 self = src[CellIdx(x), CellIdx(y), CellIdx(z)];
                    Vector3 sum = self * NORMAL_SMOOTH_SELF_WEIGHT;
                    for (int i = 0; i < FaceNeighbors.Length; i++)
                    {
                        var (dx, dy, dz) = FaceNeighbors[i];
                        int nx = x + dx, ny = y + dy, nz = z + dz;
                        if (nx < CELL_LO || nx > CELL_HI || ny < CELL_LO || ny > CELL_HI || nz < CELL_LO || nz > CELL_HI)
                        {
                            continue;
                        }
                        if (!cellHas[CellIdx(nx), CellIdx(ny), CellIdx(nz)])
                        {
                            continue;
                        }
                        Vector3 other = src[CellIdx(nx), CellIdx(ny), CellIdx(nz)];
                        if (self.Dot(other) < NORMAL_SMOOTH_MIN_DOT)
                        {
                            continue;
                        }
                        sum += other;
                    }
                    float len = sum.Length();
                    if (len > 1e-5f)
                    {
                        cellNormal[CellIdx(x), CellIdx(y), CellIdx(z)] = sum / len;
                    }
                }
            }
        }
    }

    // One quad's contribution. `outward` is the solid→air direction implied by
    // the density signs; the cross product is flipped to match it so the result
    // never depends on winding. Magnitude is left unnormalized (≈ twice the
    // quad area) so big faces dominate small ones.
    private static void AddQuadNormal(
        bool[,,] cellHas, Vector3[,,] cellVert, Vector3[,,] cellNormal, int cellLo, int cellHi,
        Vector3 outward,
        int x0, int y0, int z0, int x1, int y1, int z1,
        int x2, int y2, int z2, int x3, int y3, int z3)
    {
        if (!InRange(x0, y0, z0, cellLo, cellHi) || !InRange(x1, y1, z1, cellLo, cellHi)
            || !InRange(x2, y2, z2, cellLo, cellHi) || !InRange(x3, y3, z3, cellLo, cellHi))
        {
            return;
        }
        if (!cellHas[CellIdx(x0), CellIdx(y0), CellIdx(z0)] || !cellHas[CellIdx(x1), CellIdx(y1), CellIdx(z1)]
            || !cellHas[CellIdx(x2), CellIdx(y2), CellIdx(z2)] || !cellHas[CellIdx(x3), CellIdx(y3), CellIdx(z3)])
        {
            return;
        }

        Vector3 v0 = cellVert[CellIdx(x0), CellIdx(y0), CellIdx(z0)] + new Vector3(x0, y0, z0);
        Vector3 v1 = cellVert[CellIdx(x1), CellIdx(y1), CellIdx(z1)] + new Vector3(x1, y1, z1);
        Vector3 v2 = cellVert[CellIdx(x2), CellIdx(y2), CellIdx(z2)] + new Vector3(x2, y2, z2);
        Vector3 v3 = cellVert[CellIdx(x3), CellIdx(y3), CellIdx(z3)] + new Vector3(x3, y3, z3);

        Vector3 n = (v2 - v0).Cross(v3 - v1);
        if (n.Dot(outward) < 0f)
        {
            n = -n;
        }

        cellNormal[CellIdx(x0), CellIdx(y0), CellIdx(z0)] += n;
        cellNormal[CellIdx(x1), CellIdx(y1), CellIdx(z1)] += n;
        cellNormal[CellIdx(x2), CellIdx(y2), CellIdx(z2)] += n;
        cellNormal[CellIdx(x3), CellIdx(y3), CellIdx(z3)] += n;
    }

    private static bool InRange(int x, int y, int z, int lo, int hi)
    {
        return x >= lo && x <= hi && y >= lo && y <= hi && z >= lo && z <= hi;
    }

    // Sharp-axis vertex placement, in cell-local coords before latticeOffset.
    // Corner sampling: the majority side, so the vertex lands on the voxel
    // boundary the dilated field implies. Centre sampling: always the cell
    // centre, which after the +0.5 offset is exactly a voxel-grid corner —
    // the majority counts carry no extra information there, because each of
    // the cell's 8 corners IS a voxel.
    private static float SharpCoord(bool centerSampling, int low, int high)
    {
        if (centerSampling)
        {
            return 0.5f;
        }
        return low > high ? 0f : (high > low ? 1f : 0.5f);
    }

    // Set by Build before each axis pass so EmitQuad can log per-quad context.
    private static char s_axisTag;
    private static int s_edgeCx, s_edgeCy, s_edgeCz;
    private static sbyte s_edgeA, s_edgeB;

    private static void EmitQuad(
        SurfaceTool st,
        bool[,,] cellHas, Vector3[,,] cellVert, Vector3[,,] cellNormal, int[,,] cellTile, int[,,] cellTerrain, int[,,] cellOverlay, float[,,] cellAmp, float[,,] cellSharpness, float[,,] cellAo, float[,,] cellSun, float[,,] cellConcavity,
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

        int k0 = cellTerrain[i0x, i0y, i0z];
        int k1 = cellTerrain[i1x, i1y, i1z];
        int k2 = cellTerrain[i2x, i2y, i2z];
        int k3 = cellTerrain[i3x, i3y, i3z];

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

        float ao0 = cellAo[i0x, i0y, i0z];
        float ao1 = cellAo[i1x, i1y, i1z];
        float ao2 = cellAo[i2x, i2y, i2z];
        float ao3 = cellAo[i3x, i3y, i3z];

        float sun0 = cellSun[i0x, i0y, i0z];
        float sun1 = cellSun[i1x, i1y, i1z];
        float sun2 = cellSun[i2x, i2y, i2z];
        float sun3 = cellSun[i3x, i3y, i3z];

        float con0 = cellConcavity[i0x, i0y, i0z];
        float con1 = cellConcavity[i1x, i1y, i1z];
        float con2 = cellConcavity[i2x, i2y, i2z];
        float con3 = cellConcavity[i3x, i3y, i3z];

        Vector3 n0 = cellNormal[i0x, i0y, i0z];
        Vector3 n1 = cellNormal[i1x, i1y, i1z];
        Vector3 n2 = cellNormal[i2x, i2y, i2z];
        Vector3 n3 = cellNormal[i3x, i3y, i3z];

        // Binary density pins every edge crossing to t=0.5, so a quad's four
        // vertices are frequently non-planar and the split diagonal decides how
        // much the two triangles disagree — visible as faceting, worst near 45°.
        // Split along whichever diagonal leaves the halves more parallel. Moves
        // no vertices and reads no field, so it is lattice-independent and
        // cannot interact with sharp features.
        bool splitV1V3 = Kink(v1, v2, v3, v1, v3, v0) < Kink(v0, v1, v2, v0, v2, v3);
        if (flip)
        {
            if (splitV1V3)
            {
                AddTri(st, v1, v3, v2, n1, n3, n2, t1, t3, t2, k1, k3, k2, o1, o3, o2, a1, a3, a2, s1, s3, s2, ao1, ao3, ao2, sun1, sun3, sun2, con1, con3, con2);
                AddTri(st, v1, v0, v3, n1, n0, n3, t1, t0, t3, k1, k0, k3, o1, o0, o3, a1, a0, a3, s1, s0, s3, ao1, ao0, ao3, sun1, sun0, sun3, con1, con0, con3);
            }
            else
            {
                AddTri(st, v0, v2, v1, n0, n2, n1, t0, t2, t1, k0, k2, k1, o0, o2, o1, a0, a2, a1, s0, s2, s1, ao0, ao2, ao1, sun0, sun2, sun1, con0, con2, con1);
                AddTri(st, v0, v3, v2, n0, n3, n2, t0, t3, t2, k0, k3, k2, o0, o3, o2, a0, a3, a2, s0, s3, s2, ao0, ao3, ao2, sun0, sun3, sun2, con0, con3, con2);
            }
        }
        else
        {
            if (splitV1V3)
            {
                AddTri(st, v1, v2, v3, n1, n2, n3, t1, t2, t3, k1, k2, k3, o1, o2, o3, a1, a2, a3, s1, s2, s3, ao1, ao2, ao3, sun1, sun2, sun3, con1, con2, con3);
                AddTri(st, v1, v3, v0, n1, n3, n0, t1, t3, t0, k1, k3, k0, o1, o3, o0, a1, a3, a0, s1, s3, s0, ao1, ao3, ao0, sun1, sun3, sun0, con1, con3, con0);
            }
            else
            {
                AddTri(st, v0, v1, v2, n0, n1, n2, t0, t1, t2, k0, k1, k2, o0, o1, o2, a0, a1, a2, s0, s1, s2, ao0, ao1, ao2, sun0, sun1, sun2, con0, con1, con2);
                AddTri(st, v0, v2, v3, n0, n2, n3, t0, t2, t3, k0, k2, k3, o0, o2, o3, a0, a2, a3, s0, s2, s3, ao0, ao2, ao3, sun0, sun2, sun3, con0, con2, con3);
            }
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

    // Angle between two triangles' planes, as 1 - dot of their unit normals
    // (0 = coplanar). Used only to compare a quad's two possible splits.
    private static float Kink(Vector3 a0, Vector3 a1, Vector3 a2, Vector3 b0, Vector3 b1, Vector3 b2)
    {
        Vector3 na = (a1 - a0).Cross(a2 - a0);
        Vector3 nb = (b1 - b0).Cross(b2 - b0);
        float la = na.Length();
        float lb = nb.Length();
        if (la < 1e-9f || lb < 1e-9f)
        {
            return 0f;
        }
        return 1f - (na / la).Dot(nb / lb);
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
    //  - CUSTOM2 = (overlay_a, overlay_b, overlay_c, concavity). xyz are the
    //    per-corner authored overlay ids; the shader picks the same corner the
    //    tile/kit pick chose so overlay boundaries inherit the organic edge
    //    jitter. .w is per-vertex baked concavity (signed voxels; + = dip), read
    //    by the wetness term. Unlike xyz it is genuinely per-vertex, not a flat
    //    triangle constant, so each vertex carries its own overlay color.
    //  - COLOR.rgb = bary indicator (1,0,0)/(0,1,0)/(0,0,1). Linearly interpolated
    //    by the rasterizer so fragment.COLOR.rgb is the barycentric weight vector.
    //  - COLOR.a = baked ambient occlusion (0 = open, 1 = sheltered). Independent
    //    of the bary pick; read in voxel_clip.gdshader for the diffuse darken.
    private static void AddTri(SurfaceTool st,
        Vector3 a, Vector3 b, Vector3 c,
        Vector3 na, Vector3 nb, Vector3 nc,
        int ta, int tb, int tc,
        int ka, int kb, int kc,
        int oa, int ob, int oc,
        float ampA, float ampB, float ampC,
        float sharpA, float sharpB, float sharpC,
        float aoA, float aoB, float aoC,
        float sunA, float sunB, float sunC,
        float conA, float conB, float conC)
    {
        Color custA = new Color(ta, tb, tc, ampA);
        Color custB = new Color(ta, tb, tc, ampB);
        Color custC = new Color(ta, tb, tc, ampC);
        Color sharpCustA = new Color(sharpA, ka, kb, kc);
        Color sharpCustB = new Color(sharpB, ka, kb, kc);
        Color sharpCustC = new Color(sharpC, ka, kb, kc);
        // overlay ids xyz are constant across the tri; concavity (.w) is
        // per-vertex, so each corner gets its own CUSTOM2.
        Color overlayCustA = new Color(oa, ob, oc, conA);
        Color overlayCustB = new Color(oa, ob, oc, conB);
        Color overlayCustC = new Color(oa, ob, oc, conC);
        // CUSTOM3.x = baked sun; yzw reserved.
        st.SetNormal(na); st.SetColor(new Color(1f, 0f, 0f, aoA)); st.SetCustom(0, custA); st.SetCustom(1, sharpCustA); st.SetCustom(2, overlayCustA); st.SetCustom(3, new Color(sunA, 0f, 0f, 0f)); st.AddVertex(a);
        st.SetNormal(nb); st.SetColor(new Color(0f, 1f, 0f, aoB)); st.SetCustom(0, custB); st.SetCustom(1, sharpCustB); st.SetCustom(2, overlayCustB); st.SetCustom(3, new Color(sunB, 0f, 0f, 0f)); st.AddVertex(b);
        st.SetNormal(nc); st.SetColor(new Color(0f, 0f, 1f, aoC)); st.SetCustom(0, custC); st.SetCustom(1, sharpCustC); st.SetCustom(2, overlayCustC); st.SetCustom(3, new Color(sunC, 0f, 0f, 0f)); st.AddVertex(c);
    }

    // Pick a tile + blend-noise amplitude for the cell. Extended cells (x, y,
    // or z outside [0, N-1]) fall back to a neighbour lookup via getVoxel.
    // A cell's sharp mask is the OR of the per-voxel Shape channel over every
    // solid voxel in its neighbourhood, plus a separate anySoftY flag that
    // records whether ANY solid voxel lacked the Y bit — the caller uses the
    // flag to back off Y snapping so a ramp column's soft surface voxel can
    // smooth the ramp-base transition without getting overpowered by the OR.
    // Worldgen is authoritative for shape — this function just reads the
    // channel. Intent (architectural vs natural vs ramp) lives in the data,
    // not in heuristics here.
    private static void PickTileAndAmpForCell(
        ChunkState data, int x, int y, int z,
        Func<int, int, int, VoxelType> getVoxel,
        Func<int, int, int, VoxelTypeInfo.SharpAxes> getShape,
        Func<int, int, int, int> getTerrainId,
        Func<int, int, int, int> getOverlayId,
        bool centerSampling,
        bool needMaterials,
        int cwX, int cwY, int cwZ,
        out int tile, out int TerrainId, out int overlayId, out float amp, out VoxelTypeInfo.SharpAxes sharpMask, out bool anySoftY, out float sharpness, out VoxelType dominant)
    {
        // Neighbourhood: one voxel beyond the cell's span on every side. DC
        // cells don't "own" the voxels at their corner positions, and how far
        // the window reaches decides more than the material — sharpMask and
        // anySoftY are OR/ANY reductions over it, and they drive axis snapping
        // and flat-shading. Narrowing the reach makes anySoftY fire less often,
        // which re-hardens Y snapping into 1-voxel steps on ground the smooth
        // path used to blend; those steps read as near-vertical faces, and the
        // shader's AUTO band then picks the WALL tile for them. So keep the
        // reach identical in both lattices:
        // Window = exactly the voxels feeding this cell's 8 lattice corners.
        //   Corner: corners sit at x..x+1 and each is fed by voxels x-1..x, so
        //           the contributors are the 3×3×3 at x-1..x+1.
        //   Centre: corners ARE voxels x..x+1, so it's the cell's own 2×2×2.
        // Do NOT widen the centre window to "match" the corner one. It also
        // feeds sharpMask, an OR — every extra layer pulls in more neighbouring
        // Stone SharpAxes.All, flipping Terrain cells to sharpness=1, which
        // makes the shader swap the smooth normal for the flat face normal and
        // hand flat ground the WALL tile. Measured over a real world: 2×2×2
        // gives 0.23% of Terrain cells sharpness=1, 3-wide 0.65%, 4-wide 1.05%,
        // against 0.33% for corner sampling.
        // Majority weights each type by how much of the neighbourhood it
        // occupies, so a cliff-face cell with dirt-heavy surroundings reads as
        // dirt.
        Span<int> counts = stackalloc int[16];
        Span<int> terrainCounts = stackalloc int[256];
        Span<int> overlayCounts = stackalloc int[256];
        sharpMask = VoxelTypeInfo.SharpAxes.None;
        anySoftY = false;
        // anySoftY reads a WIDER window than sharpMask, and deliberately so.
        // The two ORs want opposite things. sharpMask drives flat-shading and
        // per-axis snapping, so every extra layer wrongly hardens Terrain next
        // to Stone. anySoftY DISABLES the Y snap, and worldgen relies on it
        // spreading: a ramp column's soft surface voxel is supposed to
        // propagate horizontally into the adjacent plateau column's surface
        // cell so the ramp base blends instead of reading as a 1-voxel step
        // (see WorldGen's per-column shape comment). Confining it to the cell's
        // own 2×2×2 leaves gentle ramps snapping into stairs.
        int lo = centerSampling ? 0 : -1;
        for (int dx = -1; dx <= 1; dx++)
        {
            for (int dy = -1; dy <= 1; dy++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    VoxelType v = getVoxel(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier) { continue; }
                    var shape = getShape(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    if ((shape & VoxelTypeInfo.SharpAxes.Y) == 0) { anySoftY = true; }
                    if (dx < lo || dy < lo || dz < lo) { continue; }
                    sharpMask |= shape;
                    if (!needMaterials) { continue; }
                    counts[(int)v]++;
                    terrainCounts[getTerrainId(cwX + x + dx, cwY + y + dy, cwZ + z + dz)]++;
                    int o = getOverlayId(cwX + x + dx, cwY + y + dy, cwZ + z + dz);
                    if (o != 0) { overlayCounts[o]++; }
                }
            }
        }
        if (!needMaterials)
        {
            tile = 0;
            TerrainId = 0;
            overlayId = 0;
            amp = 0f;
            sharpness = 0f;
            dominant = VoxelType.Air;
            return;
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
        TerrainId = 0;
        int bestTerrainCount = 0;
        for (int i = 0; i < terrainCounts.Length; i++)
        {
            if (terrainCounts[i] > bestTerrainCount)
            {
                bestTerrainCount = terrainCounts[i];
                TerrainId = i;
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
