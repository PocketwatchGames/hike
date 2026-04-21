using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

public static class WorldGen
{
    // Kit ids — indices into WorldGenData.Kits. Must match the order in
    // default_world_gen.tres. Per-voxel kit lets caves below a temperate
    // surface read as cave-palette while the surface above stays temperate.
    private const byte KIT_TEMPERATE = 0;
    private const byte KIT_CAVE = 1;
    private const byte KIT_UNDERWATER = 2;

    // Staircase spiral pattern: (dx, dz) offsets from center, actions per y-level
    private const int STAIR_KEEP = 0;
    private const int STAIR_FULL = 1;
    private const int STAIR_AIR = 2;

    private static readonly (int dx, int dz, int[] yActions)[] StaircasePattern =
    {
        (-1,  1, new[] { STAIR_FULL, STAIR_AIR,  STAIR_AIR,  STAIR_KEEP }),
        (-1,  0, new[] { STAIR_FULL, STAIR_AIR,  STAIR_AIR,  STAIR_AIR  }),
        (-1, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_AIR,  STAIR_AIR  }),
        ( 0, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_AIR,  STAIR_AIR  }),
        ( 1, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_AIR  }),
        ( 1,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_AIR  }),
        ( 1,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
        ( 0,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
        ( 0,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
    };

    // World y at and below this level is filled with water wherever terrain
    // doesn't reach up to it. Terrain perlin noise is allowed to dip below
    // this value, producing natural lakes and oceans. Set to -1 so the
    // baseline land surface (y = 0) sits one voxel above the water table.
    public const int WATER_LEVEL = -4;

    // Ground-hugging fog that clings to the water surface. Density peaks at
    // water level and linearly fades to 0 over FOG_FALLOFF_HEIGHT voxels above.
    // Only open-air voxels in the initial chunk pass get seeded — caves and
    // tunnels carved later stay fog-free, which is the effect we want (the
    // fog reads as lake/river mist, not enclosed-cave atmosphere).
    public const int FOG_FALLOFF_HEIGHT = 8;
    public const int FOG_MAX_DENSITY = 255;

    // Horizontal cells per 1 vertical voxel on the ramp skirt cast by a taller
    // plateau into its lower neighbour. With PlateauStep=4, slope=1 produces
    // a 4-cell ramp that rises one full plateau step — steep (45°) but
    // narrow, matching the "ramps should be a handful of cells wide" spec.
    private const int RAMP_SLOPE = 1;

    public static WorldState Generate(WorldGenData genData)
    {
        var min = new Vector3I(-genData.SizeX / 2, -1, -genData.SizeZ / 2);
        var max = new Vector3I(min.X + genData.SizeX - 1, min.Y + genData.SizeY - 1, min.Z + genData.SizeZ - 1);
        var ws = new WorldState(min, max, genData.SimData);

        var terrainNoise = new FastNoiseLite();
        terrainNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        terrainNoise.Seed = genData.TerrainNoiseSeed;
        terrainNoise.Frequency = 0.02f;
        terrainNoise.FractalOctaves = 4;

        var tunnelNoise = new FastNoiseLite();
        tunnelNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        tunnelNoise.Seed = genData.TunnelNoiseSeed;
        tunnelNoise.Frequency = 0.025f;
        tunnelNoise.FractalOctaves = 2;

        var caveNoise = new FastNoiseLite();
        caveNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        caveNoise.Seed = genData.CaveNoiseSeed;
        caveNoise.Frequency = genData.CaveNoiseFrequency;
        caveNoise.FractalOctaves = 2;

        var grassNoise = new FastNoiseLite();
        grassNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        grassNoise.Seed = genData.GrassNoiseSeed;
        grassNoise.Frequency = 0.1f;
        grassNoise.FractalOctaves = 2;

        // Dedicated low-frequency gate noise. Its zero-crossings mark
        // plateau-boundary segments that get ramped; everything else stays a
        // cliff. Lower frequency than pathNoise so the world has just a
        // handful of long, sparse ramp zones rather than dozens of short
        // meanders. Seeded off PathNoiseSeed so the pattern is stable with
        // the rest of the world-gen seed.
        var rampGateNoise = new FastNoiseLite();
        rampGateNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        rampGateNoise.Seed = genData.PathNoiseSeed;
        rampGateNoise.Frequency = 0.015f;
        rampGateNoise.FractalOctaves = 1;

        var forestNoise = new FastNoiseLite();
        forestNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        forestNoise.Seed = genData.ForestNoiseSeed;
        forestNoise.Frequency = genData.ForestNoiseFrequency;
        forestNoise.FractalOctaves = 2;

        int spawnFlatMinX = genData.SpawnBuildingOriginX - genData.SpawnFlatPadding;
        int spawnFlatMaxX = genData.SpawnBuildingOriginX + genData.SpawnBuildingWidth - 1 + genData.SpawnFlatPadding;
        int spawnFlatMinZ = genData.SpawnBuildingOriginZ - genData.SpawnFlatPadding;
        int spawnFlatMaxZ = genData.SpawnBuildingOriginZ + genData.SpawnBuildingDepth - 1 + genData.SpawnFlatPadding;

        // Build the integer height field once up front. Chunk and prop
        // generation read from this map instead of re-evaluating noise per
        // voxel — the shape is also authored here (plateau / ramp / river)
        // so geometry is noise-free by construction.
        var heightMap = BuildHeightMap(ws, genData, terrainNoise, rampGateNoise,
            spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);

        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int y = ws.Min.Y; y <= ws.Max.Y; y++)
            {
                for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkState(coord);
                    GenerateChunk(chunk, genData, tunnelNoise, heightMap);
                    ws._chunks[coord] = chunk;
                }
            }
        }

        // Tag the submerged shell as KIT_UNDERWATER. Runs after every chunk
        // (and its water voxels) exist so we can check actual water adjacency
        // instead of "wy <= WATER_LEVEL" — a y-only rule paints buried rock
        // under above-water cliffs as underwater, and the mesher's 27-voxel
        // kit vote then bleeds sand onto cliff faces nowhere near water.
        TagSubmergedKits(ws);

        // Carve swiss-cheese caves through terrain. Runs after terrain+tunnel
        // generation so cave carving sees the full solid column and can
        // connect tunnels vertically where they overlap.
        GenerateCaves(ws, genData, caveNoise);

        // Place fog after all terrain is final. The pass skips enclosed
        // voxels (tunnels, caves, anything with solid geometry directly
        // overhead) so fog only shows up in genuinely open-to-sky air above
        // water level.
        GenerateFog(ws);

        // Mark every buried solid voxel adjacent to carved air/water as Y.
        // Runs after all terrain and cave carving, so it sees the final
        // geometry. Catches cave ceilings, floors, walls, and noise-carved
        // "island" voxels (stalactites) in one pass — regardless of the
        // column's outdoor ramp status. The `buried` gate (solid voxel above
        // somewhere in the column) keeps the outdoor surface voxel untouched
        // so plateau vs. ramp behavior at the surface is preserved.
        MarkCaveSurfaceShapes(ws);

        // Per-voxel neighborhood slope pass: stamp OverlayId=dirt on 1-voxel
        // bumps, walkable ramps, and small plateau steps. The shader's
        // per-fragment slope on a box-smoothed normal cannot see features the
        // smoothing averages away; this authored signal puts them back.
        // Currently disabled — the ±EDGE_SCAN_WINDOW / diff-threshold heuristic
        // doesn't map cleanly to the terrain shapes we actually generate, so
        // overlays end up in the wrong places. Revisit once we have a clearer
        // read on which features need the dirt treatment (probably driven by
        // authored tags from the editor rather than derived from geometry).
        // StampEdgeOverlays(ws);

        // Scatter procedural overlays (dirt patches, field/clover patches) on
        // temperate-kit surface voxels. Noise-driven placement is a rough
        // starting point so the authored overlay art shows up in generated
        // worlds; replace with authored tags once the custom editor lands.
        // Cobblestone roads — sparse 5-voxel-wide ribbons that follow the
        // authored heightfield (step up/down ramps, avoid cliffs and water)
        // and occasionally branch. Runs BEFORE StampProceduralOverlays so
        // the procedural dirt/field scatter can be suppressed on and around
        // road columns — otherwise the mesher's 27-voxel overlay vote at
        // road edges would mix in field/dirt and the road would read as a
        // muddy path instead of paved cobblestone.
        var roadColumns = GenerateRoads(ws, heightMap, genData);

        StampProceduralOverlays(ws, roadColumns);
        StampDetailScatter(ws, roadColumns);

        // Generate world-space structures after all terrain chunks exist
        GenerateStructures(ws, genData);

        // Generate props on surface chunks after all voxels are placed.
        // Block-light sources are no longer pre-propagated here — torch
        // entities register themselves with WorldState.LightSources when
        // they spawn, which runs the BFS footprint at that point.
        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
            {
                var coord = new Vector3I(x, 0, z);
                GenerateProps(ws, coord, genData, grassNoise, forestNoise, heightMap);
            }
        }

        // Compute sunlight after all geometry exists.
        LightEngine.ComputeSunlight(ws);

        _lastHeightMap = heightMap;
        _lastPlateauStep = (int)Math.Max(1, Math.Round(genData.PlateauStep));
        return ws;
    }

    // Set at the end of Generate so debug dumps / console commands can
    // inspect the height field without the caller having to plumb it through.
    private static HeightMap? _lastHeightMap;
    private static int _lastPlateauStep = 1;

    // Writes three PPM images (plateau, height, ramp mask) and a stats text
    // file to `dir`. Called from the `worldgen_debug` console command and
    // from the headless auto-dump path in Main.
    public static void DumpDebug(string dir)
    {
        if (!_lastHeightMap.HasValue)
        {
            GD.PrintErr("worldgen_debug: no world has been generated yet.");
            return;
        }
        HeightMap hm = _lastHeightMap.Value;
        System.IO.Directory.CreateDirectory(dir);

        int sizeX = hm.Plateau.GetLength(0);
        int sizeZ = hm.Plateau.GetLength(1);
        int total = sizeX * sizeZ;

        var plateauHist = new Dictionary<int, int>();
        var heightHist = new Dictionary<int, int>();
        var liftHist = new Dictionary<int, int>();
        int rampCells = 0;
        int plateauCells = 0;
        int minP = int.MaxValue, maxP = int.MinValue;
        int minH = int.MaxValue, maxH = int.MinValue;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int p = hm.Plateau[x, z];
                int h = hm.Height[x, z];
                plateauHist.TryGetValue(p, out int pc); plateauHist[p] = pc + 1;
                heightHist.TryGetValue(h, out int hc); heightHist[h] = hc + 1;
                int lift = h - p;
                liftHist.TryGetValue(lift, out int lc); liftHist[lift] = lc + 1;
                if (lift > 0) { rampCells++; } else { plateauCells++; }
                if (p < minP) { minP = p; }
                if (p > maxP) { maxP = p; }
                if (h < minH) { minH = h; }
                if (h > maxH) { maxH = h; }
            }
        }

        // Plateau-transition count: how often adjacent columns have different
        // plateaus, split by |delta|. This is the key signal for "does the
        // plateau field actually look plateau-y, or is it a stippled mess?".
        var transHist = new Dictionary<int, int>();
        int transCount = 0;
        int adjPairs = 0;
        for (int x = 0; x < sizeX; x++)
        {
            for (int z = 0; z < sizeZ; z++)
            {
                int p = hm.Plateau[x, z];
                if (x + 1 < sizeX)
                {
                    int d = Math.Abs(hm.Plateau[x + 1, z] - p);
                    transHist.TryGetValue(d, out int tc); transHist[d] = tc + 1;
                    if (d > 0) { transCount++; }
                    adjPairs++;
                }
                if (z + 1 < sizeZ)
                {
                    int d = Math.Abs(hm.Plateau[x, z + 1] - p);
                    transHist.TryGetValue(d, out int tc); transHist[d] = tc + 1;
                    if (d > 0) { transCount++; }
                    adjPairs++;
                }
            }
        }

        using (var sw = new System.IO.StreamWriter($"{dir}/stats.txt"))
        {
            sw.WriteLine($"World: {sizeX} x {sizeZ} = {total} columns");
            sw.WriteLine($"RAMP_SLOPE={RAMP_SLOPE}, PlateauStep={_lastPlateauStep}, rampRadius={_lastPlateauStep * RAMP_SLOPE}");
            sw.WriteLine();
            sw.WriteLine($"Ramp cells: {rampCells} ({100.0 * rampCells / total:F1}%)");
            sw.WriteLine($"Plain plateau cells: {plateauCells} ({100.0 * plateauCells / total:F1}%)");
            sw.WriteLine($"Plateau range: [{minP}, {maxP}]");
            sw.WriteLine($"Height range:  [{minH}, {maxH}]");
            sw.WriteLine();
            sw.WriteLine($"Adjacent-plateau transitions: {transCount} / {adjPairs} pairs ({100.0 * transCount / adjPairs:F1}%)");
            sw.WriteLine("Transition-delta histogram (|Δplateau| between adjacent columns : count):");
            foreach (var kv in transHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Plateau histogram:");
            foreach (var kv in plateauHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Height histogram:");
            foreach (var kv in heightHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
            sw.WriteLine();
            sw.WriteLine("Ramp lift histogram (h - plateau : count):");
            foreach (var kv in liftHist.OrderBy(k => k.Key))
            {
                sw.WriteLine($"  {kv.Key,4}: {kv.Value}");
            }
        }

        WritePlateauPpm($"{dir}/plateau.ppm", hm.Plateau, minP, maxP, _lastPlateauStep);
        WritePlateauPpm($"{dir}/height.ppm", hm.Height, minH, maxH, _lastPlateauStep);
        WriteRampPpm($"{dir}/ramp.ppm", hm);

        GD.Print($"worldgen_debug: wrote {dir}/stats.txt, plateau.ppm, height.ppm, ramp.ppm");
    }

    // Paints each plateau step as a distinct hue (so 4-voxel bands read as
    // flat color patches) and darkens within a band by the voxel offset from
    // the band's base (so ramp lift inside a band shows up as a gradient).
    private static void WritePlateauPpm(string path, int[,] field, int min, int max, int step)
    {
        int w = field.GetLength(0);
        int h = field.GetLength(1);
        using var fs = System.IO.File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);
        byte[] row = new byte[w * 3];
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = 0; x < w; x++)
            {
                int v = field[x, z];
                int bandIndex = (int)Math.Floor((double)v / Math.Max(1, step));
                int within = v - bandIndex * step;
                // Cycle a 6-color palette by band; dim within the band.
                (byte r, byte g, byte b) palette = PaletteFor(bandIndex);
                float shade = 1f - 0.6f * within / Math.Max(1, step);
                row[x * 3 + 0] = (byte)Math.Clamp(palette.r * shade, 0, 255);
                row[x * 3 + 1] = (byte)Math.Clamp(palette.g * shade, 0, 255);
                row[x * 3 + 2] = (byte)Math.Clamp(palette.b * shade, 0, 255);
            }
            fs.Write(row, 0, row.Length);
        }
    }

    private static (byte r, byte g, byte b) PaletteFor(int bandIndex)
    {
        // Signed mod for stable coloring of negative bands.
        int m = ((bandIndex % 8) + 8) % 8;
        return m switch
        {
            0 => (80, 160, 80),    // green (plateau 0 = grass)
            1 => (180, 160, 80),   // tan
            2 => (200, 120, 80),   // orange
            3 => (180, 80, 80),    // red
            4 => (160, 80, 160),   // purple
            5 => (80, 80, 200),    // blue
            6 => (80, 160, 200),   // cyan
            _ => (200, 200, 80),   // yellow
        };
    }

    private static void WriteRampPpm(string path, HeightMap hm)
    {
        int w = hm.Plateau.GetLength(0);
        int h = hm.Plateau.GetLength(1);
        using var fs = System.IO.File.Create(path);
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{w} {h}\n255\n");
        fs.Write(header, 0, header.Length);
        byte[] row = new byte[w * 3];
        for (int z = h - 1; z >= 0; z--)
        {
            for (int x = 0; x < w; x++)
            {
                int lift = hm.Height[x, z] - hm.Plateau[x, z];
                if (lift == 0)
                {
                    row[x * 3 + 0] = 32;
                    row[x * 3 + 1] = 32;
                    row[x * 3 + 2] = 32;
                }
                else
                {
                    // Bright red scales with lift magnitude.
                    int shade = Math.Clamp(80 + lift * 40, 0, 255);
                    row[x * 3 + 0] = (byte)shade;
                    row[x * 3 + 1] = 0;
                    row[x * 3 + 2] = 0;
                }
            }
            fs.Write(row, 0, row.Length);
        }
    }

    // Precomputed per-column height data for the whole world. Both arrays are
    // indexed as [wx - WorldMinX, wz - WorldMinZ].
    //
    //   Plateau: the "natural" terrain height — `round(noise / step) * step`,
    //            or 0 inside the spawn-flat region. Always a plateau multiple.
    //
    //   Height:  the final integer surface height — plateau, optionally
    //            lifted by the ramp skirt cast from a nearby higher plateau.
    //            This is what chunk generation fills solid voxels up to.
    //
    // A column is a ramp iff Height > Plateau; otherwise it's a plain
    // plateau. This is the authored input to the mesher's shape-snapping rule.
    private readonly struct HeightMap
    {
        public readonly int WorldMinX;
        public readonly int WorldMinZ;
        public readonly int WorldMaxX;
        public readonly int WorldMaxZ;
        public readonly int[,] Plateau;
        public readonly int[,] Height;

        public HeightMap(int worldMinX, int worldMaxX, int worldMinZ, int worldMaxZ, int[,] plateau, int[,] height)
        {
            WorldMinX = worldMinX;
            WorldMaxX = worldMaxX;
            WorldMinZ = worldMinZ;
            WorldMaxZ = worldMaxZ;
            Plateau = plateau;
            Height = height;
        }

        public int GetHeight(int wx, int wz)
        {
            return Height[wx - WorldMinX, wz - WorldMinZ];
        }

        public int GetPlateau(int wx, int wz)
        {
            return Plateau[wx - WorldMinX, wz - WorldMinZ];
        }

        public bool IsRamp(int wx, int wz)
        {
            return GetHeight(wx, wz) > GetPlateau(wx, wz);
        }
    }

    // Build the integer height field for the whole world. Two passes:
    //   1. Per-column plateau: `round(noise / step) * step`, or 0 inside the
    //      spawn-flat region.
    //   2. Ramp skirt: every non-flat column scans neighbors within
    //      `rampRadius` and, for each higher-plateau neighbor, contributes a
    //      candidate lift. The lift is capped at one plateau step above the
    //      column's own plateau so ramps always span exactly one step — a
    //      2-step cliff stays a cliff with a single-step ramp at its base,
    //      never a funny half-height hill. The column's final height is the
    //      maximum of its own plateau and every candidate.
    //
    // Lift formula: `targetPlateau - ceil(dist / slope)`. The ceiling matters
    // — with integer floor, dist=1 produces zero lift loss, so cells directly
    // adjacent to a higher plateau would stamp a bulge at plateau height.
    private static HeightMap BuildHeightMap(WorldState ws, WorldGenData genData,
        FastNoiseLite terrainNoise, FastNoiseLite rampGateNoise,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;

        int[,] plateau = new int[sizeX, sizeZ];
        bool[,] rampAnchor = new bool[sizeX, sizeZ];
        float stepF = Math.Max(1f, genData.PlateauStep);
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        // Strict anchor band — `|pathNoise|` below this marks the core of a
        // ramp zone. Kept tight so anchors form thin, sparse meanders rather
        // than a blanket. Anchors get dilated below to give ramp skirts room
        // to form.
        const float RAMP_ANCHOR_BAND = 0.015f;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                bool flat = wx >= spawnFlatMinX && wx <= spawnFlatMaxX
                    && wz >= spawnFlatMinZ && wz <= spawnFlatMaxZ;
                if (flat)
                {
                    plateau[lx, lz] = 0;
                    continue;
                }

                float raw = genData.ElevationMultiplier * terrainNoise.GetNoise2D(wx, wz);
                plateau[lx, lz] = (int)(Mathf.Round(raw / stepF) * stepF);
                rampAnchor[lx, lz] = Math.Abs(rampGateNoise.GetNoise2D(wx, wz)) < RAMP_ANCHOR_BAND;
            }
        }

        // Dilate anchor mask by `rampRadius` cells — one full scan-radius's
        // worth on each side of the raw anchor line, so the lift scan has a
        // fully eligible neighbourhood and always produces a complete skirt.
        bool[,] rampEligible = new bool[sizeX, sizeZ];
        int rampRadiusConst = step * RAMP_SLOPE;
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
        // One step of rise takes `step * RAMP_SLOPE` horizontal cells; that's
        // also the scan radius since anything farther would only contribute
        // a zero (or clamped-away) lift.
        int rampRadius = step * RAMP_SLOPE;
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int lx = wx - worldMinX;
                int lz = wz - worldMinZ;

                bool flat = wx >= spawnFlatMinX && wx <= spawnFlatMaxX
                    && wz >= spawnFlatMinZ && wz <= spawnFlatMaxZ;
                int myPlateau = plateau[lx, lz];
                int best = myPlateau;

                // Spawn-flat columns must stay at y=0. Non-ramp-eligible
                // columns skip the scan too: only cells inside the dilated
                // ramp band can be lifted.
                if (!flat && rampEligible[lx, lz])
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
                            int verticalDrop = (dist + RAMP_SLOPE - 1) / RAMP_SLOPE;
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

        return new HeightMap(worldMinX, worldMaxX, worldMinZ, worldMaxZ, plateau, height);
    }

    // True iff (wx, wz) is a flat, dry land column with its surface at world
    // y=0. Grass/trees/mobs/loot place only on these. Post-refactor the
    // heightfield is integer everywhere, so "flat" is simply "height equals
    // plateau" (no ramp lift) "and plateau equals 0" (lowest tier). River
    // columns have height < 0, above-water plateaus have plateau > 0, so both
    // fall out automatically.
    private static bool IsFlatDryGrassAt(int wx, int wz, HeightMap heightMap)
    {
        return heightMap.GetHeight(wx, wz) == 0 && heightMap.GetPlateau(wx, wz) == 0;
    }

    // Plateau-step tunnels: the top TunnelLayerHeight voxels of every plateau
    // step (the band immediately under each plateau ceiling) are tunnel
    // candidates, gated by 3D tunnel noise. This produces tiered tunnel
    // systems whose ceilings line up with plateau elevations and whose
    // openings show up in cliff faces between adjacent plateau levels.
    private static bool IsTunnelAt(int wx, int wy, int wz, FastNoiseLite tunnelNoise, WorldGenData genData)
    {
        if (wy <= WATER_LEVEL)
        {
            return false;
        }
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        int rem = ((wy % step) + step) % step;
        if (rem < step - genData.TunnelLayerHeight)
        {
            return false;
        }
        // Sample at the band's base (rem=0 row) so all voxels in the band
        // share the same noise value — guarantees the band carves all-or-nothing
        // and never leaves sub-3-tall openings. Math.Floor (not C# integer
        // division) so negative wy snaps down, not toward zero.
        int bandBase = (int)Math.Floor((double)wy / step) * step;
        return Mathf.Abs(tunnelNoise.GetNoise3D(wx, bandBase, wz)) < genData.TunnelThreshold;
    }

    private static void GenerateChunk(ChunkState data, WorldGenData genData,
        FastNoiseLite tunnelNoise, HeightMap heightMap)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;
        int tunnelStep = Math.Max(1, (int)Math.Round(genData.PlateauStep));

        // True iff this column's surface reaches the plateau ceiling above wy,
        // so the rem=0 voxel at bandTop is solid and can serve as a tunnel
        // roof. Without this, columns whose solidHeight lands mid-band would
        // carve a tunnel with no ceiling above it, producing 1- and 2-tall
        // openings the player can't fit through.
        bool ColumnSupportsTunnel(int wy, int columnSolidHeight)
        {
            int bandTop = (int)Math.Floor((double)wy / tunnelStep) * tunnelStep + tunnelStep;
            return columnSolidHeight >= bandTop;
        }

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int z = 0; z < ChunkState.SIZE; z++)
            {
                int wx = chunkWorldX + x;
                int wz = chunkWorldZ + z;

                int solidHeight = heightMap.GetHeight(wx, wz);

                // Per-column shape: the topmost solid voxel (the surface) gets
                // None for ramp columns, Y for plateau columns. All buried
                // voxels stamp Y regardless — a ramp's softness must not leak
                // downward into caves or other surfaces that happen to share
                // the column. The mesher's "any soft voxel on Y wins" rule
                // then propagates the ramp surface's softness horizontally
                // into the adjacent plateau column's surface cell, so the
                // ramp base blends smoothly into the plateau.
                bool isRamp = heightMap.IsRamp(wx, wz);
                byte surfaceShape = (byte)(isRamp ? VoxelTypeInfo.SharpAxes.None : VoxelTypeInfo.SharpAxes.Y);

                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    int wy = chunkWorldY + y;

                    // Caves carve only when the column reaches the plateau
                    // ceiling above the band — that ceiling row (rem=0) is
                    // never carved by IsCaveAt, so it serves as the cave roof
                    // and we get a guaranteed full-height cave.
                    bool solid = wy <= solidHeight
                        && !(ColumnSupportsTunnel(wy, solidHeight) && IsTunnelAt(wx, wy, wz, tunnelNoise, genData));

                    if (!solid)
                    {
                        if (wy <= WATER_LEVEL)
                        {
                            data.Voxels[x, y, z] = VoxelType.Water;
                        }
                        // Fog isn't placed here — it's a separate pass after
                        // GenerateCaves so it can see the final geometry and
                        // skip enclosed spaces. See GenerateFog.
                        continue;
                    }

                    // Every natural solid voxel is Terrain — the shader picks
                    // the tile from the per-voxel KitId (see below) + surface
                    // normal.y. The 27-voxel majority vote in the mesher now
                    // resolves to Terrain everywhere, so cliff faces, cave
                    // walls, and seabeds all flow through the AUTO branch and
                    // read from their kit's palette. Explicit materials
                    // (Wood/Stone walls from structures) overwrite this later
                    // via SetVoxelWorld and take the non-AUTO shader path.
                    data.Voxels[x, y, z] = VoxelType.Terrain;

                    // Cave interior surfaces always snap flat, regardless of
                    // whether the outdoor ridge above this column is a plateau
                    // or a path-band ramp. Without this override a cave carved
                    // under a ramp column inherits the column's None tag, the
                    // ceiling vertex interpolates downward, and the ceiling
                    // pokes below the clip plane into the player's view.
                    // A voxel is a cave surface if the cell directly above it
                    // OR directly below it is a carved tunnel cell.
                    bool aboveIsCarved = wy + 1 <= solidHeight
                        && ColumnSupportsTunnel(wy + 1, solidHeight)
                        && IsTunnelAt(wx, wy + 1, wz, tunnelNoise, genData);
                    bool belowIsCarved = wy - 1 >= 0
                        && ColumnSupportsTunnel(wy - 1, solidHeight)
                        && IsTunnelAt(wx, wy - 1, wz, tunnelNoise, genData);
                    byte voxelShape = wy == solidHeight ? surfaceShape : (byte)VoxelTypeInfo.SharpAxes.Y;
                    data.Shape[x, y, z] = (aboveIsCarved || belowIsCarved)
                        ? (byte)VoxelTypeInfo.SharpAxes.Y
                        : voxelShape;

                    // Kit assignment: default every solid voxel to temperate.
                    // TagSubmergedKits runs after all chunks/water exist and
                    // re-tags the submerged shell to KIT_UNDERWATER based on
                    // actual water adjacency, so buried rock under above-water
                    // cliffs stays temperate (no sand bleed on cliff faces).
                    // Cave interiors are later re-tagged to KIT_CAVE by
                    // MarkCaveSurfaceShapes.
                    data.KitId[x, y, z] = KIT_TEMPERATE;
                }
            }
        }
    }

    // Ground-hugging fog above water, only placed in voxels with an
    // unobstructed vertical column to the top of the world. Runs after
    // terrain + caves so tunnel/cave interiors (which have solid geometry
    // overhead) are naturally excluded. Column-local: no dependency between
    // (wx, wz) pairs, streaming-friendly.
    private static void GenerateFog(WorldState ws)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        int fogTopY = WATER_LEVEL + FOG_FALLOFF_HEIGHT;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                // Highest non-air voxel in the column (solid OR water). Air
                // above this has a clear line up to the sky; air at or
                // below is enclosed (cave, tunnel, below overhang).
                int highestNonAir = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    if (ws.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
                    {
                        highestNonAir = wy;
                        break;
                    }
                }

                int fogStartY = Math.Max(highestNonAir + 1, WATER_LEVEL + 1);
                for (int wy = fogStartY; wy <= fogTopY && wy <= worldMaxY; wy++)
                {
                    // The "open to sky" test above already implies Air here,
                    // but check anyway in case a future voxel type (e.g. a
                    // glass roof) isn't covered by the ceiling scan.
                    if (ws.GetVoxelWorld(wx, wy, wz) != VoxelType.Air)
                    {
                        continue;
                    }
                    int heightAboveWater = wy - WATER_LEVEL;
                    float t = (float)heightAboveWater / FOG_FALLOFF_HEIGHT;
                    int density = (int)(FOG_MAX_DENSITY * (1f - t));
                    if (density > 0)
                    {
                        ws.SetFogWorld(wx, wy, wz, density);
                    }
                }
            }
        }
    }

    // Swiss-cheese caves: 3D noise carves blob-shaped holes through solid
    // terrain. Floors follow the noise surface (smooth); ceilings snap up to
    // the next plateau-step boundary so the rem=0 row above each cave stays
    // solid and acts as a flat roof. Caves never breach the surface and are
    // discarded if shorter than CaveMinHeight, guaranteeing walkable paths.
    private static void GenerateCaves(WorldState ws, WorldGenData genData, FastNoiseLite caveNoise)
    {
        int step = Math.Max(1, (int)Math.Round(genData.PlateauStep));
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        bool IsNaturallyCarved(int wx, int wy, int wz)
        {
            return Math.Abs(caveNoise.GetNoise3D(wx, wy, wz)) > genData.CaveThreshold;
        }

        // Highest solid (non-Air, non-Water) voxel in this column. Anything
        // above is sky; we never want to carve into sky (no craters).
        int FindSurface(int wx, int wz)
        {
            for (int wy = worldMaxY; wy >= worldMinY; wy--)
            {
                var v = ws.GetVoxelWorld(wx, wy, wz);
                if (v != VoxelType.Air && v != VoxelType.Water)
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

                // Walk the column bottom-up finding runs of natural carve.
                // worldMinY is preserved as bedrock, so start one above.
                int wy = worldMinY + 1;
                while (wy <= surfaceY)
                {
                    if (!IsNaturallyCarved(wx, wy, wz))
                    {
                        wy++;
                        continue;
                    }
                    int runLo = wy;
                    while (wy <= surfaceY && IsNaturallyCarved(wx, wy, wz))
                    {
                        wy++;
                    }
                    int runHi = wy - 1;

                    // Snap top up to the next plateau-step boundary. If the
                    // snap reaches above surface, that's fine — the cave just
                    // breaches as an open-topped pit.
                    int ceilingY = (int)Math.Floor((double)runHi / step) * step + step;
                    if (ceilingY - runLo < genData.CaveMinHeight)
                    {
                        continue;
                    }

                    for (int cy = runLo; cy < ceilingY; cy++)
                    {
                        var fill = cy <= WATER_LEVEL ? VoxelType.Water : VoxelType.Air;
                        ws.SetVoxelWorld(wx, cy, wz, fill);
                    }

                    // Force Y on the solid voxels bracketing the carved run
                    // (cave ceiling at ceilingY, cave floor at runLo-1), so the
                    // cave surface snaps flat regardless of whether this
                    // column's outdoor height came from the ramp branch of the
                    // height function. Cave interior geometry is its own ruleset.
                    ws.SetShapeWorld(wx, ceilingY, wz, VoxelTypeInfo.SharpAxes.Y);
                    if (runLo - 1 >= worldMinY)
                    {
                        ws.SetShapeWorld(wx, runLo - 1, wz, VoxelTypeInfo.SharpAxes.Y);
                    }
                }
            }
        }
    }

    // Sweep every column, find the topmost solid voxel, and mark any *buried*
    // solid voxel (i.e. below the top) that borders a cave-interior air/water
    // cell as SharpAxes.Y + KIT_CAVE. This is the authored form of "cave
    // interior surfaces snap flat" — ceilings, floors, lateral walls of
    // tunnels, and noise-carved island voxels all qualify.
    //
    // "Cave-interior" air is air/water that has solid above it somewhere in
    // its column — i.e. enclosed by a ceiling. A cliff face voxel ALSO sits
    // "buried" in its own (tall) column and is adjacent to air, but that air
    // is open to sky (no solid above in its own shorter column), so the
    // cave check filters it out. Without that filter every cliff face gets
    // KIT_CAVE, whose FlatTile is sand, and stone cliffs grow sand bases.
    private static void MarkCaveSurfaceShapes(WorldState ws)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        int sizeX = worldMaxX - worldMinX + 1;
        int sizeZ = worldMaxZ - worldMinZ + 1;
        int[,] columnTopY = new int[sizeX, sizeZ];
        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int t = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (VoxelTypeInfo.IsSolid(v) && v != VoxelType.Barrier)
                    {
                        t = wy;
                        break;
                    }
                }
                columnTopY[wx - worldMinX, wz - worldMinZ] = t;
            }
        }

        bool IsCaveAirOrWater(int wx, int wy, int wz)
        {
            int ax = wx - worldMinX;
            int az = wz - worldMinZ;
            if (ax < 0 || ax >= sizeX || az < 0 || az >= sizeZ) { return false; }
            var v = ws.GetVoxelWorld(wx, wy, wz);
            if (v != VoxelType.Air && v != VoxelType.Water) { return false; }
            // "Has solid above" = under a ceiling = cave interior, not cliff-side open sky.
            return wy < columnTopY[ax, az];
        }

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                int topY = columnTopY[wx - worldMinX, wz - worldMinZ];
                if (topY <= worldMinY)
                {
                    continue;
                }

                for (int wy = worldMinY; wy < topY; wy++)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
                    {
                        continue;
                    }
                    if (IsCaveAirOrWater(wx - 1, wy, wz)
                        || IsCaveAirOrWater(wx + 1, wy, wz)
                        || IsCaveAirOrWater(wx, wy - 1, wz)
                        || IsCaveAirOrWater(wx, wy + 1, wz)
                        || IsCaveAirOrWater(wx, wy, wz - 1)
                        || IsCaveAirOrWater(wx, wy, wz + 1))
                    {
                        ws.SetShapeWorld(wx, wy, wz, VoxelTypeInfo.SharpAxes.Y);
                        // Stamp the cave kit so the shader can paint it
                        // distinctly from the temperate surface above.
                        // Overrides KIT_UNDERWATER for submerged caves —
                        // the cave palette wins there.
                        ws.SetKitIdWorld(wx, wy, wz, KIT_CAVE);
                    }
                }
            }
        }
    }

    // Chebyshev radius for the water-adjacency search in TagSubmergedKits.
    // Must be >= 2: the mesher's kit vote is a 3x3x3 neighbourhood around a
    // DC cell corner, so a seabed cell's vote sees one layer of seabed (would
    // be KIT_UNDERWATER) plus one layer of buried rock just below (would be
    // KIT_TEMPERATE). With a 1-voxel shell the two layers tie at 9–9 and the
    // lower-index kit (temperate) wins — seabed reads as grass. A 2-voxel
    // shell tags both layers underwater so the vote goes 18–0.
    private const int SUBMERGED_KIT_RADIUS = 2;

    // Re-tag solid voxels at or below WATER_LEVEL to KIT_UNDERWATER iff they
    // sit within SUBMERGED_KIT_RADIUS of a water voxel. Runs after every
    // chunk exists so the water pass has already filled every non-solid
    // wy<=WATER_LEVEL cell with VoxelType.Water. Semantic "near water" beats
    // the old "wy<=WATER_LEVEL" rule because the latter paints deeply buried
    // rock under cliffs as underwater — then the mesher's 27-voxel kit vote
    // for nearby DC cells drags that sand onto the visible cliff face.
    private static void TagSubmergedKits(WorldState ws)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                for (int wy = worldMinY; wy <= WATER_LEVEL; wy++)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (!VoxelTypeInfo.IsSolid(v) || v == VoxelType.Barrier)
                    {
                        continue;
                    }

                    bool nearWater = false;
                    for (int dy = -SUBMERGED_KIT_RADIUS; dy <= SUBMERGED_KIT_RADIUS && !nearWater; dy++)
                    {
                        for (int dx = -SUBMERGED_KIT_RADIUS; dx <= SUBMERGED_KIT_RADIUS && !nearWater; dx++)
                        {
                            for (int dz = -SUBMERGED_KIT_RADIUS; dz <= SUBMERGED_KIT_RADIUS && !nearWater; dz++)
                            {
                                if (ws.GetVoxelWorld(wx + dx, wy + dy, wz + dz) == VoxelType.Water)
                                {
                                    nearWater = true;
                                }
                            }
                        }
                    }

                    if (nearWater)
                    {
                        ws.SetKitIdWorld(wx, wy, wz, KIT_UNDERWATER);
                    }
                }
            }
        }
    }

    // Overlay id values. 0 = no overlay. A non-zero OverlayId is a direct
    // tile_array base-layer index sampled by voxel_clip.gdshader with its
    // own alpha channel driving blend strength. Any tile in the global
    // catalog can be used as an overlay — add new OVERLAY_* constants rather
    // than reusing numbers so .hike files written with old values keep
    // mapping to the right tile when new tiles are added ahead of them.
    private const byte OVERLAY_NONE = 0;
    private const byte OVERLAY_DIRT = VoxelTypeInfo.TILE_DIRT_OVERLAY;
    private const byte OVERLAY_FIELD = VoxelTypeInfo.TILE_FIELD_OVERLAY;

    // How many voxels above/below `wy` to scan the neighbor column for its
    // local surface. Anything beyond this is treated as a cliff and skipped
    // (the kit's wall tile already paints cliff faces; overlays are for
    // walkable slopes the smooth normal can't see).
    private const int EDGE_SCAN_WINDOW = 4;
    // Neighbor-diff threshold at/above which we stamp OVERLAY_EDGE. 1 = any
    // non-flat cardinal neighbor (1-voxel bumps, ramps). Raise to 2+ for
    // smaller overlay coverage.
    private const int EDGE_MIN_DIFF = 1;
    // Neighbor-diff at/above which we stop stamping edge — the feature is a
    // real cliff and the wall band owns it.
    private const int EDGE_MAX_DIFF = 3;

    // Placement tuning for the procedural overlay scatter. Noise patches at
    // these frequencies produce ~10–30-voxel blobs; thresholds choose
    // roughly 10–20% coverage per type, with field winning over dirt where
    // they overlap. Feel free to tweak or replace with authored placement.
    private const int OVERLAY_DIRT_SEED = 4242;
    private const int OVERLAY_FIELD_SEED = 7373;
    private const float OVERLAY_DIRT_FREQ = 0.2f;
    private const float OVERLAY_FIELD_FREQ = 0.015f;
    private const float OVERLAY_DIRT_THRESHOLD = 0.9f;
    private const float OVERLAY_FIELD_THRESHOLD = 0.10f;

    // Test placement for detail-sprite scatter. Paints DetailGroup=1 (i.e.
    // WorldGenData.DetailGroups[0]) on temperate surface voxels wherever
    // detailNoise exceeds the threshold; strength is the noise value remapped
    // to [DETAIL_STRENGTH_MIN, 255]. Replace with authored brushes once the
    // editor lands; the runtime is happy with no DetailGroups configured (the
    // scatter pass short-circuits) so this is safe to leave on.
    private const int DETAIL_NOISE_SEED = 9191;
    private const float DETAIL_NOISE_FREQ = 0.06f;
    private const float DETAIL_NOISE_THRESHOLD = -0.1f;
    private const int DETAIL_STRENGTH_MIN = 80;

    // Noise-scatter dirt and field overlays on temperate-kit surface voxels.
    // Only top-surface voxels (solid with air above) are candidates so buried
    // geometry and cliff faces stay untouched. Kit gate restricts placement
    // to temperate — sand (underwater/cave) and cave palette stay clean.
    //
    // roadColumns carries the (wx, wz) mask of cobblestone road columns; those
    // are skipped entirely AND given a one-voxel buffer of suppression so the
    // mesher's 27-voxel overlay vote at road edges isn't polluted by field or
    // dirt stamped one voxel outside the road.
    private const int ROAD_OVERLAY_BUFFER = 1;
    private static void StampProceduralOverlays(WorldState ws, HashSet<(int, int)> roadColumns)
    {
        bool NearRoad(int wx, int wz)
        {
            for (int dx = -ROAD_OVERLAY_BUFFER; dx <= ROAD_OVERLAY_BUFFER; dx++)
            {
                for (int dz = -ROAD_OVERLAY_BUFFER; dz <= ROAD_OVERLAY_BUFFER; dz++)
                {
                    if (roadColumns.Contains((wx + dx, wz + dz)))
                    {
                        return true;
                    }
                }
            }
            return false;
        }
        var dirtNoise = new FastNoiseLite();
        dirtNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        dirtNoise.Seed = OVERLAY_DIRT_SEED;
        dirtNoise.Frequency = OVERLAY_DIRT_FREQ;
        dirtNoise.FractalOctaves = 2;

        var fieldNoise = new FastNoiseLite();
        fieldNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        fieldNoise.Seed = OVERLAY_FIELD_SEED;
        fieldNoise.Frequency = OVERLAY_FIELD_FREQ;
        fieldNoise.FractalOctaves = 2;

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
                if (NearRoad(wx, wz))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    if (ws.GetKitIdWorld(wx, wy, wz) != KIT_TEMPERATE)
                    {
                        continue;
                    }

                    // Field wins — denser grass masks muddy ground beneath.
                    if (fieldNoise.GetNoise2D(wx, wz) > OVERLAY_FIELD_THRESHOLD)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_FIELD);
                        continue;
                    }
                    if (dirtNoise.GetNoise2D(wx, wz) > OVERLAY_DIRT_THRESHOLD)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_DIRT);
                    }
                }
            }
        }
    }

    // Cobblestone road placement.
    //
    // Roads are drawn by a set of random walkers that start at scattered
    // points on dry land and step one cell at a time along an 8-connected
    // grid. Each step:
    //   - prefers to continue in the current heading, falling back to small
    //     left/right offsets if the heading is blocked;
    //   - only accepts neighbours with |height delta| <= 1, so ramps are
    //     followed naturally and cliffs / water are avoided by construction;
    //   - occasionally spawns a perpendicular branch walker.
    // The walker centres are dilated to a 5-voxel-wide disk per step so the
    // road reads as a continuous ribbon through turns and diagonals. The
    // resulting column mask is used twice: stamped as OverlayId on the
    // surface voxel in each column, and handed to StampDetailScatter so
    // grass sprites do not spawn on the pavement.
    private const int ROAD_HALF_WIDTH = 2;           // 5-wide disk (radius 2.5).
    private const int ROAD_DISK_R2 = 6;              // dx*dx+dz*dz threshold.
    private const float ROAD_TURN_CHANCE = 0.15f;    // Per step, nudge heading ±1.
    private const float ROAD_BRANCH_CHANCE = 0.004f; // Per step, spawn perpendicular walker.
    private const int ROAD_MAX_WALKERS = 16;         // Safety cap on branching.
    private const int ROAD_START_SPACING = 32;       // World voxels per start walker.
    private const int ROAD_MIN_STARTS = 2;
    private const int ROAD_START_ATTEMPTS = 32;      // Max tries to land a start on dry land.

    private static HashSet<(int, int)> GenerateRoads(WorldState ws, HeightMap heightMap, WorldGenData genData)
    {
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldExtent = Math.Max(worldMaxX - worldMinX, worldMaxZ - worldMinZ);
        int maxSteps = Math.Max(32, worldExtent * 2);
        int startCount = Math.Max(ROAD_MIN_STARTS, worldExtent / ROAD_START_SPACING);

        var roadColumns = new HashSet<(int, int)>();
        // Seeded off PathNoiseSeed so roads stay in sync with the rest of the
        // worldgen output for a given seed. XOR shifts it off the rampGateNoise
        // seed so ramps and roads don't correlate perfectly.
        var rng = new Random(genData.PathNoiseSeed ^ 0x52_4F_41_44);

        // 8-connected directions, in a ring so (dir+1)%8 is a 45° turn.
        var dirs = new (int dx, int dz)[]
        {
            ( 1,  0), ( 1,  1), ( 0,  1), (-1,  1),
            (-1,  0), (-1, -1), ( 0, -1), ( 1, -1),
        };

        bool CanWalkOnto(int wx, int wz, int fromHeight)
        {
            if (wx < worldMinX || wx > worldMaxX || wz < worldMinZ || wz > worldMaxZ)
            {
                return false;
            }
            int h = heightMap.GetHeight(wx, wz);
            if (h <= WATER_LEVEL)
            {
                return false;
            }
            return Math.Abs(h - fromHeight) <= 1;
        }

        // Flood fill from (cx, cz) out to the 5-disk, 4-connected, only
        // crossing between columns whose heights differ by <= 1. This keeps
        // the paint on a single walkable surface: when the walker straddles
        // a cliff edge, the disk cells on the other side of the cliff fail
        // the per-step height check and stay unpainted, so the road doesn't
        // smear half onto one plateau and half onto its neighbour.
        //
        // Ramps still paint fully — their per-step delta is 1 by construction
        // — so a road that follows a ramp gets a continuous cobblestone
        // ribbon up the slope.
        var floodQueue = new Queue<(int x, int z)>();
        var floodVisited = new HashSet<(int, int)>();
        void StampDisk(int cx, int cz)
        {
            floodQueue.Clear();
            floodVisited.Clear();
            floodQueue.Enqueue((cx, cz));
            floodVisited.Add((cx, cz));
            int centerH = heightMap.GetHeight(cx, cz);
            if (centerH <= WATER_LEVEL)
            {
                return;
            }
            while (floodQueue.Count > 0)
            {
                var (x, z) = floodQueue.Dequeue();
                roadColumns.Add((x, z));
                int h = heightMap.GetHeight(x, z);
                // 4-connected: diagonal neighbours would let the fill squeeze
                // across a corner between two columns at different heights
                // (e.g. A=0, B=2 diagonally) even when both cardinal edges
                // are cliffs.
                int[] ndx = { 1, -1, 0, 0 };
                int[] ndz = { 0, 0, 1, -1 };
                for (int i = 0; i < 4; i++)
                {
                    int nx = x + ndx[i];
                    int nz = z + ndz[i];
                    int ox = nx - cx;
                    int oz = nz - cz;
                    if (ox * ox + oz * oz > ROAD_DISK_R2)
                    {
                        continue;
                    }
                    if (nx < worldMinX || nx > worldMaxX || nz < worldMinZ || nz > worldMaxZ)
                    {
                        continue;
                    }
                    if (!floodVisited.Add((nx, nz)))
                    {
                        continue;
                    }
                    int nh = heightMap.GetHeight(nx, nz);
                    if (nh <= WATER_LEVEL)
                    {
                        continue;
                    }
                    if (Math.Abs(nh - h) > 1)
                    {
                        continue;
                    }
                    floodQueue.Enqueue((nx, nz));
                }
            }
        }

        var walkers = new Queue<(int wx, int wz, int dir, int stepsLeft)>();
        for (int s = 0; s < startCount; s++)
        {
            for (int attempt = 0; attempt < ROAD_START_ATTEMPTS; attempt++)
            {
                int sx = rng.Next(worldMinX, worldMaxX + 1);
                int sz = rng.Next(worldMinZ, worldMaxZ + 1);
                if (heightMap.GetHeight(sx, sz) <= WATER_LEVEL)
                {
                    continue;
                }
                walkers.Enqueue((sx, sz, rng.Next(0, 8), maxSteps));
                break;
            }
        }

        while (walkers.Count > 0)
        {
            var (wx, wz, dir, stepsLeft) = walkers.Dequeue();
            for (int step = 0; step < stepsLeft; step++)
            {
                StampDisk(wx, wz);

                if (rng.NextDouble() < ROAD_TURN_CHANCE)
                {
                    dir = (dir + (rng.Next(2) == 0 ? -1 : 1) + 8) % 8;
                }

                if (rng.NextDouble() < ROAD_BRANCH_CHANCE && walkers.Count < ROAD_MAX_WALKERS)
                {
                    // ±90° so branches read as intersections, not grazing forks.
                    int branchDir = (dir + (rng.Next(2) == 0 ? -2 : 2) + 8) % 8;
                    walkers.Enqueue((wx, wz, branchDir, stepsLeft - step));
                }

                // Try heading first, then ±45°, then ±90°. A walker that
                // can't find any walkable neighbour within ±90° ends — the
                // road has hit a cliff / dead-end and shouldn't spawn a U-turn.
                int curH = heightMap.GetHeight(wx, wz);
                int[] offsets = { 0, -1, 1, -2, 2 };
                bool moved = false;
                foreach (int o in offsets)
                {
                    int tryDir = (dir + o + 8) % 8;
                    int nx = wx + dirs[tryDir].dx;
                    int nz = wz + dirs[tryDir].dz;
                    if (!CanWalkOnto(nx, nz, curH))
                    {
                        continue;
                    }
                    wx = nx;
                    wz = nz;
                    dir = tryDir;
                    moved = true;
                    break;
                }
                if (!moved)
                {
                    break;
                }
            }
        }

        // Paint cobblestone overlay on the surface voxel in each road column.
        // Cave roof-carves can leave a column with no walkable surface at the
        // height the walker accepted, so gate on IsSurfaceVoxel.
        foreach (var (wx, wz) in roadColumns)
        {
            int wy = heightMap.GetHeight(wx, wz);
            if (!IsSurfaceVoxel(ws, wx, wy, wz))
            {
                continue;
            }
            ws.SetOverlayIdWorld(wx, wy, wz, VoxelTypeInfo.TILE_COBBLESTONE);
        }

        return roadColumns;
    }

    // Test placement for the painted detail-sprite scatter. Walks every
    // temperate-kit surface voxel and stamps DetailGroup=1 with a noise-driven
    // strength wherever detailNoise > threshold. The runtime scatter pass
    // looks up DetailGroups[0] for group=1 and silently does nothing if the
    // palette is empty, so this is safe to leave on even before any sprite art
    // is authored. Columns covered by a cobblestone road are skipped so grass
    // blades don't poke through the pavement.
    private static void StampDetailScatter(WorldState ws, HashSet<(int, int)> roadColumns)
    {
        var detailNoise = new FastNoiseLite();
        detailNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        detailNoise.Seed = DETAIL_NOISE_SEED;
        detailNoise.Frequency = DETAIL_NOISE_FREQ;
        detailNoise.FractalOctaves = 2;

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
                // Paved roads suppress grass sprites — the cobblestone is
                // authored as a ground surface, not a clearing in the grass.
                if (roadColumns.Contains((wx, wz)))
                {
                    continue;
                }
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }
                    if (ws.GetKitIdWorld(wx, wy, wz) != KIT_TEMPERATE)
                    {
                        continue;
                    }

                    float n = detailNoise.GetNoise2D(wx, wz);
                    if (n <= DETAIL_NOISE_THRESHOLD)
                    {
                        continue;
                    }

                    // Map noise (threshold..1) to (DETAIL_STRENGTH_MIN..255).
                    float t = (n - DETAIL_NOISE_THRESHOLD) / (1f - DETAIL_NOISE_THRESHOLD);
                    int strength = DETAIL_STRENGTH_MIN + (int)(t * (255 - DETAIL_STRENGTH_MIN));
                    ws.SetDetailGroupWorld(wx, wy, wz, 1);
                    ws.SetDetailStrengthWorld(wx, wy, wz, strength);
                }
            }
        }
    }

    // Stamp OVERLAY_DIRT on "surface voxels" (solid with air directly above)
    // whose local neighborhood slope is in [EDGE_MIN_DIFF, EDGE_MAX_DIFF-1].
    // Per-voxel, not per-column: correctly handles cave floors, overhangs, and
    // ledges because the ±EDGE_SCAN_WINDOW clip keeps each voxel's comparison
    // local to its own walkable layer.
    private static void StampEdgeOverlays(WorldState ws)
    {
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinX = ws.Min.X * ChunkState.SIZE;
        int worldMaxX = ws.Max.X * ChunkState.SIZE + ChunkState.SIZE - 1;
        int worldMinZ = ws.Min.Z * ChunkState.SIZE;
        int worldMaxZ = ws.Max.Z * ChunkState.SIZE + ChunkState.SIZE - 1;

        int[] dx = { 1, -1, 0, 0 };
        int[] dz = { 0, 0, 1, -1 };

        for (int wx = worldMinX; wx <= worldMaxX; wx++)
        {
            for (int wz = worldMinZ; wz <= worldMaxZ; wz++)
            {
                for (int wy = worldMinY; wy < worldMaxY; wy++)
                {
                    if (!IsSurfaceVoxel(ws, wx, wy, wz))
                    {
                        continue;
                    }

                    int maxDiff = 0;
                    for (int i = 0; i < 4; i++)
                    {
                        int nx = wx + dx[i];
                        int nz = wz + dz[i];
                        int neighborDiff = FindNearestSurfaceDiff(ws, nx, wy, nz, EDGE_SCAN_WINDOW);
                        if (neighborDiff > maxDiff)
                        {
                            maxDiff = neighborDiff;
                        }
                    }

                    if (maxDiff >= EDGE_MIN_DIFF && maxDiff < EDGE_MAX_DIFF)
                    {
                        ws.SetOverlayIdWorld(wx, wy, wz, OVERLAY_DIRT);
                    }
                }
            }
        }
    }

    // True iff the voxel at (wx, wy, wz) is solid (non-Barrier) and has air or
    // water directly above. That's the definition of "walkable surface" used by
    // the overlay pass — applies to plateau tops, cave floors, ledges alike.
    private static bool IsSurfaceVoxel(WorldState ws, int wx, int wy, int wz)
    {
        var self = ws.GetVoxelWorld(wx, wy, wz);
        if (!VoxelTypeInfo.IsSolid(self) || self == VoxelType.Barrier)
        {
            return false;
        }
        var above = ws.GetVoxelWorld(wx, wy + 1, wz);
        return !VoxelTypeInfo.IsSolid(above) || above == VoxelType.Barrier;
    }

    // Returns the vertical distance from `wy` to the nearest surface voxel in
    // the column at (wx, wz), searching ±window. Returns `window` if no
    // surface is found (treat as cliff — no overlay).
    private static int FindNearestSurfaceDiff(WorldState ws, int wx, int wy, int wz, int window)
    {
        int best = window;
        for (int d = 0; d <= window; d++)
        {
            if (IsSurfaceVoxel(ws, wx, wy + d, wz))
            {
                if (d < best) { best = d; }
                break;
            }
            if (d != 0 && IsSurfaceVoxel(ws, wx, wy - d, wz))
            {
                if (d < best) { best = d; }
                break;
            }
        }
        return best;
    }

    private static void GenerateProps(WorldState ws, Vector3I chunkCoord, WorldGenData genData,
        FastNoiseLite grassNoise, FastNoiseLite forestNoise, HeightMap heightMap)
    {
        // Grass requires both the heightmap "this is a flat y=0 column" check
        // AND a solid voxel at y=0 with air at y=1 in the actual world data —
        // caves can carve through the surface, in which case props would
        // otherwise float over an open hole.
        bool IsGrassyAt(int wx, int wz)
        {
            if (!IsFlatDryGrassAt(wx, wz, heightMap))
            {
                return false;
            }
            var ground = ws.GetVoxelWorld(wx, 0, wz);
            if (ground == VoxelType.Air || ground == VoxelType.Water)
            {
                return false;
            }
            return ws.GetVoxelWorld(wx, 1, wz) == VoxelType.Air;
        }
        ChunkState data = ws._chunks[chunkCoord];
        var rng = new Random(HashCode.Combine(chunkCoord.X, chunkCoord.Z, 7919));
        int treeCount = rng.Next(genData.TreesPerChunkMin, genData.TreesPerChunkMax + 1);

        var treedCells = new HashSet<(int, int)>();

        bool TryPlaceTree(int localX, int localZ)
        {
            if (treedCells.Contains((localX, localZ)))
            {
                return false;
            }
            int wx = chunkCoord.X * ChunkState.SIZE + localX;
            int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
            if (!IsGrassyAt(wx, wz))
            {
                return false;
            }
            for (int y = 1; y <= genData.BuildingHeight; y++)
            {
                if (data.GetVoxel(localX, y, localZ) != VoxelType.Air)
                {
                    return false;
                }
            }
            // +1.5 (not +1) because ChunkMesherDC's shallow-Y smoothing
            // averages a flat grass column's top face to 0.5 above the
            // voxel-grid top — anchoring at +1 buries sprites half a voxel
            // into the visible ground.
            ws.AddEntity(new PropSimState(PropType.Tree,
                new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f),
                genData.TreeScenes[rng.Next(genData.TreeScenes.Length)]));
            treedCells.Add((localX, localZ));
            return true;
        }

        for (int i = 0; i < treeCount; i++)
        {
            TryPlaceTree(rng.Next(1, ChunkState.SIZE - 1), rng.Next(1, ChunkState.SIZE - 1));
        }

        // Forest pockets: where forest noise is high, attempt a tree at every
        // grid cell with density that ramps up from the threshold. Sampling
        // per cell (not per chunk) means forest edges fade naturally instead
        // of snapping on chunk seams.
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                float f = forestNoise.GetNoise2D(wx, wz);
                if (f < genData.ForestThreshold)
                {
                    continue;
                }
                float t = (f - genData.ForestThreshold) / Math.Max(0.0001f, 1f - genData.ForestThreshold);
                float density = genData.ForestDensity * Mathf.Clamp(t, 0f, 1f);
                if (rng.NextDouble() >= density)
                {
                    continue;
                }
                TryPlaceTree(localX, localZ);
            }
        }

        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;

                if (grassNoise.GetNoise2D(wx, wz) < genData.GrassThreshold)
                {
                    continue;
                }
                if (!IsGrassyAt(chunkCoord.X * ChunkState.SIZE + localX, chunkCoord.Z * ChunkState.SIZE + localZ))
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }

                ws.AddEntity(new PropSimState(PropType.TallGrass, new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f), genData.TallGrassScenes[rng.Next(genData.TallGrassScenes.Length)]));
            }
        }

        // Generate goblins on grass surfaces
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (!IsGrassyAt(chunkCoord.X * ChunkState.SIZE + localX, chunkCoord.Z * ChunkState.SIZE + localZ))
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }
                if (rng.NextDouble() >= genData.GoblinChance)
                {
                    continue;
                }

                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                var mobState = new MobSimState(
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f),
                    (float)(rng.NextDouble() * Mathf.Pi * 2f),
                    genData.GoblinScene,
                    genData.GoblinData
                );
                if (rng.NextDouble() < 0.25f)
                {
                    mobState.InitialBehavior = "Wander";
                }
                ws.AddEntity(mobState);
            }
        }

        // Generate kun_kun on grass surfaces. Non-dangerous mobs that flee the
        // player and burrow once they get far enough away.
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (!IsGrassyAt(chunkCoord.X * ChunkState.SIZE + localX, chunkCoord.Z * ChunkState.SIZE + localZ))
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }
                if (rng.NextDouble() >= genData.KunKunChance)
                {
                    continue;
                }

                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                ws.AddEntity(new MobSimState(
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f),
                    (float)(rng.NextDouble() * Mathf.Pi * 2f),
                    genData.KunKunScene,
                    genData.KunKunData
                ));
            }
        }

        // Generate loot on grass surfaces
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (!IsGrassyAt(chunkCoord.X * ChunkState.SIZE + localX, chunkCoord.Z * ChunkState.SIZE + localZ))
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }
                if (rng.NextDouble() >= genData.LootChance)
                {
                    continue;
                }

                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                ws.AddEntity(new PropSimState(
                    PropType.Loot,
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f),
                    genData.LootScene
                ));
            }
        }

        // Generate chests on grass surfaces
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (!IsGrassyAt(chunkCoord.X * ChunkState.SIZE + localX, chunkCoord.Z * ChunkState.SIZE + localZ))
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }
                if (rng.NextDouble() >= genData.ChestChance)
                {
                    continue;
                }

                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                int lootCount = rng.Next(genData.ChestLootCountMin, genData.ChestLootCountMax + 1);
                ws.AddEntity(new ChestSimState(new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1.5f, wz + 0.5f),
                    genData.ChestScene,
                    lootCount,
                    genData.LootScene));
            }
        }

        // Cave pockets: scan the full vertical column and spawn mobs/chests/
        // loot/torches anywhere there's a 2-voxel air pocket with a solid
        // floor and a ceiling within reach (the "is enclosed" test is what
        // distinguishes cave pockets from open surface).
        const int HEAD_CLEARANCE = 2;
        const int CAVE_CEILING_PROBE = 6;
        int worldMinY = ws.Min.Y * ChunkState.SIZE;
        int worldMaxY = ws.Max.Y * ChunkState.SIZE + ChunkState.SIZE - 1;
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                int wx = chunkCoord.X * ChunkState.SIZE + localX;
                int wz = chunkCoord.Z * ChunkState.SIZE + localZ;
                for (int wy = worldMinY + 1; wy <= worldMaxY - HEAD_CLEARANCE; wy++)
                {
                    var below = ws.GetVoxelWorld(wx, wy - 1, wz);
                    if (below == VoxelType.Air || below == VoxelType.Water)
                    {
                        continue;
                    }
                    bool clear = true;
                    for (int c = 0; c < HEAD_CLEARANCE; c++)
                    {
                        if (ws.GetVoxelWorld(wx, wy + c, wz) != VoxelType.Air)
                        {
                            clear = false;
                            break;
                        }
                    }
                    if (!clear)
                    {
                        continue;
                    }
                    bool isCave = false;
                    for (int c = HEAD_CLEARANCE; c <= CAVE_CEILING_PROBE; c++)
                    {
                        if (ws.GetVoxelWorld(wx, wy + c, wz) != VoxelType.Air)
                        {
                            isCave = true;
                            break;
                        }
                    }
                    if (!isCave)
                    {
                        continue;
                    }

                    var pos = new Vector3(wx + 0.5f, wy, wz + 0.5f);
                    if (rng.NextDouble() < genData.GoblinChance)
                    {
                        var mobState = new MobSimState(pos,
                            (float)(rng.NextDouble() * Mathf.Pi * 2f),
                            genData.GoblinScene, genData.GoblinData);
                        if (rng.NextDouble() < 0.25f)
                        {
                            mobState.InitialBehavior = "Wander";
                        }
                        ws.AddEntity(mobState);
                    }
                    if (rng.NextDouble() < genData.KunKunChance)
                    {
                        ws.AddEntity(new MobSimState(pos,
                            (float)(rng.NextDouble() * Mathf.Pi * 2f),
                            genData.KunKunScene, genData.KunKunData));
                    }
                    if (rng.NextDouble() < genData.LootChance)
                    {
                        ws.AddEntity(new PropSimState(PropType.Loot, pos, genData.LootScene));
                    }
                    if (rng.NextDouble() < genData.ChestChance)
                    {
                        int lootCount = rng.Next(genData.ChestLootCountMin, genData.ChestLootCountMax + 1);
                        ws.AddEntity(new ChestSimState(pos, genData.ChestScene, lootCount, genData.LootScene));
                    }
                    if (rng.NextDouble() < genData.CaveTorchChance)
                    {
                        ws.AddEntity(new TorchSimState(pos, genData.TorchScene));
                    }
                }
            }
        }

        // Place torches inside houses as interactives
        GenerateTorches(ws, data, chunkCoord, genData, rng);
    }

    private static void GenerateTorches(WorldState ws, ChunkState data, Vector3I chunkCoord, WorldGenData genData, Random rng)
    {
        // Detect if this chunk has a house by checking for Wood walls at y=1
        bool hasHouse = false;
        int houseMinX = ChunkState.SIZE, houseMaxX = 0;
        int houseMinZ = ChunkState.SIZE, houseMaxZ = 0;
        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int z = 0; z < ChunkState.SIZE; z++)
            {
                if (data.Voxels[x, 1, z] == VoxelType.Wood)
                {
                    hasHouse = true;
                    houseMinX = Math.Min(houseMinX, x);
                    houseMaxX = Math.Max(houseMaxX, x);
                    houseMinZ = Math.Min(houseMinZ, z);
                    houseMaxZ = Math.Max(houseMaxZ, z);
                }
            }
        }

        if (!hasHouse)
        {
            return;
        }

        // Place torches inside the house (interior area, excluding walls)
        int interiorMinX = houseMinX + 1;
        int interiorMaxX = houseMaxX - 1;
        int interiorMinZ = houseMinZ + 1;
        int interiorMaxZ = houseMaxZ - 1;

        if (interiorMinX > interiorMaxX || interiorMinZ > interiorMaxZ)
        {
            return;
        }

        int torchCount = rng.Next(genData.TorchesPerHouseMin, genData.TorchesPerHouseMax + 1);
        var used = new HashSet<(int, int)>();
        // Bound retries so a small/full house can't spin forever trying to find
        // a free slot. With a typical 4x4 interior and a couple torches this
        // converges in 1-2 tries each iteration.
        const int MAX_PLACEMENT_ATTEMPTS = 8;
        for (int i = 0; i < torchCount; i++)
        {
            int localX = 0, localZ = 0;
            bool placed = false;
            for (int attempt = 0; attempt < MAX_PLACEMENT_ATTEMPTS; attempt++)
            {
                localX = rng.Next(interiorMinX, interiorMaxX + 1);
                localZ = rng.Next(interiorMinZ, interiorMaxZ + 1);
                if (used.Contains((localX, localZ))) { continue; }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air) { continue; }
                placed = true;
                break;
            }
            if (!placed) { continue; }
            used.Add((localX, localZ));

            float worldX = chunkCoord.X * ChunkState.SIZE + localX + 0.5f;
            float worldY = chunkCoord.Y * ChunkState.SIZE + 1f;
            float worldZ = chunkCoord.Z * ChunkState.SIZE + localZ + 0.5f;

            var torchPos = new Vector3(worldX, worldY, worldZ);
            ws.AddEntity(new TorchSimState(torchPos, genData.TorchScene));
        }
    }

    private static void GenerateStructures(WorldState ws, WorldGenData genData)
    {
        var rng = new Random(HashCode.Combine(genData.SizeX, genData.SizeZ, 42));

        // Fixed building just north of spawn (player spawns at 0,4,0)
        GenerateHouse(ws, rng, genData, genData.SpawnBuildingOriginX, genData.SpawnBuildingOriginZ,
            genData.SpawnBuildingWidth, genData.SpawnBuildingDepth, 3);
    }

    private static void GenerateHouse(WorldState ws, Random rng, WorldGenData genData, int originX, int originZ, int widthX, int widthZ, int numFloors)
    {
        const int CEILING_HEIGHT = 3;
        const int DOOR_HEIGHT = 2;
        const int WINDOW_Y = 2;
        const int WALL_TOP = CEILING_HEIGHT + 1;
        const int GROUND_Y = 0;

        int startX = originX;
        int startZ = originZ;
        int endX = startX + widthX - 1;
        int endZ = startZ + widthZ - 1;

        // Walls and ceilings for all floors
        int baseY = GROUND_Y + 1;
        int totalHeight = numFloors * WALL_TOP;
        for (int wy = baseY; wy < baseY + totalHeight; wy++)
        {
            int localY = wy - baseY;
            for (int wx = startX; wx <= endX; wx++)
            {
                for (int wz = startZ; wz <= endZ; wz++)
                {
                    bool isWall = wx == startX || wx == endX || wz == startZ || wz == endZ;
                    bool isCeiling = ((localY + 1) % WALL_TOP == 0);
                    if (isWall || isCeiling)
                    {
                        ws.SetVoxelWorld(wx, wy, wz, isCeiling ? VoxelType.Stone : VoxelType.Wood);
                    }
                }
            }
        }

        // Collect wall segments for placing doors and windows.
        // Each wall is: fixed axis, fixed coordinate, start/end along the other axis (interior only, excluding corners).
        // 0=south(-Z), 1=north(+Z), 2=west(-X), 3=east(+X)
        int[][] walls = new int[][]
        {
            new[] { startX + 1, endX - 1, startZ, 0 }, // south wall: X range, Z fixed
            new[] { startX + 1, endX - 1, endZ,   1 }, // north wall
            new[] { startZ + 1, endZ - 1, startX, 2 }, // west wall: Z range, X fixed
            new[] { startZ + 1, endZ - 1, endX,   3 }, // east wall
        };

        int doorCount = rng.Next(1, 5);
        int windowCount = rng.Next(2, 8);

        // Shuffle wall order so doors/windows distribute randomly across walls
        ShuffleArray(rng, walls);

        // Track which walls have doors so windows avoid them
        var doorWalls = new HashSet<int>();

        // Place doors — one per wall face, cycling through walls
        for (int i = 0; i < doorCount; i++)
        {
            int wallIndex = i % walls.Length;
            int[] wall = walls[wallIndex];
            int rangeStart = wall[0];
            int rangeEnd = wall[1];
            if (rangeStart > rangeEnd)
            {
                continue;
            }
            doorWalls.Add(wallIndex);
            int pos = rng.Next(rangeStart, rangeEnd + 1);
            int doorWx, doorWz;
            float doorRotY;
            if (wall[3] <= 1)
            {
                doorWx = pos;
                doorWz = wall[2];
                doorRotY = 0f;
            }
            else
            {
                doorWx = wall[2];
                doorWz = pos;
                doorRotY = Mathf.Pi / 2f;
            }
            for (int dy = 0; dy < DOOR_HEIGHT; dy++)
            {
                int wy = baseY + dy;
                ws.SetVoxelWorld(doorWx, wy, doorWz, VoxelType.Barrier);
            }
            ws.AddEntity(new DoorSimState(new Vector3(doorWx + 0.5f, baseY, doorWz + 0.5f),
                doorRotY,
                genData.DoorScene));
        }

        // Collect walls without doors for window placement
        var windowWalls = new List<int[]>();
        for (int i = 0; i < walls.Length; i++)
        {
            if (!doorWalls.Contains(i))
            {
                windowWalls.Add(walls[i]);
            }
        }

        // Place windows (1 voxel hole at y=2) — only on walls without doors
        int windowY = baseY + WINDOW_Y - 1;
        for (int i = 0; i < windowCount && windowWalls.Count > 0; i++)
        {
            int[] wall = windowWalls[i % windowWalls.Count];
            int rangeStart = wall[0];
            int rangeEnd = wall[1];
            if (rangeStart > rangeEnd)
            {
                continue;
            }
            int pos = rng.Next(rangeStart, rangeEnd + 1);
            if (wall[3] <= 1)
            {
                ws.SetVoxelWorld(pos, windowY, wall[2], VoxelType.Air);
            }
            else
            {
                ws.SetVoxelWorld(wall[2], windowY, pos, VoxelType.Air);
            }
        }

        // Add staircases for multi-floor buildings, alternating corners
        if (numFloors > 1)
        {
            int cornerAX = startX + 2;
            int cornerAZ = startZ + 2;
            int cornerBX = endX - 2;
            int cornerBZ = endZ - 2;
            for (int floor = 0; floor < numFloors - 1; floor++)
            {
                if (floor % 2 == 0)
                {
                    GenerateStaircase(ws, cornerAX, cornerAZ, floor, WALL_TOP, baseY);
                }
                else
                {
                    GenerateStaircase(ws, cornerBX, cornerBZ, floor, WALL_TOP, baseY);
                }
            }
        }
    }

    private static void GenerateStaircase(WorldState ws, int centerX, int centerZ, int floor, int wallTop, int baseY)
    {
        int floorBaseY = baseY + floor * wallTop;

        foreach (var (dx, dz, yActions) in StaircasePattern)
        {
            int wx = centerX + dx;
            int wz = centerZ + dz;

            for (int i = 0; i < 4; i++)
            {
                int wy = floorBaseY + i;
                switch (yActions[i])
                {
                    case STAIR_FULL:
                        ws.SetVoxelWorld(wx, wy, wz, VoxelType.Wood);
                        break;
                    case STAIR_AIR:
                        ws.SetVoxelWorld(wx, wy, wz, VoxelType.Air);
                        break;
                }
            }
        }
    }

    private static void ShuffleArray<T>(Random rng, T[] array)
    {
        for (int i = array.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (array[i], array[j]) = (array[j], array[i]);
        }
    }
}
