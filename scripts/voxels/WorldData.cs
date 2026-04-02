using System;
using System.Collections.Generic;
using Godot;

public class WorldData
{
    public const int SIZE_X = 8;
    public const int SIZE_Y = 3;
    public const int SIZE_Z = 8;

    private const int TREES_PER_CHUNK_MIN = 0;
    private const int TREES_PER_CHUNK_MAX = 4;
    private const int STRUCTURE_COUNT = 3;
    private const int BUILDING_MIN_DIMENSION = 12;
    private const int BUILDING_MAX_DIMENSION = 24;
    private const int BUILDING_HEIGHT = 4;
    private const int FLOORS_MIN = 1;
    private const int FLOORS_MAX = 3;
    private const int TORCHES_PER_HOUSE_MIN = 1;
    private const int TORCHES_PER_HOUSE_MAX = 3;

    public readonly Vector3I Min;
    public readonly Vector3I Max;

    private readonly Dictionary<Vector3I, ChunkData> _chunks = new();
    private readonly Dictionary<Vector3I, List<PropData>> _props = new();

    public WorldData()
    {
        Min = new Vector3I(-SIZE_X / 2, -1, -SIZE_Z / 2);
        Max = new Vector3I(Min.X + SIZE_X - 1, Min.Y + SIZE_Y - 1, Min.Z + SIZE_Z - 1);
        Generate();
    }

    // World-coordinate accessors for cross-chunk light propagation

