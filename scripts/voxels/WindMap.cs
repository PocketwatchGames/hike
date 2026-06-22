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
//   A       = WindFactor. 0 = sealed (deep cave / building interior), 255 = full
//             ambient wind.
//
// See WindowedVolumeMap for the shared toroidal windowing.
public class WindMap : WindowedVolumeMap
{
    private const int BYTES_PER_PIXEL = 4;

    public WindMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.ENV_SUBGRID_SIZE, BYTES_PER_PIXEL, Image.Format.Rgba8)
    {
        // Seed velocity channels (RGB) to byte 128 = signed zero so unencoded
        // cells decode to zero wind, not max-negative. Alpha (WindFactor)
        // defaults to 0 = sealed, the safe pre-bake state.
        for (int z = 0; z < _slicePixels.Length; z++)
        {
            byte[] slice = _slicePixels[z];
            for (int i = 0; i < slice.Length; i += BYTES_PER_PIXEL)
            {
                slice[i + 0] = 128;
                slice[i + 1] = 128;
                slice[i + 2] = 128;
                slice[i + 3] = 0;
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
                int rowOffset = ((baseY + sy) * _width + baseX) * BYTES_PER_PIXEL;
                for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
                {
                    int o = rowOffset + sx * BYTES_PER_PIXEL;
                    // Pre-multiply velocity by WindFactor so sealed cells read
                    // as zero wind on the GPU. Byte-128-zero scales linearly by
                    // factor/255: (raw - 128) * factor / 255 + 128.
                    byte factor = chunk.WindFactor[sx, sy, sz];
                    int vx = chunk.WindVelocityX[sx, sy, sz];
                    int vy = chunk.WindVelocityY[sx, sy, sz];
                    int vz = chunk.WindVelocityZ[sx, sy, sz];
                    pixels[o + 0] = (byte)(((vx - 128) * factor) / 255 + 128);
                    pixels[o + 1] = (byte)(((vy - 128) * factor) / 255 + 128);
                    pixels[o + 2] = (byte)(((vz - 128) * factor) / 255 + 128);
                    pixels[o + 3] = factor;
                }
            }
        }
    }
}
