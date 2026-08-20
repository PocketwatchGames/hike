using System;
using System.Collections.Generic;
using Godot;

public static class LightEngine
{
    // Sun channel: a max-fill flood with a per-step falloff (block light uses
    // the geodesic flood model instead — see the Block-light flood section).
    public const int MAX_LIGHT = 60;

    // The sun-channel fog and canopy extinctions live on SimData
    // (FogSunExtinction / CanopySunExtinction), read off world.SimData inside
    // ComputeSunlight. (Block light's are BlockLightFogExtinction /
    // BlockLightCanopyExtinction, applied in the flood.)

    private static readonly Vector3I[] Neighbors =
    {
        new(1, 0, 0), new(-1, 0, 0),
        new(0, 1, 0), new(0, -1, 0),
        new(0, 0, 1), new(0, 0, -1),
    };

    // Per-thread scratch reused across ComputeFloodField calls so the carried
    // torch's per-crossing flood doesn't allocate a bool[] + Queue every time.
    // [ThreadStatic] keeps it correct if flood ever moves off the main thread.
    [ThreadStatic] private static bool[] _floodVisited;
    [ThreadStatic] private static Queue<(int lx, int ly, int lz, float depth, int hops)> _floodQueue;

    // Multiplicative (Beer-Lambert) sun transmittance through one voxel of an
    // attenuating medium (canopy or fog/dust): exp(-density * extinction).
    // densityByte is 0..255 per source (callers may pass a SUM of sources, e.g.
    // canopy + its shadow, which simply reads as more optical depth); extinction
    // is the optical depth a fully-dense voxel adds. Unlike a flat subtraction
    // this compounds with depth and asymptotes
    // toward — never snaps to — zero, so a lone tree's shadow (or a dusty room)
    // stays dim-but-readable while only deep, dense medium drives it very dark.
    // Mirrors the block-light flood's medium terms.
    private static float MediumTransmittance(int densityByte, float extinction)
    {
        if (densityByte <= 0)
        {
            return 1f;
        }
        return Mathf.Exp(-(densityByte / 255f) * extinction);
    }

    // Full relight: cover, then light. What every caller outside worldgen wants
    // after changing geometry or occluders.
    //
    // Worldgen does NOT use this — it has to interleave classification and the
    // fog bake between the two halves (cover → space class → fog → light), which
    // is the whole reason they are separable. Everywhere else there is nothing
    // to put in the middle, and calling only one of the pair is a silent bug:
    // stale SkyExposure leaves rain falling through a new roof.
    // `progress` (0..1) is optional and exists for offline bakes, which are long
    // enough to need a progress bar; the runtime callers pass nothing.
    public static void Relight(WorldState world, System.Action<float> progress = null)
    {
        ComputeSkyExposure(world, progress == null ? null : p => progress(p * SKY_SHARE));
        ComputeSunlight(world, progress == null ? null : p => progress(SKY_SHARE + p * (1f - SKY_SHARE)));
    }

    // Measured on an 18x16 chunk bake: sky exposure is a small fraction of a
    // relight, the sunlight pass is the rest.
    private const float SKY_SHARE = 0.15f;

    // Geometry-only vertical cover, written into SkyExposure: solid voxels,
    // non-voxel cover (roofs) and canopy, with NO fog term.
    //
    // Fog is deliberately excluded even though ComputeSunlight's column
    // attenuates by it. SkyExposure answers "is there cover straight up", and
    // every consumer asks it as such — rain reaching the ground, terrain
    // wetness, torch dousing, the no-ceiling requirement. Haze is air, not
    // cover; counting it would make a foggy morning read as shelter from rain.
    //
    // Being fog-free is also what lets space-class classification read this:
    // interior fog is baked FROM the class, so a class derived from a
    // fog-attenuated signal would feed itself.
    //
    // Cheap relative to ComputeSunlight — one column per XZ, no BFS flood.
    public static void ComputeSkyExposure(WorldState world, System.Action<float> progress = null)
    {
        world.ClearSkyExposureAll();

        float canopySunExtinction = world.SimData.canopySunExtinction;
        int minWx = world.Min.X * ChunkState.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkState.SIZE;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;

        for (int wx = minWx; wx < maxWx; wx++)
        {
            for (int wz = minWz; wz < maxWz; wz++)
            {
                ScanSkyExposureColumn(world, wx, wz, canopySunExtinction);
            }
            if (progress != null && (wx & 15) == 0)
            {
                progress((wx - minWx) / (float)Mathf.Max(1, maxWx - minWx));
            }
        }
    }

