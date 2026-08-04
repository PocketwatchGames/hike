using Godot;

// Per-voxel air density in an R8 texture sampled by the fog raymarch shader via
// `fog_map`: authored fog raised by the space class's dust, derived at upload
// through ChunkState.GetFog so the GPU sees exactly what the CPU consumers do.
// See WindowedVolumeMap for the shared toroidal windowing.
public class FogMap : WindowedVolumeMap
{
    // Needed for the class palette the dust term resolves against.
    private readonly WorldState _world;

    public FogMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, 1, Image.Format.R8)
    {
        _world = world;
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
                    dst[rowOffset + lx] = (byte)chunk.GetFog(_world?.SimData, lx, ly, lz);
                }
            }
        }
    }
}
