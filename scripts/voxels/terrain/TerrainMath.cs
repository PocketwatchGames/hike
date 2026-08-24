using System;
using System.Collections.Generic;
using Godot;

// Pure world-shape math, shared by everything that builds or finishes a world:
// WorldGen itself, the ITerrainGenerator approaches, and the world-map painter's
// bake. Nothing here holds state or reads any — every answer is a function of
// its arguments.
//
// It exists because these helpers used to live on WorldGen, which made the
// painter and the terrain plugins reach into the generator for arithmetic, and
// made it easy for a "shared" helper to quietly grow a read of the generator's
// per-run statics (which is exactly what happened). A helper that belongs here
// is one with no run behind it; anything that needs to know where this world's
// zones are belongs on ZoneField instead.
public static class TerrainMath
{
    // The nominal waterline a GENERATED world is built around: terrain noise is
    // allowed to dip below it and the chunk fill floods what it leaves. 0 so
    // height numbers in the editor and the debug dumps read directly as "voxels
    // above sea level"; land starts at y = 1.
    //
    // It is NOT a claim that water stands below this level. A painted world puts
    // water wherever it was painted, a carve can open a dry cavern beneath it,
    // and nothing outside generation may assume a column below it is wet — ask
    // WaterSurface, which reads the voxels.
    public const int SEA_LEVEL = 0;

    // The waterline AT ONE COLUMN: the global sea, or the inland river / lake
    // surface a terrain approach put there, whichever is higher. Every
    // generation pass that would otherwise compare against SEA_LEVEL directly
    // goes through this — chunk fill, the shore-kit bands, the dry-land tests
    // and road passability — so inland water above sea level is expressible at
    // all. Approaches that make no inland water leave HeightMap.Water null and
    // this collapses back to the constant.
    public static int WaterYAt(HeightMap heightMap, int wx, int wz)
    {
        return Math.Max(SEA_LEVEL, heightMap.GetWaterY(wx, wz));
    }

    // Stable, process-independent mix of three ints. System.HashCode.Combine
    // seeds itself with a process-random salt, so it would re-randomize
    // worldgen on every launch — use this anywhere worldgen needs a
    // deterministic seed.
    public static int StableMix(int a, int b, int c)
    {
        unchecked
        {
            uint h = (uint)a * 0x9E3779B1u;
            h ^= (uint)b * 0x85EBCA77u;
            h ^= (uint)c * 0xC2B2AE3Du;
            h = ((h >> 16) ^ h) * 0x85EBCA6Bu;
            h = ((h >> 13) ^ h) * 0xC2B2AE35u;
            h = (h >> 16) ^ h;
            return (int)h;
        }
    }

    // Mix worldSeed with a per-channel salt to produce a stable sub-seed.
    // Must be deterministic across runs — see StableMix.
    public static int DeriveSeed(int worldSeed, int salt)
    {
        return StableMix(worldSeed, salt, 0);
    }

    // Deterministic per-(wx, wz, salt) hash → [0, 1). Used to make a per-voxel
    // choice without allocating a Random — same coords always produce the same
    // answer, so kit borders (and any other deterministic per-voxel choice)
    // replay identically across runs and across save/load.
    public static float HashFloat01(int wx, int wz, int salt)
    {
        unchecked
        {
            uint h = (uint)wx * 0x9E3779B1u;
            h ^= (uint)wz * 0x85EBCA77u;
            h ^= (uint)salt * 0xC2B2AE3Du;
            h = ((h >> 16) ^ h) * 0x85EBCA6Bu;
            h = ((h >> 13) ^ h) * 0xC2B2AE35u;
            h = (h >> 16) ^ h;
            return (h & 0x00FFFFFF) / (float)0x01000000;
        }
    }

    public static FastNoiseLite MakePerlin(int seed, float frequency, int octaves)
    {
        var noise = new FastNoiseLite();
        noise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        noise.Seed = seed;
        noise.Frequency = frequency;
        noise.FractalOctaves = octaves;
        return noise;
    }

    // Kept so a world whose author never assigned a terrain resource still
    // generates rather than throwing. One instance, since it carries only
    // defaults.
    private static PlateauTerrainData _fallbackTerrain;

    public static TerrainGenData TerrainOf(WorldGenData genData)
    {
        if (genData?.terrain != null)
        {
            return genData.terrain;
        }
        if (_fallbackTerrain == null)
        {
            GD.PushError("[TerrainMath] WorldGenData has no terrain resource — falling back to"
                + " plateau defaults. Assign a TerrainGenData subclass to WorldGenData.terrain.");
            _fallbackTerrain = new PlateauTerrainData();
        }
        return _fallbackTerrain;
    }