    // One XZ column of SkyExposure, top-down. The single definition of the
    // field — both the full-world bake and the incremental per-edit recompute
    // call this, so there is no pair of scans to keep in sync by hand.
    private static void ScanSkyExposureColumn(WorldState world, int wx, int wz, float canopySunExtinction)
    {
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;
        int minWy = world.Min.Y * ChunkState.SIZE;

        float level = MAX_LIGHT;
        bool blocked = false;
        for (int wy = topWy; wy >= minWy; wy--)
        {
            if (blocked)
            {
                world.SetSkyExposureWorld(wx, wy, wz, 0);
                continue;
            }
            int v = world.GetBlockWorld(wx, wy, wz);
            // Opaque ceiling — a solid voxel, or non-voxel solid cover such as
            // a roof: this voxel and everything below it are sheltered.
            if ((v != Blocks.AirId && !Blocks.IsTransparent(v)) || world.GetSunOpaqueWorld(wx, wy, wz))
            {
                blocked = true;
                world.SetSkyExposureWorld(wx, wy, wz, 0);
                continue;
            }
            level -= Blocks.LightAttenuation(v);
            level *= MediumTransmittance(world.GetCanopyAttenuationWorld(wx, wy, wz), canopySunExtinction);
            int rounded = (int)(level + 0.5f);
            if (rounded <= 0)
            {
                blocked = true;
                world.SetSkyExposureWorld(wx, wy, wz, 0);
                continue;
            }
            world.SetSkyExposureWorld(wx, wy, wz, rounded);
        }
    }

    // The lighting signal: same column walk, plus fog attenuation, plus the
    // lateral BFS spread. Does NOT write SkyExposure — that field is owned by
    // ComputeSkyExposure above and is a different question (cover, not light).
    public static void ComputeSunlight(WorldState world, System.Action<float> progress = null)
    {
        // Reset first — the column scan breaks when sunLevel reaches zero,
        // so voxels below the break aren't overwritten. Without a reset,
        // a re-propagation that adds new attenuation (e.g. canopy stamped
        // after the first pass) would leave stale-bright sunlight at the
        // bottom of darkened columns.
        world.ClearSunlightAll();

        int minWx = world.Min.X * ChunkState.SIZE;
        int maxWx = (world.Max.X + 1) * ChunkState.SIZE;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;

        var queue = new Queue<(int x, int y, int z)>();
        float canopySunExtinction = world.SimData.canopySunExtinction;
        float fogSunExtinction = world.SimData.fogSunExtinction;

        for (int wx = minWx; wx < maxWx; wx++)
        {
            for (int wz = minWz; wz < maxWz; wz++)
            {
                ScanSunlightColumn(world, wx, wz, canopySunExtinction, fogSunExtinction, queue, int.MaxValue);
            }
            if (progress != null && (wx & 15) == 0)
            {
                progress(SCAN_SHARE * (wx - minWx) / Mathf.Max(1, maxWx - minWx));
            }
        }

        progress?.Invoke(SCAN_SHARE);
        SpreadSunlight(world, queue, progress == null ? null : p => progress(SCAN_SHARE + p * (1f - SCAN_SHARE)));
        progress?.Invoke(1f);
    }

    // Direct sun down one XZ column, top-down to the first thing that stops it.
    // The single definition of the column phase — the full bake and the regional
    // relight both call it, so there is no pair of scans to keep in sync.
    // Every cell it lights is enqueued as a source for the lateral spread.
    //
    // The scan always starts at the world top (attenuation accumulates from
    // there) but writes only at or below `maxWriteY`. A regional relight uses
    // that to leave the untouched sky above its region alone rather than
    // rewriting it with identical values and dirtying every chunk it passes.
    private static void ScanSunlightColumn(WorldState world, int wx, int wz, float canopySunExtinction, float fogSunExtinction, Queue<(int x, int y, int z)> queue, int maxWriteY)
    {
        int minWy = world.Min.Y * ChunkState.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;

        float sunLevel = MAX_LIGHT;
        for (int wy = topWy; wy >= minWy; wy--)
        {
            int v = world.GetBlockWorld(wx, wy, wz);
            if (v != Blocks.AirId && !Blocks.IsTransparent(v))
            {
                break;
            }
            // Non-voxel solid cover (a roof) stops the column exactly as
            // a solid voxel does — canopy attenuation can only ever dim.
            if (world.GetSunOpaqueWorld(wx, wy, wz))
            {
                break;
            }
            sunLevel -= Blocks.LightAttenuation(v);
            sunLevel *= MediumTransmittance(world.GetFogWorld(wx, wy, wz), fogSunExtinction);
            // Canopy only, never CanopyShade. The scan carries sunLevel down, so
            // it has already paid for the leaves by the time it is under them;
            // the air below holds no foliage to charge for again. Reading the
            // shadow here would re-toll the same canopy once per voxel of trunk
            // and make a tree's darkness a function of its HEIGHT.
            sunLevel *= MediumTransmittance(world.GetCanopyAttenuationWorld(wx, wy, wz), canopySunExtinction);
            int level = (int)(sunLevel + 0.5f);
            if (level <= 0)
            {
                break;
            }
            if (wy > maxWriteY)
            {
                continue;
            }
            world.SetSunlightWorld(wx, wy, wz, level);
            queue.Enqueue((wx, wy, wz));
        }
    }

