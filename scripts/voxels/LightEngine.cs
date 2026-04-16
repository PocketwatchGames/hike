using System;
using System.Collections.Generic;
using Godot;

public static class LightEngine
{
    // Sun BFS still uses these — the sun channel is max-fill, untouched by
    // the relaxation rewrite. Block lights compute per-source kernels via
    // iterative diffusion (see ComputeFootprint), so the BFS-style "level
    // minus per-step falloff" is only a sun concept now.
    public const int MAX_LIGHT = 60;
    public const int FALLOFF_PER_VOXEL = 4;

    // --- Block-light relaxation tuning ------------------------------------

    // Per-iteration spread coefficient. Each voxel contributes this fraction
    // of its value to each open neighbor and retains (1 - DIFFUSION_RATE *
    // openNeighborCount) of itself. MUST be ≤ 1/6 ≈ 0.166 — otherwise the
    // outflow at a fully-open voxel exceeds 100% of self and energy goes
    // negative (clamped to zero, killing the source). Unity used 0.15.
    // Controls the *shape* of the spread (tight vs broad).
    private const float DIFFUSION_RATE = 0.15f;

    // Per-iteration energy absorption. Each voxel loses this fraction of
    // its energy before diffusing. Controls *reach / falloff steepness* —
    // higher absorption = shorter reach, sharper falloff. With absorption
    // the field converges to a steady state where injection = total absorbed,
    // giving a predictable exponential-ish falloff that's independent of
    // DIFFUSION_RATE. Also gives the "small room brighter than large room"
    // behavior because less total volume absorbs the same injected energy.
    private const float ABSORPTION_RATE = 0.08f;

    // Per-light reach in voxels = max(MIN_REACH, source.Level / REACH_DIVISOR).
    // Sets the bounding-box half-extent for the relaxation buffer.
    private const int REACH_DIVISOR = 4;
    private const int MIN_REACH = 2;

    // Iteration count for the diffusion = max(MIN_ITERS, source.Level /
    // ITER_DIVISOR). More iterations let energy fill the kernel further; the
    // kernel buffer is sized to bound the work.
    private const int ITER_DIVISOR = 4;
    private const int MIN_ITERS = 4;

    // Seed magnitude per unit of source.Level. With absorption, the source
    // voxel gets re-injected each iteration so it stays bright. This scale
    // factor controls the peak brightness at the source — tune until a
    // max-level white torch reads ~200-255 GPU bytes at the source voxel.
    private const float SEED_PER_LEVEL = 40.0f;

    private static readonly Vector3I[] Neighbors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static void ComputeSunlight(WorldState world)
    {
        int minWx = world.Min.X * ChunkState.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkState.SIZE;
        int minWy = world.Min.Y * ChunkState.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;

        var queue = new Queue<(int x, int y, int z)>();

        for (int wx = minWx; wx < maxWx; wx++)
        {
            for (int wz = minWz; wz < maxWz; wz++)
            {
                int sunLevel = MAX_LIGHT;
                for (int wy = topWy; wy >= minWy; wy--)
                {
                    VoxelType v = world.GetVoxelWorld(wx, wy, wz);
                    if (v != VoxelType.Air && !VoxelTypeInfo.IsTransparent(v))
                    {
                        break;
                    }
                    sunLevel -= VoxelTypeInfo.LightAttenuation(v);
                    if (sunLevel <= 0)
                    {
                        break;
                    }
                    world.SetSunlightWorld(wx, wy, wz, sunLevel);
                    queue.Enqueue((wx, wy, wz));
                }
            }
        }

        SpreadSunlight(world, queue);
    }

    public static void AddLightSource(WorldState world, LightSource source)
    {
        if (source.Level <= 0) { return; }
        ComputeFootprint(world, source);
        DepositFootprint(world, source, source.Amplitude);
        if (!world.LightSources.Contains(source))
        {
            world.LightSources.Add(source);
        }
    }

    public static void RemoveLightSource(WorldState world, LightSource source)
    {
        DepositFootprint(world, source, -source.Amplitude);
        source.Footprint.Clear();
        source.Amplitude = 1f;
        world.LightSources.Remove(source);
    }

    // Change the source's brightness without recomputing its kernel. The
    // footprint shape stays the same — only the per-voxel deposit magnitudes
    // change. Cost = O(footprint_size) array writes + chunk dirty marks.
    public static void SetAmplitude(WorldState world, LightSource source, float newAmplitude)
    {
        float delta = newAmplitude - source.Amplitude;
        if (Math.Abs(delta) < 1e-6f) { return; }
        DepositFootprint(world, source, delta);
        source.Amplitude = newAmplitude;
    }

