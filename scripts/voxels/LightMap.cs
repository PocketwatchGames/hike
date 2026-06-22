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

    protected override void EncodeChunkPixels(ChunkState chunk, int baseX, int baseY, int baseZ)
    {
        for (int lz = 0; lz < ChunkState.SIZE; lz++)
        {
            byte[] pixels = _slicePixels[baseZ + lz];
            for (int ly = 0; ly < ChunkState.SIZE; ly++)
            {
                int rowOffset = ((baseY + ly) * _width + baseX) * 4;
                for (int lx = 0; lx < ChunkState.SIZE; lx++)
                {
                    int sun = (chunk.GetSunlight(lx, ly, lz) * 255) / LightEngine.MAX_LIGHT;
                    chunk.GetBlockLight(lx, ly, lz, out int br, out int bg, out int bb);
                    if (br > 255) { br = 255; }
                    if (bg > 255) { bg = 255; }
                    if (bb > 255) { bb = 255; }
                    int o = rowOffset + lx * 4;
                    pixels[o + 0] = (byte)sun;
                    pixels[o + 1] = (byte)br;
                    pixels[o + 2] = (byte)bg;
                    pixels[o + 3] = (byte)bb;
                }
            }
        }
    }
}
