using System;
using System.Collections.Generic;
using Godot;

// THE PASSES A FINISHED WORLD DERIVES FROM ITS OWN VOXELS — the last thing both
// world producers do, and the same list for both.
//
// Two things build a WorldState: WorldGen, and the world-map painter's bake
// (WorldMapState.BuildWorld). Several of the channels the .hike format
// SERIALIZES are neither authored nor generated but derived at the end from the
// finished geometry — the surface shape channel, the detail scatter, the climb
// crust, fog density, interiorness, the environment-class grid, water currents,
// and the waterfall entities.
//
// They used to live on WorldGen as private passes, so the painter reached into
// the generator for the four it knew about and simply never ran the rest. A
// painted world therefore shipped with FogDensity, EnvTag, Interiorness and the
// current subgrid all zero, and nothing recomputed them: Main relights a loaded
// .hike but does not reclassify it. The result was a world with no fog anywhere,
// every cell reading Outdoor (so no interior ambience inside a stamped building
// or a carved tunnel), and no water currents. Nothing failed; the channels were
// simply blank.
//
// So the list lives here, Finish runs all of it, and both producers call Finish.
// A pass added to the list reaches both kinds of world the day it is added.
//
// Everything here reads the WORLD. Where a pass genuinely needs something only
// one producer has — the terrain approach's river-flow field, the generator's
// zone-placement kernel — it takes it as an argument that may be null, rather
// than reaching for state a painted world never produces.
public static class WorldFinish
{
    // What differs between the two producers. Everything optional is null/false
    // for a painted world.
    public sealed class Options
    {
        // XZ extent to re-derive shapes and scatter detail over. Null means the
        // whole world.
        public int? MinX, MaxX, MinZ, MaxZ;

        // Largest step that still counts as a walkable grade rather than a wall.
        public int MaxGradeStep = 1;

        // Columns whose surface is a deliberate bare tread — worldgen's road
        // treads, the painter's paving. Null means none.
        public Func<int, int, bool> SkipDetailColumn;

        // Skip the detail scatter entirely (WorldGen's SKIP_DETAILS).
        public bool SkipDetail;

        // The terrain the fog pools over. REQUIRED: fog is measured against the
        // GROUND, not the top of the world, so a stamped building cannot raise
        // the fog ceiling over the village it stands in.
        public Func<int, int, int> GroundYAt;

        // The generator's zone-weight kernel, for the detail scatter's
        // dominant-kit pick. Null in a painted world, which assigns kits per
        // column deterministically and has no kernel to take an argmax of.
        public ZoneField Zones;

        // Authored environment classes from stamped subscenes. Invoked between
        // classification and the dust / wind bakes, because an authored class
        // must beat the inferred one BEFORE anything reads it. Null when there
        // are none.
        public Action ApplyAuthoredEnvOverrides;

        // How each chunk's wind subgrid is seeded. Null means WindGen's own
        // derivation; the painter passes its per-chunk painted field instead.
        public Action<WorldState> StampWind;

        // The water block a column was PAINTED with, or -1 for none. Null in a
        // generated world, which has no per-column authoring — the same seam
        // shape MossCoverageAt uses, and for the same reason: the pass is
        // shared, the per-column answer is not.
        public Func<int, int, int> PaintedWaterBlockAt;

        // How mossy each column's ground and cave rock are, 0..1. Null skips the
        // moss pass. Worldgen answers from ZoneGenData through its zone kernel
        // (moss density is a property of the biome it is generating); the map
        // painter answers from the kits its ground layer paints (there, moss is
        // a property of the material the author put down). Null skips the pass.
        public Func<int, int, (float surface, float cave)> MossCoverageAt;

        // The terrain approach's per-column river flow. Null gives the ambient
        // drift only, which is the honest answer for a painted world: it has no
        // flow field to stamp.
        public HeightMap? RiverFlow;

    }

    // Ordered, and each step feeds the next:
    //
    //   grade      — surface shape re-derived from the finished voxels, so a
    //                slope meshes as a plane rather than as 1 m risers.
    //   detail     — the sprite scatter, AFTER every ground-moving pass: those
    //                overwrite the per-voxel channels wholesale, which is what
    //                used to leave a stamped building's margin bald.
    //   moss       — the moss overlay, per the caller's per-column coverage.
    //                After the climb crust and the roads (both of which each
    //                producer lays before calling this), because it leaves any
    //                voxel that already carries an overlay alone.
    //   watertypes — which water block each body is made of, from the painter's
    //                per-column layer. Reads the finished voxels, so it also
    //                reaches water a carve or a stamped scene left behind.
    //   roofs      — non-voxel cover, so a roofed room reads as enclosed exactly
    //                as a cave does. Canopy is deliberately absent: a tree
    //                should not make a cell an interior.
    //   sky        — geometry-only VERTICAL cover, for the rain / shelter
    //                consumers, and the seed interiorness floods from.
    //   classify   — cover to space class, per env cell.
    //   wind       — from interiorness and the cell's space class.
    //   fog        — humidity poured over the ground; open-to-sky air only.
    //   currents   — ambient drift, then river flow over the columns carrying it.
    //   waterfalls — cascades in the finished water, as entities.
    //
    // The SUNLIGHT flood is deliberately NOT here, and it is the pass that has
    // to come last: it reads the fog this list bakes, and it must see the canopy
    // — which only FoliageStamper knows, and that needs the main thread
    // (PackedScene.Instantiate) while both producers run Finish off one. So both
    // producers close with the same move on the main thread once Finish returns:
    // stamp the occluders, then LightEngine.Relight, then persist. Running it
    // inside Finish computed a fog-free, canopy-free field that was thrown away
    // and recomputed moments later.
    //
    // The climb crust is NOT in this list: worldgen paints it by zone coverage
    // and the painter by an authored route flag, so each calls
    // StampClimbSurfaces itself with its own per-column answer. It is shared
    // code, not a shared decision.
    public static void Finish(WorldState ws, WorldFinishData finish, Options opt)
    {
        int minX = opt.MinX ?? ws.Min.X * ChunkState.SIZE;
        int maxX = opt.MaxX ?? ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int minZ = opt.MinZ ?? ws.Min.Z * ChunkState.SIZE;
        int maxZ = opt.MaxZ ?? ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        TerrainMath.StampGradeShapes(ws, minX, maxX, minZ, maxZ, opt.MaxGradeStep);

        if (!opt.SkipDetail)
        {
            StampDetailScatter(ws, finish, opt.SkipDetailColumn, opt.Zones);
        }

        StampMossPatches(ws, finish, opt.MossCoverageAt);
        StampWaterTypes(ws, opt.PaintedWaterBlockAt);

        StampRoofSunOcclusion(ws);
        LightEngine.ComputeSkyExposure(ws);
        InteriornessGen.Compute(ws);
        EnvTagGen.ComputeEnvTagGrid(ws);
        opt.ApplyAuthoredEnvOverrides?.Invoke();
        if (opt.StampWind != null)
        {
            opt.StampWind(ws);
        }
        else
        {
            WindGen.ComputeWindGrid(ws);
        }

        GenerateFog(ws, finish, opt.GroundYAt);

        GenerateAmbientWaterCurrents(ws);
        if (opt.RiverFlow.HasValue)
        {
            StampRiverCurrents(ws, opt.RiverFlow.Value);
        }

        PlaceWaterfalls(ws);
    }

