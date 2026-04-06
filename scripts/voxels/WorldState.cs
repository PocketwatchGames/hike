using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;

    private readonly Dictionary<Vector3I, ChunkState> _chunks = new();
    private readonly Dictionary<Vector3I, List<PropSpawnState>> _props = new();
    private readonly Dictionary<Vector3I, List<MobSpawnState>> _mobs = new();
    private readonly Dictionary<Vector3I, List<InteractiveSpawnState>> _interactives = new();

    public WorldState(WorldGenData genData)
    {
        Min = new Vector3I(-genData.SizeX / 2, -1, -genData.SizeZ / 2);
        Max = new Vector3I(Min.X + genData.SizeX - 1, Min.Y + genData.SizeY - 1, Min.Z + genData.SizeZ - 1);

        Generate(genData);
    }

    // World-coordinate accessors for cross-chunk light propagation

    private static Vector3I WorldToChunkCoord(int wx, int wy, int wz)
    {
        return new Vector3I(
            (int)Math.Floor((double)wx / ChunkState.SIZE),
            (int)Math.Floor((double)wy / ChunkState.SIZE),
            (int)Math.Floor((double)wz / ChunkState.SIZE)
        );
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }

    public bool IsInBounds(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        return _chunks.ContainsKey(cc);
    }

    public VoxelType GetVoxelWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return VoxelType.Air;
        }
        return chunk.Voxels[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    public int GetSunlightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetSunlightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
    }

    public int GetBlockLightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetBlockLightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.Voxels[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)] = type;
    }

    public int GetLightLevelWorld(int wx, int wy, int wz)
    {
        return Math.Max(GetSunlightWorld(wx, wy, wz), GetBlockLightWorld(wx, wy, wz));
    }

    public ChunkState GetChunk(Vector3I coord)
    {
        _chunks.TryGetValue(coord, out ChunkState data);
        return data;
    }

    public bool ContainsChunk(Vector3I coord)
    {
        return _chunks.ContainsKey(coord);
    }

    public List<PropSpawnState> GetProps(Vector3I coord)
    {
        _props.TryGetValue(coord, out List<PropSpawnState> props);
        return props;
    }

    public void AddProp(PropSpawnState prop)
    {
        Vector3I coord = VoxelWorld.WorldToChunkCoord(prop.WorldPosition);
        if (!_props.TryGetValue(coord, out List<PropSpawnState> props))
        {
            props = new List<PropSpawnState>();
            _props[coord] = props;
        }
        props.Add(prop);
    }

   public List<MobSpawnState> GetMobs(Vector3I coord)
    {
        _mobs.TryGetValue(coord, out List<MobSpawnState> mobs);
        return mobs;
    }

    public List<InteractiveSpawnState> GetInteractives(Vector3I coord)
    {
        _interactives.TryGetValue(coord, out List<InteractiveSpawnState> interactives);
        return interactives;
    }

    private void AddInteractive(InteractiveSpawnState data)
    {
        Vector3I cc = WorldToChunkCoord(
            (int)Math.Floor(data.WorldPosition.X),
            (int)Math.Floor(data.WorldPosition.Y),
            (int)Math.Floor(data.WorldPosition.Z)
        );
        if (!_interactives.TryGetValue(cc, out List<InteractiveSpawnState> list))
        {
            list = new List<InteractiveSpawnState>();
            _interactives[cc] = list;
        }
        list.Add(data);
    }

    private void Generate(WorldGenData genData)
    {
        var terrainNoise = new FastNoiseLite();
        terrainNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        terrainNoise.Seed = genData.TerrainNoiseSeed;
        terrainNoise.Frequency = 0.02f;
        terrainNoise.FractalOctaves = 4;

        var caveNoise = new FastNoiseLite();
        caveNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        caveNoise.Seed = genData.CaveNoiseSeed;
        caveNoise.Frequency = 0.05f;
        caveNoise.FractalOctaves = 2;

        var grassNoise = new FastNoiseLite();
        grassNoise.NoiseType = FastNoiseLite.NoiseTypeEnum.Perlin;
        grassNoise.Seed = genData.GrassNoiseSeed;
        grassNoise.Frequency = 0.1f;
        grassNoise.FractalOctaves = 2;

        int spawnFlatMinX = genData.SpawnBuildingOriginX - genData.SpawnFlatPadding;
        int spawnFlatMaxX = genData.SpawnBuildingOriginX + genData.SpawnBuildingWidth - 1 + genData.SpawnFlatPadding;
        int spawnFlatMinZ = genData.SpawnBuildingOriginZ - genData.SpawnFlatPadding;
        int spawnFlatMaxZ = genData.SpawnBuildingOriginZ + genData.SpawnBuildingDepth - 1 + genData.SpawnFlatPadding;

        for (int x = Min.X; x <= Max.X; x++)
        {
            for (int y = Min.Y; y <= Max.Y; y++)
            {
                for (int z = Min.Z; z <= Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkState(coord);
                    GenerateChunk(chunk, genData, terrainNoise, caveNoise,
                        spawnFlatMinX, spawnFlatMaxX, spawnFlatMinZ, spawnFlatMaxZ);
                    _chunks[coord] = chunk;
                }
            }
        }

        // Generate world-space structures after all terrain chunks exist
        GenerateStructures(genData);

        // Generate props on surface chunks after all voxels are placed
        var blockLightSources = new List<(Vector3 position, int level)>();
        for (int x = Min.X; x <= Max.X; x++)
        {
            for (int z = Min.Z; z <= Max.Z; z++)
            {
                var coord = new Vector3I(x, 0, z);
                GenerateProps(coord, genData, grassNoise, blockLightSources);
            }
        }

        // Compute volumetric lighting after all geometry and light sources are placed
        LightEngine.ComputeLighting(this, blockLightSources);
    }

    public void UpdateLightingAt(List<Vector3I> changedPositions)
    {
        LightEngine.UpdateLighting(this, changedPositions);
    }

    public void PropagateLightingAt(List<Vector3I> sourcePositions)
    {
        LightEngine.PropagateLighting(this, sourcePositions);
    }

    private void GenerateChunk(ChunkState data, WorldGenData genData,
        FastNoiseLite terrainNoise, FastNoiseLite caveNoise,
        int spawnFlatMinX, int spawnFlatMaxX, int spawnFlatMinZ, int spawnFlatMaxZ)
    {
        int chunkWorldX = data.ChunkCoord.X * ChunkState.SIZE;
        int chunkWorldY = data.ChunkCoord.Y * ChunkState.SIZE;
        int chunkWorldZ = data.ChunkCoord.Z * ChunkState.SIZE;

        for (int x = 0; x < ChunkState.SIZE; x++)
        {
            for (int z = 0; z < ChunkState.SIZE; z++)
            {
                int wx = chunkWorldX + x;
                int wz = chunkWorldZ + z;

                float noiseVal = terrainNoise.GetNoise2D(wx, wz);
                bool isSpawnFlat = wx >= spawnFlatMinX && wx <= spawnFlatMaxX
                    && wz >= spawnFlatMinZ && wz <= spawnFlatMaxZ;
                float rawHeight = isSpawnFlat ? 0f : Math.Max(0f, genData.ElevationMultiplier * noiseVal);
                int solidHeight = (int)rawHeight;
                bool hasSlab = !isSpawnFlat && (rawHeight - solidHeight) >= 0.5f;

                for (int y = 0; y < ChunkState.SIZE; y++)
                {
                    int wy = chunkWorldY + y;

                    // Determine terrain fill before cave carving
                    bool isTerrainSlab;
                    if (hasSlab && wy == solidHeight + 1)
                    {
                        isTerrainSlab = true;
                    }
                    else if (wy <= solidHeight)
                    {
                        isTerrainSlab = false;
                    }
                    else
                    {
                        continue;
                    }

                    // Dual-sample cave noise at bottom and top halves of the voxel
                    bool bottomSolid = true;
                    bool topSolid = true;
                    if (wy > 0)
                    {
                        float caveLow = caveNoise.GetNoise3D(wx, wy + 0.25f, wz);
                        float caveHigh = caveNoise.GetNoise3D(wx, wy + 0.75f, wz);
                        bottomSolid = caveLow <= genData.CaveThreshold;
                        topSolid = caveHigh <= genData.CaveThreshold;
                    }

                    // For terrain surface slabs, only the bottom half has geometry
                    if (isTerrainSlab)
                    {
                        if (bottomSolid)
                        {
                            data.Voxels[x, y, z] = VoxelType.GrassSlabBottom;
                        }
                        continue;
                    }

                    // Determine material by depth from surface
                    VoxelType fullType;
                    VoxelType bottomSlabType;
                    VoxelType topSlabType;
                    if (wy == solidHeight && !hasSlab)
                    {
                        fullType = VoxelType.Grass;
                        bottomSlabType = VoxelType.GrassSlabBottom;
                        topSlabType = VoxelType.StoneSlabTop;
                    }
                    else if (wy >= solidHeight - genData.DirtDepth)
                    {
                        fullType = VoxelType.Dirt;
                        bottomSlabType = VoxelType.DirtSlabBottom;
                        topSlabType = VoxelType.StoneSlabTop;
                    }
                    else
                    {
                        fullType = VoxelType.Stone;
                        bottomSlabType = VoxelType.StoneSlabBottom;
                        topSlabType = VoxelType.StoneSlabTop;
                    }

                    if (bottomSolid && topSolid)
                    {
                        data.Voxels[x, y, z] = fullType;
                    }
                    else if (bottomSolid)
                    {
                        data.Voxels[x, y, z] = bottomSlabType;
                    }
                    else if (topSolid)
                    {
                        data.Voxels[x, y, z] = topSlabType;
                    }
                }
            }
        }
    }

    private void GenerateProps(Vector3I chunkCoord, WorldGenData genData,
        FastNoiseLite grassNoise, List<(Vector3 position, int level)> blockLightSources)
    {
        ChunkState data = _chunks[chunkCoord];
        var rng = new Random(HashCode.Combine(chunkCoord.X, chunkCoord.Z, 7919));
        int treeCount = rng.Next(genData.TreesPerChunkMin, genData.TreesPerChunkMax + 1);
        var props = new List<PropSpawnState>();
        var mobs = new List<MobSpawnState>();

        for (int i = 0; i < treeCount; i++)
        {
            int localX = rng.Next(1, ChunkState.SIZE - 1);
            int localZ = rng.Next(1, ChunkState.SIZE - 1);

            // Only place on grass with clear air above (up through max building height)
            if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
            {
                continue;
            }
            bool blocked = false;
            for (int y = 1; y <= genData.BuildingHeight; y++)
            {
                if (data.GetVoxel(localX, y, localZ) != VoxelType.Air)
                {
                    blocked = true;
                    break;
                }
            }
            if (blocked)
            {
                continue;
            }

            float worldX = chunkCoord.X * ChunkState.SIZE + localX + 0.5f;
            float worldY = chunkCoord.Y * ChunkState.SIZE + 1f;
            float worldZ = chunkCoord.Z * ChunkState.SIZE + localZ + 0.5f;

            props.Add(new PropSpawnState(PropType.Tree, new Vector3(worldX, worldY, worldZ), genData.TreeScene));
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
                if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
                {
                    continue;
                }
                if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
                {
                    continue;
                }

                props.Add(new PropSpawnState(PropType.TallGrass, new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f), genData.TallGrassScene));
            }
        }

        // Generate goblins on grass surfaces
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
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
                mobs.Add(new MobSpawnState(
                    new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
                    0f,
                    genData.GoblinScene
                ));
            }
        }

        // Generate loot on grass surfaces
        for (int localX = 0; localX < ChunkState.SIZE; localX++)
        {
            for (int localZ = 0; localZ < ChunkState.SIZE; localZ++)
            {
                if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
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
                props.Add(new PropSpawnState(
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
                if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
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
                AddInteractive(new ChestSpawnState(new Vector3(wx + 0.5f, chunkCoord.Y * ChunkState.SIZE + 1f, wz + 0.5f),
                    genData.ChestScene,
                    lootCount,
                    genData.LootScene));
            }
        }

        // Place torches inside houses as interactives
        GenerateTorches(data, chunkCoord, genData, rng, blockLightSources);

        if (props.Count > 0)
        {
            _props[chunkCoord] = props;
        }
        if (mobs.Count > 0)
        {
            _mobs[chunkCoord] = mobs;
        }
    }

    private void GenerateTorches(ChunkState data, Vector3I chunkCoord, WorldGenData genData,
        Random rng, List<(Vector3 position, int level)> blockLightSources)
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

        const int TORCH_LIGHT_EMISSION = 14;
        int torchCount = rng.Next(genData.TorchesPerHouseMin, genData.TorchesPerHouseMax + 1);
        for (int i = 0; i < torchCount; i++)
        {
            int localX = rng.Next(interiorMinX, interiorMaxX + 1);
            int localZ = rng.Next(interiorMinZ, interiorMaxZ + 1);

            // Only place on floor with air above
            if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
            {
                continue;
            }

            float worldX = chunkCoord.X * ChunkState.SIZE + localX + 0.5f;
            float worldY = chunkCoord.Y * ChunkState.SIZE + 1f;
            float worldZ = chunkCoord.Z * ChunkState.SIZE + localZ + 0.5f;

            var torchPos = new Vector3(worldX, worldY, worldZ);
            AddInteractive(new TorchSpawnState(torchPos, genData.TorchScene));

            blockLightSources.Add((torchPos, TORCH_LIGHT_EMISSION));
        }
    }

    private void GenerateStructures(WorldGenData genData)
    {
        var rng = new Random(HashCode.Combine(genData.SizeX, genData.SizeZ, 42));

        // Fixed building just north of spawn (player spawns at 0,4,0)
        GenerateHouse(rng, genData, genData.SpawnBuildingOriginX, genData.SpawnBuildingOriginZ,
            genData.SpawnBuildingWidth, genData.SpawnBuildingDepth, 3);
    }

    private void GenerateHouse(Random rng, WorldGenData genData, int originX, int originZ, int widthX, int widthZ, int numFloors)
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
                        SetVoxelWorld(wx, wy, wz, isCeiling ? VoxelType.Stone : VoxelType.Wood);
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
                SetVoxelWorld(doorWx, wy, doorWz, VoxelType.Barrier);
            }
            AddInteractive(new DoorSpawnState(new Vector3(doorWx + 0.5f, baseY, doorWz + 0.5f),
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
                SetVoxelWorld(pos, windowY, wall[2], VoxelType.Air);
            }
            else
            {
                SetVoxelWorld(wall[2], windowY, pos, VoxelType.Air);
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
                    GenerateStaircase(cornerAX, cornerAZ, floor, WALL_TOP, baseY);
                }
                else
                {
                    GenerateStaircase(cornerBX, cornerBZ, floor, WALL_TOP, baseY);
                }
            }
        }
    }

    // Staircase spiral pattern: (dx, dz) offsets from center, actions per y-level
    // 0=keep, 1=full block, 2=slab, 3=air
    private const int STAIR_KEEP = 0;
    private const int STAIR_FULL = 1;
    private const int STAIR_SLAB = 2;
    private const int STAIR_AIR = 3;

    private static readonly (int dx, int dz, int[] yActions)[] StaircasePattern =
    {
        (-1,  1, new[] { STAIR_SLAB, STAIR_AIR,  STAIR_AIR,  STAIR_KEEP }),
        (-1,  0, new[] { STAIR_FULL, STAIR_AIR,  STAIR_AIR,  STAIR_AIR  }),
        (-1, -1, new[] { STAIR_FULL, STAIR_SLAB, STAIR_AIR,  STAIR_AIR  }),
        ( 0, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_AIR,  STAIR_AIR  }),
        ( 1, -1, new[] { STAIR_FULL, STAIR_FULL, STAIR_SLAB, STAIR_AIR  }),
        ( 1,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_AIR  }),
        ( 1,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_SLAB }),
        ( 0,  0, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
        ( 0,  1, new[] { STAIR_FULL, STAIR_FULL, STAIR_FULL, STAIR_FULL }),
    };

    private void GenerateStaircase(int centerX, int centerZ, int floor, int wallTop, int baseY)
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
                        SetVoxelWorld(wx, wy, wz, VoxelType.Wood);
                        break;
                    case STAIR_SLAB:
                        SetVoxelWorld(wx, wy, wz, VoxelType.WoodSlabBottom);
                        break;
                    case STAIR_AIR:
                        SetVoxelWorld(wx, wy, wz, VoxelType.Air);
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
