using System.Collections.Generic;
using Godot;

public class WorldData
{
    public const int SIZE_X = 8;
    public const int SIZE_Y = 3;
    public const int SIZE_Z = 8;

    public readonly Vector3I Min;
    public readonly Vector3I Max;

    private readonly Dictionary<Vector3I, ChunkData> _chunks = new();

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
}
