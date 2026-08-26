using System;
using System.Collections.Generic;
using Godot;

public static class LightEngine
{
    // Sun channel: a max-fill flood with a per-step falloff (block light uses
    // the geodesic flood model instead — see the Block-light flood section).
    public const int MAX_LIGHT = 60;

    // Bump whenever the sunlight math, or the occluder stamping that feeds it,
    // changes. A disk-loaded world's Sunlight bytes are trusted as BAKED — the
    // load path does not re-propagate them, because a full-world Relight is
    // ~13s at the default size and was the entire cost of the load phase. This
    // version rides in the worldgen-cache fingerprint, so a change here gives
    // every cached world a new cache path and regenerates it. A hand-authored
    // .hike is not covered by that: re-bake it, or run `relight` in the console.
    public const int LIGHT_VERSION = 1;

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
    //
    // Runs one worker per CHUNK-X SLICE. A column writes only into the chunk
    // stack at its own (cx, cz), so two slices can never touch the same array —
    // that disjointness is what makes this safe, and it is why the split is by
    // chunk x rather than by voxel x. The dirty set is the one thing they share,
    // so each worker collects its own and they merge at the end.
    public static void ComputeSkyExposure(WorldState world, System.Action<float> progress = null)
    {
        world.ClearSkyExposureAll();

        float canopySunExtinction = world.SimData.canopySunExtinction;
        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;
        int sliceCount = Math.Max(1, world.Max.X - world.Min.X + 1);
        int slicesDone = 0;
        object dirtyLock = new();

        System.Threading.Tasks.Parallel.For(
            world.Min.X,
            world.Max.X + 1,
            () => new List<Vector3I>(),
            (cx, _, dirty) =>
            {
                int minWx = cx * ChunkState.SIZE;
                for (int wx = minWx; wx < minWx + ChunkState.SIZE; wx++)
                {
                    for (int wz = minWz; wz < maxWz; wz++)
                    {
                        ScanSkyExposureColumn(world, wx, wz, canopySunExtinction, dirty);
                    }
                }
                if (progress != null)
                {
                    progress(System.Threading.Interlocked.Increment(ref slicesDone) / (float)sliceCount);
                }
                return dirty;
            },
            dirty =>
            {
                lock (dirtyLock)
                {
                    for (int i = 0; i < dirty.Count; i++)
                    {
                        world.SkyExposureChunkDirty.Add(dirty[i]);
                    }
                }
            });
    }

    // One XZ column of SkyExposure, top-down. The single definition of the
    // field — both the full-world bake and the incremental per-edit recompute
    // call this, so there is no pair of scans to keep in sync by hand.
    //
    // Walks a chunk at a time rather than a voxel at a time. Every field this
    // reads (blocks, canopy, non-voxel cover) and the one it writes is a
    // separate Dictionary keyed on the chunk coord, so the straight per-voxel
    // walk paid four hash lookups per voxel for arrays that only change every
    // 16 — which is most of what a full-world sky pass cost.
    // `dirty` collects the chunks this column wrote, so the whole-world pass can
    // hand each worker a private list instead of racing on the world's set.
    private static void ScanSkyExposureColumn(WorldState world, int wx, int wz, float canopySunExtinction, ICollection<Vector3I> dirty)
    {
        int cx = FloorDiv(wx, ChunkState.SIZE);
        int cz = FloorDiv(wz, ChunkState.SIZE);
        int lx = Mod(wx, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);

        float level = MAX_LIGHT;
        bool blocked = false;
        for (int cy = world.Max.Y; cy >= world.Min.Y; cy--)
        {
            var cc = new Vector3I(cx, cy, cz);
            if (!world._chunks.TryGetValue(cc, out ChunkState chunk))
            {
                // Not resident: nothing to write, and air neither attenuates
                // nor blocks, so the column carries on unchanged below it.
                continue;
            }
            byte[,,] sky = chunk.SkyExposure;
            world.CanopyAttenuation.TryGetValue(cc, out byte[,,] canopy);
            world.SunOpaque.TryGetValue(cc, out bool[,,] sunOpaque);

            for (int ly = ChunkState.SIZE - 1; ly >= 0; ly--)
            {
                if (blocked)
                {
                    sky[lx, ly, lz] = 0;
                    continue;
                }
                int v = chunk.Voxels[lx, ly, lz];
                // Opaque ceiling — a solid voxel, or non-voxel solid cover such
                // as a roof: this voxel and everything below it are sheltered.
                if ((v != Blocks.AirId && !Blocks.IsTransparent(v)) || (sunOpaque != null && sunOpaque[lx, ly, lz]))
                {
                    blocked = true;
                    sky[lx, ly, lz] = 0;
                    continue;
                }
                level -= Blocks.LightAttenuation(v);
                if (canopy != null)
                {
                    level *= MediumTransmittance(canopy[lx, ly, lz], canopySunExtinction);
                }
                int rounded = (int)(level + 0.5f);
                if (rounded <= 0)
                {
                    blocked = true;
                    sky[lx, ly, lz] = 0;
                    continue;
                }
                sky[lx, ly, lz] = (byte)rounded;
            }
            dirty.Add(cc);
        }
    }