    // Regional twin of Relight, for a change to NON-VOXEL cover: a roof placed,
    // deleted, moved or re-styled changes what the sun reaches without changing
    // a single voxel, so OnVoxelsChanged's incremental path sees nothing to do
    // — and a full Relight pays for the whole world to fix one building.
    //
    // Retract before rescan. The column scan alone can only ever write the
    // cover's own columns, so a room a new roof just covered would keep the
    // lateral spill its formerly-lit columns had already pushed sideways into
    // it, and read as bright as before.
    //
    // Only the region's own columns are torn down and rebuilt, from its top
    // downward: nothing above the changed cover moved, so rewriting the open sky
    // over it would only dirty every chunk in the stack for identical values.
    //
    // Leaves SunlightChunkDirty naming exactly the chunks whose sunlight moved,
    // which is what the caller has to re-mesh (terrain sun is a per-vertex bake)
    // and upload.
    // Share of the sunlight pass spent scanning columns before the flood.
    // Measured on an 18x16 chunk bake: the scan is ~4s of a ~19s pass, the flood
    // the remaining ~15s, so the flood is where a bake actually spends its time.
    private const float SCAN_SHARE = 0.2f;

    // Report roughly every 64k dequeues; often enough to animate, rare enough to
    // stay off the flood's hot path.
    private const long PROGRESS_POP_MASK = 0xFFFF;

    public static void RelightRegion(WorldState world, VoxelBox region)
    {
        if (world == null || region.IsEmpty)
        {
            return;
        }
        world.SunlightChunkDirty.Clear();
        float canopySunExtinction = world.SimData.canopySunExtinction;
        float fogSunExtinction = world.SimData.fogSunExtinction;
        int minWy = world.Min.Y * ChunkState.SIZE;

        // Zero the columns outright and hand their OLD levels to the removal
        // flood, so everything they lit elsewhere is pulled back too. Same
        // machinery a voxel edit uses; only the trigger differs.
        var removeQueue = new Queue<(int x, int y, int z, int level)>();
        var refillQueue = new Queue<(int x, int y, int z)>();
        for (int wx = region.Min.X; wx <= region.Max.X; wx++)
        {
            for (int wz = region.Min.Z; wz <= region.Max.Z; wz++)
            {
                for (int wy = region.Max.Y; wy >= minWy; wy--)
                {
                    int level = world.GetSunlightWorld(wx, wy, wz);
                    if (level <= 0)
                    {
                        continue;
                    }
                    world.SetSunlightWorld(wx, wy, wz, 0);
                    removeQueue.Enqueue((wx, wy, wz, level));
                }
            }
        }
        RetractSunlight(world, removeQueue, refillQueue);

        for (int wx = region.Min.X; wx <= region.Max.X; wx++)
        {
            for (int wz = region.Min.Z; wz <= region.Max.Z; wz++)
            {
                ScanSkyExposureColumn(world, wx, wz, canopySunExtinction);
                ScanSunlightColumn(world, wx, wz, canopySunExtinction, fogSunExtinction, refillQueue, region.Max.Y);
            }
        }
        SpreadSunlight(world, refillQueue);
    }

    public static void AddLightSource(WorldState world, LightSource source)
    {
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

    // Change the source's brightness without recomputing its footprint. This is
    // the flicker path — the hottest light operation in the game, so it walks
    // the footprint ONCE and applies round(f × new) − round(f × old) per voxel.
    //
    // Both terms are re-derived from the full-amplitude footprint, so this is
    // exactly equivalent to remove-at-old then add-at-new: the world still holds
    // precisely round(footprint × amplitude) and every add is undone exactly by
    // its matching remove. What is NOT equivalent — and what the two-pass shape
    // was guarding against — is accumulating a delta from the PREVIOUS delta,
    // which re-rounds each step and leaves permanent per-channel residue after
    // enough flicker rolls.
    public static void SetAmplitude(WorldState world, LightSource source, float newAmplitude)
    {
        if (Math.Abs(newAmplitude - source.Amplitude) < 1e-6f) { return; }
        using var _prof = Profiler.Sample("LightEngine.RescaleFootprint");
        Profiler.IncrementCounter("light_deposit_voxels", source.Footprint.Count);

        float oldAmplitude = source.Amplitude;
        source.Amplitude = newAmplitude;
        List<(Vector3I pos, ushort r, ushort g, ushort b)> footprint = source.Footprint;
        for (int i = 0; i < footprint.Count; i++)
        {
            var (pos, r, g, b) = footprint[i];
            int dr = Quantize(r, newAmplitude) - Quantize(r, oldAmplitude);
            int dg = Quantize(g, newAmplitude) - Quantize(g, oldAmplitude);
            int db = Quantize(b, newAmplitude) - Quantize(b, oldAmplitude);
            if ((dr | dg | db) == 0) { continue; }
            world.AddBlockLightWorld(pos.X, pos.Y, pos.Z, dr, dg, db);
        }
    }

    private static int Quantize(ushort value, float amplitude)
    {
        return (int)(value * amplitude + 0.5f);
    }

    // Re-derive one source's footprint against current geometry, in place: drop
    // what it deposited, reflood, deposit again at the same amplitude.
    private static void RecomputeSource(WorldState world, LightSource source)
    {
        DepositFootprint(world, source, -source.Amplitude);
        source.Footprint.Clear();
        ComputeFootprint(world, source);
        DepositFootprint(world, source, source.Amplitude);
    }

    // Reflood every source whose footprint could have been changed by an edit
    // inside `region`.
    //
    // OnVoxelsChanged below is the VOXEL trigger, and it is not enough on its
    // own: block light is blocked by non-voxel cover too (see IsOpenForLight),
    // and placing, breaking or deleting a roof changes no voxel at all. Without
    // this a torch keeps the footprint it flooded through the roof that is now
    // above it — or through one that has just been removed.
    public static void RefloodSourcesIn(WorldState world, VoxelBox region)
    {
        if (world == null || region.IsEmpty)
        {
            return;
        }
        var affected = new List<LightSource>();
        foreach (LightSource src in world.LightSources)
        {
            if (src.BoundsMin.X <= region.Max.X && src.BoundsMax.X >= region.Min.X
                && src.BoundsMin.Y <= region.Max.Y && src.BoundsMax.Y >= region.Min.Y
                && src.BoundsMin.Z <= region.Max.Z && src.BoundsMax.Z >= region.Min.Z)
            {
                affected.Add(src);
            }
        }
        foreach (LightSource src in affected)
        {
            RecomputeSource(world, src);
        }
    }

    public static void OnVoxelsChanged(WorldState world, List<Vector3I> changedPositions)
    {
        // Scope SunlightChunkDirty to this call — callers re-mesh what it names.
        world.SunlightChunkDirty.Clear();
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
            RecomputeSource(world, src);
        }
    }

