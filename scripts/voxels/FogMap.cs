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

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        const int cells = ChunkState.SIZE;
        for (int lz = 0; lz < cells; lz++)
        {
            for (int ly = 0; ly < cells; ly++)
            {
                int rowOffset = (lz * cells + ly) * cells;
                for (int lx = 0; lx < cells; lx++)
                {
                    byte fog = chunk.FogDensity[lx, ly, lz];
                    // Roof dust is a floor on the authored fog, applied here so
                    // the serialized field is never touched.
                    if (chunk.RoofDust != null)
                    {
                        byte dust = chunk.RoofDust[lx, ly, lz];
                        if (dust > fog) { fog = dust; }
                    }
                    dst[rowOffset + lx] = fog;
                }
            }
        }
    }
}