    private static Vector3I WorldToChunkCoord(int wx, int wy, int wz)
    {
        return new Vector3I(
            (int)Math.Floor((double)wx / ChunkData.SIZE),
            (int)Math.Floor((double)wy / ChunkData.SIZE),
            (int)Math.Floor((double)wz / ChunkData.SIZE)
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
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return VoxelType.Air;
        }
        return chunk.Voxels[Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE)];
    }

    public int GetSunlightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return 0;
        }
        return chunk.GetSunlight(Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE));
    }

    public void SetSunlightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return;
        }
        chunk.SetSunlight(Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE), level);
    }

    public int GetBlockLightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return 0;
        }
        return chunk.GetBlockLight(Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE));
    }

    public void SetBlockLightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return;
        }
        chunk.SetBlockLight(Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE), level);
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkData chunk))
        {
            return;
        }
        chunk.Voxels[Mod(wx, ChunkData.SIZE), Mod(wy, ChunkData.SIZE), Mod(wz, ChunkData.SIZE)] = type;
    }

    public int GetLightLevelWorld(int wx, int wy, int wz)
    {
        return Math.Max(GetSunlightWorld(wx, wy, wz), GetBlockLightWorld(wx, wy, wz));
    }

    public ChunkData GetChunk(Vector3I coord)
    {
        _chunks.TryGetValue(coord, out ChunkData data);
        return data;
    }

    public bool ContainsChunk(Vector3I coord)
    {
        return _chunks.ContainsKey(coord);
    }

    public List<PropData> GetProps(Vector3I coord)
    {
        _props.TryGetValue(coord, out List<PropData> props);
        return props;
    }

    private void Generate()
    {
        for (int x = Min.X; x <= Max.X; x++)
        {
            for (int y = Min.Y; y <= Max.Y; y++)
            {
                for (int z = Min.Z; z <= Max.Z; z++)
                {
                    var coord = new Vector3I(x, y, z);
                    var chunk = new ChunkData(coord);
                    GenerateChunk(chunk);
                    _chunks[coord] = chunk;
                }
            }
        }

        // Generate world-space structures after all terrain chunks exist
        GenerateStructures();

        // Generate props on surface chunks after all voxels are placed
        var blockLightSources = new List<(Vector3 position, int level)>();
        for (int x = Min.X; x <= Max.X; x++)
        {
            for (int z = Min.Z; z <= Max.Z; z++)
            {
                var coord = new Vector3I(x, 0, z);
                GenerateProps(coord, blockLightSources);
            }
        }

        // Compute volumetric lighting after all geometry and light sources are placed
        LightEngine.ComputeLighting(this, blockLightSources);
    }

    private static void GenerateChunk(ChunkData data)
    {
        if (data.ChunkCoord.Y < 0)
        {
            for (int x = 0; x < ChunkData.SIZE; x++)
            {
                for (int y = 0; y < ChunkData.SIZE; y++)
                {
                    for (int z = 0; z < ChunkData.SIZE; z++)
                    {
                        data.Voxels[x, y, z] = VoxelType.Stone;
                    }
                }
            }
        }
        else if (data.ChunkCoord.Y == 0)
        {
            for (int x = 0; x < ChunkData.SIZE; x++)
            {
                for (int z = 0; z < ChunkData.SIZE; z++)
                {
                    data.Voxels[x, 0, z] = VoxelType.Grass;
                }
            }
        }
    }

    private void GenerateProps(Vector3I chunkCoord, List<(Vector3 position, int level)> blockLightSources)
    {
        ChunkData data = _chunks[chunkCoord];
        var rng = new Random(HashCode.Combine(chunkCoord.X, chunkCoord.Z, 7919));
        int treeCount = rng.Next(TREES_PER_CHUNK_MIN, TREES_PER_CHUNK_MAX + 1);
        var props = new List<PropData>();

        for (int i = 0; i < treeCount; i++)
        {
            int localX = rng.Next(1, ChunkData.SIZE - 1);
            int localZ = rng.Next(1, ChunkData.SIZE - 1);

            // Only place on grass with clear air above (up through max building height)
            if (data.Voxels[localX, 0, localZ] != VoxelType.Grass)
            {
                continue;
            }
            bool blocked = false;
            for (int y = 1; y <= BUILDING_HEIGHT; y++)
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

            float worldX = chunkCoord.X * ChunkData.SIZE + localX + 0.5f;
            float worldY = chunkCoord.Y * ChunkData.SIZE + 1f;
            float worldZ = chunkCoord.Z * ChunkData.SIZE + localZ + 0.5f;

            props.Add(new PropData(PropType.Tree, new Vector3(worldX, worldY, worldZ)));
        }

        // Place torches inside houses
        GenerateTorches(data, chunkCoord, rng, props, blockLightSources);

        if (props.Count > 0)
        {
            _props[chunkCoord] = props;
        }
    }

    private static void GenerateTorches(ChunkData data, Vector3I chunkCoord, Random rng,
        List<PropData> props, List<(Vector3 position, int level)> blockLightSources)
    {
        // Detect if this chunk has a house by checking for Wood walls at y=1
        bool hasHouse = false;
        int houseMinX = ChunkData.SIZE, houseMaxX = 0;
        int houseMinZ = ChunkData.SIZE, houseMaxZ = 0;
        for (int x = 0; x < ChunkData.SIZE; x++)
        {
            for (int z = 0; z < ChunkData.SIZE; z++)
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

        int torchCount = rng.Next(TORCHES_PER_HOUSE_MIN, TORCHES_PER_HOUSE_MAX + 1);
        for (int i = 0; i < torchCount; i++)
        {
            int localX = rng.Next(interiorMinX, interiorMaxX + 1);
            int localZ = rng.Next(interiorMinZ, interiorMaxZ + 1);

            // Only place on floor with air above
            if (data.GetVoxel(localX, 1, localZ) != VoxelType.Air)
            {
                continue;
            }

            float worldX = chunkCoord.X * ChunkData.SIZE + localX + 0.5f;
            float worldY = chunkCoord.Y * ChunkData.SIZE + 1f;
            float worldZ = chunkCoord.Z * ChunkData.SIZE + localZ + 0.5f;

            var torchPos = new Vector3(worldX, worldY, worldZ);
            props.Add(new PropData(PropType.Torch, torchPos));

            PropDefinition torchDef = PropDefinition.Definitions[PropType.Torch];
            blockLightSources.Add((torchPos, torchDef.LightEmission));
        }
    }

    private void GenerateStructures()
    {
        int worldMinX = Min.X * ChunkData.SIZE;
        int worldMaxX = (Max.X + 1) * ChunkData.SIZE - 1;
        int worldMinZ = Min.Z * ChunkData.SIZE;
        int worldMaxZ = (Max.Z + 1) * ChunkData.SIZE - 1;

        var rng = new Random(HashCode.Combine(SIZE_X, SIZE_Z, 42));

        // Fixed building just north of spawn (player spawns at 0,4,0)
        int spawnBuildingWidth = 20;
        int spawnBuildingDepth = 16;
        GenerateHouse(rng, -spawnBuildingWidth / 2, -5, spawnBuildingWidth, spawnBuildingDepth, 3);

    }

    private void GenerateHouse(Random rng, int originX, int originZ, int widthX, int widthZ, int numFloors)
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
            for (int dy = 0; dy < DOOR_HEIGHT; dy++)
            {
                int wy = baseY + dy;
                if (wall[3] <= 1)
                {
                    SetVoxelWorld(pos, wy, wall[2], VoxelType.Air);
                }
                else
                {
                    SetVoxelWorld(wall[2], wy, pos, VoxelType.Air);
                }
            }
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