    // Add (scale > 0) or subtract (scale < 0) the source's cached footprint
    // into the world's block-light arrays, scaled by |scale|.
    private static void DepositFootprint(WorldState world, LightSource source, float scale)
    {
        using var _prof = Profiler.Sample("LightEngine.DepositFootprint");
        // Static-light footprint add/remove: registration, teardown, and the
        // geometry-changed recompute. Flicker rolls take SetAmplitude instead.
        Profiler.IncrementCounter("light_deposit_voxels", source.Footprint.Count);
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

    // Fills source.Footprint by flooding light from the source voxel and records
    // the EffectiveBounds that OnVoxelsChanged uses to decide when a geometry
    // change forces a recompute. Uses the source's per-light λ/shape/energy
    // (0 = SimData default), which also size the flood radius.
    private static void ComputeFootprint(WorldState world, LightSource source)
    {
        BlockLightTuning tuning = ResolveTuning(world, source.Distance, source.Falloff, source.Brightness);
        var cells = new List<(Vector3I pos, float opticalDepth, float ao, float detour)>();
        ComputeFloodField(world, source.Position, tuning.FloodRadius, cells);
        ShadeFloodField(world, cells, source.Position, source.Color, tuning,
            source.Footprint, out Vector3I boundsMin, out Vector3I boundsMax);
        source.BoundsMin = boundsMin;
        source.BoundsMax = boundsMax;
    }

    // --- Block-light flood -------------------------------------------------
    //
    // Geodesic-flood model in two phases:
    //
    //   ComputeFloodField — breadth-first flood through open voxels out to a
    //     per-light Euclidean radius (derived from its λ/shape so a compact light
    //     floods a small ball), accumulating Beer-Lambert fog/canopy optical
    //     depth along each path. Output is the reachable
    //     open voxels + their optical depth + their DETOUR (below). This is the
    //     geometry-dependent work; it only changes when the source crosses a
    //     voxel or nearby geometry changes.
    //
    //   ShadeFloodField — turns that field into a quantized RGB footprint by
    //     weighting each cell exp(-distanceToSource / FalloffLambda − opticalDepth)
    //     and normalizing so total deposited energy = level × EnergyPerLevel.
    //     distanceToSource is measured from the light's FRACTIONAL position, so
    //     re-shading every frame as the source slides within a voxel glides the
    //     falloff smoothly (sub-voxel smoothing) without re-flooding. The
    //     normalization gives small-room-brighter — fewer reached voxels means
    //     each gets a larger share.
    //
    // DETOUR is what makes the walls actually shadow. Reachability alone does
    // not: a cell behind a wall that the flood can only get to the long way
    // round — over the eaves, up a stairwell, out a window — is still only a
    // metre from the source in a straight line, so a purely Euclidean falloff
    // lit it as brightly as the cell on the near side of the wall. (Measured on
    // house03: the voxel outside a wall read 123 against 122 for the torch's own
    // neighbour, and 60% of a torch's energy landed outside the building.)
    //
    // So each cell carries `detour` = its BFS hop count over its Manhattan
    // distance to the source, and the shade weights it at distance × detour.
    // The ratio is exactly 1 wherever a monotone path exists — i.e. everywhere
    // in open air and everywhere in the room the light is in — so the falloff is
    // bit-identical to a plain Euclidean one there, and stays perfectly round
    // rather than turning into the Manhattan diamond a raw hop count would give.
    // It only rises where geometry genuinely forces light the long way.
    //
    // Being a hop ratio it slightly OVERSTATES a detour (hop paths can't cut
    // diagonally, so a trip through a doorway costs a little more than the real
    // path length). Erring toward crisper rooms is the right side to be on here.
    //
    // Splitting them lets a moving light pay the O(reached voxels) flood only on
    // voxel crossings and the cheaper shade per frame. The optical-depth
    // path is the first (shortest-hop) BFS arrival — a fine approximation, not
    // the true minimal-optical-depth path (that would be Dijkstra).

    // Resolved per-light tuning: the falloff (λ, shape, energy-per-level) plus a
    // flood radius DERIVED from λ/shape, so a compact light floods (and shades) a
    // small ball and a far-reaching one a large ball — both buffer and per-frame
    // cost scale with the light's actual size, not a global worst case.
    public readonly struct BlockLightTuning
    {
        public readonly float Lambda;       // internal exp scale, derived from Distance+Falloff
        public readonly float Falloff;      // curve shape exponent
        public readonly int FloodRadius;
        public readonly float EnergyTarget; // total energy that yields a Brightness peak in open space

        public BlockLightTuning(float lambda, float falloff, int floodRadius, float energyTarget)
        {
            Lambda = lambda;
            Falloff = falloff;
            FloodRadius = floodRadius;
            EnergyTarget = energyTarget;
        }
    }

    // Relative weight at the visible edge. λ is solved so the curve reaches this
    // value at Distance — that's what makes Distance mean "reach".
    private const float FLOOD_CUTOFF = 0.01f;
    private const int MIN_FLOOD_RADIUS = 2;
    // Deposited peak for Brightness = 1 (light map is RGBA8, so 255 = white).
    private const float BYTE_MAX = 255f;

    // Resolve a light's authored Distance / Falloff / Brightness into the internal
    // falloff + energy. λ is solved so exp(-(r/λ)^Falloff) hits FLOOD_CUTOFF at
    // Distance; the flood radius is Distance + the 2-voxel window margin, clamped
    // to [MIN_FLOOD_RADIUS, BlockLightMaxDistance]. EnergyTarget is the total
    // energy the shade's geometry normalization spreads — pre-solved so the
    // OPEN-SPACE core peaks at Brightness×255, while small enclosed spaces
    // (smaller geometric sum) read brighter (small-room-brighter).
    public static BlockLightTuning ResolveTuning(WorldState world, float distance, float falloff, float brightness)
    {
        using var _prof = Profiler.Sample("LightEngine.ResolveTuning");
        SimData simData = world.SimData;
        float dist = Math.Max(0.5f, distance);
        float fall = Math.Max(0.05f, falloff);
        float bright = Math.Max(0f, brightness);

        float lambda = dist / Mathf.Pow(Mathf.Log(1f / FLOOD_CUTOFF), 1f / fall);
        int radius = Math.Clamp(Mathf.CeilToInt(dist) + 2, MIN_FLOOD_RADIUS, simData.blockLightMaxDistance);

        float windowR = Mathf.Max(1f, radius - 2f);
        float windowFloor = FalloffWeight(windowR, lambda, fall);
        float peakWeight = 1f - windowFloor;

        // Reference open-space geometric sum, ∫ 4πr²·windowedWeight dr over the
        // ball — approximates the discrete sum a light in the open would produce,
        // so EnergyTarget/that ≈ Brightness peak at the core in open air.
        float sumOpen = 0f;
        const float step = 0.25f;
        for (float r = step * 0.5f; r < radius; r += step)
        {
            float w = FalloffWeight(r, lambda, fall) - windowFloor;
            if (w <= 0f) { continue; }
            sumOpen += 4f * Mathf.Pi * r * r * w * step;
        }
        float energyTarget = peakWeight > 0f ? bright * BYTE_MAX * sumOpen / peakWeight : 0f;
        return new BlockLightTuning(lambda, fall, radius, energyTarget);
    }

    // Floods the reachable open voxels into `cells` out to floodRadius, storing
    // each voxel's path optical depth (fog/canopy), openness (open-neighbour
    // fraction, for corner AO) and detour ratio. Cleared first. Empty if the
    // source is blocked.
    public static void ComputeFloodField(
        WorldState world, Vector3I position, int floodRadius,
        List<(Vector3I pos, float opticalDepth, float ao, float detour)> cells)
    {
        using var _prof = Profiler.Sample("LightEngine.ComputeFloodField");
        cells.Clear();
        // Seeding tests the VOXEL only, deliberately: a light that ends up
        // inside non-voxel cover (a lantern hung at eave level, inside a roof's
        // SunOpaque sheet) should still emit into the open air around it rather
        // than silently going black.
        if (!IsOpenVoxel(world, position.X, position.Y, position.Z)) { return; }

        SimData simData = world.SimData;
        int maxDist = floodRadius;
        float fogExtinction = simData.blockLightFogExtinction;
        float canopyExtinction = simData.blockLightCanopyExtinction;

        int dim = maxDist * 2 + 1;
        int total = dim * dim * dim;
        int ox = position.X - maxDist;
        int oy = position.Y - maxDist;
        int oz = position.Z - maxDist;

        // visited is indexed in the local buffer; the queue carries the running
        // optical depth accumulated along the path so far. Both are pooled
        // per-thread (grow-only) to avoid per-crossing GC churn.
        bool[] visited = _floodVisited;
        if (visited == null || visited.Length < total)
        {
            visited = new bool[total];
            _floodVisited = visited;
        }
        else
        {
            Array.Clear(visited, 0, total);
        }
        Queue<(int lx, int ly, int lz, float depth, int hops)> queue = _floodQueue ??= new();
        queue.Clear();
        if (cells.Capacity < total / 4) { cells.Capacity = total / 4; }

        int seedLocal = (maxDist * dim + maxDist) * dim + maxDist;
        visited[seedLocal] = true;
        queue.Enqueue((maxDist, maxDist, maxDist, 0f, 0));

        while (queue.Count > 0)
        {
            var (lx, ly, lz, depth, hops) = queue.Dequeue();
            int wx = ox + lx, wy = oy + ly, wz = oz + lz;

            // Stop expanding at the spherical (Euclidean) boundary. Gating by
            // hop count instead caps the flood as a Manhattan octahedron, which
            // reads as a hard diamond on the ground; the radius cap keeps the
            // lit region round.
            float dx = wx - position.X, dy = wy - position.Y, dz = wz - position.Z;
            bool canExpand = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) < maxDist;

            // Examine all 6 face neighbours once: count open ones for the AO hint
            // (fewer open neighbours = more occluded corner) and, where in range,
            // expand the flood into the unvisited open ones.
            int openCount = 0;
            for (int n = 0; n < Neighbors.Length; n++)
            {
                int dnx = Neighbors[n].X, dny = Neighbors[n].Y, dnz = Neighbors[n].Z;
                int nwx = wx + dnx, nwy = wy + dny, nwz = wz + dnz;
                bool nOpen = IsOpenForLight(world, nwx, nwy, nwz);
                if (nOpen) { openCount++; }

                if (!canExpand || !nOpen) { continue; }
                int nlx = lx + dnx, nly = ly + dny, nlz = lz + dnz;
                if (nlx < 0 || nlx >= dim || nly < 0 || nly >= dim || nlz < 0 || nlz >= dim) { continue; }
                int nLocal = (nlz * dim + nly) * dim + nlx;
                if (visited[nLocal]) { continue; }
                visited[nLocal] = true;
                // Add this voxel's fog/canopy extinction to the path's optical
                // depth as the light steps into it.
                float stepDepth = depth
                    + world.GetFogWorld(nwx, nwy, nwz) * (fogExtinction / 255f)
                    + world.GetCanopyAttenuationWorld(nwx, nwy, nwz) * (canopyExtinction / 255f);
                queue.Enqueue((nlx, nly, nlz, stepDepth, hops + 1));
            }
            // FIFO over unit-cost steps, so `hops` is the exact shortest hop
            // count and equals the Manhattan distance whenever nothing is in the
            // way — see the DETOUR note above.
            int manhattan = Math.Abs(wx - position.X) + Math.Abs(wy - position.Y) + Math.Abs(wz - position.Z);
            float detour = manhattan > 0 ? hops / (float)manhattan : 1f;
            cells.Add((new Vector3I(wx, wy, wz), depth, openCount / 6f, detour));
        }
        Profiler.IncrementCounter("light_flood_cells", cells.Count);
    }

