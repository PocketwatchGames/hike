using System;
using Godot;

// The PLATEAU terrain approach: quantize height noise to plateau bands, paint
// ramps back in where a low-frequency gate allows, then carve tunnel slabs at
// the band boundaries and swiss-cheese caves through the rock.
//
// This was the original generator, lifted out of WorldGen.cs unchanged so the
// two approaches stop sharing a file as well as a resource. Its tuning lives on
// PlateauTerrainData; the noise channels below are per-run and belong to this
// object, not to the resource.
//
// KNOWN SHAPE PROBLEMS, kept here rather than in a commit message because they
// are why the other approaches exist: quantization caps cliff height at
// plateauStep, so no wall can be taller than one band; the tunnel slabs carve
// the top of every band across unbounded horizontal extent, which leaves
// one-voxel roofs floating over open air; and cave ceilings snap upward past
// the surface, breaching it as open pits.
public class PlateauTerrainGen : ITerrainGenerator
{
    // Seed salts for this approach's channels. Values must stay distinct from
    // every other salt in the project (WorldGen's shared block and the other
    // approaches') — two channels sharing a salt are the same field, and the
    // correlation shows up as terrain features that suspiciously line up.
    private const int SEED_SALT_TERRAIN = 0x01;
    private const int SEED_SALT_TUNNEL = 0x02;
    private const int SEED_SALT_CAVE = 0x03;
    private const int SEED_SALT_RAMP_GATE = 0x05;
    private const int SEED_SALT_ELEVATION = 0x0A;

    private readonly PlateauTerrainData _data;
    private readonly WorldGenData _genData;

    // This run's zone placement + blend kernel.
    private readonly ZoneField _zones;
    private readonly FastNoiseLite _terrainNoise;
    private readonly FastNoiseLite _elevationNoise;
    private readonly FastNoiseLite _rampGateNoise;
    private readonly FastNoiseLite _tunnelNoise;
    private readonly FastNoiseLite _caveNoise;

