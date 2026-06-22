using Godot;

// Per-voxel light texture sampled by the world shaders via the `light_map`
// global. RGBA8: R = sun mask (0..255), G/B/A = block light R/G/B (summed per
// channel at deposit, byte-saturated). See WindowedVolumeMap for the toroidal
// player-centric windowing all five maps share.
public class LightMap : WindowedVolumeMap
{
    public LightMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, 4, Image.Format.Rgba8)
    {
        InitialEncodeAndUpload(world);
    }

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        const int cells = ChunkState.SIZE;
        for (int lz = 0; lz < cells; lz++)
        {
            for (int ly = 0; ly < cells; ly++)
            {
                int rowOffset = ((lz * cells + ly) * cells) * 4;
                for (int lx = 0; lx < cells; lx++)
                {
                    int sun = (chunk.GetSunlight(lx, ly, lz) * 255) / LightEngine.MAX_LIGHT;
                    chunk.GetBlockLight(lx, ly, lz, out int br, out int bg, out int bb);
                    if (br > 255) { br = 255; }
                    if (bg > 255) { bg = 255; }
                    if (bb > 255) { bb = 255; }
                    int o = rowOffset + lx * 4;
                    dst[o + 0] = (byte)sun;
                    dst[o + 1] = (byte)br;
                    dst[o + 2] = (byte)bg;
                    dst[o + 3] = (byte)bb;
                }
            }
        }
    }
}