    public static void OnVoxelsChanged(WorldState world, List<Vector3I> changedPositions)
    {
        UpdateSunlightAt(world, changedPositions);

        // Use stored EffectiveBounds for a fast AABB test instead of
        // iterating footprints or computing manhattan distance.
        var affected = new List<LightSource>();
        foreach (LightSource src in world.LightSources)
        {
            foreach (Vector3I cp in changedPositions)
            {
                if (cp.X >= src.BoundsMin.X && cp.X <= src.BoundsMax.X
                    && cp.Y >= src.BoundsMin.Y && cp.Y <= src.BoundsMax.Y
                    && cp.Z >= src.BoundsMin.Z && cp.Z <= src.BoundsMax.Z)
                {
                    affected.Add(src);
                    break;
                }
            }
        }

        foreach (LightSource src in affected)
        {
            DepositFootprint(world, src, -src.Amplitude);
            src.Footprint.Clear();
            ComputeFootprint(world, src);
            DepositFootprint(world, src, src.Amplitude);
        }
    }

    // Add (scale > 0) or subtract (scale < 0) the source's cached footprint
    // into the world's block-light arrays, scaled by |scale|.
    private static void DepositFootprint(WorldState world, LightSource source, float scale)
    {
        for (int i = 0; i < source.Footprint.Count; i++)
        {
            var (pos, r, g, b) = source.Footprint[i];
            int sr = (int)(r * Math.Abs(scale) + 0.5f);
            int sg = (int)(g * Math.Abs(scale) + 0.5f);
            int sb = (int)(b * Math.Abs(scale) + 0.5f);
            if (scale >= 0)
            {
                world.AddBlockLightWorld(pos.X, pos.Y, pos.Z, sr, sg, sb);
            }
            else
            {
                world.SubtractBlockLightWorld(pos.X, pos.Y, pos.Z, sr, sg, sb);
            }
        }
    }

