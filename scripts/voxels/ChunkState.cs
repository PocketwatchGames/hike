using Godot;

public class ChunkState
{
    public const int SIZE = 16;

    public readonly Vector3I ChunkCoord;
    public readonly VoxelType[,,] Voxels;

    // Sunlight: byte 0..LightEngine.MAX_LIGHT. Single source, max-fill BFS so
    // there's no overlap to worry about. Color tinting (sunset, etc.) happens
    // in the shader via the sun_color uniform — the storage is just a mask.
    public readonly byte[,,] Sunlight;

    // Block light: per-color-channel additive sums of post-pow contributions
    // from registered LightSources. "Post-pow" means each light's BFS stores
    // pow(level/MAX_LIGHT, exp) * 255 * color.channel at deposit time, so the
    // shader can sum overlaps with correct perceptual brightness instead of
    // getting a "brilliance bonus" from sum-then-pow. Ushort holds the raw
    // sum so subtraction stays exact when stacked lights are removed; the
    // LightMap upload clamps to 0-255 per channel for the GPU.
    public readonly ushort[,,] BlockLightR;
    public readonly ushort[,,] BlockLightG;
    public readonly ushort[,,] BlockLightB;

    // Fog density: 0 = clear air, 255 = thickest fog. Two consumers:
    //   - LightEngine BFS uses this as extra per-step falloff so torches dim
    //     faster in fog (step 5 of the roadmap).
    //   - A Godot FogVolume binds this as its density texture so god rays /
    //     volumetric scattering carve correctly through caves vs foggy
    //     clearings (step 6 of the roadmap).
    // Chunk-local by design — an unloaded neighbor reads as 0, which is the
    // correct streaming default (no fog where there's no data).
    public readonly byte[,,] FogDensity;

    public ChunkState(Vector3I chunkCoord)
    {
        ChunkCoord = chunkCoord;
        Voxels = new VoxelType[SIZE, SIZE, SIZE];
        Sunlight = new byte[SIZE, SIZE, SIZE];
        BlockLightR = new ushort[SIZE, SIZE, SIZE];
        BlockLightG = new ushort[SIZE, SIZE, SIZE];
        BlockLightB = new ushort[SIZE, SIZE, SIZE];
        FogDensity = new byte[SIZE, SIZE, SIZE];
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
        return Sunlight[x, y, z];
    }

    public void SetSunlight(int x, int y, int z, int level)
    {
        Sunlight[x, y, z] = (byte)level;
    }

    public void GetBlockLight(int x, int y, int z, out int r, out int g, out int b)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            r = 0;
            g = 0;
            b = 0;
            return;
        }
        r = BlockLightR[x, y, z];
        g = BlockLightG[x, y, z];
        b = BlockLightB[x, y, z];
    }

    public void AddBlockLight(int x, int y, int z, int r, int g, int b)
    {
        int sr = BlockLightR[x, y, z] + r;
        int sg = BlockLightG[x, y, z] + g;
        int sb = BlockLightB[x, y, z] + b;
        BlockLightR[x, y, z] = sr > ushort.MaxValue ? ushort.MaxValue : (ushort)sr;
        BlockLightG[x, y, z] = sg > ushort.MaxValue ? ushort.MaxValue : (ushort)sg;
        BlockLightB[x, y, z] = sb > ushort.MaxValue ? ushort.MaxValue : (ushort)sb;
    }

    public void SubtractBlockLight(int x, int y, int z, int r, int g, int b)
    {
        int sr = BlockLightR[x, y, z] - r;
        int sg = BlockLightG[x, y, z] - g;
        int sb = BlockLightB[x, y, z] - b;
        BlockLightR[x, y, z] = sr < 0 ? (ushort)0 : (ushort)sr;
        BlockLightG[x, y, z] = sg < 0 ? (ushort)0 : (ushort)sg;
        BlockLightB[x, y, z] = sb < 0 ? (ushort)0 : (ushort)sb;
    }

    public int GetFog(int x, int y, int z)
    {
        if (x < 0 || x >= SIZE || y < 0 || y >= SIZE || z < 0 || z >= SIZE)
        {
            return 0;
        }
        return FogDensity[x, y, z];
    }

    public void SetFog(int x, int y, int z, int density)
    {
        if (density < 0) { density = 0; }
        if (density > 255) { density = 255; }
        FogDensity[x, y, z] = (byte)density;
    }
}
