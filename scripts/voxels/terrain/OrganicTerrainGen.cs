using System;
using Godot;

// The ORGANIC terrain approach: build a continuous, domain-warped surface and
// let cliffs emerge where it is already steep, rather than quantizing height to
// a fixed lattice. Nothing is carved — no tunnel slabs, no cave pass — so no
// roof is ever left floating over a void.
//
// Shape, in order: warped continental base, ridged relief gated by a
// hill-country mask, roughness, a soft basin floor; then drainage incision;
// then fault blocks and strata benching; then a relaxation pass that enforces
// the walkability invariants. See OrganicTerrainData for the knobs and the
// reasoning behind each stage.
public class OrganicTerrainGen : ITerrainGenerator
{
    // Defaults for a zone with no OrganicZoneTerrainData — a plain instance so
    // the fallback cannot drift from the authored defaults.
    private static readonly OrganicZoneTerrainData ZoneDefaults = new();

    private readonly OrganicTerrainData _data;
    private readonly WorldGenData _genData;
    private readonly int _worldSeed;

    public OrganicTerrainGen(OrganicTerrainData data, WorldGenData genData, int worldSeed)
    {
        _data = data;
        _genData = genData;
        _worldSeed = worldSeed;
    }

    // This approach hollows nothing as it fills: every voxel at or below the
    // column height is solid. Caverns will return as a pass that snaps ceilings
    // to HeightMap.LevelStep and keeps a minimum rock thickness under the
    // surface — the two rules the plateau approach's tunnels broke.
    public bool IsCarvedAt(int wx, int wy, int wz, int columnSolidHeight) => false;

    public bool IsSealedFromWaterAt(int wx, int wy, int wz) => false;

    public void CarveVolumes(WorldState ws) { }

    public System.Collections.Generic.IReadOnlyList<
        System.Collections.Generic.KeyValuePair<string, Vector3>> GetNamedFeatures()
        => Array.Empty<System.Collections.Generic.KeyValuePair<string, Vector3>>();

    public void DumpDiagnostics(string dir) { }

    // Noise channel salts for this path only, kept clear of the legacy block
    // (which ends at 0x18) so both paths can coexist in one world file.
    private const int SEED_SALT_WARP_X = 0x20;
    private const int SEED_SALT_WARP_Z = 0x21;
    private const int SEED_SALT_MACRO = 0x22;
    private const int SEED_SALT_RELIEF = 0x23;
    private const int SEED_SALT_RELIEF_MASK = 0x24;
    private const int SEED_SALT_ROUGHNESS = 0x25;
    private const int SEED_SALT_BENCH_MASK = 0x26;
    private const int SEED_SALT_BENCH_STEP = 0x27;
    private const int SEED_SALT_BENCH_PHASE = 0x28;
    private const int SEED_SALT_FAULT = 0x29;
    private const int SEED_SALT_FAULT_BREACH = 0x2A;

    // Horizontal reach, in columns, of the slope measured for the bench gate.
    // Wider than one column on purpose: at one column the reading is dominated
    // by the roughness channel, so flat ground reads as steep and gets terraced.
    // This measures the REGIONAL grade instead.
    private const int SLOPE_STENCIL = 3;

    // Input-coordinate multiplier for the fault edge warp, so the world warp
    // channel can be re-read at a finer scale instead of allocating its own.
    private const float FAULT_EDGE_WARP_SCALE = 5f;

    // Input-coordinate multiplier for the per-column wall-height cap, relative
    // to the bench-step channel it reuses.
    private const float WALL_CAP_SCALE = 12f;