    // Chunk-XZ to stamped ZoneIndex, resolved once. Both producers write
    // ChunkState.ZoneIndex, so reading it back is the one zone lookup that works
    // for a generated world and a painted one alike — and it costs one grid
    // build rather than a chunk-dictionary probe per kernel cell per column.
    private sealed class ChunkZoneGrid
    {
        private readonly byte[] _grid;
        private readonly int _minCX, _minCZ, _sizeX, _sizeZ;

        public ChunkZoneGrid(WorldState ws)
        {
            _minCX = ws.Min.X;
            _minCZ = ws.Min.Z;
            _sizeX = ws.Max.X - ws.Min.X + 1;
            _sizeZ = ws.Max.Z - ws.Min.Z + 1;
            _grid = new byte[_sizeX * _sizeZ];
            foreach (ChunkState chunk in ws._chunks.Values)
            {
                int ix = chunk.ChunkCoord.X - _minCX;
                int iz = chunk.ChunkCoord.Z - _minCZ;
                if (ix < 0 || ix >= _sizeX || iz < 0 || iz >= _sizeZ) { continue; }
                _grid[ix * _sizeZ + iz] = chunk.ZoneIndex;
            }
        }

        public byte At(int cx, int cz)
        {
            int ix = Math.Clamp(cx - _minCX, 0, _sizeX - 1);
            int iz = Math.Clamp(cz - _minCZ, 0, _sizeZ - 1);
            return _grid[ix * _sizeZ + iz];
        }

        // The same smoothstep kernel ZoneField uses, over stamped indices.
        public void Weights(int wx, int wz, int zoneCount, Span<float> weights, float blendRadius)
        {
            for (int i = 0; i < zoneCount; i++) { weights[i] = 0f; }
            if (zoneCount <= 0) { return; }

            int chunkX = wx >> CHUNK_SHIFT;
            int chunkZ = wz >> CHUNK_SHIFT;
            int half = Mathf.CeilToInt(blendRadius);
            for (int dx = -half; dx <= half; dx++)
            {
                for (int dz = -half; dz <= half; dz++)
                {
                    int cx = chunkX + dx;
                    int cz = chunkZ + dz;
                    int zoneIdx = At(cx, cz);
                    if (zoneIdx >= zoneCount) { continue; }
                    float dxw = wx - (cx + 0.5f) * ChunkState.SIZE;
                    float dzw = wz - (cz + 0.5f) * ChunkState.SIZE;
                    float distChunks = Mathf.Sqrt(dxw * dxw + dzw * dzw) / ChunkState.SIZE;
                    float w = Mathf.SmoothStep(blendRadius, 0f, distChunks);
                    if (w > 0f) { weights[zoneIdx] += w; }
                }
            }

            float total = 0f;
            for (int i = 0; i < zoneCount; i++) { total += weights[i]; }
            if (total > 1e-6f)
            {
                float inv = 1f / total;
                for (int i = 0; i < zoneCount; i++) { weights[i] *= inv; }
            }
        }
    }

    // ChunkState.SIZE is 16; the shift floors correctly for negative coords,
    // which integer division does not.
    private const int CHUNK_SHIFT = 4;

    // Fog storage cap (byte max), not a tunable.
    public const int FOG_MAX_DENSITY = 255;

    private const int MOSS_PATCH_SEED = 4243;
    private const int MOSS_CAPILLARY_SEED = 4244;
    private const int MOSS_PATCHINESS_SEED = 4245;

    // FastNoiseLite's fractal Perlin does not reach +/-1 — the moss channel
    // spans well under 0..1, so the gain restores the authored coverage's reach
    // over the vein width.
    private const float MOSS_NOISE_GAIN = 2.2f;

    private const int CLIMB_PATCH_SEED = 4246;

    // FastNoiseLite's CellValue is BELL-SHAPED, not uniform. Measured over 576k
    // samples it spans -0.96..0.90, but the middle is only ~0.56 as wide as a
    // uniform field would be, so thresholding it directly at the authored
    // coverage delivered 9% for an authored 25%. Remapping about the median
    // makes the knob mean what it says across the useful range. Re-measure it
    // if the cellular settings change; don't assume it carries over.
    private const float CLIMB_CELL_SPREAD = 0.563f;
    private const int DETAIL_NOISE_SEED = 9191;
    private const int SUBSURFACE_NOISE_SEED = 9192;

    // How far above / below a river's surface its flow is stamped, so a boat on
    // the surface and a swimmer just under it read the same current.
    private const int CURRENT_STAMP_ABOVE = ChunkState.ENV_VOXELS_PER_CELL;
    private const int CURRENT_STAMP_BELOW = ChunkState.ENV_VOXELS_PER_CELL;

