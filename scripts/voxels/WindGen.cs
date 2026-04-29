using Godot;

// Bakes the per-chunk WindFactor subgrid from already-computed sunlight.
// Sky-exposed voxels fill to MAX_LIGHT in the vertical seed pass; lateral
// BFS spread decays by FALLOFF_PER_VOXEL=4 per voxel, so raw sunlight
// reaches ~15 voxels into a cave before hitting zero. Averaging raw
// sunlight per cell gives a long, soft falloff — wind tapers gradually
// as the player walks deep into a cave instead of snapping off at the
// entrance. Cleared transparency (water surface) inherits the column's
// near-MAX value, so outdoor lake cells read as full wind without any
// special handling.
//
// Called once at the end of WorldGen (after sunlight) so wind ships
// baked into chunks. Disk-loaded chunks already carry the bytes from the
// .hike file and skip this pass entirely.
public static class WindGen
{
    public static void ComputeWindGrid(WorldState ws)
    {
        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }
                    ComputeChunkWind(chunk);
                }
            }
        }
    }

    public static void ComputeChunkWind(ChunkState chunk)
    {
        const int cellSize = ChunkState.ENV_VOXELS_PER_CELL;
        const int voxelsPerCell = cellSize * cellSize * cellSize;
        int divisor = voxelsPerCell * LightEngine.MAX_LIGHT;

        for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
        {
            int x0 = sx * cellSize;
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                int y0 = sy * cellSize;
                for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                {
                    int z0 = sz * cellSize;
                    // Average raw sunlight across the cell, normalized to
                    // 0..255. Sunlight's lateral BFS decay (~4 per voxel)
                    // produces a long, smooth taper — full at open sky,
                    // ~half ~7 voxels into a cave, near zero ~15 voxels in.
                    int sum = 0;
                    for (int dx = 0; dx < cellSize; dx++)
                    {
                        for (int dy = 0; dy < cellSize; dy++)
                        {
                            for (int dz = 0; dz < cellSize; dz++)
                            {
                                sum += chunk.Sunlight[x0 + dx, y0 + dy, z0 + dz];
                            }
                        }
                    }
                    chunk.SetWindFactor(sx, sy, sz, (sum * 255) / divisor);
                }
            }
        }
    }
}