    // Shades a cached flood field into `footprint`. sourcePos is the light's
    // FRACTIONAL world position; distance is measured from it, so re-shading as
    // the source moves within a voxel slides the falloff smoothly. tuning must be
    // the same one used to flood the field (its FloodRadius drives the window).
    public static void ShadeFloodField(
        WorldState world, List<(Vector3I pos, float opticalDepth, float ao, float detour)> cells,
        Vector3 sourcePos, Color color, BlockLightTuning tuning,
        List<(Vector3I pos, ushort r, ushort g, ushort b)> footprint,
        out Vector3I boundsMin, out Vector3I boundsMax)
    {
        using var _prof = Profiler.Sample("LightEngine.ShadeFloodField");
        footprint.Clear();
        var seed = new Vector3I((int)sourcePos.X, (int)sourcePos.Y, (int)sourcePos.Z);
        boundsMin = seed;
        boundsMax = seed;
        if (cells.Count == 0) { return; }

        float lambda = tuning.Lambda;
        float shape = tuning.Falloff;
        float aoStrength = world.SimData.blockLightAO;

        // Window the geometric falloff to compact support: subtract the curve's
        // value at radius windowR so the weight reaches exactly zero there.
        // Distance is measured from the FRACTIONAL source, so the outer edge is a
        // soft vignette that tracks the source smoothly — instead of the hard
        // cell-set boundary (a sphere pinned to the integer voxel, rebuilt only
        // on crossing) that otherwise snaps each time the carrier crosses a
        // voxel. windowR sits ~2 voxels inside the flood radius so every cell at
        // the integer-centered boundary is zeroed regardless of sub-voxel offset.
        float windowR = Mathf.Max(1f, tuning.FloodRadius - 2f);
        float windowFloor = FalloffWeight(windowR, lambda, shape);

        // Geometry normalization: spread EnergyTarget across the GEOMETRIC weight
        // sum (no fog, no AO). Fewer reached voxels (a small enclosed space) ⇒
        // smaller sum ⇒ brighter — that's small-room-brighter. In the open the
        // sum ≈ the reference baked into EnergyTarget, so the core ≈ Brightness.
        // Fog (exp(-depth)) and AO are applied per-voxel AFTER, so they dim
        // absolutely instead of redistributing energy.
        float sumWgeom = 0f;
        for (int i = 0; i < cells.Count; i++)
        {
            var (pos, _, _, detour) = cells[i];
            float dx = pos.X - sourcePos.X, dy = pos.Y - sourcePos.Y, dz = pos.Z - sourcePos.Z;
            float g = FalloffWeight(Mathf.Sqrt(dx * dx + dy * dy + dz * dz) * detour, lambda, shape) - windowFloor;
            if (g > 0f) { sumWgeom += g; }
        }
        if (sumWgeom <= 0f) { return; }
        float scale = tuning.EnergyTarget / sumWgeom;

        int bMinX = int.MaxValue, bMinY = int.MaxValue, bMinZ = int.MaxValue;
        int bMaxX = int.MinValue, bMaxY = int.MinValue, bMaxZ = int.MinValue;

        footprint.Capacity = cells.Count;
        for (int i = 0; i < cells.Count; i++)
        {
            var (pos, depth, aoRaw, detour) = cells[i];
            float dx = pos.X - sourcePos.X, dy = pos.Y - sourcePos.Y, dz = pos.Z - sourcePos.Z;
            // Distance the light actually had to travel to get here: the smooth
            // Euclidean distance (re-evaluated per frame from the fractional
            // source, so sub-voxel motion still glides) scaled by the cell's
            // detour, which the flood measured against static geometry.
            float r = Mathf.Sqrt(dx * dx + dy * dy + dz * dz) * detour;
            float g = FalloffWeight(r, lambda, shape) - windowFloor;
            if (g <= 0f) { continue; }
            // AO darkens occluded corners (fewer open neighbours) absolutely.
            float ao = 1f - aoStrength * (1f - aoRaw);
            float v = g * ao * Mathf.Exp(-depth) * scale;
            ushort qr = ClampU16((int)(v * color.R + 0.5f));
            ushort qg = ClampU16((int)(v * color.G + 0.5f));
            ushort qb = ClampU16((int)(v * color.B + 0.5f));
            if (qr == 0 && qg == 0 && qb == 0) { continue; }

            footprint.Add((pos, qr, qg, qb));

            if (pos.X < bMinX) { bMinX = pos.X; }
            if (pos.X > bMaxX) { bMaxX = pos.X; }
            if (pos.Y < bMinY) { bMinY = pos.Y; }
            if (pos.Y > bMaxY) { bMaxY = pos.Y; }
            if (pos.Z < bMinZ) { bMinZ = pos.Z; }
            if (pos.Z > bMaxZ) { bMaxZ = pos.Z; }
        }

        // Pad bounds by 1 so a wall placed just outside the lit zone still
        // triggers a recompute (it could now block light that was leaking out).
        if (footprint.Count > 0)
        {
            boundsMin = new Vector3I(bMinX - 1, bMinY - 1, bMinZ - 1);
            boundsMax = new Vector3I(bMaxX + 1, bMaxY + 1, bMaxZ + 1);
        }
        Profiler.IncrementCounter("light_shade_voxels", footprint.Count);
    }

