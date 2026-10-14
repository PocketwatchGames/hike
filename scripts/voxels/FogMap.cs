using Godot;

// Per-voxel air volume in an Rg8 texture sampled by the fog raymarch shader via
// `fog_map`. See WindowedVolumeMap for the shared toroidal windowing.
//   R = air density: authored fog raised by the space class's dust, derived at
//       upload through ChunkState.GetFog so the GPU sees exactly what the CPU
//       consumers do.
//   G = water occupancy (255 = a water block). Water draws with
//       depth_draw_never, so the depth buffer at a water pixel holds the terrain
//       UNDER it; the raymarch reads this channel to find where each ray
//       actually met the water. Linear filtering puts the 0.5 crossing exactly
//       on the voxel face.
public class FogMap : WindowedVolumeMap
{
    private const int BYTES_PER_PIXEL = 2;
    private const byte WATER = 255;

    // Needed for the class palette the dust term resolves against.
    private readonly WorldState _world;

    public FogMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, BYTES_PER_PIXEL, Image.Format.Rg8)
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
                int rowOffset = (lz * cells + ly) * cells * BYTES_PER_PIXEL;
                for (int lx = 0; lx < cells; lx++)
                {
                    int o = rowOffset + lx * BYTES_PER_PIXEL;
                    dst[o + 0] = (byte)chunk.GetFog(_world?.SimData, lx, ly, lz);
                    dst[o + 1] = Blocks.IsWater(chunk.Voxels[lx, ly, lz]) ? WATER : (byte)0;
                }
            }
        }
    }
}