    public static ZoneGenData FirstZoneGen(WorldGenData genData)
    {
        if (genData?.ZoneGens == null) { return null; }
        for (int i = 0; i < genData.ZoneGens.Length; i++)
        {
            if (genData.ZoneGens[i] != null) { return genData.ZoneGens[i]; }
        }
        return null;
    }

    // Stamp a chunk into one of the world's named regions. The legacy
    // 4-quadrant split, so default_world_gen.tres can line its Regions[] up
    // with its Zones[] in the same order until the editor produces arbitrary
    // region polygons. Deliberately independent of zone placement — the two are
    // orthogonal subdivisions, not parallel.
    public static byte PickRegionIndex(Vector3I chunkCoord, int regionCount)
    {
        if (regionCount <= 0) { return 0; }
        int quadrant;
        if (chunkCoord.X >= 0 && chunkCoord.Z >= 0) { quadrant = 0; }       // NE
        else if (chunkCoord.X < 0 && chunkCoord.Z >= 0) { quadrant = 1; }   // NW
        else if (chunkCoord.X >= 0 && chunkCoord.Z < 0) { quadrant = 2; }   // SE
        else { quadrant = 3; }                                              // SW
        if (quadrant >= regionCount) { quadrant = regionCount - 1; }
        return (byte)quadrant;
    }

    // The ground level a footprint should seat on: the most common plateau level
    // across it, ties going to the lower. Averaging or taking the max floats a
    // building over a dip; the stamp overwrites its whole bbox, so cutting in is
    // self-correcting and floating is not.
    //
    // Takes the ground lookup rather than a HeightMap so the painter — which has
    // no height field — seats its stamps by the identical rule.
    public static int FootprintPlateauY(Func<int, int, int> plateauAt, int levelStep,
        Vector3I origin, Vector3I size, out int levelCount)
    {
        int step = Math.Max(1, levelStep);
        var counts = new Dictionary<int, int>();
        for (int dx = 0; dx < size.X; dx++)
        {
            for (int dz = 0; dz < size.Z; dz++)
            {
                int raw = plateauAt(origin.X + dx, origin.Z + dz);
                int plateau = (int)Math.Floor((double)raw / step) * step;
                counts.TryGetValue(plateau, out int seen);
                counts[plateau] = seen + 1;
            }
        }

        levelCount = counts.Count;
        int best = 0;
        int bestCount = -1;
        foreach (KeyValuePair<int, int> level in counts)
        {
            if (level.Value > bestCount || (level.Value == bestCount && level.Key < best))
            {
                best = level.Key;
                bestCount = level.Value;
            }
        }
        return best;
    }

    // Floor division that rounds toward negative infinity. C#'s `/` truncates
    // toward zero, which puts world coordinate -1 in the same cell as +1.
    public static int FloorDiv(int a, int b)
    {
        int q = a / b;
        return (a % b != 0 && (a < 0) != (b < 0)) ? q - 1 : q;
    }

    // Solid (non-Barrier) with air or water directly above — the definition of
    // "walkable surface", applying to plateau tops, cave floors and ledges alike.
    public static bool IsSurfaceVoxel(WorldState ws, int wx, int wy, int wz)
    {
        var self = ws.GetBlockWorld(wx, wy, wz);
        if (!Blocks.IsSolid(self) || self == Blocks.BarrierId)
        {
            return false;
        }
        var above = ws.GetBlockWorld(wx, wy + 1, wz);
        return !Blocks.IsSolid(above) || above == Blocks.BarrierId;
    }

    // Natural terrain — the materials worldgen fills ground with. Excludes
    // architecture (Stone/Wood walls) and Barrier so they never read as ground.
    public static bool IsNaturalGround(WorldState ws, int wx, int wy, int wz)
    {
        return Blocks.IsNaturalGround(ws.GetBlockWorld(wx, wy, wz));
    }

    public static bool IsSolidOpaque(WorldState ws, int wx, int wy, int wz)
    {
        var v = ws.GetBlockWorld(wx, wy, wz);
        return Blocks.IsSolid(v) && v != Blocks.BarrierId;
    }

    // Solid voxel with air or water on any of its six sides — the definition of
    // "you can see this face", covering ground tops and cliff faces alike.
    public static bool IsAirExposed(WorldState ws, int wx, int wy, int wz)
    {
        return !IsSolidOpaque(ws, wx + 1, wy, wz)
            || !IsSolidOpaque(ws, wx - 1, wy, wz)
            || !IsSolidOpaque(ws, wx, wy + 1, wz)
            || !IsSolidOpaque(ws, wx, wy - 1, wz)
            || !IsSolidOpaque(ws, wx, wy, wz + 1)
            || !IsSolidOpaque(ws, wx, wy, wz - 1);
    }

