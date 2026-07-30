using System;
using System.Collections.Generic;
using Godot;

// Authored chunk state that isn't per-voxel: the zone / region indices and the
// coarse env subgrids (wind factor, environment tag) a subscene stamp can
// override. One snapshot covers a whole chunk — the subgrids are 4x4x4, so
// cloning the lot costs a few hundred bytes and saves every tool from tracking
// which env cells it touched.
public sealed class ChunkFieldsAspect : IEditorEditAspect
{
    private sealed class Snapshot
    {
        public byte ZoneIndex;
        public byte RegionIndex;
        public byte[,,] WindFactor;
        public byte[,,] EnvTag;
    }

    private Dictionary<Vector3I, Snapshot> _before = new Dictionary<Vector3I, Snapshot>();
    private readonly Dictionary<Vector3I, Snapshot> _after = new Dictionary<Vector3I, Snapshot>();

    public void Touch(WorldState world, Vector3I chunkCoord)
    {
        if (_before.ContainsKey(chunkCoord))
        {
            return;
        }
        ChunkState chunk = world.GetChunk(chunkCoord);
        if (chunk == null)
        {
            return;
        }
        _before[chunkCoord] = Capture(chunk);
    }

    public bool CaptureAfter(WorldState world)
    {
        var changed = new Dictionary<Vector3I, Snapshot>(_before.Count);
        foreach (KeyValuePair<Vector3I, Snapshot> kvp in _before)
        {
            ChunkState chunk = world.GetChunk(kvp.Key);
            if (chunk == null)
            {
                continue;
            }
            Snapshot now = Capture(chunk);
            if (Same(now, kvp.Value))
            {
                continue;
            }
            changed[kvp.Key] = kvp.Value;
            _after[kvp.Key] = now;
        }
        _before = changed;
        return _after.Count > 0;
    }

    public void Restore(WorldState world, bool redo, EditorRefresh refresh)
    {
        Dictionary<Vector3I, Snapshot> target = redo ? _after : _before;
        foreach (KeyValuePair<Vector3I, Snapshot> kvp in target)
        {
            ChunkState chunk = world.GetChunk(kvp.Key);
            if (chunk == null)
            {
                continue;
            }
            chunk.ZoneIndex = kvp.Value.ZoneIndex;
            chunk.RegionIndex = kvp.Value.RegionIndex;
            Array.Copy(kvp.Value.WindFactor, chunk.WindFactor, chunk.WindFactor.Length);
            Array.Copy(kvp.Value.EnvTag, chunk.EnvTag, chunk.EnvTag.Length);
            // Nothing per-voxel moved, so name one cell of the chunk to get it
            // requeued for the mesh / volume-map upload that carries these.
            refresh.AddVoxel(kvp.Key * ChunkState.SIZE);
        }
    }

    private static Snapshot Capture(ChunkState chunk)
    {
        return new Snapshot
        {
            ZoneIndex = chunk.ZoneIndex,
            RegionIndex = chunk.RegionIndex,
            WindFactor = (byte[,,])chunk.WindFactor.Clone(),
            EnvTag = (byte[,,])chunk.EnvTag.Clone(),
        };
    }

    private static bool Same(Snapshot a, Snapshot b)
    {
        if (a.ZoneIndex != b.ZoneIndex || a.RegionIndex != b.RegionIndex)
        {
            return false;
        }
        const int S = ChunkState.ENV_SUBGRID_SIZE;
        for (int x = 0; x < S; x++)
        {
            for (int y = 0; y < S; y++)
            {
                for (int z = 0; z < S; z++)
                {
                    if (a.WindFactor[x, y, z] != b.WindFactor[x, y, z] || a.EnvTag[x, y, z] != b.EnvTag[x, y, z])
                    {
                        return false;
                    }
                }
            }
        }
        return true;
    }
}