    // Computes the source's deposit field via iterative diffusion in a local
    // float buffer, then quantizes to ushort RGB per voxel and stores in
    // source.Footprint. Replaces the old BFS max-fill model.
    //
    // The diffusion gives several wins over BFS:
    //   - Multiple seeds (corner-splat for sub-voxel carriers) blend smoothly
    //     because contributions add through diffusion, not max-fill.
    //   - Energy concentrates in small enclosed rooms and disperses in open
    //     space — the small/large-room brightness behavior comes for free.
    //   - Corners get less light than open voxels (fewer open neighbors to
    //     pull energy in), giving a natural AO hint without a separate pass.
    //   - Output values are perceptual; no shader pow needed for block.
    //
    // Per-source cost: O(iters * (2*reach+1)^3 * 6). At default tuning for a
    // level-56 torch this is ~5M float ops, ~1ms in C#. Static torches pay
    // this once at AddLightSource. Carrier torches pay it on each move.
    private static void ComputeFootprint(WorldState world, LightSource source)
    {
        int level = Math.Min(source.Level, MAX_LIGHT);
        if (level <= 0) { return; }

        int reach = Math.Max(MIN_REACH, level / REACH_DIVISOR);
        int iterations = Math.Max(MIN_ITERS, level / ITER_DIVISOR);
        int dim = reach * 2 + 1;
        int total = dim * dim * dim;
        int dimSq = dim * dim;

        int ox = source.Position.X - reach;
        int oy = source.Position.Y - reach;
        int oz = source.Position.Z - reach;

        // Sample world opacity once into a flat bool array. Avoids repeated
        // VoxelType lookups inside the iteration loop, which otherwise
        // dominate the relaxation cost.
        var open = new bool[total];
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    VoxelType v = world.GetVoxelWorld(ox + lx, oy + ly, oz + lz);
                    open[rowBase + lx] = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
                }
            }
        }

        // For static lights, run a single diffusion with the 8-corner trilinear
        // seeds merged (by linearity, this equals summing 8 separate corner
        // kernels weighted by the trilinear coefficients). Static lights don't
        // move, so the frozen blend is correct forever.
        float seedR = level * SEED_PER_LEVEL * source.Color.R;
        float seedG = level * SEED_PER_LEVEL * source.Color.G;
        float seedB = level * SEED_PER_LEVEL * source.Color.B;

        float sx = Mathf.Clamp(source.SubVoxelOffset.X, 0f, 0.99999f);
        float sy = Mathf.Clamp(source.SubVoxelOffset.Y, 0f, 0.99999f);
        float sz = Mathf.Clamp(source.SubVoxelOffset.Z, 0f, 0.99999f);

        // Accumulate weighted corner contributions into one buffer via
        // separate Diffuse calls (linearity guarantees correctness).
        var rA = new float[total];
        var gA = new float[total];
        var bA = new float[total];
        var tempR = new float[total];
        var tempG = new float[total];
        var tempB = new float[total];
        var scrR = new float[total];
        var scrG = new float[total];
        var scrB = new float[total];

        for (int cx = 0; cx <= 1; cx++)
        {
            float wx = cx == 0 ? (1f - sx) : sx;
            for (int cy = 0; cy <= 1; cy++)
            {
                float wy = cy == 0 ? (1f - sy) : sy;
                for (int cz = 0; cz <= 1; cz++)
                {
                    float wz = cz == 0 ? (1f - sz) : sz;
                    float w = wx * wy * wz;
                    if (w < 0.001f) { continue; }
                    int seedIdx = ((reach + cz) * dim + (reach + cy)) * dim + (reach + cx);
                    if (!open[seedIdx]) { continue; }

                    Diffuse(open, dim, total, iterations,
                        seedIdx, seedR * w, seedG * w, seedB * w,
                        tempR, tempG, tempB, scrR, scrG, scrB);

                    for (int i = 0; i < total; i++)
                    {
                        rA[i] += tempR[i];
                        gA[i] += tempG[i];
                        bA[i] += tempB[i];
                    }
                }
            }
        }

        // Quantize float field → ushort RGB deposits, skipping near-zero
        // voxels. Also compute EffectiveBounds (1 voxel past the non-zero
        // extent) for fast geo-change intersection tests.
        int bMinX = int.MaxValue, bMinY = int.MaxValue, bMinZ = int.MaxValue;
        int bMaxX = int.MinValue, bMaxY = int.MinValue, bMaxZ = int.MinValue;

        source.Footprint.Capacity = total / 4;
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    int idx = rowBase + lx;
                    if (!open[idx]) { continue; }
                    float fr = rA[idx], fg = gA[idx], fb = bA[idx];
                    if (fr < 0.5f && fg < 0.5f && fb < 0.5f) { continue; }
                    ushort qr = ClampU16((int)(fr + 0.5f));
                    ushort qg = ClampU16((int)(fg + 0.5f));
                    ushort qb = ClampU16((int)(fb + 0.5f));
                    if (qr == 0 && qg == 0 && qb == 0) { continue; }

                    int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                    source.Footprint.Add((new Vector3I(wx, wy, wz), qr, qg, qb));

                    if (wx < bMinX) { bMinX = wx; }
                    if (wx > bMaxX) { bMaxX = wx; }
                    if (wy < bMinY) { bMinY = wy; }
                    if (wy > bMaxY) { bMaxY = wy; }
                    if (wz < bMinZ) { bMinZ = wz; }
                    if (wz > bMaxZ) { bMaxZ = wz; }
                }
            }
        }

        // Pad bounds by 1 so a wall placed just outside the lit region still
        // triggers a recompute (it could now block light that was leaking out).
        if (source.Footprint.Count > 0)
        {
            source.BoundsMin = new Vector3I(bMinX - 1, bMinY - 1, bMinZ - 1);
            source.BoundsMax = new Vector3I(bMaxX + 1, bMaxY + 1, bMaxZ + 1);
        }
        else
        {
            source.BoundsMin = source.Position;
            source.BoundsMax = source.Position;
        }
    }

    // Compute 8 independent corner kernels for a carrier light. Each kernel
    // is the diffusion result from seeding at one corner of the source voxel.
    // The caller blends them per-frame by trilinear weights.
    public static CornerKernels ComputeCornerKernels(WorldState world, Vector3I position, int level, Color color)
    {
        level = Math.Min(level, MAX_LIGHT);
        int reach = Math.Max(MIN_REACH, level / REACH_DIVISOR);
        int iterations = Math.Max(MIN_ITERS, level / ITER_DIVISOR);
        int dim = reach * 2 + 1;
        int total = dim * dim * dim;

        int ox = position.X - reach;
        int oy = position.Y - reach;
        int oz = position.Z - reach;

        var open = new bool[total];
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    VoxelType v = world.GetVoxelWorld(ox + lx, oy + ly, oz + lz);
                    open[rowBase + lx] = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
                }
            }
        }

        float baseR = level * SEED_PER_LEVEL * color.R;
        float baseG = level * SEED_PER_LEVEL * color.G;
        float baseB = level * SEED_PER_LEVEL * color.B;

        var kernels = new CornerKernels
        {
            Reach = reach,
            Dim = dim,
            Total = total,
            Origin = new Vector3I(ox, oy, oz),
            Open = open,
            R = new float[8][],
            G = new float[8][],
            B = new float[8][],
            SeedIdx = new int[8],
            SeedOpen = new bool[8],
        };

        var scratchR = new float[total];
        var scratchG = new float[total];
        var scratchB = new float[total];

        for (int cx = 0; cx <= 1; cx++)
        {
            for (int cy = 0; cy <= 1; cy++)
            {
                for (int cz = 0; cz <= 1; cz++)
                {
                    int c = cx | (cy << 1) | (cz << 2);
                    int seedIdx = ((reach + cz) * dim + (reach + cy)) * dim + (reach + cx);
                    kernels.SeedIdx[c] = seedIdx;
                    kernels.SeedOpen[c] = open[seedIdx];

                    kernels.R[c] = new float[total];
                    kernels.G[c] = new float[total];
                    kernels.B[c] = new float[total];

                    if (!open[seedIdx]) { continue; }

                    Diffuse(open, dim, total, iterations,
                        seedIdx, baseR, baseG, baseB,
                        kernels.R[c], kernels.G[c], kernels.B[c],
                        scratchR, scratchG, scratchB);
                }
            }
        }

        // Build sparse index list: any voxel that's non-zero in any corner.
        var nonZero = new List<int>(total / 4);
        const float THRESHOLD = 0.5f;
        for (int idx = 0; idx < total; idx++)
        {
            if (!open[idx]) { continue; }
            bool any = false;
            for (int c = 0; c < 8; c++)
            {
                if (kernels.R[c][idx] > THRESHOLD || kernels.G[c][idx] > THRESHOLD || kernels.B[c][idx] > THRESHOLD)
                {
                    any = true;
                    break;
                }
            }
            if (any)
            {
                nonZero.Add(idx);
            }
        }
        kernels.NonZeroIndices = nonZero.ToArray();
        kernels.NonZeroCount = nonZero.Count;

        return kernels;
    }

    // Single-seed iterative diffusion with absorption + re-injection.
    // Shared by both ComputeFootprint (static lights) and ComputeCornerKernels
    // (carrier lights). Writes results into outR/G/B; scrR/G/B are scratch.
    private static void Diffuse(
        bool[] open, int dim, int total, int iterations,
        int seedIdx, float seedR, float seedG, float seedB,
        float[] outR, float[] outG, float[] outB,
        float[] scrR, float[] scrG, float[] scrB)
    {
        int dimSq = dim * dim;
        Array.Clear(outR, 0, total);
        Array.Clear(outG, 0, total);
        Array.Clear(outB, 0, total);

        outR[seedIdx] = seedR;
        outG[seedIdx] = seedG;
        outB[seedIdx] = seedB;

        for (int iter = 0; iter < iterations; iter++)
        {
            for (int lz = 0; lz < dim; lz++)
            {
                for (int ly = 0; ly < dim; ly++)
                {
                    int rowBase = (lz * dim + ly) * dim;
                    for (int lx = 0; lx < dim; lx++)
                    {
                        int idx = rowBase + lx;
                        if (!open[idx])
                        {
                            scrR[idx] = 0f; scrG[idx] = 0f; scrB[idx] = 0f;
                            continue;
                        }

                        float selfR = outR[idx] * (1f - ABSORPTION_RATE);
                        float selfG = outG[idx] * (1f - ABSORPTION_RATE);
                        float selfB = outB[idx] * (1f - ABSORPTION_RATE);

                        float sumR = 0f, sumG = 0f, sumB = 0f;
                        int openCount = 0;

                        if (lx > 0 && open[idx - 1])
                        { sumR += outR[idx - 1]; sumG += outG[idx - 1]; sumB += outB[idx - 1]; openCount++; }
                        if (lx < dim - 1 && open[idx + 1])
                        { sumR += outR[idx + 1]; sumG += outG[idx + 1]; sumB += outB[idx + 1]; openCount++; }
                        if (ly > 0 && open[idx - dim])
                        { sumR += outR[idx - dim]; sumG += outG[idx - dim]; sumB += outB[idx - dim]; openCount++; }
                        if (ly < dim - 1 && open[idx + dim])
                        { sumR += outR[idx + dim]; sumG += outG[idx + dim]; sumB += outB[idx + dim]; openCount++; }
                        if (lz > 0 && open[idx - dimSq])
                        { sumR += outR[idx - dimSq]; sumG += outG[idx - dimSq]; sumB += outB[idx - dimSq]; openCount++; }
                        if (lz < dim - 1 && open[idx + dimSq])
                        { sumR += outR[idx + dimSq]; sumG += outG[idx + dimSq]; sumB += outB[idx + dimSq]; openCount++; }

                        float retain = 1f - DIFFUSION_RATE * openCount;
                        if (retain < 0f) { retain = 0f; }

                        scrR[idx] = sumR * DIFFUSION_RATE + selfR * retain;
                        scrG[idx] = sumG * DIFFUSION_RATE + selfG * retain;
                        scrB[idx] = sumB * DIFFUSION_RATE + selfB * retain;
                    }
                }
            }

            (outR, scrR) = (scrR, outR);
            (outG, scrG) = (scrG, outG);
            (outB, scrB) = (scrB, outB);

            outR[seedIdx] = seedR;
            outG[seedIdx] = seedG;
            outB[seedIdx] = seedB;
        }
    }

    private static ushort ClampU16(int v)
    {
        if (v < 0) { return 0; }
        if (v > ushort.MaxValue) { return ushort.MaxValue; }
        return (ushort)v;
    }

    // --- Sunlight propagation (max-fill BFS) -------------------------------

    private static void SpreadSunlight(WorldState world, Queue<(int x, int y, int z)> queue)
    {
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            int currentLevel = world.GetSunlightWorld(x, y, z);
            if (currentLevel <= FALLOFF_PER_VOXEL) { continue; }

            foreach (Vector3I offset in Neighbors)
            {
                int nx = x + offset.X;
                int ny = y + offset.Y;
                int nz = z + offset.Z;

                if (!world.IsInBounds(nx, ny, nz)) { continue; }
                VoxelType v = world.GetVoxelWorld(nx, ny, nz);
                if (v != VoxelType.Air && !VoxelTypeInfo.IsTransparent(v)) { continue; }

                int newLevel = currentLevel - FALLOFF_PER_VOXEL - VoxelTypeInfo.LightAttenuation(v);
                if (newLevel <= 0) { continue; }

                if (newLevel > world.GetSunlightWorld(nx, ny, nz))
                {
                    world.SetSunlightWorld(nx, ny, nz, newLevel);
                    queue.Enqueue((nx, ny, nz));
                }
            }
        }
    }

    private static void UpdateSunlightAt(WorldState world, List<Vector3I> changedPositions)
    {
        var removeQueue = new Queue<(int x, int y, int z, int level)>();
        var refillQueue = new Queue<(int x, int y, int z)>();

        foreach (Vector3I pos in changedPositions)
        {
            VoxelType v = world.GetVoxelWorld(pos.X, pos.Y, pos.Z);
            bool isAir = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
            int level = world.GetSunlightWorld(pos.X, pos.Y, pos.Z);

            if (isAir && level > 0)
            {
                removeQueue.Enqueue((pos.X, pos.Y, pos.Z, level));
                world.SetSunlightWorld(pos.X, pos.Y, pos.Z, 0);
            }
            else if (isAir)
            {
                foreach (Vector3I offset in Neighbors)
                {
                    int nx = pos.X + offset.X;
                    int ny = pos.Y + offset.Y;
                    int nz = pos.Z + offset.Z;
                    if (!world.IsInBounds(nx, ny, nz)) { continue; }
                    if (world.GetSunlightWorld(nx, ny, nz) > 0)
                    {
                        refillQueue.Enqueue((nx, ny, nz));
                    }
                }
            }
            else if (level > 0)
            {
                removeQueue.Enqueue((pos.X, pos.Y, pos.Z, level));
                world.SetSunlightWorld(pos.X, pos.Y, pos.Z, 0);
            }
        }

        while (removeQueue.Count > 0)
        {
            var (x, y, z, level) = removeQueue.Dequeue();
            foreach (Vector3I offset in Neighbors)
            {
                int nx = x + offset.X;
                int ny = y + offset.Y;
                int nz = z + offset.Z;
                if (!world.IsInBounds(nx, ny, nz)) { continue; }

                int neighborLevel = world.GetSunlightWorld(nx, ny, nz);
                if (neighborLevel > 0 && neighborLevel < level)
                {
                    removeQueue.Enqueue((nx, ny, nz, neighborLevel));
                    world.SetSunlightWorld(nx, ny, nz, 0);
                }
                else if (neighborLevel >= level)
                {
                    refillQueue.Enqueue((nx, ny, nz));
                }
            }
        }

        SpreadSunlight(world, refillQueue);
    }
}
