using Godot;

// Bakes the per-chunk wind VELOCITY subgrid — direction and speed.
//
// How much of that velocity actually reaches a cell is no longer baked here.
// It is derived from Interiorness and the cell's space class
// (ChunkState.GetWindFactor), so there is exactly one measure of "how open is
// this" in the world instead of a sunlight-derived copy that had to be re-baked
// to stay in step with it.
//
// WindVelocity: every cell starts at the chunk's zone wind direction
// (per-chunk via ZoneIndex) × DEFAULT_BASE_SPEED. Authored overrides
// (mountain pass funnels, cave drafts, localized gusts) are stamped by
// later worldgen passes or the editor.
//
// Called once at the end of WorldGen (after sunlight + zone assignment)
// so wind ships baked into chunks. Disk-loaded chunks already carry the
// bytes from the .hike file and skip this pass entirely.
public static class WindGen
{
    // World m/s of wind velocity at stored ±1 in the byte-128-zero
    // encoding. MUST match `wind_velocity_scale` in project.godot —
    // any change here requires re-baking chunks (or a matching shader
    // global update via SkyController / CVars).
    public const float WIND_VELOCITY_SCALE = 30f;

    // Baseline ambient wind speed in m/s, used to seed every cell from
    // its chunk's zone wind direction. Per-frame weather modulates the
    // effective wind separately via a global multiplier; this is just
    // the "calm-day" magnitude that lives on disk.
    public const float DEFAULT_BASE_SPEED = 5f;

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
                    ComputeChunkWind(ws, chunk);
                }
            }
        }
    }

    public static void ComputeChunkWind(WorldState ws, ChunkState chunk)
    {
        // Look up this chunk's zone direction. ZoneIndex is per-chunk,
        // so every cell in this chunk seeds from the same direction;
        // overrides at sub-chunk granularity are a later pass's job.
        Vector3 zoneDir = Vector3.Zero;
        if (ws.Zones != null && chunk.ZoneIndex < ws.Zones.Length)
        {
            zoneDir = ws.Zones[chunk.ZoneIndex].WindDirection;
        }
        if (zoneDir.LengthSquared() > 1e-6f)
        {
            zoneDir = zoneDir.Normalized();
        }
        FillChunkWind(chunk, zoneDir * DEFAULT_BASE_SPEED);
    }

    // Write one uniform velocity (world m/s) across a chunk's whole subgrid.
    // Split out so a caller that already knows the velocity — the map painter's
    // wind layer — seeds a chunk without going through the zone lookup.
    public static void FillChunkWind(ChunkState chunk, Vector3 velocity)
    {
        // Pre-divide by the storage scale once per chunk; SetWindVelocity
        // expects values already in [-1, 1].
        Vector3 storedVel = velocity / WIND_VELOCITY_SCALE;

        for (int sx = 0; sx < ChunkState.ENV_SUBGRID_SIZE; sx++)
        {
            for (int sy = 0; sy < ChunkState.ENV_SUBGRID_SIZE; sy++)
            {
                for (int sz = 0; sz < ChunkState.ENV_SUBGRID_SIZE; sz++)
                {
                    chunk.SetWindVelocity(sx, sy, sz, storedVel.X, storedVel.Y, storedVel.Z);
                }
            }
        }
    }
}
