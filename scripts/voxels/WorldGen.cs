using System;
using System.Collections.Generic;
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

        var pathNoise = new FastNoiseLite();
        pathNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        pathNoise.Seed = genData.PathNoiseSeed;
        pathNoise.Frequency = 0.05f;
        pathNoise.FractalOctaves = 2;

        var riverNoise = new FastNoiseLite();
        riverNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        riverNoise.Seed = genData.RiverNoiseSeed;
        riverNoise.Frequency = 0.015f;
        riverNoise.FractalOctaves = 2;

        var forestNoise = new FastNoiseLite();
        forestNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        forestNoise.Seed = genData.ForestNoiseSeed;
        forestNoise.Frequency = genData.ForestNoiseFrequency;
        forestNoise.FractalOctaves = 2;

        int spawnFlatMinX = genData.SpawnBuildingOriginX - genData.SpawnFlatPadding;
        int spawnFlatMaxX = genData.SpawnBuildingOriginX + genData.SpawnBuildingWidth - 1 + genData.SpawnFlatPadding;
        int spawnFlatMinZ = genData.SpawnBuildingOriginZ - genData.SpawnFlatPadding;
        int spawnFlatMaxZ = genData.SpawnBuildingOriginZ + genData.SpawnBuildingDepth - 1 + genData.SpawnFlatPadding;

        for (int x = ws.Min.X; x <= ws.Max.X; x++)
        {
            for (int y = ws.Min.Y; y <= ws.Max.Y; y++)
            {
                for (int z = ws.Min.Z; z <= ws.Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkState(coord);
                    GenerateChunk(chunk, genData, terrainNoise, tunnelNoise, pathNoise, riverNoise,
                        spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
                    ws._chunks[coord] = chunk;
                }
            }
        }

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
        StampProceduralOverlays(ws);
        StampDetailScatter(ws);

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
                GenerateProps(ws, coord, genData, terrainNoise, tunnelNoise, grassNoise, pathNoise, riverNoise, forestNoise,
                    spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
            }
        }

        // Compute sunlight after all geometry exists.
        LightEngine.ComputeSunlight(ws);

        return ws;
    }

    // Shared with GenerateProps so the prop "is this a flat grassy spot?"
    // check evaluates the same noise field as terrain generation.
    private static float RawHeightAt(int wx, int wz, FastNoiseLite terrainNoise, FastNoiseLite pathNoise, FastNoiseLite riverNoise, WorldGenData genData,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        bool flat = wx >= spawnFlatMinX && wx <= spawnFlatMaxX
            && wz >= spawnFlatMinZ && wz <= spawnFlatMaxZ;
        if (flat)
        {
            return 0f;
        }
        float raw = genData.ElevationMultiplier * terrainNoise.GetNoise2D(wx, wz);
        // Quantize to plateau steps; smoothly fall back to the raw height in
        // path bands (where |pathNoise| > PathThreshold) so paths form ramps
        // between plateaus instead of sharp cliffs. Below water level we keep
        // the raw (smooth) height so basin floors aren't terraced under water.
        float step = Math.Max(0.0001f, genData.PlateauStep);
        float plateau = Mathf.Round(raw / step) * step;
        float pathiness = Math.Abs(pathNoise.GetNoise2D(wx, wz));
        float t = 1f - Mathf.SmoothStep(genData.PathThreshold, genData.PathThreshold + genData.PathBlendBand, pathiness);
        float h = Mathf.Lerp(plateau, raw, t);
        if (h < WATER_LEVEL)
        {
            h = raw;
        }

        // River carving: in river bands, lower terrain that sits close to the
        // water level down below it. Influence falls off the higher the
        // surrounding terrain rises, so rivers don't gouge into mountains.
        float riverness = Math.Abs(riverNoise.GetNoise2D(wx, wz));
        float rt = 1f - Mathf.SmoothStep(genData.RiverThreshold, genData.RiverThreshold + genData.RiverBlendBand, riverness);
        if (rt > 0f)
        {
            float aboveWater = Math.Max(0f, h - WATER_LEVEL);
            float proximity = 1f - Mathf.Clamp(aboveWater / Math.Max(0.0001f, genData.RiverInfluenceMaxHeight), 0f, 1f);
            h = Mathf.Lerp(h, WATER_LEVEL - genData.RiverDepth, rt * proximity);
        }

        return h;
    }

    // True iff (wx, wz) is a flat, dry land column with its surface at world
    // y=0. Used by GenerateProps to decide where trees/grass/loot/mobs go now
    // that the surface voxel type is uniformly Terrain (the visual look comes
    // from the shader's slope rule, so prop placement has to recompute slope
    // from the source noise).
    private static bool IsFlatDryGrassAt(int wx, int wz,
        FastNoiseLite terrainNoise, FastNoiseLite pathNoise, FastNoiseLite riverNoise, WorldGenData genData,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        // tan(10°) ≈ 0.176 — flatter than this is "grassy".
        const float DIRT_SLOPE_MIN = 0.176f;

        float h = RawHeightAt(wx, wz, terrainNoise, pathNoise, riverNoise, genData, spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
        int solidHeight = (int)Math.Round(h);
        // Grass only on the lowest dry plateau (surface at y=0). Underwater,
        // shore, and elevated plateaus stay free of grass/trees/mobs/loot.
        if (solidHeight != 0)
        {
            return false;
        }
        float dxF = Math.Abs(
            RawHeightAt(wx + 1, wz, terrainNoise, pathNoise, riverNoise, genData, spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ)
            - RawHeightAt(wx - 1, wz, terrainNoise, pathNoise, riverNoise, genData, spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ)
        ) * 0.5f;
        float dzF = Math.Abs(
            RawHeightAt(wx, wz + 1, terrainNoise, pathNoise, riverNoise, genData, spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ)
            - RawHeightAt(wx, wz - 1, terrainNoise, pathNoise, riverNoise, genData, spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ)
        ) * 0.5f;
        return Math.Max(dxF, dzF) <= DIRT_SLOPE_MIN;
    }

    // A column is a "ramp" if its surface height comes out of the smooth
    // (non-quantized) branch of RawHeightAt: path bands where we blend raw→plateau,
    // river bands where we carve down to water, or the spawn-area flat patch (which
    // itself is flat but covered below). Surface voxels in ramp columns get
    // shape=None so DC smooths across them; non-ramp (flat plateau) columns get
    // shape=Y so the mesher snaps their surface to the voxel grid. This is the
    // authored replacement for the old cliff-top / cave-ceiling heuristics.
    private static bool IsRampColumn(int wx, int wz,
        FastNoiseLite pathNoise, FastNoiseLite riverNoise, WorldGenData genData,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        bool flat = wx >= spawnFlatMinX && wx <= spawnFlatMaxX
            && wz >= spawnFlatMinZ && wz <= spawnFlatMaxZ;
        if (flat)
        {
            return false;
        }
        float pathiness = Math.Abs(pathNoise.GetNoise2D(wx, wz));
        if (pathiness < genData.PathThreshold + genData.PathBlendBand)
        {
            return true;
        }
        float riverness = Math.Abs(riverNoise.GetNoise2D(wx, wz));
        if (riverness < genData.RiverThreshold + genData.RiverBlendBand)
        {
            return true;
        }
        return false;
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
        FastNoiseLite terrainNoise, FastNoiseLite tunnelNoise, FastNoiseLite pathNoise, FastNoiseLite riverNoise,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;
        int tunnelStep = Math.Max(1, (int)Math.Round(genData.PlateauStep));

        // True iff this column's surface reaches the plateau ceiling above wy,
        // so the rem=0 voxel at bandTop is solid and can serve as a tunnel
        // roof. Without this, columns whose solidHeight lands mid-band (in
        // path-blend regions) would carve a tunnel with no ceiling above it,
        // producing 1- and 2-tall openings the player can't fit through.
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

                float rawHeight = RawHeightAt(wx, wz, terrainNoise, pathNoise, riverNoise, genData,
                    spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
                int solidHeight = (int)Math.Round(rawHeight);

                // Per-column shape: ramps get None (smooth), everything else Y
                // (snap vertical). Applied to every solid voxel in the column —
                // buried voxels still carry the tag so they feed the 3x3x3 OR
                // at neighbouring surface cells (e.g. a cave ceiling picks up Y
                // from the Terrain voxel above it via the mesher's neighbour OR).
                bool isRamp = IsRampColumn(wx, wz, pathNoise, riverNoise, genData,
                    spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
                byte columnShape = (byte)(isRamp ? VoxelTypeInfo.SharpAxes.None : VoxelTypeInfo.SharpAxes.Y);

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
                    data.Shape[x, y, z] = (aboveIsCarved || belowIsCarved)
                        ? (byte)VoxelTypeInfo.SharpAxes.Y
                        : columnShape;

                    // Kit assignment: submerged voxels get the underwater kit,
                    // everything else starts temperate. Cave interiors are
                    // re-tagged to KIT_CAVE by MarkCaveSurfaceShapes after the
                    // swiss-cheese cave carving pass — that's where the "buried
                    // solid adjacent to carved air/water" heuristic lives, so
                    // we let it own cave kit assignment uniformly across both
                    // the tunnel and cave paths.
                    data.KitId[x, y, z] = wy <= WATER_LEVEL ? KIT_UNDERWATER : KIT_TEMPERATE;
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
    // solid voxel (i.e. below the top) that has an air/water 6-neighbour as
    // SharpAxes.Y. This is the authored form of "cave interior surfaces snap
    // flat" — ceilings, floors, lateral walls of tunnels, and the noise-carved
    // island voxels that sit inside a cave all qualify, and they all want the
    // same ruleset regardless of whether the outdoor column above is a plateau
    // or a path-band ramp. The `buried` gate leaves the outdoor surface voxel
    // alone so plateau-vs-ramp shape at the surface is whatever GenerateChunk
    // already wrote.
    private static void MarkCaveSurfaceShapes(WorldState ws)
    {
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
                int topY = worldMinY - 1;
                for (int wy = worldMaxY; wy >= worldMinY; wy--)
                {
                    var v = ws.GetVoxelWorld(wx, wy, wz);
                    if (VoxelTypeInfo.IsSolid(v) && v != VoxelType.Barrier)
                    {
                        topY = wy;
                        break;
                    }
                }
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
                    if (IsAirOrWater(ws, wx - 1, wy, wz)
                        || IsAirOrWater(ws, wx + 1, wy, wz)
                        || IsAirOrWater(ws, wx, wy - 1, wz)
                        || IsAirOrWater(ws, wx, wy + 1, wz)
                        || IsAirOrWater(ws, wx, wy, wz - 1)
                        || IsAirOrWater(ws, wx, wy, wz + 1))
                    {
                        ws.SetShapeWorld(wx, wy, wz, VoxelTypeInfo.SharpAxes.Y);
                        // Same "buried + adjacent to carved" heuristic that
                        // identifies a cave-interior surface. Stamp the cave
                        // kit so the shader can later paint it distinctly from
                        // the temperate surface above. Overrides KIT_UNDERWATER
                        // for below-water caves — the cave palette wins there.
                        ws.SetKitIdWorld(wx, wy, wz, KIT_CAVE);
                    }
                }
            }
        }
    }

    private static bool IsAirOrWater(WorldState ws, int wx, int wy, int wz)
    {
        var v = ws.GetVoxelWorld(wx, wy, wz);
        return v == VoxelType.Air || v == VoxelType.Water;
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
    private static void StampProceduralOverlays(WorldState ws)
    {
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

    // Test placement for the painted detail-sprite scatter. Walks every
    // temperate-kit surface voxel and stamps DetailGroup=1 with a noise-driven
    // strength wherever detailNoise > threshold. The runtime scatter pass
    // looks up DetailGroups[0] for group=1 and silently does nothing if the
    // palette is empty, so this is safe to leave on even before any sprite art
    // is authored.
    private static void StampDetailScatter(WorldState ws)
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
        FastNoiseLite terrainNoise, FastNoiseLite tunnelNoise,
        FastNoiseLite grassNoise, FastNoiseLite pathNoise, FastNoiseLite riverNoise, FastNoiseLite forestNoise,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        // Grass requires both the noise-derived "would be grassy" check AND a
        // solid voxel at y=0 with air at y=1 in the actual world data — caves
        // can carve through the surface, in which case props would otherwise
        // float over an open hole.
        bool IsGrassyAt(int wx, int wz)
        {
            if (!IsFlatDryGrassAt(wx, wz, terrainNoise, pathNoise, riverNoise, genData,
                spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ))
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
            ws.AddEntity(new PropSimState(PropType.Tree,
                new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
                genData.TreeScene));
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

                ws.AddEntity(new PropSimState(PropType.TallGrass, new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f), genData.TallGrassScene));
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
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
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
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
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
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
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
                ws.AddEntity(new ChestSimState(new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
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
