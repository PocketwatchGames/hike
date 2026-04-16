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

    // Shadow-casting sun direction (unit vector, the direction light travels)
    // and strength [0, 1]. Simulation state so time-of-day can drive them;
    // ShadowMapRenderer reads these each frame. CVars.shadowStrength is a
    // multiplier applied on top for visual tuning.
    public Vector3 ShadowLightDirection = new Vector3(-0.215f, -0.819f, -0.532f).Normalized();
    public float ShadowStrength = 0.3f;
    // Tint applied to fully shadowed fragments. (0,0,0) = pure black shadow,
    // (0.3, 0.4, 0.6) = cool blue-tinted, (1,1,1) = no visible shadow.
    public Color ShadowColor = new Color(0f, 0.2f, 0.5f);

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

    public WorldState(Vector3I min, Vector3I max, SimData simData)
    {
        Min = min;
        Max = max;
        SimData = simData;
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

    public void SetVoxelWorld(int wx, int wy, int wz, VoxelType type)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.Voxels[Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE)] = type;
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
