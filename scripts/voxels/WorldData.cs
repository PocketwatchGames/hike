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
    private const int BUILDING_HEIGHT = 4;

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

        // Generate props on surface chunks after all voxels are placed
        for (int x = Min.X; x <= Max.X; x++)
        {
            for (int z = Min.Z; z <= Max.Z; z++)
            {
                var coord = new Vector3I(x, 0, z);
                GenerateProps(coord);
            }
        }
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

            GenerateHouse(data);
        }
    }

    private void GenerateProps(Vector3I chunkCoord)
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

        if (props.Count > 0)
        {
            _props[chunkCoord] = props;
        }
    }

    private static void GenerateHouse(ChunkData data)
    {
        const int CEILING_HEIGHT = 3;
        const int DOOR_HEIGHT = 2;
        const int MIN_DIMENSION = 5;
        const int MAX_DIMENSION = 9;
        const int WINDOW_Y = 2;
        const int WALL_TOP = CEILING_HEIGHT + 1;

        var rng = new Random(HashCode.Combine(data.ChunkCoord.X, data.ChunkCoord.Z));
        int widthX = rng.Next(MIN_DIMENSION, MAX_DIMENSION + 1);
        int widthZ = rng.Next(MIN_DIMENSION, MAX_DIMENSION + 1);

        int startX = (ChunkData.SIZE - widthX) / 2;
        int startZ = (ChunkData.SIZE - widthZ) / 2;
        int endX = startX + widthX - 1;
        int endZ = startZ + widthZ - 1;

        // Walls and roof
        for (int y = 1; y <= WALL_TOP; y++)
        {
            for (int x = startX; x <= endX; x++)
            {
                for (int z = startZ; z <= endZ; z++)
                {
                    bool isWall = x == startX || x == endX || z == startZ || z == endZ;
                    bool isRoof = y == WALL_TOP;
                    if (isWall || isRoof)
                    {
                        data.Voxels[x, y, z] = isRoof ? VoxelType.Stone : VoxelType.Wood;
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
        int windowCount = rng.Next(1, 5);

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
            for (int y = 1; y <= DOOR_HEIGHT; y++)
            {
                if (wall[3] <= 1)
                {
                    data.Voxels[pos, y, wall[2]] = VoxelType.Air;
                }
                else
                {
                    data.Voxels[wall[2], y, pos] = VoxelType.Air;
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
                data.Voxels[pos, WINDOW_Y, wall[2]] = VoxelType.Air;
            }
            else
            {
                data.Voxels[wall[2], WINDOW_Y, pos] = VoxelType.Air;
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