    public PlateauTerrainGen(PlateauTerrainData data, WorldGenData genData, int worldSeed,
        ZoneField zones)
    {
        _data = data;
        _genData = genData;
        _zones = zones;
        _terrainNoise = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(worldSeed, SEED_SALT_TERRAIN),
            data.terrainNoiseFrequency, data.terrainNoiseOctaves);
        _elevationNoise = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(worldSeed, SEED_SALT_ELEVATION),
            data.elevationNoiseFrequency, data.elevationNoiseOctaves);
        _rampGateNoise = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(worldSeed, SEED_SALT_RAMP_GATE),
            data.rampGateNoiseFrequency, data.rampGateNoiseOctaves);
        _tunnelNoise = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(worldSeed, SEED_SALT_TUNNEL),
            data.tunnelNoiseFrequency, data.tunnelNoiseOctaves);
        // One cave field spans the world, so its frequency comes from the first
        // zone; CaveThreshold still blends per column, which is what lets zones
        // differ in cave density while sharing the pattern.
        _caveNoise = TerrainMath.MakePerlin(TerrainMath.DeriveSeed(worldSeed, SEED_SALT_CAVE),
            (TerrainMath.FirstZoneGen(genData)?.terrain as PlateauZoneTerrainData)?.caveNoiseFrequency ?? 0.04f, data.caveNoiseOctaves);
    }


    // Defaults for a zone with no PlateauZoneTerrainData. A plain instance
    // rather than a duplicated set of constants, so the fallback can never
    // drift from the authored defaults.
    private static readonly PlateauZoneTerrainData ZoneDefaults = new();

    // Blend one of THIS approach's per-zone scalars. Shares the kernel weights
    // with the approach-agnostic blend, so folding an extra field costs a few
    // multiplies rather than a second weight solve. A zone carrying another
    // approach's terrain resource contributes the defaults instead of dropping
    // out, which would silently skew its neighbours' share.
    private float BlendZoneScalar(int wx, int wz, System.Func<PlateauZoneTerrainData, float> pick)
    {
        ZoneGenData[] zones = _genData.ZoneGens;
        int n = zones != null ? zones.Length : 0;
        if (n == 0) { return pick(ZoneDefaults); }
        Span<float> weights = n <= 32 ? stackalloc float[n] : new float[n];
        _zones.SampleBlended(wx, wz, weights);
        float sum = 0f;
        for (int i = 0; i < n; i++)
        {
            if (weights[i] <= 0f) { continue; }
            sum += pick(zones[i]?.terrain as PlateauZoneTerrainData ?? ZoneDefaults) * weights[i];
        }
        return sum;
    }

    // A voxel is carved when it falls in a tunnel band AND its column reaches
    // the plateau ceiling above that band — without the second test a column
    // ending mid-band gets a tunnel with no roof, which is a 1- or 2-voxel
    // opening the player cannot fit through.
    public bool IsCarvedAt(int wx, int wy, int wz, int columnSolidHeight)
    {
        int step = Math.Max(1, (int)Math.Round(_data.plateauStep));
        int bandTop = (int)Math.Floor((double)wy / step) * step + step;
        return columnSolidHeight >= bandTop && IsTunnelAt(wx, wy, wz);
    }


    public HeightMap BuildHeightMap(WorldState ws)
    {
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        int[,] plateau = new int[sizeX, sizeZ];
        bool[,] rampAnchor = new bool[sizeX, sizeZ];
        int step = Math.Max(1, (int)Math.Round(_data.plateauStep));
        // `|rampGateNoise|` below rampAnchorBand marks the core of a ramp
        // zone; the macro noise adds +/-macroElevationRangePlateaus steps; the
        // far east drops to ocean over shorelineChunks chunks.
        float rampAnchorBand = _data.rampAnchorBand;
        float macroElevationRangePlateaus = _data.macroElevationRangePlateaus;
        float oceanDepthPlateaus = _data.oceanDepthPlateaus;
        float shorelineFalloffWidth = _data.shorelineChunks * ChunkState.SIZE;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                // Step 1: kernel-blend authored center elevation + range
                // across zones. Eventually `Elevation` will be sampled from
                // an authored coarse heightmap; the blended ElevationRange
                // term still rides on top.
                BlendedZoneGen blend = _zones.SampleBlended(wx, wz);

                // Step 2: weighted noise in plateau-step units.
                float terrainN = _terrainNoise.GetNoise2D(wx, wz);
                float macroN = _elevationNoise.GetNoise2D(wx, wz);
                float plateaus = blend.Elevation
                               + blend.ElevationRange * terrainN
                               + macroN * macroElevationRangePlateaus;

                // Step 2.5: flatten override. Where a FlattenSurface zone has
                // weight, pull the (noisy + macro) plateau toward its fixed
                // FlattenLevel — guaranteeing e.g. the village core lands exactly
                // on the beach plateau regardless of the world-wide macro wave,
                // while the partial-weight edge blends back into the terrain.
                if (blend.FlattenWeight > 0f)
                {
                    plateaus = plateaus * (1f - blend.FlattenWeight) + blend.FlattenLevel;
                }

                // Step 3: plateau-step quantization (round to integer
                // plateau count). Done BEFORE the ocean falloff so cliffs
                // inland snap cleanly while the coast still gets a smooth
                // descent. Elevation = 0 is treated as sea level: the world-y
                // offset by TerrainMath.SEA_LEVEL is applied at the end so authored
                // ZoneGenData.Elevation reads naturally — +1 means one
                // plateau step above sea level, -1 means one below.
                int plateauSteps = (int)Mathf.Round(plateaus);

                // Hard floor inside a flatten zone: where it clearly dominates,
                // never let the surface drop below its FlattenPlateau, so the
                // surrounding zone's deep (underwater) columns can't bleed a pond
                // into the village core. The blend already pulls heights toward
                // the target; this just removes residual below-water spikes.
                if (blend.FlattenWeight > 0.5f)
                {
                    int floorLevel = Mathf.RoundToInt(blend.FlattenLevel / blend.FlattenWeight);
                    if (plateauSteps < floorLevel) { plateauSteps = floorLevel; }
                }

                // Step 4: east-edge ocean falloff in plateau-step units.
                // Inland (coastT = 1) → unchanged plateauSteps. Coastal
                // (coastT → 0) → -oceanDepthPlateaus (deep ocean below
                // sea level).
                int distFromEastEdge = worldMaxX - wx;
                float coastT = Mathf.Clamp(distFromEastEdge / shorelineFalloffWidth, 0f, 1f);
                coastT = Mathf.SmoothStep(0f, 1f, coastT);
                int effectivePlateaus = (int)Mathf.Round(
                    Mathf.Lerp(-oceanDepthPlateaus, plateauSteps, coastT));

                // Step 5: convert plateau steps → world voxels with
                // Elevation = 0 anchored at sea level. Sea is at TerrainMath.SEA_LEVEL
                // (= -1 plateau step in voxel units), so a plateau-step value
                // of 0 lands at TerrainMath.SEA_LEVEL and each unit of Elevation /
                // ElevationRange shifts the surface by exactly one plateau
                // step (4 voxels) above or below the water plane.
                plateau[lx, lz] = TerrainMath.SEA_LEVEL + effectivePlateaus * step;
                rampAnchor[lx, lz] = Math.Abs(_rampGateNoise.GetNoise2D(wx, wz)) < rampAnchorBand;
            }
        }

        // Dilate anchor mask by `rampRadius` cells — one full scan-radius's
        // worth on each side of the raw anchor line, so the lift scan has a
        // fully eligible neighbourhood and always produces a complete skirt.
        bool[,] rampEligible = new bool[sizeX, sizeZ];
        int rampRadiusConst = step * _data.rampSlope;
        int dilateRadius = rampRadiusConst;
        for (int lx = 0; lx < sizeX; lx++)
        {
            for (int lz = 0; lz < sizeZ; lz++)
            {
                for (int dx = -dilateRadius; dx <= dilateRadius && !rampEligible[lx, lz]; dx++)
                {
                    int nx = lx + dx;
                    if (nx < 0 || nx >= sizeX)
                    {
                        continue;
                    }
                    for (int dz = -dilateRadius; dz <= dilateRadius; dz++)
                    {
                        int nz = lz + dz;
                        if (nz < 0 || nz >= sizeZ)
                        {
                            continue;
                        }
                        if (rampAnchor[nx, nz])
                        {
                            rampEligible[lx, lz] = true;
                            break;
                        }
                    }
                }
            }
        }

        int[,] height = new int[sizeX, sizeZ];
        // One step of rise takes `step * RampSlope` horizontal cells; that's
        // also the scan radius since anything farther would only contribute
        // a zero (or clamped-away) lift.
        int rampSlope = _data.rampSlope;
        int rampRadius = step * rampSlope;
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                int myPlateau = plateau[lx, lz];
                int best = myPlateau;

                // Spawn-flat columns must stay at y=0. Non-ramp-eligible
                // columns skip the scan too: only cells inside the dilated
                // ramp band can be lifted.
                if (rampEligible[lx, lz])
                {
                    int oneStepUp = myPlateau + step;
                    for (int dx = -rampRadius; dx <= rampRadius; dx++)
                    {
                        int nx = wx + dx;
                        if (nx < worldMinX || nx > worldMaxX)
                        {
                            continue;
                        }
                        for (int dz = -rampRadius; dz <= rampRadius; dz++)
                        {
                            int nz = wz + dz;
                            if (nz < worldMinZ || nz > worldMaxZ)
                            {
                                continue;
                            }
                            if (dx == 0 && dz == 0)
                            {
                                continue;
                            }
                            int neighborPlateau = plateau[nx - worldMinX, nz - worldMinZ];
                            if (neighborPlateau <= myPlateau)
                            {
                                continue;
                            }
                            // Clamp to one step up so a taller plateau farther
                            // out can't out-vote a closer single-step plateau.
                            int target = Math.Min(neighborPlateau, oneStepUp);
                            int dist = Math.Max(Math.Abs(dx), Math.Abs(dz));
                            int verticalDrop = (dist + rampSlope - 1) / rampSlope;
                            int candidate = target - verticalDrop;
                            if (candidate > best)
                            {
                                best = candidate;
                            }
                        }
                    }
                }

                height[lx, lz] = best;
            }
        }

        // Nothing has been carved yet, so the live surface starts equal to the
        // authored height; DeriveSurface re-derives it after the carving passes.
        var surface = (int[,])height.Clone();
        var noSpawn = new bool[sizeX, sizeZ];
        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height, surface, noSpawn, step);
    }


    // Plateau-step tunnels: the top TunnelLayerHeight voxels of every plateau
    // step (the band immediately under each plateau ceiling) are tunnel
    // candidates, gated by 3D tunnel noise. This produces tiered tunnel
    // systems whose ceilings line up with plateau elevations and whose
    // openings show up in cliff faces between adjacent plateau levels.
    private bool IsTunnelAt(int wx, int wy, int wz)
    {
        if (wy <= TerrainMath.SEA_LEVEL)
        {
            return false;
        }
        int step = Math.Max(1, (int)Math.Round(_data.plateauStep));
        int rem = ((wy % step) + step) % step;
        if (rem < step - _data.tunnelLayerHeight)
        {
            return false;
        }
        // Sample at the band's base (rem=0 row) so all voxels in the band
        // share the same noise value — guarantees the band carves all-or-nothing
        // and never leaves sub-3-tall openings. Math.Floor (not C# integer
        // division) so negative wy snaps down, not toward zero.
        int bandBase = (int)Math.Floor((double)wy / step) * step;
        float threshold = BlendZoneScalar(wx, wz, z => z.tunnelThreshold);
        return Mathf.Abs(_tunnelNoise.GetNoise3D(wx, bandBase, wz)) < threshold;
    }


    // Swiss-cheese caves: 3D noise carves blob-shaped holes through solid
    // terrain. Floors follow the noise surface (smooth); ceilings snap up to
    // the next plateau-step boundary so the rem=0 row above each cave stays
    // solid and acts as a flat roof. Caves never breach the surface and are
    // discarded if shorter than CaveMinHeight, guaranteeing walkable paths.
    public System.Collections.Generic.IReadOnlyList<
        System.Collections.Generic.KeyValuePair<string, Vector3>> GetNamedFeatures()
        => Array.Empty<System.Collections.Generic.KeyValuePair<string, Vector3>>();

    public void DumpDiagnostics(string dir) { }

    public bool IsSealedFromWaterAt(int wx, int wy, int wz) => false;

    public void CarveVolumes(WorldState ws)
    {
        int step = Math.Max(1, (int)Math.Round(_data.plateauStep));
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        bool IsNaturallyCarved(int wx, int wy, int wz, float caveThreshold)
        {
            return Math.Abs(_caveNoise.GetNoise3D(wx, wy, wz)) > caveThreshold;
        }

        // Highest solid (non-Air, non-Water) voxel in this column. Anything
        // above is sky; we never want to carve into sky (no craters).
        int FindSurface(int wx, int wz)
        {
            for (int wy = worldMaxY; wy >= worldMinY; wy--)
            {
                var v = ws.GetBlockWorld(wx, wy, wz);
                if (v != Blocks.AirId && v != Blocks.WaterId)
                {
                    return wy;
                }
            }
            return worldMinY - 1;
        }

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int surfaceY = FindSurface(wx, wz);
                if (surfaceY <= worldMinY)
                {
                    continue;
                }

                // No caves under an authored flat clearing (a FlattenSurface
                // zone, e.g. the village). Caves snap their ceiling up to the
                // next plateau step and can breach the surface as an open pit;
                // on a clearing pinned to the water line that pit fills with
                // water, punching ponds into what should be solid dry ground.
                int domZone = _zones.DominantIndex(wx, wz);
                if (domZone >= 0 && _genData.ZoneGens[domZone]?.terrain?.flattenSurface == true)
                {
                    continue;
                }

                // Threshold blends per-column so cave density transitions
                // smoothly across zone borders. Sampled once per column
                // since the kernel is XZ-only.
                float caveThreshold = BlendZoneScalar(wx, wz, z => z.caveThreshold);

                // Walk the column bottom-up finding runs of natural carve.
                // worldMinY is preserved as bedrock, so start one above.
                int wy = worldMinY + 1;
                while (wy <= surfaceY)
                {
                    if (!IsNaturallyCarved(wx, wy, wz, caveThreshold))
                    {
                        wy++;
                        continue;
                    }
                    int runLo = wy;
                    while (wy <= surfaceY && IsNaturallyCarved(wx, wy, wz, caveThreshold))
                    {
                        wy++;
                    }
                    int runHi = wy - 1;

                    // Snap top up to the next plateau-step boundary. If the
                    // snap reaches above surface, that's fine — the cave just
                    // breaches as an open-topped pit.
                    int ceilingY = (int)Math.Floor((double)runHi / step) * step + step;
                    if (ceilingY - runLo < _data.caveMinHeight)
                    {
                        continue;
                    }

                    for (int cy = runLo; cy < ceilingY; cy++)
                    {
                        var fill = cy <= TerrainMath.SEA_LEVEL ? Blocks.WaterId : Blocks.AirId;
                        ws.SetBlockWorld(wx, cy, wz, fill);
                    }

                    // Force Y on the solid voxels bracketing the carved run
                    // (cave ceiling at ceilingY, cave floor at runLo-1), so the
                    // cave surface snaps flat regardless of whether this
                    // column's outdoor height came from the ramp branch of the
                    // height function. Cave interior geometry is its own ruleset.
                    ws.SetShapeWorld(wx, ceilingY, wz, SharpAxes.Y);
                    if (runLo - 1 >= worldMinY)
                    {
                        ws.SetShapeWorld(wx, runLo - 1, wz, SharpAxes.Y);
                    }
                }
            }
        }
    }
}
