using Godot;

public class ChunkData
{
    public const int SIZE = 16;

    public readonly Vector3I ChunkCoord;
    public readonly VoxelType[,,] Voxels;

    public ChunkData(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Voxels = new VoxelType[SIZE, SIZE, SIZE];
        Generate();
    }

    private void Generate()
    {
        if (ChunkCoord.Y < 0)
        {
            for (int x = 0; x < SIZE; x++)
            {
                for (int y = 0; y < SIZE; y++)
                {
                    for (int z = 0; z < SIZE; z++)
                    {
                        Voxels[x, y, z] = VoxelType.Stone;
                    }
                }
            }
        }
        else if (ChunkCoord.Y == 0)
        {
            for (int x = 0; x < SIZE; x++)
            {
                for (int z = 0; z < SIZE; z++)
                {
                    Voxels[x, 0, z] = VoxelType.Grass;
                }
            }
        }
    }

    public VoxelType GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return VoxelType.Air;
        }
        return Voxels[x, y, z];
    }
}