    private static bool IsOpenVoxel(WorldState world, int wx, int wy, int wz)
    {
        int v = world.GetBlockWorld(wx, wy, wz);
        return v == Blocks.AirId || Blocks.IsTransparent(v);
    }

    // Can block light cross into this cell? Solid voxels stop it, and so does
    // non-voxel cover: a roof is an ENTITY, so a purely voxel-based test let
    // torchlight pour straight up through a cottage roof, over the walls and
    // back down outside. Roofs stamp their cover into SunOpaque (one sheet at
    // eave level, punched wherever the roof is holed), which is the same barrier
    // the sun column, the mesher's vertex bake and InteriornessGen already read
    // — block light was the one consumer that ignored it. Partial-cover roofs
    // write CanopyAttenuation instead, which the flood already charges as
    // optical depth, so both kinds are handled with nothing new to author.
    private static bool IsOpenForLight(WorldState world, int wx, int wy, int wz)
    {
        return IsOpenVoxel(world, wx, wy, wz) && !world.GetSunOpaqueWorld(wx, wy, wz);
    }

    // True if a light at this voxel would emit (the voxel is open). A moving
    // light checks this before re-flooding so it can keep its previous field
    // instead of blanking when it briefly crosses into solid / unloaded space.
    // Voxels only, matching ComputeFloodField's seed test.
    public static bool CanEmitFrom(WorldState world, Vector3I voxel)
    {
        return IsOpenVoxel(world, voxel.X, voxel.Y, voxel.Z);
    }