    private static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }

    // Everything the sunlight passes read or write, resolved off the chunk
    // dictionary ONCE: the flat chunk grid, each per-voxel channel flattened
    // onto its indices, the two medium-transmittance tables, and per-chunk
    // dirty flags.
    //
    // Every one of those used to be a Dictionary<Vector3I, …> lookup PER
    // NEIGHBOUR — eight of them, so ~48 hashes per popped voxel — and the two
    // Mathf.Exp calls were per neighbour too. Both were most of what a bake's
    // sun flood cost. Voxels are addressed as ChunkGrid packed indices
    // throughout, which is also what lets the seed queue be one int per entry
    // (a painted world seeds ~16M of them).
    //
    // Build one per pass and drop it: it caches chunk references, so anything
    // that adds or removes a chunk invalidates it.
    private sealed class SunField
    {
        public readonly WorldState World;
        public readonly ChunkGrid Grid;
        public readonly byte[][,,] Voxels;
        public readonly byte[][,,] Sun;
        public readonly byte[][,,] Fog;
        public readonly byte[][,,] Canopy;
        public readonly byte[][,,] Shade;
        public readonly bool[][,,] Opaque;
        // exp(-d/255 * extinction), indexed by density byte. The canopy table
        // reaches 510 because the flood charges canopy PLUS its shadow.
        public readonly float[] FogTransmit;
        public readonly float[] CanopyTransmit;
        public readonly int FalloffPerVoxel;
        private readonly bool[] _dirty;

        public SunField(WorldState world)
        {
            World = world;
            Grid = new ChunkGrid(world);
            int count = Grid.Count;
            Voxels = new byte[count][,,];
            Sun = new byte[count][,,];
            Fog = new byte[count][,,];
            for (int i = 0; i < count; i++)
            {
                ChunkState chunk = Grid.Chunk(i);
                if (chunk == null)
                {
                    continue;
                }
                Voxels[i] = chunk.Voxels;
                Sun[i] = chunk.Sunlight;
                Fog[i] = EffectiveFog(chunk, world.SimData);
            }
            Canopy = Grid.Resolve(world.CanopyAttenuation);
            Shade = Grid.Resolve(world.CanopyShade);
            Opaque = Grid.Resolve(world.SunOpaque);
            _dirty = new bool[count];

            float fogSunExtinction = world.SimData.fogSunExtinction;
            float canopySunExtinction = world.SimData.canopySunExtinction;
            FogTransmit = new float[256];
            for (int d = 0; d < FogTransmit.Length; d++)
            {
                FogTransmit[d] = MediumTransmittance(d, fogSunExtinction);
            }
            CanopyTransmit = new float[511];
            for (int d = 0; d < CanopyTransmit.Length; d++)
            {
                CanopyTransmit[d] = MediumTransmittance(d, canopySunExtinction);
            }
            FalloffPerVoxel = Math.Max(1, world.SimData.sunFalloffPerVoxel);
        }

        // Fog as the sun passes MUST see it: ChunkState.GetFog, not the raw
        // FogDensity channel. An interior class's dust floor is fog too —
        // max(authored, dustFloor * interiorness) over air — and reading past it
        // makes every building, tunnel and cave bake a level or two brighter
        // than it should. Materialized per chunk so the flood keeps its single
        // array read; a chunk whose cells carry no dust aliases the channel
        // itself and costs nothing.
        private static byte[,,] EffectiveFog(ChunkState chunk, SimData simData)
        {
            const int CELLS = ChunkState.ENV_SUBGRID_SIZE;
            bool any = false;
            for (int cx = 0; cx < CELLS; cx++)
            {
                for (int cy = 0; cy < CELLS; cy++)
                {
                    for (int cz = 0; cz < CELLS; cz++)
                    {
                        InteriorAmbienceData ambience =
                            simData?.GetInteriorAmbience(chunk.GetEnvTag(cx, cy, cz));
                        if (ambience == null || ambience.dustFloor <= 0f)
                        {
                            continue;
                        }
                        any |= chunk.GetInteriorness(cx, cy, cz) > 0;
                    }
                }
            }
            if (!any)
            {
                return chunk.FogDensity;
            }
            // GetFog itself, never a second copy of its rule — the dust floor is
            // the kind of thing that drifts the moment it is written twice.
            var fog = new byte[ChunkState.SIZE, ChunkState.SIZE, ChunkState.SIZE];
            for (int x = 0; x < ChunkState.SIZE; x++)
            {
                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    for (int z = 0; z < ChunkState.SIZE; z++)
                    {
                        fog[x, y, z] = (byte)chunk.GetFog(simData, x, y, z);
                    }
                }
            }
            return fog;
        }

        public int Sunlight(int packed)
        {
            return Sun[packed >> ChunkGrid.VOXEL_BITS][
                ChunkGrid.LocalX(packed), ChunkGrid.LocalY(packed), ChunkGrid.LocalZ(packed)];
        }

        public void SetSunlight(int packed, int level)
        {
            int ci = packed >> ChunkGrid.VOXEL_BITS;
            Sun[ci][ChunkGrid.LocalX(packed), ChunkGrid.LocalY(packed), ChunkGrid.LocalZ(packed)] = (byte)level;
            _dirty[ci] = true;
        }

        public void MarkDirty(int chunkIndex)
        {
            _dirty[chunkIndex] = true;
        }

        // The dirty sets and SunlightVersion, marked once per CHUNK at the end
        // rather than per voxel write. A full bake writes tens of millions of
        // voxels for a dirty set that is "every chunk" by the time it finishes,
        // and each write was two HashSet.Add plus a version bump.
        public void FlushDirty()
        {
            for (int i = 0; i < _dirty.Length; i++)
            {
                if (!_dirty[i])
                {
                    continue;
                }
                ChunkState chunk = Grid.Chunk(i);
                chunk.MarkSunlightChanged();
                World.LightChunkDirty.Add(chunk.ChunkCoord);
                World.SunlightChunkDirty.Add(chunk.ChunkCoord);
                _dirty[i] = false;
            }
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

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var field = new SunField(world);
        long fieldMs = sw.ElapsedMilliseconds;

        int minWz = world.Min.Z * ChunkState.SIZE;
        int maxWz = (world.Max.Z + 1) * ChunkState.SIZE;
        int sliceCount = Math.Max(1, world.Max.X - world.Min.X + 1);
        var seedQueues = new Queue<int>[sliceCount];
        int slicesDone = 0;

        // One worker per chunk-X SLICE, exactly as ComputeSkyExposure is split
        // and for the same reason: a column writes only into the chunk stack at
        // its own (cx, cz), so two slices can never touch the same array. The
        // seed queue is the one thing they would share, so each fills its own
        // list and they concatenate below — the flood is a max-relaxation whose
        // fixpoint does not depend on the order seeds are popped in.
        System.Threading.Tasks.Parallel.For(0, sliceCount, slice =>
        {
            var seeds = new Queue<int>();
            int minWx = (world.Min.X + slice) * ChunkState.SIZE;
            for (int wx = minWx; wx < minWx + ChunkState.SIZE; wx++)
            {
                for (int wz = minWz; wz < maxWz; wz++)
                {
                    ScanSunlightColumn(field, wx, wz, seeds, int.MaxValue);
                }
            }
            seedQueues[slice] = seeds;
            if (progress != null)
            {
                progress(SCAN_SHARE * System.Threading.Interlocked.Increment(ref slicesDone) / sliceCount);
            }
        });

        int seedCount = 0;
        foreach (Queue<int> slice in seedQueues)
        {
            seedCount += slice.Count;
        }
        var queue = new Queue<int>(seedCount);
        for (int i = 0; i < seedQueues.Length; i++)
        {
            foreach (int packed in seedQueues[i])
            {
                queue.Enqueue(packed);
            }
            seedQueues[i] = null;   // ~64MB of seeds on a painted world; free as we go
        }
        long scanMs = sw.ElapsedMilliseconds;

        progress?.Invoke(SCAN_SHARE);
        SpreadSunlight(field, queue, progress == null ? null : p => progress(SCAN_SHARE + p * (1f - SCAN_SHARE)));
        field.FlushDirty();
        progress?.Invoke(1f);
        if (progress != null)
        {
            // Offline bakes only — this is minutes of a bake and "the relight
            // was slow" is not a place to start optimizing from.
            GD.Print($"[sunlight] field={fieldMs}ms scan={scanMs - fieldMs}ms "
                + $"flood={sw.ElapsedMilliseconds - scanMs}ms seeds={seedCount}");
        }
    }

    // Direct sun down one XZ column, top-down to the first thing that stops it.
    // The single definition of the column phase — the full bake and the regional
    // relight both call it, so there is no pair of scans to keep in sync.
    // Every cell it lights is appended to `seeds` as a source for the spread.
    //
    // The scan always starts at the world top (attenuation accumulates from
    // there) but writes only at or below `maxWriteY`. A regional relight uses
    // that to leave the untouched sky above its region alone rather than
    // rewriting it with identical values and dirtying every chunk it passes.
    private static void ScanSunlightColumn(SunField f, int wx, int wz, Queue<int> seeds, int maxWriteY)
    {
        WorldState world = f.World;
        int minWy = world.Min.Y * ChunkState.SIZE;
        int topWy = (world.Max.Y + 1) * ChunkState.SIZE - 1;

        float sunLevel = MAX_LIGHT;
        for (int wy = topWy; wy >= minWy; wy--)
        {
            int packed = f.Grid.Pack(wx, wy, wz);
            if (packed < 0)
            {
                // Not resident: nothing to write, and air neither blocks nor
                // attenuates, so the column carries on unchanged below it.
                continue;
            }
            int ci = packed >> ChunkGrid.VOXEL_BITS;
            int lx = ChunkGrid.LocalX(packed);
            int ly = ChunkGrid.LocalY(packed);
            int lz = ChunkGrid.LocalZ(packed);

            int v = f.Voxels[ci][lx, ly, lz];
            if (v != Blocks.AirId && !Blocks.IsTransparent(v))
            {
                break;
            }
            // Non-voxel solid cover (a roof) stops the column exactly as
            // a solid voxel does — canopy attenuation can only ever dim.
            bool[,,] opaque = f.Opaque[ci];
            if (opaque != null && opaque[lx, ly, lz])
            {
                break;
            }
            sunLevel -= Blocks.LightAttenuation(v);
            int fog = f.Fog[ci][lx, ly, lz];
            if (fog > 0)
            {
                sunLevel *= f.FogTransmit[fog];
            }
            // Canopy only, never CanopyShade. The scan carries sunLevel down, so
            // it has already paid for the leaves by the time it is under them;
            // the air below holds no foliage to charge for again. Reading the
            // shadow here would re-toll the same canopy once per voxel of trunk
            // and make a tree's darkness a function of its HEIGHT.
            byte[,,] canopy = f.Canopy[ci];
            if (canopy != null)
            {
                int density = canopy[lx, ly, lz];
                if (density > 0)
                {
                    sunLevel *= f.CanopyTransmit[density];
                }
            }
            int level = (int)(sunLevel + 0.5f);
            if (level <= 0)
            {
                break;
            }
            if (wy > maxWriteY)
            {
                continue;
            }
            f.Sun[ci][lx, ly, lz] = (byte)level;
            f.MarkDirty(ci);
            seeds.Enqueue(packed);
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
        int minWy = world.Min.Y * ChunkState.SIZE;
        var field = new SunField(world);

        // Zero the columns outright and hand their OLD levels to the removal
        // flood, so everything they lit elsewhere is pulled back too. Same
        // machinery a voxel edit uses; only the trigger differs.
        var removeQueue = new Queue<(int packed, int level)>();
        var refillQueue = new Queue<int>();
        for (int wx = region.Min.X; wx <= region.Max.X; wx++)
        {
            for (int wz = region.Min.Z; wz <= region.Max.Z; wz++)
            {
                for (int wy = region.Max.Y; wy >= minWy; wy--)
                {
                    int packed = field.Grid.Pack(wx, wy, wz);
                    if (packed < 0)
                    {
                        continue;
                    }
                    int level = field.Sunlight(packed);
                    if (level <= 0)
                    {
                        continue;
                    }
                    field.SetSunlight(packed, 0);
                    removeQueue.Enqueue((packed, level));
                }
            }
        }
        RetractSunlight(field, removeQueue, refillQueue);

        for (int wx = region.Min.X; wx <= region.Max.X; wx++)
        {
            for (int wz = region.Min.Z; wz <= region.Max.Z; wz++)
            {
                ScanSkyExposureColumn(world, wx, wz, canopySunExtinction, world.SkyExposureChunkDirty);
                ScanSunlightColumn(field, wx, wz, refillQueue, region.Max.Y);
            }
        }
        SpreadSunlight(field, refillQueue);
        field.FlushDirty();
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
    private static void SpreadSunlight(SunField f, Queue<int> queue, System.Action<float> progress = null)
    {
        ChunkGrid grid = f.Grid;
        byte[][,,] voxels = f.Voxels;
        byte[][,,] sun = f.Sun;
        byte[][,,] fog = f.Fog;
        byte[][,,] canopy = f.Canopy;
        byte[][,,] shade = f.Shade;
        bool[][,,] opaque = f.Opaque;
        float[] fogTransmit = f.FogTransmit;
        float[] canopyTransmit = f.CanopyTransmit;
        int falloffPerVoxel = f.FalloffPerVoxel;
        bool report = progress != null;
        float drained = 0f;
        long popped = 0;
        while (queue.Count > 0)
        {
            int packed = queue.Dequeue();
            int currentLevel = sun[packed >> ChunkGrid.VOXEL_BITS][
                ChunkGrid.LocalX(packed), ChunkGrid.LocalY(packed), ChunkGrid.LocalZ(packed)];
            if (report && (++popped & PROGRESS_POP_MASK) == 0)
            {
                float ratio = popped / (float)(popped + queue.Count);
                if (ratio > drained) { drained = ratio; }
                progress(Mathf.Clamp(drained, 0f, 1f));
            }
            if (currentLevel <= falloffPerVoxel) { continue; }

            for (int d = 0; d < ChunkGrid.Offsets.Length; d++)
            {
                // -1 covers both "off the world" and "that chunk isn't
                // resident" — the grid's neighbour table already knows.
                int n = grid.Step(packed, d);
                if (n < 0) { continue; }
                int nc = n >> ChunkGrid.VOXEL_BITS;
                int lx = ChunkGrid.LocalX(n);
                int ly = ChunkGrid.LocalY(n);
                int lz = ChunkGrid.LocalZ(n);

                int v = voxels[nc][lx, ly, lz];
                if (v != Blocks.AirId && !Blocks.IsTransparent(v)) { continue; }
                // Non-voxel solid cover (a roof) is a barrier to the flood as
                // well as to the column scan. Without this the lit air directly
                // above it spreads straight back down through it — one step, one
                // level — and refills the room the column scan just darkened.
                bool[,,] op = opaque[nc];
                if (op != null && op[lx, ly, lz]) { continue; }

                float stepped = currentLevel - falloffPerVoxel - Blocks.LightAttenuation(v);
                int fogDensity = fog[nc][lx, ly, lz];
                if (fogDensity > 0) { stepped *= fogTransmit[fogDensity]; }
                // Canopy AND its shadow, unlike the column scan above, which
                // reads only the canopy. This is the one pass that must see the
                // shadow: without it a neighbouring un-canopied column refills
                // the shaded voxel at nearly MAX_LIGHT and the tree casts no
                // shade at all. Charging the shadow at the canopy's own column
                // integral means one lateral step in costs what coming down
                // through the leaves did, so refill can raise a voxel to — but
                // never above — the vertical answer.
                byte[,,] can = canopy[nc];
                byte[,,] sh = shade[nc];
                int leaves = (can != null ? can[lx, ly, lz] : 0) + (sh != null ? sh[lx, ly, lz] : 0);
                if (leaves > 0) { stepped *= canopyTransmit[leaves]; }
                int newLevel = (int)(stepped + 0.5f);
                if (newLevel <= 0) { continue; }

                byte[,,] target = sun[nc];
                if (newLevel > target[lx, ly, lz])
                {
                    target[lx, ly, lz] = (byte)newLevel;
                    f.MarkDirty(nc);
                    queue.Enqueue(n);
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
            ScanSkyExposureColumn(world, wx, wz, canopySunExtinction, world.SkyExposureChunkDirty);
        }
    }

    private static void UpdateSunlightAt(WorldState world, List<Vector3I> changedPositions)
    {
        var field = new SunField(world);
        var removeQueue = new Queue<(int packed, int level)>();
        var refillQueue = new Queue<int>();

        foreach (Vector3I pos in changedPositions)
        {
            int packed = field.Grid.Pack(pos.X, pos.Y, pos.Z);
            if (packed < 0)
            {
                continue;
            }
            int v = field.Voxels[packed >> ChunkGrid.VOXEL_BITS][
                ChunkGrid.LocalX(packed), ChunkGrid.LocalY(packed), ChunkGrid.LocalZ(packed)];
            bool isAir = v == Blocks.AirId || Blocks.IsTransparent(v);
            int level = field.Sunlight(packed);

            if (isAir && level > 0)
            {
                removeQueue.Enqueue((packed, level));
                field.SetSunlight(packed, 0);
            }
            else if (isAir)
            {
                for (int d = 0; d < ChunkGrid.Offsets.Length; d++)
                {
                    int n = field.Grid.Step(packed, d);
                    if (n < 0) { continue; }
                    if (field.Sunlight(n) > 0)
                    {
                        refillQueue.Enqueue(n);
                    }
                }
            }
            else if (level > 0)
            {
                removeQueue.Enqueue((packed, level));
                field.SetSunlight(packed, 0);
            }
        }

        RetractSunlight(field, removeQueue, refillQueue);
        SpreadSunlight(field, refillQueue);
        field.FlushDirty();
    }

    // Darkening half of an incremental sunlight update: walks outward from cells
    // that just lost light, zeroing everything dimmer (which could only have
    // come from them) and collecting anything still at least as bright as a
    // source to refill from.
    private static void RetractSunlight(SunField f, Queue<(int packed, int level)> removeQueue, Queue<int> refillQueue)
    {
        while (removeQueue.Count > 0)
        {
            var (packed, level) = removeQueue.Dequeue();
            for (int d = 0; d < ChunkGrid.Offsets.Length; d++)
            {
                int n = f.Grid.Step(packed, d);
                if (n < 0) { continue; }

                int neighborLevel = f.Sunlight(n);
                if (neighborLevel > 0 && neighborLevel < level)
                {
                    removeQueue.Enqueue((n, neighborLevel));
                    f.SetSunlight(n, 0);
                }
                else if (neighborLevel >= level)
                {
                    refillQueue.Enqueue(n);
                }
            }
        }
    }
}
