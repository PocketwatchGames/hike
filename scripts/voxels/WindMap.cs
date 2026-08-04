using Godot;

// Coarse wind texture (RGBA8, one cell per ENV_VOXELS_PER_CELL voxel block),
// sampled by shaders and the GPU-particle attractor via `wind_map`.
//
// Channel layout:
//   R, G, B = EFFECTIVE wind velocity (raw zone/override velocity already
//             multiplied by WindFactor). Signed, byte-128-zero encoding —
//             shader does `texture(wind_map, uvw).rgb * 2.0 - 1.0` to recover
//             [-1, 1], then multiplies by `wind_velocity_scale` for world m/s.
//             Sealed cells (WindFactor = 0) decode to zero, so a single lookup
//             gives the attractor the right "no wind in caves" behavior. The
//             pre-multiply happens here at upload; the ChunkState arrays stay
//             raw (CPU consumers read WindVelocityX/Y/Z for the calm direction).
//   A       = wind factor, DERIVED per cell from Interiorness and the space
//             class's windSuppression (ChunkState.GetWindFactor) rather than
//             read from a baked channel. 0 = sealed (deep cave / building
//             interior), 255 = full ambient wind.
//
// See WindowedVolumeMap for the shared toroidal windowing.
public class WindMap : WindowedVolumeMap
{
    private const int BYTES_PER_PIXEL = 4;

    // The wind factor is derived per cell from Interiorness plus the cell's
    // space class, so the encoder needs the palette. Held as the world rather
    // than a snapshot of SimData so a world swap can't leave a stale palette.
    private readonly WorldState _world;

    public WindMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.ENV_SUBGRID_SIZE, BYTES_PER_PIXEL, Image.Format.Rgba8)
    {
        _world = world;
        InitialEncodeAndUpload(world);
    }

    // Cells outside any resident chunk: velocity RGB = byte 128 (signed zero, so
    // they decode to zero wind not max-negative), WindFactor alpha = 0 (sealed).
    protected override byte[] DefaultPixel => new byte[] { 128, 128, 128, 0 };

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        const int cells = ChunkState.ENV_SUBGRID_SIZE;
        for (int sz = 0; sz < cells; sz++)
        {
            for (int sy = 0; sy < cells; sy++)
            {
                int rowOffset = (sz * cells + sy) * cells * BYTES_PER_PIXEL;
                for (int sx = 0; sx < cells; sx++)
                {
                    int o = rowOffset + sx * BYTES_PER_PIXEL;
                    // Pre-multiply velocity by WindFactor so sealed cells read
                    // as zero wind on the GPU. Byte-128-zero scales linearly by
                    // factor/255: (raw - 128) * factor / 255 + 128.
                    byte factor = (byte)chunk.GetWindFactor(_world?.SimData, sx, sy, sz);
                    int vx = chunk.WindVelocityX[sx, sy, sz];
                    int vy = chunk.WindVelocityY[sx, sy, sz];
                    int vz = chunk.WindVelocityZ[sx, sy, sz];
                    dst[o + 0] = (byte)(((vx - 128) * factor) / 255 + 128);
                    dst[o + 1] = (byte)(((vy - 128) * factor) / 255 + 128);
                    dst[o + 2] = (byte)(((vz - 128) * factor) / 255 + 128);
                    dst[o + 3] = factor;
                }
            }
        }
    }
}
