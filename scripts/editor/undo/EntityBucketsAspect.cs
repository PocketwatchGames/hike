using System.Collections.Generic;
using Godot;

// The list of EntitySimStates filed under a chunk. Snapshotting the whole
// bucket rather than individual adds/removes means one Touch covers a
// placement, a delete, a bulk subscene stamp, or a future move/paste tool —
// and the states themselves are kept by reference, so an undone-then-redone
// chest keeps the loot it was rolled with.
public sealed class EntityBucketsAspect : IEditorEditAspect
{
    private Dictionary<Vector3I, List<EntitySimState>> _before = new Dictionary<Vector3I, List<EntitySimState>>();
    private readonly Dictionary<Vector3I, List<EntitySimState>> _after = new Dictionary<Vector3I, List<EntitySimState>>();

    public void Touch(WorldState world, Vector3I chunkCoord)
    {
        if (!_before.ContainsKey(chunkCoord))
        {
            _before[chunkCoord] = Copy(world.GetEntities(chunkCoord));
        }
    }

    public bool CaptureAfter(WorldState world)
    {
        var changed = new Dictionary<Vector3I, List<EntitySimState>>(_before.Count);
        foreach (KeyValuePair<Vector3I, List<EntitySimState>> kvp in _before)
        {
            List<EntitySimState> now = Copy(world.GetEntities(kvp.Key));
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
        Dictionary<Vector3I, List<EntitySimState>> target = redo ? _after : _before;
        foreach (KeyValuePair<Vector3I, List<EntitySimState>> kvp in target)
        {
            world.ReplaceChunkEntities(kvp.Key, kvp.Value);
            refresh.AddEntityChunk(kvp.Key);
        }
    }

    private static List<EntitySimState> Copy(List<EntitySimState> bucket)
    {
        return bucket == null ? new List<EntitySimState>() : new List<EntitySimState>(bucket);
    }

    private static bool Same(List<EntitySimState> a, List<EntitySimState> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }
        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }
        return true;
    }
}
