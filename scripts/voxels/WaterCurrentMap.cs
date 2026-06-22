using Godot;

// Coarse water-current texture (Rg8, one cell per ENV_VOXELS_PER_CELL voxel
// block), sampled by the water shader via `water_current_map`. R = current X,
// G = current Z, byte-128-zero encoded so the shader does `.rg * 2.0 - 1.0` to
// recover [-1, 1] and advect the surface ripple pattern along the flow. See
// WindowedVolumeMap for the shared toroidal windowing.
public class WaterCurrentMap : WindowedVolumeMap
{
    public WaterCurrentMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.ENV_SUBGRID_SIZE, 2, Image.Format.Rg8)
    {
        // Seed every byte to 128 = signed zero (default 0 would decode to max
        // negative current under the byte-128-is-zero convention).
        for (int z = 0; z < _slicePixels.Length; z++)
        {
            byte[] slice = _slicePixels[z];
            for (int i = 0; i < slice.Length; i++)
            {
                slice[i] = 128;
            }
        }
        InitialEncodeAndUpload(world);
    }

    protected override void EncodeChunkPixels(ChunkState chunk, int baseX, int baseY, int baseZ)
    {
        for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
        {
            byte[] pixels = _slicePixels[baseZ + sz];
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                int rowOffset = ((baseY + sy) * _width + baseX) * 2;
                for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
                {
                    int o = rowOffset + sx * 2;
                    pixels[o + 0] = chunk.CurrentX[sx, sy, sz];
                    pixels[o + 1] = chunk.CurrentZ[sx, sy, sz];
                }
            }
        }
    }
}