    public HeightMap BuildHeightMap(WorldState ws)
    {
        OrganicTerrainData org = _data;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        var warpX = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_WARP_X), org.warpFrequency, 2);
        var warpZ = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_WARP_Z), org.warpFrequency, 2);
        var macro = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_MACRO), org.macroFrequency, org.macroOctaves);
        var relief = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_RELIEF), org.reliefFrequency, org.reliefOctaves);
        var reliefMask = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_RELIEF_MASK), org.reliefMaskFrequency, 2);
        var roughness = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_ROUGHNESS), org.roughnessFrequency, org.roughnessOctaves);
        var benchMask = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_BENCH_MASK), org.benchMaskFrequency, 2);
        var benchStep = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_BENCH_STEP), org.benchStepFrequency, 1);
        var benchPhase = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_BENCH_PHASE), org.benchPhaseFrequency, 2);
        // CellValue: one random level per fault block, constant across the
        // block's interior and discontinuous at its edges — the discontinuity
        // IS the scarp, so no explicit line geometry is needed.
        var faultBlock = MakeCellular(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_FAULT), org.faultFrequency);
        var faultBreach = WorldGen.MakePerlin(WorldGen.DeriveSeed(_worldSeed, SEED_SALT_FAULT_BREACH), org.faultBreachFrequency, 2);

        // Pass 1 — the continuous field, in voxels relative to sea level. Held
        // as float through pass 2 because the bench gate needs a real slope,
        // which an already-rounded field can't express (every neighbour delta
        // would be 0 or 1).
        float[,] field = new float[sizeX, sizeZ];
        // The zone's own base level per column, kept so pass 2's bench gate and
        // the basin floor can be expressed relative to it rather than to y=0.
        float[,] baseLevel = new float[sizeX, sizeZ];
        float unit = org.zoneElevationUnit;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                // Every channel reads the SAME warped coordinate, so the
                // continental swell, the ridges riding on it and the bench
                // bands cut into it all bend together instead of sliding
                // against each other.
                float sx = wx + org.warpAmplitude * warpX.GetNoise2D(wx, wz);
                float sz = wz + org.warpAmplitude * warpZ.GetNoise2D(wx, wz);

                WorldGen.BlendedZoneGen blend = WorldGen.SampleBlendedZoneGen(wx, wz, _genData.ZoneGens);
                float bse = blend.Elevation * unit + org.macroAmplitude * macro.GetNoise2D(sx, sz);

                // Ridged relief in [0,1]: 1 along the noise's zero crossings
                // (which branch like a drainage divide), 0 in the troughs.
                // Sharpening pinches the crests without moving the valleys.
                float ridged = 1f - Math.Abs(relief.GetNoise2D(sx, sz));
                ridged = Mathf.Pow(Math.Clamp(ridged, 0f, 1f), org.reliefSharpness);
                float hills = Mathf.SmoothStep(org.reliefMaskLow, org.reliefMaskHigh, reliefMask.GetNoise2D(sx, sz));
                float amplitude = blend.ElevationRange * unit * org.reliefAmplitudeScale;

                float h = bse + hills * amplitude * ridged
                        + hills * org.roughnessAmplitude * roughness.GetNoise2D(sx, sz);

                // Soft floor: valleys that dip below it flatten out, and the
                // softness keeps the join a curve rather than a crease.
                h = SoftMax(h, bse + org.basinFloorOffset, org.basinSoftness);

                field[lx, lz] = h;
                baseLevel[lx, lz] = bse;
            }
        }

        // Pass 1.5 — cut the drainage network into the field, before anything
        // reads slope. Valleys carved here steepen their own flanks, so the
        // bench pass below sees them and walls the larger ones.
        CarveDrainage(field, org);

        // Pass 2 — faults, benching, authored overrides, integerize.
        int[,] height = new int[sizeX, sizeZ];
        // Per-column ceiling on how tall a wall may be HERE. The wall-maker
        // passes only propose a rise; what the player actually meets is that
        // rise plus whatever the surrounding relief was already doing, which is
        // why controlling the proposal does not control the result. Capping the
        // finished drop does. A drop over its cap is shaved by the relaxation,
        // and the excess becomes another wall further up rather than one taller.
        int[,] wallCap = new int[sizeX, sizeZ];
        float shorelineWidth = Math.Max(1f, org.shorelineChunks * ChunkState.SIZE);
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;
                float h = field[lx, lz];

                // Regional grade, in voxels of rise per column, measured across
                // SLOPE_STENCIL columns. Gating on this is what puts cliffs on
                // flanks and leaves open ground alone — benching everywhere
                // turns a plain into contour rings.
                float slope = Math.Max(
                    Math.Max(Math.Abs(FieldAt(field, lx - SLOPE_STENCIL, lz) - h),
                             Math.Abs(FieldAt(field, lx + SLOPE_STENCIL, lz) - h)),
                    Math.Max(Math.Abs(FieldAt(field, lx, lz - SLOPE_STENCIL) - h),
                             Math.Abs(FieldAt(field, lx, lz + SLOPE_STENCIL) - h))) / SLOPE_STENCIL;

                // Warp recomputed rather than carried from pass 1 (two noise
                // samples beats two more world-sized arrays) so the bedrock
                // bands bend with the landforms they cut.
                float sx = wx + org.warpAmplitude * warpX.GetNoise2D(wx, wz);
                float sz = wz + org.warpAmplitude * warpZ.GetNoise2D(wx, wz);
                // One weight solve feeds both the shared scalars and this
                // approach's own — see SampleBlendedZoneGen's weights-out
                // overload for why the weights come back rather than the caller
                // asking for them separately.
                ZoneGenData[] zones = _genData.ZoneGens;
                int zoneCount = zones != null ? zones.Length : 0;
                Span<float> zoneWeights = zoneCount <= 32 ? stackalloc float[zoneCount] : new float[zoneCount];
                WorldGen.BlendedZoneGen blend = WorldGen.SampleBlendedZoneGen(wx, wz, zones, zoneWeights);
                float benchedFraction = 0f;
                float cliffScaleSum = 0f;
                for (int zi = 0; zi < zoneCount; zi++)
                {
                    if (zoneWeights[zi] <= 0f) { continue; }
                    OrganicZoneTerrainData zt = zones[zi]?.terrain as OrganicZoneTerrainData ?? ZoneDefaults;
                    benchedFraction += zt.benchedFraction * zoneWeights[zi];
                    cliffScaleSum += zt.cliffScale * zoneWeights[zi];
                }
                float cliffScale = Math.Max(0.05f, cliffScaleSum);

                // Faults, applied before benching so a block's interior still
                // terraces normally and only its edges read as scarps. Needs no
                // slope of its own, which is exactly why it is here: it is the
                // one wall-maker that reaches flat and gently graded country.
                if (org.faultRaisedFraction > 0f)
                {
                    // Cell edges re-warped at a much finer scale than the world
                    // warp (same noise, input coords scaled). Without it the
                    // blocks keep the straight edges of the underlying cell
                    // diagram and read as map polygons rather than landforms.
                    float ex = sx + org.faultEdgeWarp * warpX.GetNoise2D(wx * FAULT_EDGE_WARP_SCALE, wz * FAULT_EDGE_WARP_SCALE);
                    float ez = sz + org.faultEdgeWarp * warpZ.GetNoise2D(wx * FAULT_EDGE_WARP_SCALE, wz * FAULT_EDGE_WARP_SCALE);
                    // Breach is a THRESHOLD, not a multiplier: a scarp is either
                    // fully there or fully open. Scaling it continuously tapers
                    // the wall down through the jumpable range on its way to a
                    // pass, which is a wall that isn't one.
                    bool walled = Mathf.SmoothStep(org.faultBreachLow, org.faultBreachHigh,
                        faultBreach.GetNoise2D(sx, sz)) > 0.5f;
                    // UPLIFT only, never negative. A signed throw drops blocks as
                    // readily as it raises them, and a dropped block in low
                    // country simply floods — the coastal plain filled with
                    // inland seas. Raising can only ever add land.
                    //
                    // A raised block steps up by ONE drawn wall height, so the
                    // scarp around it is exactly that height where it meets
                    // unraised ground, and the difference of two draws where it
                    // meets another raised block. Both stay inside the band.
                    float uplift = 0.5f + 0.5f * faultBlock.GetNoise2D(ex, ez);
                    float raised = Math.Clamp(org.faultRaisedFraction, 0.05f, 0.95f);
                    if (walled && uplift >= raised)
                    {
                        h += PickWallHeight((uplift - raised) / (1f - raised), cliffScale, org);
                    }
                }

                // Bedrock patches, thresholded so the zone's benchedFraction
                // sets what share of its sloping ground terraces. Near-binary
                // by design (see benchMaskEdge).
                float center = Mathf.Lerp(org.benchMaskCenterRange, -org.benchMaskCenterRange,
                    Math.Clamp(benchedFraction, 0f, 1f));
                float bedrock = Mathf.SmoothStep(center - org.benchMaskEdge, center + org.benchMaskEdge,
                    benchMask.GetNoise2D(sx, sz));
                float steep = Mathf.SmoothStep(org.benchSlopeLow, org.benchSlopeHigh, slope);
                float strength = org.benchMaxStrength * bedrock * steep;
                // Past the walkable grade the bedrock mask stops getting a
                // vote: an open slope that steep plays badly wherever it is, so
                // it terraces into bench-and-cliff regardless of region. The
                // relaxation below enforces the same limit on whatever survives
                // this; doing it here as well means the excess becomes a proper
                // cliff rather than being shaved flat.
                if (slope > org.maxWalkableGrade)
                {
                    strength = org.benchMaxStrength;
                }
                // Thresholded, never blended. A PARTIAL bench is the worst of
                // both worlds: lerping toward the terraced field scales the
                // riser down with it, so ground at half strength comes out
                // covered in 1-2 voxel bumps — too small to be walls, too
                // frequent to be slope, and exactly the lumpiness that reads as
                // noise underfoot. A bench is either built at full height or not
                // built at all.
                strength = strength > 0.5f ? org.benchMaxStrength : 0f;
                if (strength > 0f)
                {
                    // Floored at the wall threshold for the same reason the
                    // fault throw is: a bench whose riser cannot clear it is
                    // erased by the relaxation, so the terracing silently
                    // vanishes and the slope it was meant to break up survives.
                    // The bench step IS the riser height, so it draws from the
                    // same wall distribution the fault scarps do.
                    float step = PickWallHeight(0.5f + 0.5f * benchStep.GetNoise2D(sx, sz),
                        cliffScale, org);
                    // Phase is added going in and NOT taken back out: the bench
                    // top stays a flat multiple of the step, while the boundary
                    // between bands follows a contour of (h + phase) rather than
                    // of h — which is what stops a dome from banding into
                    // concentric rings.
                    float phase = org.benchPhaseAmplitude * benchPhase.GetNoise2D(sx, sz);
                    h = Mathf.Lerp(h, Bench(h + phase, Math.Max(1f, step), org.benchRiserFraction), strength);
                }

                // Authored flat clearings (a FlattenSurface zone, e.g. the
                // village) pull the surface to their fixed level by weight, so
                // the core is dead flat and the edge melts back into terrain.
                if (blend.FlattenWeight > 0f)
                {
                    h = h * (1f - blend.FlattenWeight) + blend.FlattenLevel * unit;
                }

                // East-edge ocean falloff, applied last so the coast descends
                // smoothly through whatever shape the inland passes produced.
                float coastT = Mathf.SmoothStep(0f, 1f,
                    Math.Clamp((worldMaxX - wx) / shorelineWidth, 0f, 1f));
                h = Mathf.Lerp(-org.oceanDepth, h, coastT);

                height[lx, lz] = WorldGen.WATER_LEVEL + Mathf.RoundToInt(h);
                // Sampled at WALL_CAP_SCALE times the bench-step channel's own
                // frequency: the cap must vary over a distance comparable to a
                // single landform, not a whole region. At region scale one draw
                // decides the ceiling for everything in it, and a low draw planes
                // the entire area flat instead of shortening one wall.
                wallCap[lx, lz] = Mathf.RoundToInt(PickWallHeight(
                    0.5f + 0.5f * benchStep.GetNoise2D(sx * WALL_CAP_SCALE, sz * WALL_CAP_SCALE),
                    cliffScale, org));
            }
        }

        RelaxTalus(height, wallCap, org, GradeRun(org));

        // Plateau doubles as the "is this column flat" reference in the legacy
        // path (flat <=> Height == Plateau, i.e. not on a painted ramp). There
        // is no such thing here — slope is everywhere and legitimate — so it
        // mirrors Height, which makes IsFlatDryGrassAt a pure above-water test
        // and lets scatter cover hillsides. Spawns that genuinely need level
        // ground already opt into IsFlatTerrainAt's 8-neighbour equality test.
        var plateau = (int[,])height.Clone();
        var surface = (int[,])height.Clone();
        var noSpawn = new bool[sizeX, sizeZ];
        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height, surface, noSpawn,
            org.interiorLevelStep);
    }

    // One random level per cell, constant inside it and discontinuous at its
    // edges. Fractal off: octaves would blur the cell edges into a gradient,
    // and the edge is the entire point.
    private static FastNoiseLite MakeCellular(int seed, float frequency)
    {
        var noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
        noise.Seed = seed;
        noise.Frequency = frequency;
        noise.FractalType = FastNoiseLite.FractalTypeEnum.None;
        noise.CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean;
        noise.CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue;
        noise.CellularJitter = 1f;
        return noise;
    }

    // Cut the drainage network into the field: accumulate one unit of rainfall
    // per column downhill, then lower each column in proportion to the flow
    // crossing it. Shape comes from the terrain rather than a noise field,
    // which is why the valleys branch and meet the way real ones do.
    //
    // Single-flow-direction (steepest of 8) over columns visited in descending
    // height order — that order is what makes one pass sufficient, since every
    // contributor to a column is processed before the column itself. There is
    // no depression filling: flow entering a local minimum stops there, which
    // costs some accumulation in pitted ground and saves a priority flood.
    private static void CarveDrainage(float[,] field, OrganicTerrainData org)
    {
        if (org.drainageCarveDepth <= 0f)
        {
            return;
        }
        int sizeX = field.GetLength(0);
        int sizeZ = field.GetLength(1);
        int count = sizeX * sizeZ;

        var order = new int[count];
        var keys = new float[count];
        for (int i = 0; i < count; i++)
        {
            order[i] = i;
            keys[i] = -field[i / sizeZ, i % sizeZ];
        }
        Array.Sort(keys, order);

        var flow = new float[count];
        Array.Fill(flow, 1f);
        for (int i = 0; i < count; i++)
        {
            int idx = order[i];
            int lx = idx / sizeZ;
            int lz = idx % sizeZ;
            float h = field[lx, lz];

            int bestIdx = -1;
            float bestHeight = h;
            for (int dx = -1; dx <= 1; dx++)
            {
                int nx = lx + dx;
                if (nx < 0 || nx >= sizeX) { continue; }
                for (int dz = -1; dz <= 1; dz++)
                {
                    int nz = lz + dz;
                    if ((dx == 0 && dz == 0) || nz < 0 || nz >= sizeZ) { continue; }
                    float nh = field[nx, nz];
                    if (nh < bestHeight)
                    {
                        bestHeight = nh;
                        bestIdx = nx * sizeZ + nz;
                    }
                }
            }
            if (bestIdx >= 0)
            {
                flow[bestIdx] += flow[idx];
            }
        }

        // Incision is proportional to flow AND to local grade — the stream-power
        // form. The grade term is not a refinement, it is what keeps the pass
        // honest: flow accumulates enormously across a wide flat basin, so a
        // flow-only rule lowers the entire basin by the full carve depth and
        // (near sea level) drowns it. Water cuts where it runs fast; on the flat
        // it deposits instead.
        //
        // Logarithmic in flow, because flow is heavy-tailed: a linear map would
        // put the whole visible effect into the few trunk channels and leave
        // every tributary uncut.
        float logReference = Mathf.Log(Math.Max(2f, org.drainageFlowReference));
        float slopeReference = Math.Max(0.001f, org.drainageSlopeReference);
        var carved = new float[sizeX, sizeZ];
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                float h = field[lx, lz];
                float grade = Math.Max(
                    Math.Max(Math.Abs(FieldAt(field, lx - SLOPE_STENCIL, lz) - h),
                             Math.Abs(FieldAt(field, lx + SLOPE_STENCIL, lz) - h)),
                    Math.Max(Math.Abs(FieldAt(field, lx, lz - SLOPE_STENCIL) - h),
                             Math.Abs(FieldAt(field, lx, lz + SLOPE_STENCIL) - h))) / SLOPE_STENCIL;

                float power = Math.Clamp(Mathf.Log(flow[lx * sizeZ + lz]) / logReference, 0f, 1f)
                            * Math.Clamp(grade / slopeReference, 0f, 1f);
                carved[lx, lz] = h - org.drainageCarveDepth * power;
            }
        }
        Array.Copy(carved, field, carved.Length);
    }

    // The height of one wall, drawn from a 0..1 sample. Every wall in the world
    // — bench riser and fault scarp alike — comes from here, so the world has a
    // single wall-height distribution rather than one per system.
    //
    // Shaped by WallHeightFalloff so short walls dominate: the exponent bends an
    // even sample toward CliffMinDrop, and everything within half a voxel of the
    // floor rounds onto it, which is where the pile-up at the minimum comes from.
    // The zone's cliffScale divides the exponent, so a mountain leans toward the
    // tall end WITHOUT leaving the band — scaling the height directly would.
    private static float PickWallHeight(float t, float cliffScale, OrganicTerrainData org)
    {
        float shaped = Mathf.Pow(Math.Clamp(t, 0f, 1f),
            Math.Max(0.1f, org.wallHeightFalloff) / Math.Max(0.05f, cliffScale));
        return Mathf.Lerp(org.cliffMinDrop, Math.Max(org.cliffMinDrop, org.cliffMaxDrop), shaped);
    }

    // Clamped read — edge columns compare against themselves, so the slope at
    // the world border reads as flat rather than sampling out of bounds.
    private static float FieldAt(float[,] field, int lx, int lz)
    {
        lx = Math.Clamp(lx, 0, field.GetLength(0) - 1);
        lz = Math.Clamp(lz, 0, field.GetLength(1) - 1);
        return field[lx, lz];
    }

    // Smooth maximum: `a` where it clears `b` by more than k, `b` where it
    // falls short by more than k, a quadratic blend across the middle. Used as
    // a floor that terrain settles onto instead of clipping against.
    private static float SoftMax(float a, float b, float k)
    {
        float d = a - b;
        if (d >= k) { return a; }
        if (d <= -k) { return b; }
        float t = (d + k) / (2f * k);
        return b + k * t * t;
    }

    // Strata profile for one band: dead-flat bench across the lower
    // (1 - riserFraction) of the band, then the whole step's worth of rise
    // compressed into the remainder. Compression is the point — it multiplies
    // the local slope by 1/riserFraction, which is what turns a gentle noise
    // gradient into a wall the terrain mesher will render crisp. A fraction of
    // 1 is a plain linear ramp (no benching at all).
    private static float Bench(float h, float step, float riserFraction)
    {
        float f = h / step;
        float band = Mathf.Floor(f);
        float r = f - band;
        float rise = Math.Clamp((r - (1f - riserFraction)) / riserFraction, 0f, 1f);
        return (band + Mathf.SmoothStep(0f, 1f, rise)) * step;
    }

    // Enforce the walkability invariant: an adjacent pair is either a grade
    // (<= maxStep apart) or a wall (>= cliffMin apart). Anything between is
    // shaved down to a grade, which is also what leaves scree at the foot of
    // steep ground. True cliffs are never touched — they are the walls the
    // world is supposed to have.
    //
    // Strictly monotone: a column is only ever LOWERED, never raised. The
    // volume-conserving version (drop one, raise the neighbour) oscillates
    // across any uniformly steep face — every column sees itself as the high
    // side, and the field ends up striped one voxel up, one down. Losing a
    // little material is much cheaper than losing stability.
    // Horizontal run over which the sustained-grade cap is measured: the
    // shortest run that can carry one voxel of rise without exceeding the cap.
    // At 0.45 that is 3 columns, since 1-in-2 would be a 50% grade.
    private static int GradeRun(OrganicTerrainData org)
    {
        float grade = Math.Clamp(org.maxWalkableGrade, 0.05f, 1f);
        return Math.Clamp(Mathf.CeilToInt(1f / grade), 1, 16);
    }

    private static void RelaxTalus(int[,] height, int[,] wallCap, OrganicTerrainData org, int gradeRun)
    {
        int sizeX = height.GetLength(0);
        int sizeZ = height.GetLength(1);
        int maxStep = org.maxWalkableStep;
        // A cliff has to clear a grade by more than the shave itself, or the
        // loop would have nothing it could legally resolve to.
        int cliffMin = Math.Max(org.cliffMinDrop, maxStep + 2);
        // Rise permitted across the whole run. Anything above it is a sustained
        // slope steeper than the cap and gets shaved, which leaves the steep
        // ground to the bench pass to express as cliffs instead.
        int gradeRise = Math.Max(1, (int)(gradeRun * org.maxWalkableGrade));
        int cliffMax = Math.Max(cliffMin, org.cliffMaxDrop);
        int passes = org.talusPasses;

        for (int pass = 0; pass < passes; pass++)
        {
            bool changed = false;
            // Alternate sweep direction so a face isn't always relaxed from the
            // same end (which biases material downhill along one axis).
            bool forward = (pass & 1) == 0;
            for (int i = 0; i < sizeX; i++)
            {
                int lx = forward ? i : sizeX - 1 - i;
                for (int j = 0; j < sizeZ; j++)
                {
                    int lz = forward ? j : sizeZ - 1 - j;
                    int h = height[lx, lz];

                    // Lowest neighbour this column is AMBIGUOUSLY above —
                    // tested per neighbour, not against the single lowest one.
                    // A column standing on a cliff shoulder has one neighbour a
                    // long way down (fine, that's the wall) and another two
                    // voxels down (not fine); judging by the lowest alone would
                    // see the cliff, call the column resolved, and leave the
                    // shoulder as a hard 2-voxel stair.
                    int lowAmbiguous = int.MaxValue;
                    Consider(height, lx - 1, lz, h, maxStep, cliffMin, ref lowAmbiguous);
                    Consider(height, lx + 1, lz, h, maxStep, cliffMin, ref lowAmbiguous);
                    Consider(height, lx, lz - 1, h, maxStep, cliffMin, ref lowAmbiguous);
                    Consider(height, lx, lz + 1, h, maxStep, cliffMin, ref lowAmbiguous);

                    int target = lowAmbiguous == int.MaxValue ? int.MaxValue : lowAmbiguous + maxStep;

                    // Sustained-grade cap, measured across gradeRun columns. The
                    // per-pair test above cannot see this case: a hillside
                    // climbing one voxel every column satisfies it at every pair
                    // while being a 45-degree ramp end to end.
                    int lowRun = int.MaxValue;
                    Consider(height, lx - gradeRun, lz, h, gradeRise, cliffMin, ref lowRun);
                    Consider(height, lx + gradeRun, lz, h, gradeRise, cliffMin, ref lowRun);
                    Consider(height, lx, lz - gradeRun, h, gradeRise, cliffMin, ref lowRun);
                    Consider(height, lx, lz + gradeRun, h, gradeRise, cliffMin, ref lowRun);
                    if (lowRun != int.MaxValue)
                    {
                        target = Math.Min(target, lowRun + gradeRise);
                    }

                    // Wall ceiling. Two wall-makers landing on one column (a
                    // bench riser on a fault edge) stack into a face taller than
                    // anything either would build alone, so the cap is enforced
                    // here as well as at the source. Lowering turns the excess
                    // into ground above the wall rather than a taller wall.
                    int localMax = Math.Clamp(wallCap[lx, lz], cliffMin, cliffMax);
                    int tallest = int.MaxValue;
                    ConsiderTall(height, lx - 1, lz, h, localMax, ref tallest);
                    ConsiderTall(height, lx + 1, lz, h, localMax, ref tallest);
                    ConsiderTall(height, lx, lz - 1, h, localMax, ref tallest);
                    ConsiderTall(height, lx, lz + 1, h, localMax, ref tallest);
                    if (tallest != int.MaxValue)
                    {
                        target = Math.Min(target, tallest + localMax);
                    }

                    if (target == int.MaxValue || target >= h)
                    {
                        continue;
                    }
                    height[lx, lz] = target;
                    changed = true;
                }
            }
            if (!changed)
            {
                break;
            }
        }
    }

    // Track the lowest neighbour this column stands MORE than cliffMax above —
    // a wall taller than the band allows.
    private static void ConsiderTall(int[,] height, int lx, int lz, int h, int cliffMax, ref int tallest)
    {
        if (lx < 0 || lx >= height.GetLength(0) || lz < 0 || lz >= height.GetLength(1))
        {
            return;
        }
        int n = height[lx, lz];
        if (h - n > cliffMax && n < tallest)
        {
            tallest = n;
        }
    }

    // Track the lowest neighbour that sits in the ambiguous band below `h`
    // (more than a grade, less than a wall). Out-of-bounds neighbours are
    // skipped, so a world-edge column never reads as standing above a void.
    private static void Consider(int[,] height, int lx, int lz, int h, int maxStep, int cliffMin, ref int lowAmbiguous)
    {
        if (lx < 0 || lx >= height.GetLength(0) || lz < 0 || lz >= height.GetLength(1))
        {
            return;
        }
        int n = height[lx, lz];
        int drop = h - n;
        if (drop > maxStep && drop < cliffMin && n < lowAmbiguous)
        {
            lowAmbiguous = n;
        }
    }
}
