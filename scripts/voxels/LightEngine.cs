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
    //
    // The shaping/cost knobs (diffusion rate, absorption, seed-per-level,
    // reach divisor, iteration divisor) now live on SimData as [Export]s so
    // they're inspector-tunable for a feel pass — see the "Block Light
    // Diffusion" group there. Each is read ONCE per kernel build and hoisted
    // into a local before the hot inner loop, so this is as cheap as the old
    // consts (the const-vs-field cost only matters for values read inside the
    // per-voxel loop, which these never are). The two floors below stay as
    // consts — they're structural safety minimums, not feel tuning.

    // Floor on the per-light reach (buffer half-extent) regardless of how high
    // SimData.BlockLightReachDivisor is set.
    private const int MIN_REACH = 2;

    // Floor on the diffusion iteration count regardless of
    // SimData.BlockLightIterationDivisor.
    private const int MIN_ITERS = 4;

    // --- Fog-scaled attenuation ------------------------------------------
    //
    // Fog density (byte, per voxel, see ChunkState.FogDensity) adds extra
    // attenuation to light propagation so torches and sunbeams dim faster
    // in foggy air. Linear in density — zero fog = unchanged behavior.
    //
    // Sun BFS is integer-stepped with FALLOFF_PER_VOXEL=4. FOG_SUN_FALLOFF_255
    // is the extra falloff subtracted per voxel at maximum density (255);
    // intermediate densities scale linearly via integer math.
    //
    // Block-light diffusion uses per-iteration SimData.BlockLightAbsorptionRate.
    // FOG_BLOCK_ABSORPTION_255 is the *additional* absorption applied at max
    // density, so foggy cells retain less of their energy each iteration. Kept
    // small relative to that absorption rate so total retention stays well-behaved.
    public const int FOG_SUN_FALLOFF_255 = 4;
    private const float FOG_BLOCK_ABSORPTION_255 = 0.04f;

    // The canopy attenuation strength constants live on SimData
    // (CanopySunFalloffPeak, CanopyBlockAbsorptionPeak) — read off
    // world.SimData inside the methods that use them, with the per-call
    // precompute hoisted out of the inner loop.

    private static readonly Vector3I[] Neighbors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    public static void ComputeSunlight(WorldState world)
    {
        // Reset first — the column scan breaks when sunLevel reaches zero,
        // so voxels below the break aren't overwritten. Without a reset,
        // a re-propagation that adds new attenuation (e.g. canopy stamped
        // after the first pass) would leave stale-bright sunlight at the
        // bottom of darkened columns.
        world.ClearSunlightAll();
        // SkyExposure is captured from this same column scan (below) and never
        // touched by the BFS spread, so it needs the same baseline reset.
        world.ClearSkyExposureAll();

        int minWx = world.Min.X * ChunkState.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkState.SIZE;
        int minWy = world.Min.Y * ChunkState.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;

        var queue = new Queue<(int x, int y, int z)>();
        int canopySunFalloffPeak = world.SimData.CanopySunFalloffPeak;

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
                    sunLevel -= (world.GetFogWorld(wx, wy, wz) * FOG_SUN_FALLOFF_255) / 255;
                    sunLevel -= (world.GetCanopyAttenuationWorld(wx, wy, wz) * canopySunFalloffPeak) / 255;
                    if (sunLevel <= 0)
                    {
                        break;
                    }
                    world.SetSunlightWorld(wx, wy, wz, sunLevel);
                    // Capture the vertical column value BEFORE the BFS spread
                    // can raise it — this is the non-leaky sky-exposure field.
                    world.SetSkyExposureWorld(wx, wy, wz, sunLevel);
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
        RecomputeSkyExposureColumns(world, changedPositions);

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

        SimData sim = world.SimData;
        float diffusionRate = sim.BlockLightDiffusionRate;
        float absorptionRate = sim.BlockLightAbsorptionRate;
        float seedPerLevel = sim.BlockLightSeedPerLevel;

        int reach = Math.Max(MIN_REACH, level / sim.BlockLightReachDivisor);
        int iterations = Math.Max(MIN_ITERS, level / sim.BlockLightIterationDivisor);
        int dim = reach * 2 + 1;
        int total = dim * dim * dim;
        int dimSq = dim * dim;

        int ox = source.Position.X - reach;
        int oy = source.Position.Y - reach;
        int oz = source.Position.Z - reach;

        // Sample world opacity + fog absorption once into flat arrays. Avoids
        // repeated VoxelType / fog lookups inside the iteration loop, which
        // otherwise dominate the relaxation cost. fogAbsorb is the extra
        // per-iteration absorption from fog density AND foliage-canopy density
        // at each voxel — both attenuate block light symmetrically.
        var open = new bool[total];
        var fogAbsorb = new float[total];
        float canopyBlockAbsorbFactor = world.SimData.CanopyBlockAbsorptionPeak / 255f;
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                    VoxelType v = world.GetVoxelWorld(wx, wy, wz);
                    open[rowBase + lx] = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
                    fogAbsorb[rowBase + lx] =
                        world.GetFogWorld(wx, wy, wz) * (FOG_BLOCK_ABSORPTION_255 / 255f)
                        + world.GetCanopyAttenuationWorld(wx, wy, wz) * canopyBlockAbsorbFactor;
                }
            }
        }

        // For static lights, run a single diffusion with the 8-corner trilinear
        // seeds merged (by linearity, this equals summing 8 separate corner
        // kernels weighted by the trilinear coefficients). Static lights don't
        // move, so the frozen blend is correct forever.
        float seedR = level * seedPerLevel * source.Color.R;
        float seedG = level * seedPerLevel * source.Color.G;
        float seedB = level * seedPerLevel * source.Color.B;

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

                    Diffuse(open, fogAbsorb, dim, total, iterations,
                        absorptionRate, diffusionRate,
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

        // Pad bounds by 1 so a wall placed just outside the lit zone still
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
        SimData sim = world.SimData;
        float diffusionRate = sim.BlockLightDiffusionRate;
        float absorptionRate = sim.BlockLightAbsorptionRate;
        float seedPerLevel = sim.BlockLightSeedPerLevel;

        int reach = Math.Max(MIN_REACH, level / sim.BlockLightReachDivisor);
        int iterations = Math.Max(MIN_ITERS, level / sim.BlockLightIterationDivisor);
        int dim = reach * 2 + 1;
        int total = dim * dim * dim;

        int ox = position.X - reach;
        int oy = position.Y - reach;
        int oz = position.Z - reach;

        var open = new bool[total];
        var fogAbsorb = new float[total];
        float canopyBlockAbsorbFactor = sim.CanopyBlockAbsorptionPeak / 255f;
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                    VoxelType v = world.GetVoxelWorld(wx, wy, wz);
                    open[rowBase + lx] = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
                    fogAbsorb[rowBase + lx] =
                        world.GetFogWorld(wx, wy, wz) * (FOG_BLOCK_ABSORPTION_255 / 255f)
                        + world.GetCanopyAttenuationWorld(wx, wy, wz) * canopyBlockAbsorbFactor;
                }
            }
        }

        float baseR = level * seedPerLevel * color.R;
        float baseG = level * seedPerLevel * color.G;
        float baseB = level * seedPerLevel * color.B;

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

                    Diffuse(open, fogAbsorb, dim, total, iterations,
                        absorptionRate, diffusionRate,
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

    // Adjacent carrier voxels share 4 of 8 corners (the corners on the face
    // between them). For an axis-aligned ±1 voxel crossing, this skips 4 of
    // the 8 diffusions by translating the shared kernels from the previous
    // CornerKernels buffer into the new buffer, then running fresh diffusions
    // for the 4 new-face corners. Falls back to a full recompute for any
    // non-axis-aligned or multi-voxel delta.
    //
    // Edge truncation: the translated shared kernels lose a 1-voxel slab on
    // the far side of the new buffer (those voxels had no source in the old
    // buffer). The 4 fresh corners on the new face fill that slab with their
    // own contributions; the diffusion field is small at that distance from
    // any shared seed anyway, so the artifact is below the quantize threshold.
    public static CornerKernels ComputeCornerKernelsIncremental(
        WorldState world,
        Vector3I position,
        int level,
        Color color,
        CornerKernels prevKernels,
        Vector3I delta)
    {
        int absX = Math.Abs(delta.X);
        int absY = Math.Abs(delta.Y);
        int absZ = Math.Abs(delta.Z);
        if (prevKernels == null || absX + absY + absZ != 1)
        {
            return ComputeCornerKernels(world, position, level, color);
        }

        level = Math.Min(level, MAX_LIGHT);
        SimData sim = world.SimData;
        float diffusionRate = sim.BlockLightDiffusionRate;
        float absorptionRate = sim.BlockLightAbsorptionRate;
        float seedPerLevel = sim.BlockLightSeedPerLevel;

        int reach = Math.Max(MIN_REACH, level / sim.BlockLightReachDivisor);
        int iterations = Math.Max(MIN_ITERS, level / sim.BlockLightIterationDivisor);
        int dim = reach * 2 + 1;
        int total = dim * dim * dim;

        // Sanity: reach/dim must match the previous buffer for index translation
        // to be valid. Normally holds (Emission and the SimData reach divisor are
        // fixed at runtime); if a designer retunes BlockLightReachDivisor live,
        // dim changes for one crossing and we self-heal via a full recompute.
        if (prevKernels.Dim != dim)
        {
            return ComputeCornerKernels(world, position, level, color);
        }

        int ox = position.X - reach;
        int oy = position.Y - reach;
        int oz = position.Z - reach;

        var open = new bool[total];
        var fogAbsorb = new float[total];
        float canopyBlockAbsorbFactor = sim.CanopyBlockAbsorptionPeak / 255f;
        for (int lz = 0; lz < dim; lz++)
        {
            for (int ly = 0; ly < dim; ly++)
            {
                int rowBase = (lz * dim + ly) * dim;
                for (int lx = 0; lx < dim; lx++)
                {
                    int wx = ox + lx, wy = oy + ly, wz = oz + lz;
                    VoxelType v = world.GetVoxelWorld(wx, wy, wz);
                    open[rowBase + lx] = v == VoxelType.Air || VoxelTypeInfo.IsTransparent(v);
                    fogAbsorb[rowBase + lx] =
                        world.GetFogWorld(wx, wy, wz) * (FOG_BLOCK_ABSORPTION_255 / 255f)
                        + world.GetCanopyAttenuationWorld(wx, wy, wz) * canopyBlockAbsorbFactor;
                }
            }
        }

        float baseR = level * seedPerLevel * color.R;
        float baseG = level * seedPerLevel * color.G;
        float baseB = level * seedPerLevel * color.B;

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

        // Translation: world voxel at NEW buffer index (lx,ly,lz) corresponds
        // to OLD buffer index (lx+delta.X, ly+delta.Y, lz+delta.Z). Valid
        // when the OLD index is in [0, dim-1] on each axis.
        int lxMin = Math.Max(0, -delta.X);
        int lxMax = Math.Min(dim - 1, dim - 1 - delta.X);
        int lyMin = Math.Max(0, -delta.Y);
        int lyMax = Math.Min(dim - 1, dim - 1 - delta.Y);
        int lzMin = Math.Max(0, -delta.Z);
        int lzMax = Math.Min(dim - 1, dim - 1 - delta.Z);

        for (int c = 0; c < 8; c++)
        {
            int cx = c & 1;
            int cy = (c >> 1) & 1;
            int cz = (c >> 2) & 1;
            int seedIdx = ((reach + cz) * dim + (reach + cy)) * dim + (reach + cx);
            kernels.SeedIdx[c] = seedIdx;
            kernels.SeedOpen[c] = open[seedIdx];

            kernels.R[c] = new float[total];
            kernels.G[c] = new float[total];
            kernels.B[c] = new float[total];

            // New corner c (offset cx,cy,cz from V_new) coincides with old
            // corner c_old (offset cxOld,cyOld,czOld from V_old) iff their
            // world positions match: V_new + (cx,cy,cz) = V_old + (cxOld,...)
            // => cxOld = cx + delta.X, etc. Valid only when ∈ {0,1}.
            int cxOld = cx + delta.X;
            int cyOld = cy + delta.Y;
            int czOld = cz + delta.Z;
            bool shared = (cxOld >= 0 && cxOld <= 1)
                       && (cyOld >= 0 && cyOld <= 1)
                       && (czOld >= 0 && czOld <= 1);

            if (shared)
            {
                int cOld = cxOld | (cyOld << 1) | (czOld << 2);
                float[] oldR = prevKernels.R[cOld];
                float[] oldG = prevKernels.G[cOld];
                float[] oldB = prevKernels.B[cOld];
                float[] newR = kernels.R[c];
                float[] newG = kernels.G[c];
                float[] newB = kernels.B[c];

                for (int lz = lzMin; lz <= lzMax; lz++)
                {
                    int lzOld = lz + delta.Z;
                    for (int ly = lyMin; ly <= lyMax; ly++)
                    {
                        int lyOld = ly + delta.Y;
                        int rowNew = (lz * dim + ly) * dim;
                        int rowOld = (lzOld * dim + lyOld) * dim;
                        for (int lx = lxMin; lx <= lxMax; lx++)
                        {
                            int lxOld = lx + delta.X;
                            newR[rowNew + lx] = oldR[rowOld + lxOld];
                            newG[rowNew + lx] = oldG[rowOld + lxOld];
                            newB[rowNew + lx] = oldB[rowOld + lxOld];
                        }
                    }
                }
            }
            else
            {
                if (!open[seedIdx]) { continue; }
                Diffuse(open, fogAbsorb, dim, total, iterations,
                    absorptionRate, diffusionRate,
                    seedIdx, baseR, baseG, baseB,
                    kernels.R[c], kernels.G[c], kernels.B[c],
                    scratchR, scratchG, scratchB);
            }
        }

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
    // fogAbsorb[idx] is extra per-iteration absorption at each voxel due to
    // local fog density — 0 in clear air, up to FOG_BLOCK_ABSORPTION_255 at
    // max fog. Clamped so total absorption never exceeds 1 (energy negative).
    private static void Diffuse(
        bool[] open, float[] fogAbsorb, int dim, int total, int iterations,
        float absorptionRate, float diffusionRate,
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

                        float keep = 1f - absorptionRate - fogAbsorb[idx];
                        if (keep < 0f) { keep = 0f; }
                        float selfR = outR[idx] * keep;
                        float selfG = outG[idx] * keep;
                        float selfB = outB[idx] * keep;

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

                        float retain = 1f - diffusionRate * openCount;
                        if (retain < 0f) { retain = 0f; }

                        scrR[idx] = sumR * diffusionRate + selfR * retain;
                        scrG[idx] = sumG * diffusionRate + selfG * retain;
                        scrB[idx] = sumB * diffusionRate + selfB * retain;
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
        int canopySunFalloffPeak = world.SimData.CanopySunFalloffPeak;
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

                int fogFalloff = (world.GetFogWorld(nx, ny, nz) * FOG_SUN_FALLOFF_255) / 255;
                int canopyFalloff = (world.GetCanopyAttenuationWorld(nx, ny, nz) * canopySunFalloffPeak) / 255;
                int newLevel = currentLevel - FALLOFF_PER_VOXEL - VoxelTypeInfo.LightAttenuation(v) - fogFalloff - canopyFalloff;
                if (newLevel <= 0) { continue; }

                if (newLevel > world.GetSunlightWorld(nx, ny, nz))
                {
                    world.SetSunlightWorld(nx, ny, nz, newLevel);
                    queue.Enqueue((nx, ny, nz));
                }
            }
        }
    }

    // Recompute the vertical SkyExposure column for every distinct XZ touched
    // by the changed voxels. SkyExposure is a pure top-down property — a voxel
    // change only affects its own (x, z) column — so rescanning each affected
    // column from the world top is both correct and cheap (one column per
    // distinct XZ, not a flood fill). This mirrors the column scan in
    // ComputeSunlight and MUST stay in sync with it (same attenuation terms).
    private static void RecomputeSkyExposureColumns(WorldState world, List<Vector3I> changedPositions)
    {
        int canopySunFalloffPeak = world.SimData.CanopySunFalloffPeak;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;
        int minWy = world.Min.Y * ChunkState.SIZE;

        var columns = new HashSet<(int x, int z)>();
        foreach (Vector3I pos in changedPositions)
        {
            columns.Add((pos.X, pos.Z));
        }

        foreach (var (wx, wz) in columns)
        {
            int sunLevel = MAX_LIGHT;
            bool blocked = false;
            for (int wy = topWy; wy >= minWy; wy--)
            {
                if (blocked)
                {
                    world.SetSkyExposureWorld(wx, wy, wz, 0);
                    continue;
                }
                VoxelType v = world.GetVoxelWorld(wx, wy, wz);
                if (v != VoxelType.Air && !VoxelTypeInfo.IsTransparent(v))
                {
                    // Opaque ceiling: this voxel and everything below it are
                    // sheltered from the sky.
                    blocked = true;
                    world.SetSkyExposureWorld(wx, wy, wz, 0);
                    continue;
                }
                sunLevel -= VoxelTypeInfo.LightAttenuation(v);
                sunLevel -= (world.GetFogWorld(wx, wy, wz) * FOG_SUN_FALLOFF_255) / 255;
                sunLevel -= (world.GetCanopyAttenuationWorld(wx, wy, wz) * canopySunFalloffPeak) / 255;
                if (sunLevel <= 0)
                {
                    blocked = true;
                    world.SetSkyExposureWorld(wx, wy, wz, 0);
                    continue;
                }
                world.SetSkyExposureWorld(wx, wy, wz, sunLevel);
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
