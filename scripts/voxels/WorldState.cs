using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;
    public SimData SimData;

    // Default spawn point baked into the world. Set by the loader (from the
    // world file header) or by Main when starting a procedurally-generated
    // game. The packed world file persists this so a save can recreate the
    // intended starting position.
    public Vector3 Spawn;

    // Persistent simulation clock in milliseconds. Advanced by World.Tick while
    // unpaused; serialized with the rest of the world state so cooldowns,
    // AI timers, etc. survive save/load.
    public ulong GameTimeMs;

    // Normalized time-of-day in [0, 1): 0 = midnight, 0.25 = sunrise,
    // 0.5 = noon, 0.75 = sunset. Advanced by World.Tick scaled by
    // SimData.DayLengthSeconds and the time_scale CVar. SkyController reads
    // this each frame to compute sun/moon orbit and blend day/sunset/night
    // colors. Seeded from SimData.InitialTimeOfDay at world creation.
    public double TimeOfDay01;

    // Sun direction (unit vector, the direction light travels). Written by
    // SkyController each frame from TimeOfDay01; read by
    // World.IsPointInDirectionalSun for the gameplay shadow-reach raycast.
    // During night this holds the moon's light direction (the primary source
    // at night) so shadow-reach queries still make sense.
    public Vector3 ShadowLightDirection = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();

    // Prevailing wind direction in world XZ. Lives here rather than on
    // SimData because it's a MUTABLE sim property — a future weather
    // system will rotate this as storms move in, and it'll want to be
    // serialized with the rest of the world state so reloading a save
    // doesn't reset the weather pattern. WeatherData tunes the strength
    // and rhythm of wind per preset (amplitude, frequency, gusts);
    // direction is orthogonal to that. Y component unused — wind
    // treated as horizontal.
    public Vector3 WindDirection = new Vector3(0.7f, 0f, 0.7f);

    public readonly Dictionary<Vector3I, ChunkState> _chunks = new();
    public readonly Dictionary<Vector3I, List<EntitySimState>> _entities = new();

    // Active block-light sources. Each entry contributes additively to the
    // BlockLight channel via its cached footprint. Added/removed through
    // LightEngine.AddLightSource / RemoveLightSource.
    public readonly List<LightSource> LightSources = new();

    // Set of chunk coords whose stored sunlight or block-light arrays have
    // been written since the last LightMap upload. ChunkManager drains this
    // after each light operation so the GPU upload only re-encodes touched
    // chunks. Populated automatically by SetSunlightWorld / AddBlockLightWorld
    // / SubtractBlockLightWorld — callers don't need to remember.
    public readonly HashSet<Vector3I> LightChunkDirty = new();

    // Same pattern for FogMap. Populated automatically by SetFogWorld.
    // Currently only worldgen writes fog, so this only trips if something
    // mutates fog at runtime (e.g. a weather CVar or future fog emitter).
    public readonly HashSet<Vector3I> FogChunkDirty = new();

    public WorldState(Vector3I min, Vector3I max, SimData simData)
    {
        Min = min;
        Max = max;
        SimData = simData;
        TimeOfDay01 = simData?.InitialTimeOfDay ?? 0.3f;
    }

    // World-coordinate accessors for cross-chunk light propagation

    private static Vector3I WorldToChunkCoord(int wx, int wy, int wz)
    {
        return new Vector3I(
            (int)Math.Floor((double)wx / ChunkState.SIZE),
            (int)Math.Floor((double)wy / ChunkState.SIZE),
            (int)Math.Floor((double)wz / ChunkState.SIZE)
        );
    }

    private static int Mod(int a, int m)
    {
        return ((a % m) + m) % m;
    }

    public bool IsInBounds(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        return _chunks.ContainsKey(cc);
    }

    public VoxelType GetVoxelWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return VoxelType.Air;
        }
        return chunk.Voxels[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)];
    }

    public VoxelTypeInfo.SharpAxes GetShapeWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return VoxelTypeInfo.SharpAxes.None;
        }
        return chunk.GetShape(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetShapeWorld(int wx, int wy, int wz, VoxelTypeInfo.SharpAxes shape)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetShape(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), shape);
    }

    public int GetKitIdWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetKitId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetKitIdWorld(int wx, int wy, int wz, int kitId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetKitId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), kitId);
    }

    public int GetOverlayIdWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetOverlayId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetOverlayIdWorld(int wx, int wy, int wz, int overlayId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetOverlayId(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), overlayId);
    }

    public int GetDetailGroupWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetDetailGroup(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetDetailGroupWorld(int wx, int wy, int wz, int groupId)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetDetailGroup(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), groupId);
    }

    public int GetDetailStrengthWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetDetailStrength(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetDetailStrengthWorld(int wx, int wy, int wz, int strength)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetDetailStrength(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), strength);
    }

    public int GetSunlightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetSunlightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetSunlight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
        LightChunkDirty.Add(cc);
    }

    public void GetBlockLightWorld(int wx, int wy, int wz, out int r, out int g, out int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            r = 0; g = 0; b = 0;
            return;
        }
        chunk.GetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), out r, out g, out b);
    }

    public void AddBlockLightWorld(int wx, int wy, int wz, int r, int g, int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.AddBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b);
        LightChunkDirty.Add(cc);
    }

    public void SubtractBlockLightWorld(int wx, int wy, int wz, int r, int g, int b)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SubtractBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), r, g, b);
        LightChunkDirty.Add(cc);
    }

    // Fog density at a world-space voxel, byte 0-255. 0 = clear air, 255 =
    // thickest. Unloaded chunks return 0 — the streaming-correct default so a
    // chunk outside the resident window reads as "no fog data here" rather
    // than bleeding fog across the window edge.
    public int GetFogWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetFog(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetFogWorld(int wx, int wy, int wz, int density)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetFog(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), density);
        FogChunkDirty.Add(cc);
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type)
    {
        SetVoxelWorld(wx, wy, wz, type, VoxelTypeInfo.GetDefaultShape(type));
    }

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type, VoxelTypeInfo.SharpAxes shape)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        int lx = Mod(wx, ChunkState.SIZE);
        int ly = Mod(wy, ChunkState.SIZE);
        int lz = Mod(wz, ChunkState.SIZE);
        chunk.Voxels[lx, ly, lz] = type;
        chunk.Shape[lx, ly, lz] = (byte)shape;
    }

    // Combined "how lit is this voxel" used by AI visibility checks. Returns
    // a value in [0, LightEngine.MAX_LIGHT]. Sunlight is already in that
    // space; block light is per-channel byte-scale post-pow values, so we
    // collapse to luminance and rescale.
    public int GetLightLevelWorld(int wx, int wy, int wz)
    {
        int sun = GetSunlightWorld(wx, wy, wz);
        GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);
        // Rec.601 luminance, integer-scaled. Each channel saturates at 255
        // for the GPU but the stored ushort can be larger; clamp here too.
        if (r > 255) { r = 255; }
        if (g > 255) { g = 255; }
        if (b > 255) { b = 255; }
        int lum = (r * 299 + g * 587 + b * 114) / 1000;             // 0..255
        int blkScaled = (lum * LightEngine.MAX_LIGHT) / 255;        // 0..MAX_LIGHT
        return Math.Max(sun, blkScaled);
    }
    public int GetLightLevelWorld(Vector3 position)
    {
        int wx = Mathf.FloorToInt(position.X);
        int wy = Mathf.FloorToInt(position.Y);
        int wz = Mathf.FloorToInt(position.Z);
        return GetLightLevelWorld(wx, wy, wz);
    }

    // Perceived brightness at a point, in the same model the shaders use, so
    // gameplay decisions (stealth, mob spawn, etc.) match what the player sees.
    // Mirrors voxel_clip.gdshader / sprite_lit.gdshader:
    //   sun = (bfs_sun / MAX_LIGHT) * sun_intensity
    //         * (sun_ambient + (sunReachesPoint ? (1 - sun_ambient) : 0))
    //   block = max(r, g, b) / 255
    //   perceived = max(sun, block)   (additive in shader, but max here matches
    //                                  how callers reason about "lit vs dark")
    // sunReachesPoint is the result of a directional-shadow raycast: true if
    // the point is in direct sun, false if a tree / cliff / cave is in the way.
    // Callers without physics access (pure-sim code) can pass false to get the
    // shadow-ambient-only value, which is the conservative answer for stealth.
    // Returns float in [0, 1+]; over-bright is possible when block lights stack.
    public float GetPerceivedLightWorld(int wx, int wy, int wz, bool sunReachesPoint)
    {
        int sunBfs = GetSunlightWorld(wx, wy, wz);
        GetBlockLightWorld(wx, wy, wz, out int r, out int g, out int b);

        float sunMask = (float)sunBfs / LightEngine.MAX_LIGHT;
        // Use the time-of-day-blended ambient (not raw weather.sunAmbient) so
        // night/sunset dim the "in shadow" floor the same way sprites see it.
        float ambient = SkyController.Current?.CurrentAmbient ?? 0.4f;
        float sunFactor = ambient + (sunReachesPoint ? (1f - ambient) : 0f);
        float sun = sunMask * CVars.sunIntensity.Value * sunFactor;

        if (r > 255) { r = 255; }
        if (g > 255) { g = 255; }
        if (b > 255) { b = 255; }
        float block = Math.Max(r, Math.Max(g, b)) / 255f;

        return Math.Max(sun, block);
    }

    public float GetPerceivedLightWorld(Vector3 position, bool sunReachesPoint)
    {
        int wx = Mathf.FloorToInt(position.X);
        int wy = Mathf.FloorToInt(position.Y);
        int wz = Mathf.FloorToInt(position.Z);
        return GetPerceivedLightWorld(wx, wy, wz, sunReachesPoint);
    }

    public ChunkState GetChunk(Vector3I coord)
    {
        _chunks.TryGetValue(coord, out ChunkState data);
        return data;
    }

    public bool ContainsChunk(Vector3I coord)
    {
        return _chunks.ContainsKey(coord);
    }

    public List<EntitySimState> GetEntities(Vector3I coord)
    {
        _entities.TryGetValue(coord, out List<EntitySimState> entities);
        return entities;
    }

    public void AddEntity(EntitySimState entity)
    {
        Vector3I coord = World.WorldToChunkCoord(entity.WorldPosition);
        if (!_entities.TryGetValue(coord, out List<EntitySimState> entities))
        {
            entities = new List<EntitySimState>();
            _entities[coord] = entities;
        }
        entities.Add(entity);
    }

    public bool RemoveEntity(EntitySimState entity)
    {
        Vector3I coord = World.WorldToChunkCoord(entity.WorldPosition);
        if (_entities.TryGetValue(coord, out List<EntitySimState> entities))
        {
            return entities.Remove(entity);
        }
        return false;
    }

    public void OnVoxelsChanged(List<Vector3I> changedPositions)
    {
        LightEngine.OnVoxelsChanged(this, changedPositions);
    }

    public void AddLightSource(LightSource source)
    {
        LightEngine.AddLightSource(this, source);
    }

    public void RemoveLightSource(LightSource source)
    {
        LightEngine.RemoveLightSource(this, source);
    }

    public void SetLightAmplitude(LightSource source, float amplitude)
    {
        LightEngine.SetAmplitude(this, source, amplitude);
    }
}
