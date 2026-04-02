using Godot;

public class ChunkData
{
    public const int SIZE = 16;

    public readonly Vector3I ChunkCoord;
    public readonly VoxelType[,,] Voxels;
    public readonly byte[,,] Light; // High nibble = sunlight (0-15), low nibble = block light (0-15)

    public ChunkData(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Voxels = new VoxelType[SIZE, SIZE, SIZE];
        Light = new byte[SIZE, SIZE, SIZE];
    }

    public VoxelType GetVoxel(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return VoxelType.Air;
        }
        return Voxels[x, y, z];
    }

    public int GetSunlight(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return (Light[x, y, z] >> 4) & 0x0F;
    }

    public void SetSunlight(int x, int y, int z, int level)
    {
        Light[x, y, z] = (byte)((Light[x, y, z] & 0x0F) | ((level & 0x0F) << 4));
    }

    public int GetBlockLight(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return Light[x, y, z] & 0x0F;
    }

    public void SetBlockLight(int x, int y, int z, int level)
    {
        Light[x, y, z] = (byte)((Light[x, y, z] & 0xF0) | (level & 0x0F));
    }
}
