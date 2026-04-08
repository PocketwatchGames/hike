using System;
using System.Collections.Generic;
using Godot;

public class WorldState
{
    public readonly Vector3I Min;
    public readonly Vector3I Max;
    public SimData SimData;

    // Persistent simulation clock in milliseconds. Advanced by World.Tick while
    // unpaused; serialized with the rest of the world state so cooldowns,
    // AI timers, etc. survive save/load.
    public ulong GameTimeMs;

    public readonly Dictionary<Vector3I, ChunkState> _chunks = new();
    public readonly Dictionary<Vector3I, List<PropSimState>> _props = new();
    public readonly Dictionary<Vector3I, List<MobSimState>> _mobs = new();
    public readonly Dictionary<Vector3I, List<InteractiveSimState>> _interactives = new();

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
    }

    public int GetBlockLightWorld(int wx, int wy, int wz)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return 0;
        }
        return chunk.GetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE));
    }

    public void SetBlockLightWorld(int wx, int wy, int wz, int level)
    {
        Vector3I cc = WorldToChunkCoord(wx, wy, wz);
        if (!_chunks.TryGetValue(cc, out ChunkState chunk))
        {
            return;
        }
        chunk.SetBlockLight(Mod(wx, ChunkState.SIZE), Mod(wy, ChunkState.SIZE), Mod(wz, ChunkState.SIZE), level);
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

    public int GetLightLevelWorld(int wx, int wy, int wz)
    {
        return Math.Max(GetSunlightWorld(wx, wy, wz), GetBlockLightWorld(wx, wy, wz));
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

    public List<PropSimState> GetProps(Vector3I coord)
    {
        _props.TryGetValue(coord, out List<PropSimState> props);
        return props;
    }

    public void AddProp(PropSimState prop)
    {
        Vector3I coord = World.WorldToChunkCoord(prop.WorldPosition);
        if (!_props.TryGetValue(coord, out List<PropSimState> props))
        {
            props = new List<PropSimState>();
            _props[coord] = props;
        }
        props.Add(prop);
    }

   public List<MobSimState> GetMobs(Vector3I coord)
    {
        _mobs.TryGetValue(coord, out List<MobSimState> mobs);
        return mobs;
    }

    public List<InteractiveSimState> GetInteractives(Vector3I coord)
    {
        _interactives.TryGetValue(coord, out List<InteractiveSimState> interactives);
        return interactives;
    }

    public void AddInteractive(InteractiveSimState data)
    {
        Vector3I cc = WorldToChunkCoord(
            (int)Math.Floor(data.WorldPosition.X),
            (int)Math.Floor(data.WorldPosition.Y),
            (int)Math.Floor(data.WorldPosition.Z)
        );
        if (!_interactives.TryGetValue(cc, out List<InteractiveSimState> list))
        {
            list = new List<InteractiveSimState>();
            _interactives[cc] = list;
        }
        list.Add(data);
    }

    public void UpdateLightingAt(List<Vector3I> changedPositions)
    {
        LightEngine.UpdateLighting(this, changedPositions);
    }

    public void PropagateLightingAt(List<Vector3I> sourcePositions)
    {
        LightEngine.PropagateLighting(this, sourcePositions);
    }
}
