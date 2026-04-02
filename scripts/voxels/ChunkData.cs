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