    // Unwindowed geometric falloff weight at distance r: exp(-(r/λ)^shape).
    // shape<1 = sharp hotspot + long tail; 1 = exponential; >1 = soft plateau.
    // Monotonic decreasing in r, so subtracting its value at the window radius
    // gives a smooth compact-support kernel (see ShadeFloodField).
    private static float FalloffWeight(float r, float lambda, float shape)
    {
        return Mathf.Exp(-Mathf.Pow(r / lambda, shape));
    }

    private static ushort ClampU16(int v)
    {
        if (v < 0) { return 0; }
        if (v > ushort.MaxValue) { return ushort.MaxValue; }
        return (ushort)v;
    }

    // --- Sunlight propagation (max-fill BFS) -------------------------------

    // `progress` is for offline bakes only (see Relight). The flood has no total
    // to count against, so progress is the queue's drain ratio,
    // processed / (processed + pending): the seeded queue is huge and the ratio
    // rises as it empties. Tracked as a high-water mark, since a burst of
    // re-enqueues can push the ratio back down and a bar must not walk backwards.
    // (Fading light level was tried first and is useless here: the column scan
    // seeds voxels at every level at once, so the dimmest is hit within the first
    // few thousand pops and the bar pins itself at ~93% for the whole flood.)
    private static void SpreadSunlight(WorldState world, Queue<(int x, int y, int z)> queue, System.Action<float> progress = null)
    {
        float canopySunExtinction = world.SimData.canopySunExtinction;
        float fogSunExtinction = world.SimData.fogSunExtinction;
        int falloffPerVoxel = Math.Max(1, world.SimData.sunFalloffPerVoxel);
        bool report = progress != null;
        float drained = 0f;
        long popped = 0;
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            int currentLevel = world.GetSunlightWorld(x, y, z);
            if (report && (++popped & PROGRESS_POP_MASK) == 0)
            {
                float ratio = popped / (float)(popped + queue.Count);
                if (ratio > drained) { drained = ratio; }
                progress(Mathf.Clamp(drained, 0f, 1f));
            }
            if (currentLevel <= falloffPerVoxel) { continue; }

            foreach (Vector3I offset in Neighbors)
            {
                int nx = x + offset.X;
                int ny = y + offset.Y;
                int nz = z + offset.Z;

                if (!world.IsInBounds(nx, ny, nz)) { continue; }
                int v = world.GetBlockWorld(nx, ny, nz);
                if (v != Blocks.AirId && !Blocks.IsTransparent(v)) { continue; }
                // Non-voxel solid cover (a roof) is a barrier to the flood as
                // well as to the column scan. Without this the lit air directly
                // above it spreads straight back down through it — one step, one
                // level — and refills the room the column scan just darkened.
                if (world.GetSunOpaqueWorld(nx, ny, nz)) { continue; }

                float stepped = currentLevel - falloffPerVoxel - Blocks.LightAttenuation(v);
                stepped *= MediumTransmittance(world.GetFogWorld(nx, ny, nz), fogSunExtinction);
                // Canopy AND its shadow, unlike the column scan above, which
                // reads only the canopy. This is the one pass that must see the
                // shadow: without it a neighbouring un-canopied column refills
                // the shaded voxel at nearly MAX_LIGHT and the tree casts no
                // shade at all. Charging the shadow at the canopy's own column
                // integral means one lateral step in costs what coming down
                // through the leaves did, so refill can raise a voxel to — but
                // never above — the vertical answer.
                stepped *= MediumTransmittance(
                    world.GetCanopyAttenuationWorld(nx, ny, nz) + world.GetCanopyShadeWorld(nx, ny, nz),
                    canopySunExtinction);
                int newLevel = (int)(stepped + 0.5f);
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
    // distinct XZ, not a flood fill).
    private static void RecomputeSkyExposureColumns(WorldState world, List<Vector3I> changedPositions)
    {
        float canopySunExtinction = world.SimData.canopySunExtinction;

        var columns = new HashSet<(int x, int z)>();
        foreach (Vector3I pos in changedPositions)
        {
            columns.Add((pos.X, pos.Z));
        }

        foreach (var (wx, wz) in columns)
        {
            ScanSkyExposureColumn(world, wx, wz, canopySunExtinction);
        }
    }

    private static void UpdateSunlightAt(WorldState world, List<Vector3I> changedPositions)
    {
        var removeQueue = new Queue<(int x, int y, int z, int level)>();
        var refillQueue = new Queue<(int x, int y, int z)>();

        foreach (Vector3I pos in changedPositions)
        {
            int v = world.GetBlockWorld(pos.X, pos.Y, pos.Z);
            bool isAir = v == Blocks.AirId || Blocks.IsTransparent(v);
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

        RetractSunlight(world, removeQueue, refillQueue);
        SpreadSunlight(world, refillQueue);
    }

    // Darkening half of an incremental sunlight update: walks outward from cells
    // that just lost light, zeroing everything dimmer (which could only have
    // come from them) and collecting anything still at least as bright as a
    // source to refill from.
    private static void RetractSunlight(WorldState world, Queue<(int x, int y, int z, int level)> removeQueue, Queue<(int x, int y, int z)> refillQueue)
    {
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
    }
}