    // Walks every surface voxel and stamps the voxel's kit's DefaultDetail
    // wherever the appropriate noise field crosses the kit's threshold. Two
    // noise fields are kept (Surface vs other) so cave/submerged scatter
    // doesn't visually correlate with the surface scatter directly above;
    // the kit's Purpose picks which one. Frequency is per-kit: each noise
    // object is sampled at base frequency 1, with coords pre-scaled by the
    // kit's DetailNoiseFrequency, so kits within a single zone read
    // different noise patterns (sharp transitions where kits change).
    //
    // Two knobs, because the painter's bake runs this same pass over a painted
    // world (see WorldMapState.BuildWorld): `skipColumn` names the columns whose
    // surface is a deliberate tread — worldgen's roads, the painter's paving —
    // and `zones` is null there, since a painted world assigns kits per column
    // deterministically and has no zone-weight kernel to take an argmax of.
    // Everything else — the surface walk, the gates, the noise, the strength
    // ramp — is shared rather than reimplemented per caller.
    public static void StampDetailScatter(WorldState ws, WorldFinishData finish,
        Func<int, int, bool> skipColumn, ZoneField zones)
    {
        var surfaceNoise = new FastNoiseLite();
        surfaceNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        surfaceNoise.Seed = DETAIL_NOISE_SEED;
        surfaceNoise.Frequency = 1f;
        surfaceNoise.FractalOctaves = 2;

        var subsurfaceNoise = new FastNoiseLite();
        subsurfaceNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        subsurfaceNoise.Seed = SUBSURFACE_NOISE_SEED;
        subsurfaceNoise.Frequency = 1f;
        subsurfaceNoise.FractalOctaves = 2;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        // Hoisted so the shell test below doesn't allocate a delegate per voxel.
        Func<int, int, int, int> getVoxel = ws.GetBlockWorld;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // A road tread is bare dirt, and this pass runs after it is laid
                // — so the road is kept clear here rather than by clearing the
                // detail channel again after the fact.
                if (skipColumn != null && skipColumn(wx, wz))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!TerrainMath.IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    // Detail decorates ground, never masonry: IsSurfaceVoxel is
                    // satisfied by the top of a wall, which would run grass and
                    // flowers along the battlements.
                    if (!TerrainMath.IsNaturalGround(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    // IsSurfaceVoxel accepts water above (it's "non-solid"),
                    // which suits the kit-tagging passes but would scatter
                    // upright sprites at the water surface. Caves and test
                    // lakes create new water voxels AFTER TagSubmergedKits
                    // runs — the lake floor still carries SurfaceKit and
                    // would otherwise spawn grass inside the water. Reject
                    // any surface voxel whose air-above slot is water.
                    if (Blocks.IsWater(ws.GetBlockWorld(wx, wy + 1, wz)))
                    {
                        continue;
                    }
                    // The water mesh also dilates a voxel laterally into the
                    // shore and skins that shell cell's top face at the
                    // waterline, so its ground is submerged even though the
                    // slot above is air. Shore kits carry real detail (grass on
                    // shore_swamp, pebbles on shore_sand), so without this the
                    // waterline row sprouts sprites standing in the water.
                    if (WaterMesher.IsCoveredShell(getVoxel, wx, wy, wz))
                    {
                        continue;
                    }

                    // Surface detail follows the DETERMINISTIC dominant zone
                    // (argmax of the smooth zone-weight kernel), NOT this voxel's
                    // stamped kit. Kit borders are assigned by a per-column random
                    // hash (PickWeightedZoneFromHash) so the terrain reads as a
                    // jagged organic transition once the shader blends it — but
                    // detail renders each per-column pick as a discrete sprite, so
                    // that RNG shows up as a salt-and-pepper of off-biome grass
                    // (and the occasional dense off-biome clump where the hash
                    // rolled one way several columns running). Keying detail off
                    // the dominant zone instead snaps its boundary to the blend
                    // midline, so detail tracks the terrain's visual transition
                    // rather than its per-voxel randomness. Cave / submerged
                    // detail keeps the voxel's own kit (less visible, not the
                    // reported problem). The chosen kit's DetailNoise* thresholds
                    // still drive presence/strength below.
                    int voxelTerrainId = ws.GetTerrainIdWorld(wx, wy, wz);
                    bool isSurface = ws.Kits.IsSurfaceKit(voxelTerrainId);
                    TerrainKitData kit = isSurface && zones != null
                        ? (zones.DominantSurfaceKit(wx, wz) ?? ws.Kits.KitAt(voxelTerrainId))
                        : ws.Kits.KitAt(voxelTerrainId);
                    if (kit == null || kit.defaultDetail == null)
                    {
                        continue;
                    }

                    FastNoiseLite noise = isSurface ? surfaceNoise : subsurfaceNoise;
                    float n = noise.GetNoise2D(wx * kit.detailNoiseFrequency, wz * kit.detailNoiseFrequency);
                    if (n <= kit.detailNoiseThreshold)
                    {
                        continue;
                    }

                    // Map noise (threshold..1) to (strengthMin..255). The kit
                    // owns both the threshold and the floor, so a sandstone-
                    // cave kit can thin its pebble scatter without affecting
                    // a sibling kit in the same zone.
                    float t = (n - kit.detailNoiseThreshold) / Math.Max(0.0001f, 1f - kit.detailNoiseThreshold);
                    int strengthMin = kit.detailStrengthMin;
                    int strength = strengthMin + (int)(t * (255 - strengthMin));
                    strength = Mathf.Clamp(strength, 0, 255);
                    if (strength <= 0)
                    {
                        continue;
                    }

                    ws.SetDetailGroupWorld(wx, wy, wz, ws.Kits.DetailSlotOf(kit.defaultDetail));
                    ws.SetDetailStrengthWorld(wx, wy, wz, strength);
                }
            }
        }
    }

    // Dresses tall cliff faces with a climbable overlay, in cellular patches.
    //
    // Height is measured as an unbroken run of exposure on ONE face, walked up
    // the column, rather than from the heightfield: the heightfield knows what a
    // column's top is, not how much of a given side of it stands open, and those
    // differ at every overhang, bench and cave mouth. Runs also cost nothing
    // extra — the walk is already happening.
    //
    // Cellular rather than the vein noise moss uses, because the shapes want to
    // read differently: moss is strands seeping down a face, ivy is colonies
    // that own a patch of it. CellValue gives one random value per cell, so a
    // threshold keeps WHOLE cells and their irregular borders; a distance return
    // would give circular blooms with soft edges instead.
    // The two per-column answers are supplied rather than read, because the
    // world-map painter runs this same pass over a world it built itself: it has
    // no HeightMap and its coverage is a painted scalar, not a zone's. Everything
    // else — the face walk, the run heights, the patch noise, the per-block
    // growth table — is identical for both, and reimplementing it painter-side is
    // how the waterfall shading ended up as two copies that drifted.
    // minWallVoxels is the shortest wall worth dressing, and `patchy` decides
    // whether coverage means "this fraction of the face, in cellular patches" or
    // "dress the whole face". Worldgen wants patches — a zone of cliffs where
    // some are climbable. The painter marks INDIVIDUAL routes, and a route with
    // holes in it is not a route.
    public static void StampClimbSurfaces(WorldState ws, WorldFinishData finish,
        Func<int, int, float> coverageAt, Func<int, int, int> waterYAt,
        int minWallVoxels, bool patchy)
    {
        // Which crust each block grows, flattened to an id-indexed table once —
        // the walk below asks per voxel, and ChunkState.OVERLAY_NONE means "this rock grows
        // nothing", which skips it. Resolving per block rather than per zone is
        // what lets one zone's caves differ from its surface (desert sandstone
        // keeps lichen where the limestone everyone else's caves are cut from
        // takes moss) and what keeps a mantle lip matching the wall under it.
        var growthByBlock = new byte[BlockCatalog.MAX_BLOCKS];
        bool anyGrowth = false;
        BlockCatalog catalog = BlockCatalog.Active;
        for (int id = 0; id < growthByBlock.Length; id++)
        {
            BlockSurfaceData growth = catalog.ClimbGrowthFor(id);
            if (growth == null)
            {
                growthByBlock[id] = ChunkState.OVERLAY_NONE;
                continue;
            }
            if (growth.atlasBaseIndex <= 0)
            {
                GD.PushError($"WorldGen: climb growth surface '{growth.surfaceName}' has no atlas layer; add it to voxel_atlas_manifest.tres and rebuild.");
                growthByBlock[id] = ChunkState.OVERLAY_NONE;
                continue;
            }
            growthByBlock[id] = (byte)growth.atlasBaseIndex;
            anyGrowth = true;
        }
        if (!anyGrowth)
        {
            return;
        }
        int minHeight = Mathf.Max(minWallVoxels, 2);
        float yStretch = Mathf.Max(finish.climbVerticalStretch, 0.01f);

        var patchNoise = new FastNoiseLite();
        patchNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Cellular;
        patchNoise.Seed = CLIMB_PATCH_SEED;
        patchNoise.Frequency = finish.climbCellFrequency;
        patchNoise.CellularDistanceFunction = FastNoiseLite.CellularDistanceFunctionEnum.Euclidean;
        patchNoise.CellularReturnType = FastNoiseLite.CellularReturnTypeEnum.CellValue;
        patchNoise.CellularJitter = finish.climbCellJitter;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        long faceRuns = 0, faceVoxels = 0, stamped = 0;
        var runStart = new int[ClimbFaces.Length];

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                float coverage = coverageAt(wx, wz);
                if (coverage <= 0f)
                {
                    continue;
                }

                // Deepest voxel this column may still mark climbable, from its
                // OWN waterline — so a lake or river bounds its walls the same
                // way the sea does.
                int climbLowestY = waterYAt(wx, wz) - finish.climbUnderwaterVoxels;

                for (int f = 0; f < runStart.Length; f++)
                {
                    runStart[f] = int.MinValue;
                }

                // One extra step past the top so a run ending at the world
                // ceiling is flushed by the same branch as any other.
                for (int wy = worldMinY; wy <= worldMaxY + 1; wy++)
                {
                    bool inRange = wy <= worldMaxY;
                    int v = inRange ? ws.GetBlockWorld(wx, wy, wz) : Blocks.AirId;
                    bool wall = inRange && Blocks.IsSolid(v) && v != Blocks.BarrierId;

                    for (int f = 0; f < ClimbFaces.Length; f++)
                    {
                        (int dx, int dz, EVoxelFace face) = ClimbFaces[f];
                        // Air exposes a face. Water does too, but only down to
                        // climbUnderwaterVoxels below the waterline: the rock a
                        // swimmer can reach is climbable, anything drowned deeper
                        // is not. IsEmpty excludes water ("non-solid but it is
                        // content"), so water has to be admitted explicitly.
                        int neighbour = ws.GetBlockWorld(wx + dx, wy, wz + dz);
                        bool exposed = wall
                            && (Blocks.IsEmpty(neighbour)
                                || (Blocks.IsWater(neighbour) && wy > climbLowestY));
                        if (exposed)
                        {
                            if (runStart[f] == int.MinValue)
                            {
                                runStart[f] = wy;
                            }
                            continue;
                        }
                        if (runStart[f] == int.MinValue)
                        {
                            continue;
                        }

                        int start = runStart[f];
                        runStart[f] = int.MinValue;
                        if (wy - start < minHeight)
                        {
                            continue;
                        }
                        faceRuns++;
                        faceVoxels += wy - start;
                        stamped += StampClimbRun(ws, finish, patchy ? patchNoise : null, coverage,
                            growthByBlock, face, wx, wz, start, wy, yStretch);
                    }
                }
            }
        }

        // The denominator is FACE-voxels: a corner column stands in two runs and
        // is counted once per face, while `stamped` counts each voxel once. So
        // the percentage reads a little under the true share of dressed rock.
        GD.Print($"WorldGen: climbable cliffs — {faceRuns} qualifying faces "
            + $"(>= {minHeight} voxels), {stamped} voxels dressed of {faceVoxels} "
            + $"face-voxels ({100.0 * stamped / Math.Max(faceVoxels, 1):0.0}%)");
    }

    // Scatter the moss OVERLAY over exposed rock and ground.
    //
    // Moss is an overlay, not a block: it is a skin over whatever is underneath,
    // so the stone stays stone underfoot and in the sim. Coverage is per zone and
    // split surface/cave, because caves are damp and want much more of it (and
    // the desert wants none, above or below).
    //
    // Unlike the dirt pass this walks every AIR-EXPOSED voxel, not just the ones
    // with air above — cliff faces are the whole point, and a face has air to the
    // side. Whether a given layer is allowed onto a wall is the shader's call via
    // BlockSurfaceData.overlayOnCliffs, so this pass just marks the rock.
    //
    // SHAPE: moss creeps in strands, so the field is a VEIN distance, not a
    // blob threshold. Thresholding noise itself keeps whichever side of the
    // contour is above the cut — a filled region, hence round patches, and no
    // amount of frequency tuning makes it stringy. |noise| instead measures
    // distance from the noise's ZERO SET, a curved sheet through the world
    // whose intersection with the terrain is a meandering line, so a low cut
    // keeps a thin band either side of it. Three things then make that read as
    // growth: a domain warp so the strands crook rather than flow, a second
    // finer network unioned in (min = the union of both bands) for hairlines
    // branching off the trunks, and a long-wavelength coverage modulation so a
    // strand thins and dies along its length.
    // The four horizontal faces a wall can present. Vertical faces are excluded
    // deliberately: a floor or a ceiling is not something the climb affordance
    // attaches to, and dressing them would put ivy on every cliff TOP.
    private static readonly (int dx, int dz, EVoxelFace face)[] ClimbFaces =
    {
        (1, 0, EVoxelFace.PosX),
        (-1, 0, EVoxelFace.NegX),
        (0, 1, EVoxelFace.PosZ),
        (0, -1, EVoxelFace.NegZ),
    };

    // Dresses one exposed run [startY, endY). Returns how many voxels took the
    // overlay.
    private static long StampClimbRun(WorldState ws, WorldFinishData finish, FastNoiseLite patchNoise,
        float coverage, byte[] growthByBlock, EVoxelFace face, int wx, int wz,
        int startY, int endY, float yStretch)
    {
        long stamped = 0;
        for (int wy = startY; wy < endY; wy++)
        {
            // Per voxel, not per run: a run is one face of one column, but a
            // cliff can change block partway up it (a limestone shoulder over
            // sandstone), and the crust has to follow the rock it grows on.
            byte climbOverlay = growthByBlock[
                Mathf.Clamp(ws.GetBlockWorld(wx, wy, wz), 0, growthByBlock.Length - 1)];
            if (climbOverlay == ChunkState.OVERLAY_NONE)
            {
                continue;
            }
            // Leave an authored overlay (a road tread) alone, but let our own
            // pass revisit a voxel — a corner voxel is a face on two sides and
            // has to accumulate both bits.
            int existingOverlay = ws.GetOverlayIdWorld(wx, wy, wz);
            if (existingOverlay != ChunkState.OVERLAY_NONE && existingOverlay != climbOverlay)
            {
                continue;
            }

            // No noise = an authored route: dress every voxel of the face. Even
            // at coverage 1 the cellular test still drops ~a fifth of them, which
            // is right for a hillside and wrong for a line someone drew.
            if (patchNoise != null)
            {
                float value = patchNoise.GetNoise3D(wx, wy * yStretch, wz) * 0.5f + 0.5f;
                if (value >= 0.5f + CLIMB_CELL_SPREAD * (coverage - 0.5f))
                {
                    continue;
                }
            }

            ws.SetOverlayIdWorld(wx, wy, wz, climbOverlay);
            // OR, never assign: the two faces of a corner are found by separate
            // runs, and the second must not erase the first.
            int faces = ws.GetOverlayFacesWorld(wx, wy, wz);
            ws.SetOverlayFacesWorld(wx, wy, wz, faces | (int)face);
            if (existingOverlay != climbOverlay)
            {
                stamped++;
            }
        }
        return stamped;
    }

    // Rasterize roof sun occlusion so the SkyExposure column scan below sees
    // roofs as cover. Foliage is NOT stamped here: its occluders come from
    // PackedScene.Instantiate, a Node API that can't run on the worldgen worker
    // thread, so canopy is stamped later by FoliageStamper on the main thread.
    // That split is also the correct semantics — a tree canopy shouldn't make a
    // cell an interior, only a real ceiling should.
    public static void StampRoofSunOcclusion(WorldState ws)
    {
        foreach (List<EntitySimState> bucket in ws._entities.Values)
        {
            for (int i = 0; i < bucket.Count; i++)
            {
                if (bucket[i] is RoofSimState roof)
                {
                    RoofSunStamper.Stamp(ws, roof);
                }
            }
        }
    }

    // Bucket-fill ground fog. For each zone we compute a "fog level" Y_i
    // by pouring a humidity-scaled volume into the zone's heightmap (sorted
    // floor heights, water-clamped) and finding where it settles. Per voxel
    // the level blends across zones via the prop/kit kernel; density falls
    // off linearly with distance below the level. Only open-to-sky voxels
    // get seeded — caves / tunnels stay fog-free.
    // `groundYAt` is the TERRAIN the fog pools over, not the top of the world:
    // a stamped building must not raise the fog ceiling over the village it
    // stands in. Zone identity comes off the finished chunks rather than the
    // generator's zone placement, so a painted world — whose chunks carry the
    // painted index — fogs by the same rule.
    public static void GenerateFog(WorldState ws, WorldFinishData finish,
        Func<int, int, int> groundYAt)
    {
        int zoneCount = ws.Zones != null ? ws.Zones.Length : 0;
        if (zoneCount == 0)
        {
            return;
        }

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        // Step 1 — gather each zone's column floor heights, water-clamped.
        // Floors below TerrainMath.SEA_LEVEL get pinned there: water surface IS the
        // bucket bottom over submerged columns (we don't fog underwater).
        var zoneGrid = new ChunkZoneGrid(ws);
        var zoneFloors = new List<int>[zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            zoneFloors[i] = new List<int>();
        }
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Clamped: a chunk stamped against a longer zone table (a world
                // re-baked after a zone was removed) would otherwise index off
                // the end of the bucket array and take the whole bake down.
                int zoneIdx = Math.Min(zoneGrid.At(wx >> CHUNK_SHIFT, wz >> CHUNK_SHIFT), zoneCount - 1);
                int h = groundYAt(wx, wz);
                zoneFloors[zoneIdx].Add(Math.Max(h, TerrainMath.SEA_LEVEL));
            }
        }

        // Step 2 — solve bucket-fill per zone. Humidity * volume-per-column
        // gives the total fog volume to pour; SolveBucketFill returns the
        // resulting level Y. Floors are sorted in place (cheap; we only walk
        // the list once after this).
        float[] fogLevelY = new float[zoneCount];
        for (int i = 0; i < zoneCount; i++)
        {
            List<int> floors = zoneFloors[i];
            if (floors.Count == 0)
            {
                fogLevelY[i] = float.NegativeInfinity;
                continue;
            }
            floors.Sort();
            float humidity = 0f;
            WeatherData weather = ws.Zones[i].Data?.weather;
            if (weather != null)
            {
                humidity = weather.humidity;
            }
            float desiredVolume = humidity * finish.fogVolumePerHumidity * floors.Count;
            fogLevelY[i] = SolveBucketFill(floors, desiredVolume);
        }

        // Step 3 — stamp fog density per voxel under the kernel-blended level.
        long fogged = 0;
        Span<float> weights = zoneCount <= 32
            ? stackalloc float[zoneCount]
            : new float[zoneCount];

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Highest non-air voxel: air above it is open-to-sky; air at
                // or below is enclosed (cave / tunnel / under-overhang) and
                // stays fog-free.
                int highestNonAir = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    if (ws.GetBlockWorld(wx, wy, wz) != Blocks.AirId)
                    {
                        highestNonAir = wy;
                        break;
                    }
                }
                int fogStartY = Math.Max(highestNonAir + 1, TerrainMath.SEA_LEVEL + 1);
                if (fogStartY > worldMaxY) { continue; }

                // Per-column blended fog level. Skip when no neighbouring
                // zone offers a level — empty kernel.
                zoneGrid.Weights(wx, wz, zoneCount, weights, finish.zoneGenBlendRadius);
                float blendedLevel = 0f;
                float wSum = 0f;
                for (int i = 0; i < zoneCount; i++)
                {
                    if (weights[i] <= 0f) { continue; }
                    if (float.IsNegativeInfinity(fogLevelY[i])) { continue; }
                    blendedLevel += weights[i] * fogLevelY[i];
                    wSum += weights[i];
                }
                if (wSum < 1e-6f) { continue; }
                blendedLevel /= wSum;

                int ceilingY = (int)Math.Floor(blendedLevel);
                if (ceilingY < fogStartY) { continue; }

                for (int wy = fogStartY; wy <= ceilingY && wy <= worldMaxY; wy++)
                {
                    if (ws.GetBlockWorld(wx, wy, wz) != Blocks.AirId)
                    {
                        continue;
                    }
                    float depth = blendedLevel - wy;
                    int density = (int)Mathf.Clamp(depth * finish.fogDensityPerVoxel, 0f, FOG_MAX_DENSITY);
                    if (density > 0)
                    {
                        ws.SetFogWorld(wx, wy, wz, density);
                        fogged++;
                    }
                }
            }
        }
        GD.Print($"[WorldFinish] fog: {fogged} voxels seeded across {zoneCount} zone(s)");
    }

    // Bucket-fill: given column floor heights sorted ascending and a desired
    // total volume V (in voxel-units of integrated air below the level), find
    // Y such that sum_c max(0, Y - floors[c]) = V. Volume vs Y is non-decreasing
    // piecewise-linear; we walk segments [floors[k], floors[k+1]] (slope k+1
    // since k+1 columns sit below Y in that segment) and solve linearly in
    // the segment that contains the target volume.
    private static float SolveBucketFill(List<int> sortedFloors, float desiredVolume)
    {
        int n = sortedFloors.Count;
        if (n == 0)
        {
            return 0f;
        }
        if (desiredVolume <= 0f)
        {
            return sortedFloors[0];
        }
        float v = 0f;
        for (int k = 0; k < n - 1; k++)
        {
            int slope = k + 1;
            int dh = sortedFloors[k + 1] - sortedFloors[k];
            if (dh <= 0) { continue; }
            float segVol = (float)slope * dh;
            if (v + segVol >= desiredVolume)
            {
                return sortedFloors[k] + (desiredVolume - v) / slope;
            }
            v += segVol;
        }
        // All floors submerged in the bucket — slope is n above floors[n-1].
        return sortedFloors[n - 1] + (desiredVolume - v) / n;
    }

    // The BACKGROUND drift, everywhere: per-cell current perpendicular to the
    // chunk's zone wind direction in XZ — a 90° CCW rotation, so wind (wx, wz)
    // maps to current (-wz, wx). Matches WindGen's per-zone seeding shape: one
    // direction per chunk, stamped uniformly into every cell. Run after voxel
    // carving; cells whose voxels contain no water still get stamped, but the
    // water shader only samples on water surface fragments so unused stamps cost
    // nothing at render time.
    //
    // This is what the SEA gets. Inland water overwrites it from the real
    // drainage direction — see StampRiverCurrents, which must run after this.
    public static void GenerateAmbientWaterCurrents(WorldState ws)
    {
        // MUST stay well below the slowest river, and that is a hard constraint
        // rather than a taste call. This value is stamped into EVERY cell in the
        // world, including the dry ones flanking a channel, and the shader
        // samples water_current_map TRILINEARLY on a 4 m grid while rivers are
        // ~2-11 m wide — so a river fragment's sample is a blend of its own cell
        // with neighbours carrying this. At 0.7 it beat the fastest river the
        // default world produces (0.65, per the "columns carrying a current"
        // log line) and every river read as the ambient's wind-derived
        // direction instead of its own. It only has to keep the open sea's
        // ripple texture from freezing, which needs very little.
        const float Magnitude = 0.08f;
        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }

                    Vector3 zoneDir = Vector3.Zero;
                    if (ws.Zones != null && chunk.ZoneIndex < ws.Zones.Length)
                    {
                        zoneDir = ws.Zones[chunk.ZoneIndex].WindDirection;
                    }
                    float dx = zoneDir.X;
                    float dz = zoneDir.Z;
                    float len = Mathf.Sqrt(dx * dx + dz * dz);
                    if (len < 1e-6f) { continue; }
                    float invLen = 1f / len;
                    float fx = -dz * invLen * Magnitude;
                    float fz = dx * invLen * Magnitude;

                    for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
                    {
                        for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
                        {
                            for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                            {
                                chunk.SetCurrent(sx, sy, sz, fx, fz);
                            }
                        }
                    }
                }
            }
        }
    }

    // Stamp the terrain approach's per-column river flow into the env-cell
    // current subgrid, overwriting the ambient drift wherever inland water runs.
    //
    // Columns are AVERAGED into their cell rather than last-one-wins: an env cell
    // is 4 m across and a channel is 3-8 m wide, so a cell routinely holds both
    // bank and midstream columns, and taking whichever the scan met last makes
    // the current flicker between neighbouring cells down a straight river.
    public static void StampRiverCurrents(WorldState ws, HeightMap heightMap)
    {
        if (heightMap.Current == null || heightMap.Water == null) { return; }
        const int CELL = ChunkState.ENV_VOXELS_PER_CELL;

        var sums = new Dictionary<Vector3I, Vector2>();
        var counts = new Dictionary<Vector3I, int>();
        for (int wx = heightMap.WorldMinX; wx <= heightMap.WorldMaxX; wx++)
        {
            for (int wz = heightMap.WorldMinZ; wz <= heightMap.WorldMaxZ; wz++)
            {
                int waterY = heightMap.GetWaterY(wx, wz);
                if (waterY == HeightMap.NoWater) { continue; }
                Vector2 v = heightMap.GetCurrent(wx, wz);
                if (v == Vector2.Zero) { continue; }

                int cellX = TerrainMath.FloorDiv(wx, CELL);
                int cellZ = TerrainMath.FloorDiv(wz, CELL);
                int loY = TerrainMath.FloorDiv(waterY - CURRENT_STAMP_BELOW, CELL);
                int hiY = TerrainMath.FloorDiv(waterY + CURRENT_STAMP_ABOVE, CELL);
                for (int cellY = loY; cellY <= hiY; cellY++)
                {
                    var key = new Vector3I(cellX, cellY, cellZ);
                    sums.TryGetValue(key, out Vector2 sum);
                    counts.TryGetValue(key, out int n);
                    sums[key] = sum + v;
                    counts[key] = n + 1;
                }
            }
        }

        foreach (KeyValuePair<Vector3I, Vector2> kv in sums)
        {
            Vector2 v = kv.Value / counts[kv.Key];
            ws.SetCurrentAtCell(kv.Key.X, kv.Key.Y, kv.Key.Z, v.X, v.Y);
        }
        GD.Print($"[WorldGen] water currents: {sums.Count} env cells stamped from river flow");
    }

    // Turn the cascades in the finished voxels into entities — where a drop stops
    // being a hole in the water field and becomes something you can see and hear.
    // Reads the WORLD, so worldgen and the map painter's bake share it verbatim
    // (see WaterfallFinder); it only has to run after everything that writes
    // water or ground.
    //
    // The entity is filed at the LIP rather than at the landing: that is where
    // the fall reads from above, and it keeps a cascade in the same chunk as the
    // river that feeds it. A tall one still spans several chunks vertically, so
    // its sheet is drawn while the lip's chunk is resident and not otherwise —
    // acceptable while the load radius is generous, and the same bargain roofs
    // and other tall entities already make.
    public static void PlaceWaterfalls(WorldState ws)
    {
        // Nothing shorter than the smallest authored tier is ever drawn, so a
        // shorter one is not worth an entity either.
        WaterfallData style = ws.SimData?.waterfalls;
        int placed = 0;
        foreach (WaterfallSite site in WaterfallFinder.Find(ws, style?.SmallestDrawnFall() ?? 0f))
        {
            if (site.Lips.Count == 0) { continue; }
            var lips = new WaterfallLip[site.Lips.Count];
            for (int i = 0; i < lips.Length; i++)
            {
                lips[i] = site.Lips[i];
            }
            // Both Y values name a water SURFACE, and a surface sits one voxel
            // above the topmost voxel it caps — the site records voxels.
            ws.AddEntity(new WaterfallSimState(site.Top, site.Top.Y + 1f, site.BottomY + 1f, lips));
            placed++;
        }
        if (placed > 0)
        {
            GD.Print($"[WorldGen] placed {placed} waterfalls");
        }
    }


    // Which water block each body is made of, from the world-map painter's
    // per-column layer.
    //
    // AUTHORED, never derived. A per-zone rule lived here and was removed: it
    // dressed swamp water in scum that the painter did not draw, so the map and
    // the baked world disagreed and the only way to find out was to go and look.
    // The painter's own rule is that a preview reproduces the bake (its spawn
    // dots run the same roll the bake runs), and a zone default could not honour
    // that without the painter reimplementing the rule — which is how the
    // waterfall shading became two copies that drifted. So there is one source:
    // the layer. A generated world passes null and keeps standard water, which
    // is the identity.
    //
    // The block is written down the WHOLE water column, not just the free
    // surface: a block says what this body IS — how far its clarity sits from
    // the zone's, and what floats on it — and a scummy pond is thick all the way
    // down. Only the top face ever draws the film, but the optics belong to the
    // column.
    public static void StampWaterTypes(WorldState ws, Func<int, int, int> paintedAt)
    {
        if (paintedAt == null)
        {
            return;
        }

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        long painted = 0;
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int block = paintedAt(wx, wz);
                if (block < 0)
                {
                    continue;
                }

                // The free surface is where the film would show, and the column
                // below it is the body that film sits on.
                int surfaceY = int.MinValue;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    if (!Blocks.IsWater(ws.GetBlockWorld(wx, wy, wz)))
                    {
                        continue;
                    }
                    int above = ws.GetBlockWorld(wx, wy + 1, wz);
                    if (!Blocks.IsWater(above) && !Blocks.IsSolid(above))
                    {
                        surfaceY = wy;
                        break;
                    }
                }
                if (surfaceY == int.MinValue)
                {
                    continue;
                }

                painted++;
                for (int wy = surfaceY; wy >= worldMinY; wy--)
                {
                    if (!Blocks.IsWater(ws.GetBlockWorld(wx, wy, wz)))
                    {
                        break;
                    }
                    ws.SetBlockWorld(wx, wy, wz, block);
                }
            }
        }

        if (painted > 0)
        {
            GD.Print($"[worldgen] water types: {painted} painted columns");
        }
    }

    // Moss OVERLAY over exposed rock and ground.
    //
    // `coverageAt` is the whole seam: it answers, per column, how much of the
    // open ground and how much of the cave rock this place dresses in moss.
    // Worldgen answers it from ZoneGenData through its zone kernel — moss
    // density is generation tuning, and ZoneGenData is where generation tuning
    // lives. Any other producer answers it however it can; passing null skips
    // the pass entirely.
    //
    // Exactly the shape StampClimbSurfaces already uses, and for the same
    // reason: the pass is shared, the per-column ANSWER is not.
    //
    // Runs after the climb crust and after roads: it skips any voxel that
    // already carries an overlay, so whatever claimed the face first keeps it.
    public static void StampMossPatches(WorldState ws, WorldFinishData finish,
        Func<int, int, (float surface, float cave)> coverageAt)
    {
        if (coverageAt == null)
        {
            return;
        }
        BlockSurfaceData moss = finish.mossSurface;
        if (moss == null)
        {
            return;
        }
        if (moss.atlasBaseIndex <= 0)
        {
            GD.PushError($"WorldGen: moss surface '{moss.surfaceName}' has no atlas layer; add it to voxel_atlas_manifest.tres and rebuild.");
            return;
        }
        byte mossOverlay = (byte)moss.atlasBaseIndex;

        FastNoiseLite trunkNoise = CreateMossVeinNoise(finish, MOSS_PATCH_SEED, finish.mossPatchFrequency);
        FastNoiseLite capillaryNoise = CreateMossVeinNoise(finish, MOSS_CAPILLARY_SEED,
            finish.mossPatchFrequency * Mathf.Max(finish.mossCapillaryFrequencyScale, 1f));
        // Unwarped: this one says how much moss a REGION carries, so it wants
        // to stay smooth — warping it just adds noise no one can read.
        var patchinessNoise = new FastNoiseLite();
        patchinessNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        patchinessNoise.Seed = MOSS_PATCHINESS_SEED;
        patchinessNoise.Frequency = finish.mossPatchinessFrequency;
        patchinessNoise.FractalOctaves = 2;

        float capillaryWidth = Mathf.Max(finish.mossCapillaryWidth, 0.05f);
        float yStretch = Mathf.Max(finish.mossVerticalStretch, 0.01f);

        long surfaceCandidates = 0, surfaceStamped = 0, caveCandidates = 0, caveStamped = 0;

        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                (float surfaceCoverage, float caveCoverage) = coverageAt(wx, wz);
                if (surfaceCoverage <= 0f && caveCoverage <= 0f)
                {
                    continue;
                }

                for (int wy = worldMinY; wy <= worldMaxY; wy++)
                {
                    var v = ws.GetBlockWorld(wx, wy, wz);
                    if (!Blocks.IsSolid(v) || v == Blocks.BarrierId)
                    {
                        continue;
                    }
                    // Leave an authored overlay (a road tread) alone.
                    if (ws.GetOverlayIdWorld(wx, wy, wz) != ChunkState.OVERLAY_NONE)
                    {
                        continue;
                    }
                    if (!TerrainMath.IsAirExposed(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    // Moss grows on rock and soil, never on masonry. Natural
                    // cliff faces qualify (they are Grass / Rock / cave blocks
                    // wearing a stone SIDE surface); a stamped building's Stone
                    // and a paved Cobblestone tread do not.
                    //
                    // This has to be an explicit material test rather than an
                    // ordering trick. The pass used to run before the subscenes
                    // were stamped, so it never SAW a wall — which meant the
                    // rule was "whatever exists yet", and it quietly stopped
                    // holding the moment the pass moved into the shared finish
                    // list (where it must sit, after the climb crust and the
                    // roads whose overlays it defers to).
                    if (!TerrainMath.IsNaturalGround(ws, wx, wy, wz))
                    {
                        continue;
                    }

                    bool isCave = ws.Kits.IsCaveKit(ws.GetTerrainIdWorld(wx, wy, wz));
                    float coverage = isCave ? caveCoverage : surfaceCoverage;
                    if (coverage <= 0f)
                    {
                        continue;
                    }
                    // 3D noise, so a cliff face gets vertical variation instead
                    // of the whole column inheriting one 2D sample. Squashing Y
                    // stretches the strands taller than they are wide, which is
                    // what makes moss run DOWN a wall rather than around it.
                    float sy = wy * yStretch;
                    float trunk = Mathf.Abs(trunkNoise.GetNoise3D(wx, sy, wz)) * MOSS_NOISE_GAIN;
                    float capillary = Mathf.Abs(capillaryNoise.GetNoise3D(wx, sy, wz))
                        * MOSS_NOISE_GAIN / capillaryWidth;
                    float veinDist = Mathf.Min(trunk, capillary);

                    // Mean-preserving swing about 1, so raising patchiness
                    // redistributes coverage instead of adding or removing it.
                    float patch01 = Mathf.Clamp(
                        0.5f + patchinessNoise.GetNoise3D(wx, sy, wz) * MOSS_NOISE_GAIN * 0.5f, 0f, 1f);
                    float localCoverage = coverage * finish.mossStrandWidth
                        * Mathf.Lerp(1f, patch01 * 2f, finish.mossPatchinessAmount);

                    if (isCave) { caveCandidates++; } else { surfaceCandidates++; }
                    if (veinDist < localCoverage)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, mossOverlay);
                        if (isCave) { caveStamped++; } else { surfaceStamped++; }
                    }
                }
            }
        }

        GD.Print($"[WorldFinish] moss: surface {surfaceStamped}/{surfaceCandidates}"
            + $" ({100.0 * surfaceStamped / Math.Max(surfaceCandidates, 1):0.0}%),"
            + $" cave {caveStamped}/{caveCandidates}"
            + $" ({100.0 * caveStamped / Math.Max(caveCandidates, 1):0.0}%).");
    }

    // One strand network. The warp is applied by FastNoiseLite inside GetNoise,
    // so callers sample world position and get a crooked field for free. BOTH
    // networks warp off the TRUNK wavelength, not their own — a capillary warped
    // at its own finer scale shakes itself into specks.
    private static FastNoiseLite CreateMossVeinNoise(WorldFinishData finish, int seed, float frequency)
    {
        float baseFrequency = Mathf.Max(finish.mossPatchFrequency, 1e-4f);
        var noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        noise.Seed = seed;
        noise.Frequency = frequency;
        noise.FractalOctaves = 2;
        noise.DomainWarpEnabled = finish.mossWarpWavelengths > 0f;
        noise.DomainWarpType = FastNoiseLite.DomainWarpTypeEnum.Simplex;
        noise.DomainWarpAmplitude = finish.mossWarpWavelengths / baseFrequency;
        noise.DomainWarpFrequency = baseFrequency * finish.mossWarpFrequencyScale;
        // One warp application, not FastNoiseLite's default 5-octave progressive
        // one: this pass samples two networks per air-exposed voxel in the world,
        // and the extra octaves buy detail far under a voxel.
        noise.DomainWarpFractalType = FastNoiseLite.DomainWarpFractalTypeEnum.None;
        return noise;
    }
}
