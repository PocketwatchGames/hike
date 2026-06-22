using Godot;

// Per-voxel fog density (ChunkState.FogDensity) in an R8 texture sampled by the
// fog raymarch shader via `fog_map`. See WindowedVolumeMap for the shared
// toroidal windowing.
public class FogMap : WindowedVolumeMap
{
    public FogMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
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
                    pixels[rowOffset + lx] = chunk.FogDensity[lx, ly, lz];
                }
            }
        }
    }
}
