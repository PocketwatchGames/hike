using Godot;

// Per-voxel light texture sampled by the world shaders via the `light_map`
// global. RGBA8: R = sun mask (0..255), G/B/A = block light R/G/B (summed per
// channel at deposit, byte-saturated). See WindowedVolumeMap for the toroidal
// player-centric windowing all five maps share.
//
// SUN DILATION: sunlight propagates into AIR only, so a voxel with geometry in
// it has a sun value of 0. A shader sampling near a surface therefore has the
// ground's own black texels inside its trilinear footprint, dragging the sample
// toward zero by a fraction that cycles with the surface's sub-voxel position —
// which is the slope banding that made the per-vertex sun bake necessary in the
// first place. Each geometry-bearing voxel is written the max sun of its six
// face neighbours instead, which removes the sink while leaving every air cell
// (what fog, motes, models, particles and detail sprites actually sample) at
// its true propagated value.
public class LightMap : WindowedVolumeMap
{
    private static readonly (int dx, int dy, int dz)[] FaceNeighbors =
    {
        (1, 0, 0), (-1, 0, 0), (0, 1, 0), (0, -1, 0), (0, 0, 1), (0, 0, -1),
    };

    // Held for the cross-chunk neighbour reads the dilation needs at chunk
    // borders — EncodeChunkPixels is handed only the chunk itself.
    private readonly WorldState _world;

    public LightMap(WorldState world, Vector3I centerChunk, int windowDiameterChunks)
        : base(world, centerChunk, windowDiameterChunks, ChunkState.SIZE, 4, Image.Format.Rgba8)
    {
        _world = world;
        InitialEncodeAndUpload(world);
    }

    protected override void EncodeChunkPixels(ChunkState chunk, byte[] dst)
    {
        const int cells = ChunkState.SIZE;
        int baseX = chunk.ChunkCoord.X * cells;
        int baseY = chunk.ChunkCoord.Y * cells;
        int baseZ = chunk.ChunkCoord.Z * cells;
        for (int lz = 0; lz < cells; lz++)
        {
            for (int ly = 0; ly < cells; ly++)
            {
                int rowOffset = ((lz * cells + ly) * cells) * 4;
                for (int lx = 0; lx < cells; lx++)
                {
                    // Density is the geometry test the mesher uses, so Barrier
                    // (an invisible light/nav marker with no surface) stays dark
                    // rather than leaking its neighbours' sun back through a
                    // shut door.
                    int sunRaw = Density.TypeDensity(chunk.Voxels[lx, ly, lz]) < 0
                        ? DilatedSunlight(chunk, lx, ly, lz, baseX, baseY, baseZ)
                        : chunk.GetSunlight(lx, ly, lz);
                    int sun = (sunRaw * 255) / LightEngine.MAX_LIGHT;
                    chunk.GetBlockLight(lx, ly, lz, out int br, out int bg, out int bb);
                    if (br > 255) { br = 255; }
                    if (bg > 255) { bg = 255; }
                    if (bb > 255) { bb = 255; }
                    int o = rowOffset + lx * 4;
                    dst[o + 0] = (byte)sun;
                    dst[o + 1] = (byte)br;
                    dst[o + 2] = (byte)bg;
                    dst[o + 3] = (byte)bb;
                }
            }
        }
    }

    // Max sun over the six face neighbours. Six rather than all 26 because the
    // trilinear footprint spans two texels per axis, so a face neighbour is what
    // a sample straddling this surface actually reaches; a cell open only
    // diagonally sits in a corner whose neighbours are dark regardless.
    private int DilatedSunlight(ChunkState chunk, int lx, int ly, int lz, int baseX, int baseY, int baseZ)
    {
        const int cells = ChunkState.SIZE;
        int best = 0;
        for (int i = 0; i < FaceNeighbors.Length; i++)
        {
            (int dx, int dy, int dz) = FaceNeighbors[i];
            int nx = lx + dx;
            int ny = ly + dy;
            int nz = lz + dz;
            int sun;
            if (nx >= 0 && nx < cells && ny >= 0 && ny < cells && nz >= 0 && nz < cells)
            {
                sun = chunk.GetSunlight(nx, ny, nz);
            }
            else
            {
                sun = _world.GetSunlightWorld(baseX + nx, baseY + ny, baseZ + nz);
            }
            if (sun > best)
            {
                best = sun;
            }
        }
        return best;
    }
}
