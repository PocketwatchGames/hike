using Godot;

// Bakes per-chunk wind subgrids: WindFactor (openness, computed from
// already-computed sunlight) and WindVelocity (per-zone direction at a
// baseline ambient speed).
//
// WindFactor: sky-exposed voxels fill to MAX_LIGHT in the vertical seed
// pass; lateral BFS spread decays by FALLOFF_PER_VOXEL=4 per voxel, so
// raw sunlight reaches ~15 voxels into a cave before hitting zero.
// Averaging raw sunlight per cell gives a long, soft falloff — wind
// tapers gradually as the player walks deep into a cave instead of
// snapping off at the entrance. Cleared transparency (water surface)
// inherits the column's near-MAX value, so outdoor lake cells read as
// full wind without any special handling.
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
        const int cellSize = ChunkState.ENV_VOXELS_PER_CELL;
        const int voxelsPerCell = cellSize * cellSize * cellSize;
        int divisor = voxelsPerCell * LightEngine.MAX_LIGHT;

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
        Vector3 baseVelocity = zoneDir * DEFAULT_BASE_SPEED;
        // Pre-divide by the storage scale once per chunk; SetWindVelocity
        // expects values already in [-1, 1].
        Vector3 storedVel = baseVelocity / WIND_VELOCITY_SCALE;

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
                    int windFactor = (sum * 255) / divisor;
                    // The cell's space class damps what gets in. Baked rather
                    // than applied at sample time because WindFactor is
                    // uploaded as wind_map's alpha and read by the sprite
                    // shaders — a CPU-side multiply would leave grass swaying
                    // inside a sealed building. Safe to bake because this
                    // recomputes from Sunlight every run and never reads the
                    // previous value back, so re-running can't compound it.
                    InteriorAmbienceData ambience = ws.SimData?.GetInteriorAmbience(chunk.EnvTag[sx, sy, sz]);
                    if (ambience != null && ambience.windSuppression > 0f)
                    {
                        windFactor = (int)(windFactor * (1f - Mathf.Clamp(ambience.windSuppression, 0f, 1f)));
                    }
                    chunk.SetWindFactor(sx, sy, sz, windFactor);
                    chunk.SetWindVelocity(sx, sy, sz, storedVel.X, storedVel.Y, storedVel.Z);
                }
            }
        }
    }
}
