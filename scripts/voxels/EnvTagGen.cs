using Godot;

// Bakes the per-chunk EnvTag subgrid as a default for procedural worlds.
// Cells with full ambient wind (open to sky) become Outdoor; cells that
// are sealed off (deep cave, sky blocked) become Cave. Building and
// Tunnel are author-only — worldgen has no signal to distinguish a stone
// hallway from a narrow cave, so leaving the editor in charge avoids
// false positives that would route a natural cavern through a building
// reverb preset.
//
// Called once at the end of WorldGen, after WindGen has filled
// ChunkState.WindFactor — that's the input signal. Disk-loaded chunks
// already carry the EnvTag bytes from the .hike file and skip this pass.
public static class EnvTagGen
{
    // WindFactor (0..255) split point. Above this the cell is treated as
    // sky-exposed (Outdoor); at or below, sealed-off (Cave). Matched to
    // WindGen's smooth sunlight taper: 128 corresponds roughly to the
    // half-attenuation point ~7 voxels into a cave entrance, which is
    // about where reverb should start picking up cave character.
    private const int OUTDOOR_WIND_THRESHOLD = 128;

    public static void ComputeEnvTagGrid(WorldState ws)
    {
        for (int cz = ws.Min.Z; cz <= ws.Max.Z; cz++)
        {
            for (int cy = ws.Min.Y; cy <= ws.Max.Y; cy++)
            {
                for (int cx = ws.Min.X; cx <= ws.Max.X; cx++)
                {
                    ChunkState chunk = ws.GetChunk(new Vector3I(cx, cy, cz));
                    if (chunk == null) { continue; }
                    ComputeChunkEnvTag(chunk);
                }
            }
        }
    }

    public static void ComputeChunkEnvTag(ChunkState chunk)
    {
        for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
        {
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                {
                    int wind = chunk.WindFactor[sx, sy, sz];
                    EnvironmentTag tag = wind > OUTDOOR_WIND_THRESHOLD
                        ? EnvironmentTag.Outdoor
                        : EnvironmentTag.Cave;
                    chunk.SetEnvTag(sx, sy, sz, tag);
                }
            }
        }
    }
}
