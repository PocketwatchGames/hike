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
        InitialEncodeAndUpload(world);
    }

    // Cells outside any resident chunk: both channels = byte 128 (signed zero;
    // default 0 would decode to max-negative current under the byte-128 convention).
    protected override byte[] DefaultPixel => new byte[] { 128, 128 };

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        const int cells = ChunkState.ENV_SUBGRID_SIZE;
        for (int sz = 0; sz < cells; sz++)
        {
            for (int sy = 0; sy < cells; sy++)
            {
                int rowOffset = (sz * cells + sy) * cells * 2;
                for (int sx = 0; sx < cells; sx++)
                {
                    int o = rowOffset + sx * 2;
                    dst[o + 0] = chunk.CurrentX[sx, sy, sz];
                    dst[o + 1] = chunk.CurrentZ[sx, sy, sz];
                }
            }
        }
    }
}