    // Re-derive the surface SHAPE channel from the FINISHED voxels: a floor
    // whose neighbours are within maxGradeStep on either axis becomes
    // SharpAxes.None and meshes as a real plane, anything else stays a crisp
    // terrace. Without it a slope draws as flat treads with 1 m vertical
    // risers — it reads as a ramp from above and the player walks into a wall.
    //
    // Bounds-taking rather than HeightMap-taking: the height field was only ever
    // used for horizontal extent here, and everything else is read off the
    // voxels. That is what lets the painter — which has no HeightMap — get the
    // same grades as worldgen instead of a second implementation that drifts.
    public static void StampGradeShapes(WorldState ws, int worldMinX, int worldMaxX,
        int worldMinZ, int worldMaxZ, int maxGradeStep)
    {
        int minY = ws.Min.Y * ChunkState.SIZE;
        int maxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        // Every floor surface in the world (solid voxel with a non-solid voxel
        // directly above), grouped by column: column n owns
        // surfaces[starts[n] .. starts[n + 1]), descending in Y. Columns are
        // walked in a fixed order so each run is contiguous — no per-column
        // allocation, and no per-column cap that would silently drop surfaces in
        // a heavily caved column.
        var surfaces = new List<int>(sizeX * sizeZ * 2);
        var starts = new int[sizeX * sizeZ + 1];

        for (int ix = 0; ix < sizeX; ix++)
        {
            for (int iz = 0; iz < sizeZ; iz++)
            {
                starts[ix * sizeZ + iz] = surfaces.Count;
                int wx = worldMinX + ix;
                int wz = worldMinZ + iz;
                // Above the world ceiling is open sky, so a column that reaches
                // maxY still registers its top voxel as a surface.
                bool aboveSolid = false;
                for (int wy = maxY; wy >= minY; wy--)
                {
                    bool solid = Blocks.IsSolid(ws.GetBlockWorld(wx, wy, wz));
                    if (solid && !aboveSolid)
                    {
                        surfaces.Add(wy);
                    }
                    aboveSolid = solid;
                }
            }
        }
        starts[sizeX * sizeZ] = surfaces.Count;

        // Far enough outside any grade window that an axis with no facing
        // surface reads as a discontinuity rather than a slope.
        const int NO_SURFACE = 1 << 20;

        // The neighbouring column's surface that this one actually faces: the
        // nearest in Y. At a cave mouth the cave floor and the outdoor surface
        // are one continuous sheet and pair up correctly; across a wall (no
        // surface at all) the axis falls out of the window and stays snapped.
        int FacingSurfaceY(int wx, int wz, int y)
        {
            int ix = Math.Clamp(wx, worldMinX, worldMaxX) - worldMinX;
            int iz = Math.Clamp(wz, worldMinZ, worldMaxZ) - worldMinZ;
            int n = ix * sizeZ + iz;
            int bestY = y + NO_SURFACE;
            for (int k = starts[n]; k < starts[n + 1]; k++)
            {
                if (Math.Abs(surfaces[k] - y) < Math.Abs(bestY - y))
                {
                    bestY = surfaces[k];
                }
            }
            return bestY;
        }

        // Same per-axis rule as HeightMap.IsGrade (see there for why it is per
        // axis, and why the step size rather than the angle is the
        // discriminator), applied to one surface layer.
        bool IsGradeAt(int wx, int wz, int y)
        {
            return HeightMap.AxisIsGrade(y, FacingSurfaceY(wx - 1, wz, y), FacingSurfaceY(wx + 1, wz, y), maxGradeStep)
                || HeightMap.AxisIsGrade(y, FacingSurfaceY(wx, wz - 1, y), FacingSurfaceY(wx, wz + 1, y), maxGradeStep);
        }

        for (int ix = 0; ix < sizeX; ix++)
        {
            for (int iz = 0; iz < sizeZ; iz++)
            {
                int wx = worldMinX + ix;
                int wz = worldMinZ + iz;
                int n = ix * sizeZ + iz;
                for (int k = starts[n]; k < starts[n + 1]; k++)
                {
                    int y = surfaces[k];

                    // Every natural surface material, not just Terrain — desert
                    // and marsh columns are their own int and were being
                    // skipped, so their grades never got re-derived.
                    int surface = ws.GetBlockWorld(wx, y, wz);
                    if (!Blocks.IsNaturalGround(surface))
                    {
                        continue;
                    }
                    if (y > minY && !Blocks.IsSolid(ws.GetBlockWorld(wx, y - 1, wz)))
                    {
                        continue;
                    }
                    ws.SetShapeWorld(wx, y, wz, IsGradeAt(wx, wz, y)
                        ? SharpAxes.None
                        : SharpAxes.Y);
                }
            }
        }
    }
}
