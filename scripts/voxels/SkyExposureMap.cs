using Godot;

// Per-voxel non-leaky VERTICAL sky-reach (ChunkState.SkyExposure) in an R8
// texture, exposed via `sky_exposure_map`. The rain shader samples it to clip
// falling drops at the true overhead-cover line (roof / overhang / cave ceiling
// / dense canopy) instead of the horizontally-leaking BFS sun mask. Stored
// 0..LightEngine.MAX_LIGHT, encoded to 0..255. See WindowedVolumeMap for the
// shared toroidal windowing.
public class SkyExposureMap : WindowedVolumeMap
{
    public SkyExposureMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, 1, Image.Format.R8)
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
                int rowOffset = (baseY + ly) * _width + baseX;
                for (int lx = 0; lx < ChunkState.SIZE; lx++)
                {
                    int sky = (chunk.GetSkyExposure(lx, ly, lz) * 255) / LightEngine.MAX_LIGHT;
                    pixels[rowOffset + lx] = (byte)sky;
                }
            }
        }
    }
}
